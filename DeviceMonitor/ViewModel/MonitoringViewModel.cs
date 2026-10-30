// 管理监控页设备勾选、批量启停、读数汇总和曲线选择。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public partial class MonitoringViewModel : PageViewModel
{
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    private readonly DispatcherTimer _dataAgeTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    // 合并短时间内的读数列表刷新请求，避免重复向 UI 队列提交操作。
    private DispatcherOperation? _readingRefreshOperation;
    // 只订阅当前任务一次；任务删除或页面退出时配对解除。
    private readonly HashSet<DeviceSamplingViewModel> _observedSessions = new();
    private bool _isShuttingDown;
    private bool _isChangingChecks;
    private bool _isRefreshingReadingRows;
    // 列表重建时按设备和点位编号恢复曲线选择。
    private (long DeviceId, long PointId)? _lastSelectedPoint;
    private int _deviceRemovalVersion;

    public DeviceTasks DeviceTasks
    {
        get;
    }

    public ObservableCollection<MonitoringDeviceViewModel> Devices { get; } = new();
    // 显示名单引用任务原有的点位对象，不复制读数或转发一套属性。
    public ObservableCollection<PointMonitorViewModel> Readings { get; } = new();
    public LiveTrendViewModel Trend { get; } = new();
    public int ActiveSamplingCount => DeviceTasks.ActiveSamplingCount;

    [ObservableProperty]
    private PointMonitorViewModel? _selectedReading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSamplingCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAllDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearDeviceChecksCommand))]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSamplingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSamplingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopAllSamplingCommand))]
    private bool _isStoppingSampling;

    [ObservableProperty]
    private string _pageMessage = string.Empty;

    public bool HasReadings => Readings.Count > 0;

    public MonitoringViewModel(UserAccount currentUser, DeviceStore deviceStore,
    DeviceTasks deviceTasks) : base("实时监控")
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        DeviceTasks = deviceTasks ?? throw new ArgumentNullException(nameof(deviceTasks));

        if (!_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("当前账户不能查看实时监控。");

        ((INotifyCollectionChanged)DeviceTasks.Sessions).CollectionChanged += Sessions_CollectionChanged;

        DeviceTasks.PropertyChanged += DeviceTasks_PropertyChanged;
        DeviceTasks.DeviceDeleted += DeviceTasks_DeviceDeleted;

        _dataAgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dataAgeTimer.Tick += DataAgeTimer_Tick;
        _dataAgeTimer.Start();
    }

    /// <summary>加载全部设备配置，按编号保留原勾选；新设备不勾选，也不自动开始采集。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private async Task RefreshDevicesAsync()
    {
        if (!CanRefreshDevices())
            return;

        IsRefreshing = true;
        int removalVersionAtStart = _deviceRemovalVersion;
        PageMessage = "正在加载设备配置…";

        try
        {
            var latest = await Task.Run(() => _deviceStore.GetDevices(_currentUser));

            if (_isShuttingDown || DeviceTasks.IsShuttingDown || removalVersionAtStart != _deviceRemovalVersion)
                return;

            HashSet<long> checkedIds = Devices.Where(row => row.IsChecked).Select(row => row.Device.Id).ToHashSet();

            DetachDeviceRows();

            Devices.Clear();

            foreach (DeviceInfo device in latest)
            {
                DeviceSamplingViewModel? session = DeviceTasks.Sessions.FirstOrDefault(item => item.DeviceId == device.Id);
                var row = new MonitoringDeviceViewModel(device, session) { IsChecked = checkedIds.Contains(device.Id) };
                row.PropertyChanged += DeviceRow_PropertyChanged;
                Devices.Add(row);
            }

            RefreshCheckedDevices();

            PageMessage = Devices.Count == 0 ? "暂无设备配置。" : string.Empty;
        }
        catch (Exception ex)
        {
            if (!_isShuttingDown && !DeviceTasks.IsShuttingDown)
                PageMessage = $"加载设备失败：{ex.Message}。保留原来的列表。";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private bool CanRefreshDevices() => !_isShuttingDown && !DeviceTasks.IsShuttingDown && !IsRefreshing;

    /// <summary>全部勾选按钮的命令</summary>
    [RelayCommand(CanExecute = nameof(CanChangeChecks))]
    private void CheckAllDevices() => ChangeAllChecks(true);

    /// <summary>清除勾选按钮的命令</summary>
    [RelayCommand(CanExecute = nameof(CanChangeChecks))]
    private void ClearDeviceChecks() => ChangeAllChecks(false);

    private bool CanChangeChecks() => !_isShuttingDown && !DeviceTasks.IsShuttingDown && !IsRefreshing && Devices.Count > 0;

    /// <summary>批量勾选或清除勾选</summary>
    private void ChangeAllChecks(bool isChecked)
    {
        if (!CanChangeChecks())
            return;

        _isChangingChecks = true;
        try
        {
            foreach (MonitoringDeviceViewModel row in Devices)
                row.IsChecked = isChecked;
        }
        finally
        {
            _isChangingChecks = false;
        }
        RefreshCheckedDevices();
    }

    /// <summary>设备行变化时，勾选变化刷新汇总，任务状态变化刷新按钮。</summary>
    private void DeviceRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isShuttingDown)
            return;
        if (e.PropertyName == nameof(MonitoringDeviceViewModel.IsChecked) && !_isChangingChecks)
            RefreshCheckedDevices();
        else if (e.PropertyName is nameof(MonitoringDeviceViewModel.Sampling) or nameof(MonitoringDeviceViewModel.SamplingStateText))
            NotifySamplingCommands();
    }

    /// <summary>勾选改完后，更新点位表和按钮；不开始采集。</summary>
    private void RefreshCheckedDevices()
    {
        RefreshReadingRows();
        NotifySamplingCommands();
        CheckAllDevicesCommand.NotifyCanExecuteChanged();
        ClearDeviceChecksCommand.NotifyCanExecuteChanged();
    }

    /// <summary>把勾选且允许启动的设备交给 DeviceTasks，再由各自的任务开始循环采集。</summary>
    [RelayCommand(CanExecute = nameof(CanStartSampling))]
    private void StartSampling()
    {
        MonitoringDeviceViewModel[] checkedRows = Devices.Where(row => row.IsChecked).ToArray();

        if (checkedRows.Length == 0)
        {
            PageMessage = "请先在设备表中勾选要启动的设备。";
            return;
        }
        if (!CanStartSampling())
            return;

        MonitoringDeviceViewModel[] targets = checkedRows.Where(row => row.Device.IsEnabled
            && row.Sampling?.IsRunning != true && !DeviceTasks.IsDeletingDevice(row.Device.Id)).ToArray();

        int started = 0;
        var failures = new List<string>();

        foreach (MonitoringDeviceViewModel row in targets)
        {
            try
            {
                DeviceTasks.Start(row.Device.Id);

                started++;
            }
            catch (Exception ex)
            {
                failures.Add($"{row.Device.Name}：{ex.Message}");
            }
        }

        PageMessage = $"已向 {started} 台设备发出启动请求；跳过 {checkedRows.Length - targets.Length} 台已运行、停用或正在删除的设备。"
            + (failures.Count == 0 ? string.Empty : $" 启动失败：{string.Join("；", failures)}");

        NotifySamplingCommands();
    }

    private bool CanStartSampling() => CanControlSampling() && !IsRefreshing && !IsStoppingSampling
        && Devices.Any(row => row.IsChecked && row.Device.IsEnabled && row.Sampling?.IsRunning != true
            && !DeviceTasks.IsDeletingDevice(row.Device.Id));

    /// <summary>同时请求停止勾选设备，再等它们各自完成读取和保存收尾。</summary>
    [RelayCommand(CanExecute = nameof(CanStopSampling))]
    private async Task StopSamplingAsync()
    {
        if (!Devices.Any(row => row.IsChecked))
        {
            PageMessage = "请先勾选要停止的设备。";
            return;
        }
        if (!CanStopSampling())
            return;

        DeviceSamplingViewModel[] targets = Devices.Where(row => row.IsChecked && row.Sampling?.IsRunning == true)
            .Select(row => row.Sampling!).ToArray();
        await StopTasksAsync(targets);
    }

    /// <summary>不看当前勾选，停止所有正在运行的设备；之后仍允许再次开始。</summary>
    [RelayCommand(CanExecute = nameof(CanStopAllSampling))]
    private async Task StopAllSamplingAsync()
    {
        if (!CanStopAllSampling())
            return;
        await StopTasksAsync(DeviceTasks.Sessions.Where(session => session.IsRunning).ToArray());
    }

    private bool CanStopAllSampling() => CanControlSampling() && !IsStoppingSampling
        && DeviceTasks.ActiveSamplingCount > 0;

    private async Task StopTasksAsync(IReadOnlyList<DeviceSamplingViewModel> targets)
    {
        IsStoppingSampling = true;
        try
        {
            var failures = new List<string>();

            await Task.WhenAll(targets.Select(async session =>
            {
                try
                {
                    await DeviceTasks.StopAsync(session.DeviceId);
                }
                catch (Exception ex)
                {
                    string name = Devices.FirstOrDefault(row => row.Device.Id == session.DeviceId)?.Device.Name
                        ?? $"设备 #{session.DeviceId}";
                    failures.Add($"{name}：{ex.Message}");
                }
            }));

            if (!_isShuttingDown)
                PageMessage = $"已停止 {targets.Count - failures.Count} 台设备。"
                    + (failures.Count == 0 ? string.Empty : $" 停止失败：{string.Join("；", failures)}");
        }

        finally
        {
            IsStoppingSampling = false;
        }
    }

    private bool CanStopSampling() => CanControlSampling() && !IsStoppingSampling
        && Devices.Any(row => row.IsChecked && row.Sampling?.IsRunning == true);

    private bool CanControlSampling() => !_isShuttingDown && !DeviceTasks.IsShuttingDown && _currentUser.IsEnabled
        && _currentUser.Role is UserRole.Administrator or UserRole.Operator;

    /// <summary>设备任务的任务集合变化后，按设备编号给显示行接上任务，再刷新读数。</summary>
    private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isShuttingDown)
            return;

        foreach (MonitoringDeviceViewModel row in Devices)
            row.AttachSampling(DeviceTasks.Sessions.FirstOrDefault(item => item.DeviceId == row.Device.Id));

        RefreshReadingRows();
        NotifySamplingCommands();
    }

    /// <summary>从勾选设备的采集任务中取出读数行，补上设备名称后放进右侧表格。</summary>
    private void RefreshReadingRows()
    {
        long? selectedDeviceId = SelectedReading?.DeviceId ?? _lastSelectedPoint?.DeviceId;
        long? selectedPointId = SelectedReading?.Point.Id ?? _lastSelectedPoint?.PointId;

        MonitoringDeviceViewModel[] checkedRows = Devices.Where(row => row.IsChecked && row.Sampling is not null).ToArray();

        HashSet<DeviceSamplingViewModel> desiredSessions = checkedRows.Select(row => row.Sampling!).ToHashSet();

        foreach (DeviceSamplingViewModel session in _observedSessions.Where(item => !desiredSessions.Contains(item)).ToArray())
        {
            session.Readings.CollectionChanged -= SessionReadings_CollectionChanged;
            _observedSessions.Remove(session);
        }

        foreach (DeviceSamplingViewModel session in desiredSessions)
            if (_observedSessions.Add(session))
                session.Readings.CollectionChanged += SessionReadings_CollectionChanged;

        var desiredRows = new List<PointMonitorViewModel>();
        foreach (MonitoringDeviceViewModel deviceRow in checkedRows)
        {
            foreach (PointMonitorViewModel reading in deviceRow.Sampling!.Readings)
            {
                reading.UpdateDevice(deviceRow.Device);
                desiredRows.Add(reading);
            }
        }

        _isRefreshingReadingRows = true;
        try
        {
            // 只在勾选或点位清单变化时重填引用；不重建点位对象，不清除它们的曲线。
            Readings.Clear();
            foreach (PointMonitorViewModel row in desiredRows)
                Readings.Add(row);

            SelectedReading = Readings.FirstOrDefault(row => row.DeviceId == selectedDeviceId && row.Point.Id == selectedPointId)
                ?? Readings.FirstOrDefault();
        }
        finally
        {
            _isRefreshingReadingRows = false;
        }
        OnPropertyChanged(nameof(HasReadings));
        Trend.Select(SelectedReading);
    }

    private void SessionReadings_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isShuttingDown || _readingRefreshOperation is not null)
            return;

        _readingRefreshOperation = _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _readingRefreshOperation = null;

            if (!_isShuttingDown)
                RefreshReadingRows();
        }));
    }

    /// <summary>设备任务状态变了，更新运行台数和按钮是否可点。</summary>
    private void DeviceTasks_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceTasks.ActiveSamplingCount))
            OnPropertyChanged(nameof(ActiveSamplingCount));
        if (e.PropertyName is nameof(DeviceTasks.ActiveSamplingCount) or nameof(DeviceTasks.IsShuttingDown)
            or nameof(DeviceTasks.IsDeletingDevices))
            NotifySamplingCommands();
        if (e.PropertyName == nameof(DeviceTasks.IsShuttingDown))
        {
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            CheckAllDevicesCommand.NotifyCanExecuteChanged();
            ClearDeviceChecksCommand.NotifyCanExecuteChanged();
        }
    }

    private void DeviceTasks_DeviceDeleted(long deviceId)
    {
        if (_isShuttingDown)
            return;
        _deviceRemovalVersion++;
        MonitoringDeviceViewModel? row = Devices.FirstOrDefault(item => item.Device.Id == deviceId);
        if (row is not null)
        {
            row.PropertyChanged -= DeviceRow_PropertyChanged;
            row.DetachSampling();
            Devices.Remove(row);
        }
        RefreshCheckedDevices();
        PageMessage = "设备已删除。";
    }

    /// <summary>让开始、停止按钮重新检查能否点击；不执行启停操作。</summary>
    private void NotifySamplingCommands()
    {
        StartSamplingCommand.NotifyCanExecuteChanged();
        StopSamplingCommand.NotifyCanExecuteChanged();
        StopAllSamplingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedReadingChanged(PointMonitorViewModel? value)
    {
        if (value is not null)
            _lastSelectedPoint = (value.DeviceId, value.Point.Id);
        if (!_isRefreshingReadingRows)
            Trend.Select(value);
    }

    /// <summary>每秒更新设备行的“几秒前”提示，不读取设备。</summary>
    private void DataAgeTimer_Tick(object? sender, EventArgs e)
    {
        if (_isShuttingDown || DeviceTasks.IsShuttingDown)
            return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MonitoringDeviceViewModel row in Devices)
            row.UpdateDataAge(now);
    }

    /// <summary>解除旧设备行的通知订阅，不停止采集任务。</summary>
    private void DetachDeviceRows()
    {
        foreach (MonitoringDeviceViewModel row in Devices)
        {
            row.PropertyChanged -= DeviceRow_PropertyChanged;
            row.DetachSampling();
        }
    }

    /// <summary>取消待执行的刷新，解除读数通知，清掉显示行；不删除数据库历史。</summary>
    private void ClearReadingRows()
    {
        _readingRefreshOperation?.Abort();
        _readingRefreshOperation = null;
        foreach (DeviceSamplingViewModel session in _observedSessions)
            session.Readings.CollectionChanged -= SessionReadings_CollectionChanged;
        _observedSessions.Clear();
        SelectedReading = null;
        Readings.Clear();
        OnPropertyChanged(nameof(HasReadings));
    }

    /// <summary>退出时等本页操作结束，再解除计时器和事件订阅。</summary>
    public override async Task StopOperationsAsync()
    {
        _isShuttingDown = true;
        _dataAgeTimer.Stop();
        _dataAgeTimer.Tick -= DataAgeTimer_Tick;
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        CheckAllDevicesCommand.NotifyCanExecuteChanged();
        ClearDeviceChecksCommand.NotifyCanExecuteChanged();
        NotifySamplingCommands();

        await Task.WhenAll(RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask,
            StopSamplingCommand.ExecutionTask ?? Task.CompletedTask,
            StopAllSamplingCommand.ExecutionTask ?? Task.CompletedTask);
        ((INotifyCollectionChanged)DeviceTasks.Sessions).CollectionChanged -= Sessions_CollectionChanged;
        DeviceTasks.PropertyChanged -= DeviceTasks_PropertyChanged;
        DeviceTasks.DeviceDeleted -= DeviceTasks_DeviceDeleted;
        DetachDeviceRows();
        ClearReadingRows();
        Trend.Dispose();
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Sampling;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 实时监控的数据源：左侧勾选设备，顶部批量启停，右侧显示这些设备已有任务的最新读数和报警。
/// 高亮行不控制任务；创建任务和循环读取交给 DeviceRuntimeManager/DeviceSamplingViewModel。
/// </summary>
public partial class MonitoringViewModel : PageViewModel
{
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    private readonly DispatcherTimer _dataAgeTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DispatcherOperation? _readingRefreshOperation;
    private readonly HashSet<DeviceSamplingViewModel> _observedSessions = new();
    // 键是原任务里的读数行，值是监控表格显示的包装行；换个显示方式，不另建读取循环。
    private readonly Dictionary<PointReadingViewModel, MonitoringReadingViewModel> _readingRows = new();
    private bool _isShuttingDown;
    private bool _isChangingChecks;
    private int _deviceListVersion;

    public DeviceRuntimeManager DeviceRuntime { get; }
    public ObservableCollection<MonitoringDeviceViewModel> Devices { get; } = new();
    public ObservableCollection<MonitoringReadingViewModel> Readings { get; } = new();
    public ICollectionView AlarmRecords { get; }
    public string[] AlarmCategories { get; } = { "全部", "数值报警", "采集异常" };
    public int ActiveSamplingCount => DeviceRuntime.ActiveSamplingCount;
    public int CheckedDeviceCount => Devices.Count(row => row.IsChecked);
    public string SelectionDescription => CheckedDeviceCount == 0
        ? "尚未勾选设备。请先勾选，顶部按钮只控制勾选设备。"
        : $"已勾选 {CheckedDeviceCount} 台：{string.Join("、", Devices.Where(row => row.IsChecked).Select(row => row.Device.Name))}。右侧汇总这些设备的最新读数。";

    // 保存当前高亮的行；批量启停和右侧读数仍看 IsChecked，不看行高亮。
    [ObservableProperty]
    private MonitoringDeviceViewModel? _selectedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSamplingCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAllDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearDeviceChecksCommand))]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSamplingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSamplingCommand))]
    private bool _isStoppingSampling;

    [ObservableProperty]
    private string _pageMessage = "进入页面会自动加载配置，但不会自动勾选或启动设备。";

    [ObservableProperty]
    private string _selectedAlarmCategory = "全部";

    [ObservableProperty]
    private bool _onlyCheckedDevices;

    /// <summary>
    /// MainViewModel 创建这里并传同一份 DeviceRuntimeManager。登记任务变化通知，准备报警筛选和计时器。
    /// 这里只准备页面；设备列表由 Loaded 后的刷新命令加载，采集必须手动开始。
    /// </summary>
    public MonitoringViewModel(UserAccount currentUser, DeviceStore deviceStore,
        DeviceRuntimeManager deviceRuntimeManager) : base("实时监控")
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        DeviceRuntime = deviceRuntimeManager ?? throw new ArgumentNullException(nameof(deviceRuntimeManager));
       
        if (!_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("当前账户不能查看实时监控。");

        ((INotifyCollectionChanged)DeviceRuntime.Sessions).CollectionChanged += Sessions_CollectionChanged;
       
        DeviceRuntime.PropertyChanged += DeviceRuntime_PropertyChanged;
        DeviceRuntime.DeviceDeleted += DeviceRuntime_DeviceDeleted;

        // 默认显示所有设备的报警，不因为某台设备没有勾选就把它的异常藏起来。
        // 单独创建本页的筛选视图，避免另一处查看同一集合时互相覆盖筛选条件。
        AlarmRecords = new ListCollectionView((IList)DeviceRuntime.Alarms.Records);
        AlarmRecords.Filter = MatchesAlarmFilter;
        AlarmRecords.SortDescriptions.Add(new SortDescription(nameof(AlarmRecord.StartedAtLocal), ListSortDirection.Descending));

        // 每秒只更新“几秒前”的文字，不读取设备，也不写数据库。
        _dataAgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dataAgeTimer.Tick += DataAgeTimer_Tick;
        _dataAgeTimer.Start();
    }

    /// <summary>
    /// 页面 Loaded 和刷新按钮调用。查询所有设备（含停用），按 Id 保留原勾选。
    /// 新增设备默认不勾选；刷新配置不会创建任务，更不会自动开始采集。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private async Task RefreshDevicesAsync()
    {
        if (!CanRefreshDevices()) return;

        //记住现在正在刷新，相关按钮重新判断能否操作
        IsRefreshing = true;
        // 记住当前版本
        int listVersion = _deviceListVersion;
        PageMessage = "正在加载设备配置…";

        try
        {
            // 查询
            var latest = await Task.Run(() => _deviceStore.GetDevices(_currentUser));
            // 查询等待时可能刚删除了设备，旧查询不能把已删除的行重新放回表格。
            if (_isShuttingDown || DeviceRuntime.IsShuttingDown || listVersion != _deviceListVersion) return;

            // 记住刷新前的勾选和高亮行，刷新后尽量恢复；新设备默认不勾选。
            long? selectedId = SelectedDevice?.Device.Id;

            HashSet<long> checkedIds = Devices.Where(row => row.IsChecked).Select(row => row.Device.Id).ToHashSet();
            
            SelectedDevice = null;
            DetachDeviceRows();
            ClearReadingRows();
            Devices.Clear();
            
            foreach (DeviceInfo device in latest)
            {
                DeviceSamplingViewModel? session = DeviceRuntime.Sessions.FirstOrDefault(item => item.DeviceId == device.Id);
                var row = new MonitoringDeviceViewModel(device, session) { IsChecked = checkedIds.Contains(device.Id) };
                row.PropertyChanged += DeviceRow_PropertyChanged;
                Devices.Add(row);
            }
            SelectedDevice = Devices.FirstOrDefault(row => row.Device.Id == selectedId) ?? Devices.FirstOrDefault();
            RefreshCheckedDevices();
            PageMessage = Devices.Count == 0
                ? "尚无设备配置。管理员可先到设备管理新增设备。"
                : $"已加载 {Devices.Count} 台设备。勾选后可批量启停；停用设备可查看，但不会被启动。";
        }
        catch (Exception ex)
        {
            if (!_isShuttingDown && !DeviceRuntime.IsShuttingDown)
                PageMessage = $"加载设备失败：{ex.Message}。保留原来的列表。";
        }
        finally { IsRefreshing = false; }
    }


    private bool CanRefreshDevices() => !_isShuttingDown && !DeviceRuntime.IsShuttingDown && !IsRefreshing;

    
    /// <summary>
    /// 全部勾选按钮的命令
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeChecks))]
    private void CheckAllDevices() => ChangeAllChecks(true);


    /// <summary>
    /// 清除勾选按钮的命令
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeChecks))]
    private void ClearDeviceChecks() => ChangeAllChecks(false);


    private bool CanChangeChecks() => !_isShuttingDown && !DeviceRuntime.IsShuttingDown && !IsRefreshing && Devices.Count > 0;
    
   /// <summary>
   /// 批量勾选或清除勾选
   /// </summary>
   /// <param name="isChecked"></param>
    private void ChangeAllChecks(bool isChecked)
    {
        if (!CanChangeChecks()) return;
        // 一次改多行时，最后统一汇总，避免每勾一行都重建一遍读数表。
        _isChangingChecks = true;
        try
        {
            foreach (MonitoringDeviceViewModel row in Devices) row.IsChecked = isChecked;
        }
        finally { _isChangingChecks = false; }
        RefreshCheckedDevices();
    }


    /// <summary>
    /// 刷新列表时为每行订阅了这里。勾选变化就更新说明、汇总读数和按钮；任务状态变化只重新检查按钮。
    /// </summary>
    private void DeviceRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isShuttingDown) return;
        if (e.PropertyName == nameof(MonitoringDeviceViewModel.IsChecked) && !_isChangingChecks)
            RefreshCheckedDevices();
        else if (e.PropertyName is nameof(MonitoringDeviceViewModel.Sampling) or nameof(MonitoringDeviceViewModel.SamplingStateText))
            NotifySamplingCommands();
    }

    /// <summary>
    /// 某行勾选变化 或 批量勾选结束后 调用。更新选中说明，再整理已存在任务的读数和报警筛选。
    /// 不在这里创建任务，也不在这里连接设备。
    /// </summary>
    private void RefreshCheckedDevices()
    {
        OnPropertyChanged(nameof(CheckedDeviceCount));
        OnPropertyChanged(nameof(SelectionDescription));
        RefreshReadingRows();
        AlarmRecords.Refresh();
        NotifySamplingCommands();
        CheckAllDevicesCommand.NotifyCanExecuteChanged();
        ClearDeviceChecksCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// “开始采集”命令到这里。记住点击时的勾选列表，只启动已启用且未运行的设备。
    /// 同一个 Id 仍交给管理器复用原任务，重复点击不会多建一个读取循环。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartSampling))]
    private void StartSampling()
    {
        // 拿到勾选的设备存入数组
        MonitoringDeviceViewModel[] checkedRows = Devices.Where(row => row.IsChecked).ToArray();

        // 判断
        if (checkedRows.Length == 0) { PageMessage = "请先在设备表中勾选要启动的设备。"; return; }
        if (!CanStartSampling()) return;

        // 从勾选设备中，筛出真正需要启动的设备
        MonitoringDeviceViewModel[] targets = checkedRows.Where(row => row.Device.IsEnabled
            && row.Sampling?.IsRunning != true && !DeviceRuntime.IsDeletingDevice(row.Device.Id)).ToArray();

        // 逐台调用管理器启动
        int started = 0;
        var failures = new List<string>();
        
        foreach (MonitoringDeviceViewModel row in targets)
        {
            try { DeviceRuntime.Start(row.Device.Id); started++; }
            catch (Exception ex) { failures.Add($"{row.Device.Name}：{ex.Message}"); }
        }
       
        PageMessage = $"已向 {started} 台设备发出启动请求；跳过 {checkedRows.Length - targets.Length} 台已运行、停用或正在删除的设备。"
            + (failures.Count == 0 ? "实际结果请看读数和报警。" : $" 启动失败：{string.Join("；", failures)}");
        NotifySamplingCommands();
    }

    private bool CanStartSampling() => CanControlSampling() && !IsRefreshing && !IsStoppingSampling
        && Devices.Any(row => row.IsChecked && row.Device.IsEnabled && row.Sampling?.IsRunning != true
            && !DeviceRuntime.IsDeletingDevice(row.Device.Id));

    /// <summary>
    /// “停止采集”命令到这里。先保存点击时正在运行的勾选设备，不会误停后来换选的设备。
    /// 多台同时请求停止，各自等待读取取消、正在保存的一条记录结束。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopSampling))]
    private async Task StopSamplingAsync()
    {
        if (!Devices.Any(row => row.IsChecked)) { PageMessage = "请先勾选要停止的设备。"; return; }
        if (!CanStopSampling()) return;

        // 保存勾选且正在运行的设备
        MonitoringDeviceViewModel[] targets = Devices.Where(row => row.IsChecked && row.Sampling?.IsRunning == true).ToArray();
        IsStoppingSampling = true;
        try
        {
            var failures = new List<string>();
            await Task.WhenAll(targets.Select(async row =>
            {
                try { await DeviceRuntime.StopAsync(row.Device.Id); }
                catch (Exception ex) { failures.Add($"{row.Device.Name}：{ex.Message}"); }
            }));
            if (!_isShuttingDown)
                PageMessage = $"已停止 {targets.Length - failures.Count} 台勾选设备；其他设备不受影响，读数保留最后结果。"
                    + (failures.Count == 0 ? string.Empty : $" 停止失败：{string.Join("；", failures)}");
        }
        finally { IsStoppingSampling = false; }
    }

    // 刷新配置期间仍可停止已有任务，不必等查询完成。
    private bool CanStopSampling() => CanControlSampling() && !IsStoppingSampling
        && Devices.Any(row => row.IsChecked && row.Sampling?.IsRunning == true);

    private bool CanControlSampling() => !_isShuttingDown && !DeviceRuntime.IsShuttingDown && _currentUser.IsEnabled
        && _currentUser.Role is UserRole.Administrator or UserRole.Operator;

    /// <summary>
    /// 构造时订阅了管理器 Sessions。开始采集创建新任务后，给同设备 Id 的界面行接上这个原任务。
    /// 随后汇总其读数；不是把 DeviceInfo 转成采集对象。
    /// </summary>
    private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isShuttingDown) return;
        foreach (MonitoringDeviceViewModel row in Devices)
            row.AttachSampling(DeviceRuntime.Sessions.FirstOrDefault(item => item.DeviceId == row.Device.Id));
        RefreshReadingRows();
        NotifySamplingCommands();
    }

    /// <summary>
    /// 勾选变化或任务读数行变化后调用。从设备行的 Sampling 找到原 Readings，包装为右侧表格行。
    /// 字典按原读数行复用包装对象；不再展示的行解除订阅，避免收到旧更新。
    /// </summary>
    private void RefreshReadingRows()
    {
        MonitoringDeviceViewModel[] checkedRows = Devices.Where(row => row.IsChecked && row.Sampling is not null).ToArray();
        HashSet<DeviceSamplingViewModel> desiredSessions = checkedRows.Select(row => row.Sampling!).ToHashSet();
        foreach (DeviceSamplingViewModel session in _observedSessions.Where(item => !desiredSessions.Contains(item)).ToArray())
        {
            session.Readings.CollectionChanged -= SessionReadings_CollectionChanged;
            _observedSessions.Remove(session);
        }
        foreach (DeviceSamplingViewModel session in desiredSessions)
            if (_observedSessions.Add(session)) session.Readings.CollectionChanged += SessionReadings_CollectionChanged;

        var desiredRows = new List<MonitoringReadingViewModel>();
        var desiredReadings = new HashSet<PointReadingViewModel>();
        foreach (MonitoringDeviceViewModel deviceRow in checkedRows)
        {
            foreach (PointReadingViewModel reading in deviceRow.Sampling!.Readings)
            {
                desiredReadings.Add(reading);
                if (!_readingRows.TryGetValue(reading, out MonitoringReadingViewModel? row))
                {
                    row = new MonitoringReadingViewModel(deviceRow.Device, reading);
                    _readingRows.Add(reading, row);
                }
                desiredRows.Add(row);
            }
        }
        foreach (PointReadingViewModel reading in _readingRows.Keys.Where(item => !desiredReadings.Contains(item)).ToArray())
        {
            _readingRows[reading].Dispose();
            _readingRows.Remove(reading);
        }
        Readings.Clear();
        foreach (MonitoringReadingViewModel row in desiredRows) Readings.Add(row);
    }

    private void SessionReadings_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isShuttingDown || _readingRefreshOperation is not null)
            return;

        // 一次重载点位会先 Clear 再逐个 Add。等这一小批改完再汇总一次，别每加一行就重建全部读数表。
        _readingRefreshOperation = _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _readingRefreshOperation = null;
            // 回调排队期间可能已经退出登录，不能再给旧页面回填读数。
            if (!_isShuttingDown)
                RefreshReadingRows();
        }));
    }

    /// <summary>
    /// 构造时订阅了管理器状态。运行数量变化就更新计数；开始退出/删除时，重新检查有关命令。
    /// </summary>
    private void DeviceRuntime_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceRuntimeManager.ActiveSamplingCount)) OnPropertyChanged(nameof(ActiveSamplingCount));
        if (e.PropertyName is nameof(DeviceRuntimeManager.ActiveSamplingCount) or nameof(DeviceRuntimeManager.IsShuttingDown)
            or nameof(DeviceRuntimeManager.IsDeletingDevices)) NotifySamplingCommands();
        if (e.PropertyName == nameof(DeviceRuntimeManager.IsShuttingDown))
        {
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            CheckAllDevicesCommand.NotifyCanExecuteChanged();
            ClearDeviceChecksCommand.NotifyCanExecuteChanged();
        }
    }

    // 管理页删完设备后，移除监控页的同一行和读数；其他行的勾选、采集不受影响。
    private void DeviceRuntime_DeviceDeleted(long deviceId)
    {
        if (_isShuttingDown) return;
        _deviceListVersion++;
        MonitoringDeviceViewModel? row = Devices.FirstOrDefault(item => item.Device.Id == deviceId);
        if (row is not null)
        {
            row.PropertyChanged -= DeviceRow_PropertyChanged;
            row.DetachSampling();
            Devices.Remove(row);
            if (ReferenceEquals(SelectedDevice, row)) SelectedDevice = null;
        }
        RefreshCheckedDevices();
        PageMessage = "已移除删除的设备；其他设备保持原来的采集状态，过去的记录可到历史记录查询。";
    }

    /// <summary>
    /// 勾选或任务状态变化后调用，要求开始/停止命令重新检查 CanExecute；不会直接开始或停止。
    /// </summary>
    private void NotifySamplingCommands()
    {
        StartSamplingCommand.NotifyCanExecuteChanged();
        StopSamplingCommand.NotifyCanExecuteChanged();
    }

    // 右下报警筛选属性改变后，生成的 setter 自动调用这里；只筛显示，不删记录。
    partial void OnSelectedAlarmCategoryChanged(string value) => AlarmRecords.Refresh();
    partial void OnOnlyCheckedDevicesChanged(bool value) => AlarmRecords.Refresh();

    /// <summary>
    /// 报警集合视图自动调用。按类别和“仅看勾选设备”决定这一条是否显示；不删除报警记录。
    /// </summary>
    private bool MatchesAlarmFilter(object item)
    {
        if (item is not AlarmRecord alarm) return false;
        if (SelectedAlarmCategory == "数值报警" && alarm.Category != AlarmCategory.Value) return false;
        if (SelectedAlarmCategory == "采集异常" && alarm.Category != AlarmCategory.Acquisition) return false;
        return !OnlyCheckedDevices || Devices.Any(row => row.IsChecked && row.Device.Id == alarm.DeviceId);
    }

    /// <summary>
    /// 每秒由 DispatcherTimer 调用。只更新“几秒前”的显示，不采集、不查询、不保存。
    /// </summary>
    private void DataAgeTimer_Tick(object? sender, EventArgs e)
    {
        if (_isShuttingDown || DeviceRuntime.IsShuttingDown) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MonitoringDeviceViewModel row in Devices) row.UpdateDataAge(now);
    }

    /// <summary>
    /// 刷新要换掉旧设备行或关窗时调用，解除旧行状态订阅；不停止原采集任务。
    /// </summary>
    private void DetachDeviceRows()
    {
        foreach (MonitoringDeviceViewModel row in Devices)
        {
            row.PropertyChanged -= DeviceRow_PropertyChanged;
            row.DetachSampling();
        }
    }

    /// <summary>
    /// 刷新设备或关窗时调用，取消排队的汇总更新，解除读数行订阅并清空显示集合；不删除数据库历史。
    /// </summary>
    private void ClearReadingRows()
    {
        // 刷新设备或关窗要丢弃旧汇总时，排队中的旧回调也一起取消。
        _readingRefreshOperation?.Abort();
        _readingRefreshOperation = null;
        foreach (DeviceSamplingViewModel session in _observedSessions) session.Readings.CollectionChanged -= SessionReadings_CollectionChanged;
        _observedSessions.Clear();
        foreach (MonitoringReadingViewModel row in _readingRows.Values) row.Dispose();
        _readingRows.Clear();
        Readings.Clear();
    }

    /// <summary>
    /// MainViewModel 关窗时调用：等查询和批量停止完成，再解除本页全部订阅。
    /// 切换页面不走这里，勾选会保留，已经启动的设备继续采集。
    /// </summary>
    public async Task StopOperationsAsync()
    {
        _isShuttingDown = true;
        _dataAgeTimer.Stop();
        _dataAgeTimer.Tick -= DataAgeTimer_Tick;
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        CheckAllDevicesCommand.NotifyCanExecuteChanged();
        ClearDeviceChecksCommand.NotifyCanExecuteChanged();
        NotifySamplingCommands();
        await Task.WhenAll(RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask,
            StopSamplingCommand.ExecutionTask ?? Task.CompletedTask);
        ((INotifyCollectionChanged)DeviceRuntime.Sessions).CollectionChanged -= Sessions_CollectionChanged;
        DeviceRuntime.PropertyChanged -= DeviceRuntime_PropertyChanged;
        DeviceRuntime.DeviceDeleted -= DeviceRuntime_DeviceDeleted;
        DetachDeviceRows();
        ClearReadingRows();
        // 集合视图的筛选委托也引用本页。关窗后解除，不让报警集合再间接留住页面。
        AlarmRecords.Filter = null;
    }
}

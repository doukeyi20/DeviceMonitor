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
/// 实时监控页面的数据：勾选设备后批量启停，右侧显示最新读数和报警。
/// 本类管页面操作，DeviceRuntimeManager 管任务，DeviceSamplingViewModel 真正循环采集。
/// </summary>
public partial class MonitoringViewModel : PageViewModel
{
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    private readonly DispatcherTimer _dataAgeTimer; // 每秒更新“多久没读到新数据”的提示。
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DispatcherOperation? _readingRefreshOperation; // 已排队的汇总刷新，避免连续增删行时重复刷新。
    private readonly HashSet<DeviceSamplingViewModel> _observedSessions = new(); // 当前已订阅读数行变化的任务。
    // 原读数行 → 监控显示行；显示行补上设备名称，仍读取原对象的数值。
    private readonly Dictionary<PointReadingViewModel, MonitoringReadingViewModel> _readingRows = new();
    private bool _isShuttingDown; // 退出后不再给旧页面回填数据。
    private bool _isChangingChecks; // 批量改勾选时，等全部改完再统一汇总。
    private int _deviceListVersion; // 删除设备后递增，让删除前启动的旧查询作废。

    public DeviceRuntimeManager DeviceRuntime { get; } // 主窗口传来的共用管理器。
    // 左侧设备表、右侧读数表和报警表分别绑定这三个集合/视图。
    public ObservableCollection<MonitoringDeviceViewModel> Devices { get; } = new();
    public ObservableCollection<MonitoringReadingViewModel> Readings { get; } = new();
    public ICollectionView AlarmRecords { get; }
    public string[] AlarmCategories { get; } = { "全部", "数值报警", "采集异常" };
    public int ActiveSamplingCount => DeviceRuntime.ActiveSamplingCount;
    public int CheckedDeviceCount => Devices.Count(row => row.IsChecked);
    public string SelectionDescription => CheckedDeviceCount == 0
        ? "尚未勾选设备。请先勾选，顶部按钮只控制勾选设备。"
        : $"已勾选 {CheckedDeviceCount} 台：{string.Join("、", Devices.Where(row => row.IsChecked).Select(row => row.Device.Name))}。右侧汇总这些设备的最新读数。";

    // 鼠标高亮的行；批量启停仍看 IsChecked，不看这里。
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
    /// 接上共用管理器的通知，准备报警筛选和计时器；采集仍需手动开始。
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

        // 为本页单独做报警筛选，不影响其他页面；默认显示所有设备的报警。
        AlarmRecords = new ListCollectionView((IList)DeviceRuntime.Alarms.Records);
        AlarmRecords.Filter = MatchesAlarmFilter;
        AlarmRecords.SortDescriptions.Add(new SortDescription(nameof(AlarmRecord.StartedAtLocal), ListSortDirection.Descending));

        // 每秒只更新“几秒前”的提示，不读取设备。
        _dataAgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dataAgeTimer.Tick += DataAgeTimer_Tick;
        _dataAgeTimer.Start();
    }

    /// <summary>
    /// 由 RefreshDevicesCommand 调用；MonitoringView_Loaded 也会执行此命令。
    /// 加载全部设备配置，按编号保留原勾选；新设备不勾选，也不自动开始采集。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private async Task RefreshDevicesAsync()
    {
        if (!CanRefreshDevices()) return;

        // 标记正在刷新，并记下版本，等查询回来时判断结果是否过期。
        IsRefreshing = true;
        int listVersion = _deviceListVersion;
        PageMessage = "正在加载设备配置…";

        try
        {
            // 后台查询，避免界面卡住。
            var latest = await Task.Run(() => _deviceStore.GetDevices(_currentUser));
            // 退出了或期间删过设备，就丢弃这次旧结果。
            if (_isShuttingDown || DeviceRuntime.IsShuttingDown || listVersion != _deviceListVersion) return;

            // 记住原勾选和高亮行，刷新后按设备编号恢复。
            long? selectedId = SelectedDevice?.Device.Id;

            HashSet<long> checkedIds = Devices.Where(row => row.IsChecked).Select(row => row.Device.Id).ToHashSet();
            
            // 断开旧行通知，清掉旧显示；不会停止正在采集的任务。
            SelectedDevice = null;
            DetachDeviceRows();
            ClearReadingRows();
            Devices.Clear();
            
            // 为每台设备创建显示行，有旧采集任务就接上它。
            foreach (DeviceInfo device in latest)
            {
                DeviceSamplingViewModel? session = DeviceRuntime.Sessions.FirstOrDefault(item => item.DeviceId == device.Id);
                var row = new MonitoringDeviceViewModel(device, session) { IsChecked = checkedIds.Contains(device.Id) };
                row.PropertyChanged += DeviceRow_PropertyChanged;
                Devices.Add(row);
            }

            // 恢复高亮行，再统一更新读数汇总、说明和按钮。
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


    // 未退出且没在刷新时，刷新按钮才能点。
    // 由 RefreshDevicesCommand 判断按钮能不能点时调用。
    private bool CanRefreshDevices() => !_isShuttingDown && !DeviceRuntime.IsShuttingDown && !IsRefreshing;

    
    /// <summary>
    /// 由 CheckAllDevicesCommand 调用。
    /// 全部勾选按钮的命令
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeChecks))]
    private void CheckAllDevices() => ChangeAllChecks(true);


    /// <summary>
    /// 由 ClearDeviceChecksCommand 调用。
    /// 清除勾选按钮的命令
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeChecks))]
    private void ClearDeviceChecks() => ChangeAllChecks(false);


    // 有设备、没在刷新或退出时，才允许全选和清空勾选。
    // 由 CheckAllDevicesCommand、ClearDeviceChecksCommand 判断按钮能不能点时调用。
    private bool CanChangeChecks() => !_isShuttingDown && !DeviceRuntime.IsShuttingDown && !IsRefreshing && Devices.Count > 0;
    
   /// <summary>
   /// 由 CheckAllDevices 和 ClearDeviceChecks 调用。
   /// 批量勾选或清除勾选
   /// </summary>
    private void ChangeAllChecks(bool isChecked)
    {
        if (!CanChangeChecks()) return;
        // 批量改完再汇总一次，避免每改一行都重建读数表。
        _isChangingChecks = true;
        try
        {
            foreach (MonitoringDeviceViewModel row in Devices) row.IsChecked = isChecked;
        }
        finally { _isChangingChecks = false; }
        RefreshCheckedDevices();
    }


    /// <summary>
    /// 由设备显示行 MonitoringDeviceViewModel 的 PropertyChanged 事件调用。
    /// 设备行变化时，勾选变化刷新汇总，任务状态变化刷新按钮。
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
    /// 由批量勾选、设备行变化、刷新设备和删除设备后的处理方法调用。
    /// 勾选改完后，更新勾选说明、读数、报警筛选和按钮；不开始采集。
    /// </summary>
    private void RefreshCheckedDevices()
    {
        // 先更新数据和显示，再让各按钮重新判断能否点击。
        OnPropertyChanged(nameof(CheckedDeviceCount));
        OnPropertyChanged(nameof(SelectionDescription));
        RefreshReadingRows();
        AlarmRecords.Refresh();
        NotifySamplingCommands();
        CheckAllDevicesCommand.NotifyCanExecuteChanged();
        ClearDeviceChecksCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 由 StartSamplingCommand 调用。
    /// 把勾选且允许启动的设备交给 DeviceRuntimeManager，再由各自的任务开始循环采集。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartSampling))]
    private void StartSampling()
    {
        // ToArray 固定本次勾选名单，后面改勾选也不影响本次操作。
        MonitoringDeviceViewModel[] checkedRows = Devices.Where(row => row.IsChecked).ToArray();

        // 没勾选或当前不允许启动，就直接返回。
        if (checkedRows.Length == 0) { PageMessage = "请先在设备表中勾选要启动的设备。"; return; }
        if (!CanStartSampling()) return;

        // 只启动已启用、未运行且不在删除的设备。
        // Sampling 为 null 表示还没创建任务，也可以启动。
        MonitoringDeviceViewModel[] targets = checkedRows.Where(row => row.Device.IsEnabled
            && row.Sampling?.IsRunning != true && !DeviceRuntime.IsDeletingDevice(row.Device.Id)).ToArray();

        // 逐台发启动请求，分别统计成功发出的请求和当场失败的原因。
        int started = 0;
        var failures = new List<string>();
        
        foreach (MonitoringDeviceViewModel row in targets)
        {
            try 
            { 
                // 传设备编号，让管理器找到或新建对应任务并启动，不是转换设备对象。
                DeviceRuntime.Start(row.Device.Id);
                // 请求发出不代表设备已经连接成功。
                started++; 
            }
            catch (Exception ex) 
            { 
                // 一台失败先记下，后面的设备仍继续尝试。
                failures.Add($"{row.Device.Name}：{ex.Message}"); 
            }
        }
       
        PageMessage = $"已向 {started} 台设备发出启动请求；跳过 {checkedRows.Length - targets.Length} 台已运行、停用或正在删除的设备。"
            + (failures.Count == 0 ? "实际结果请看读数和报警。" : $" 启动失败：{string.Join("；", failures)}");
        // 重新判断按钮能否点击，不会再次执行启停。
        NotifySamplingCommands();
    }

    // 至少有一台勾选设备能启动，且当前允许操作，开始按钮才可点。
    // 由 StartSamplingCommand 判断按钮能不能点时调用。
    private bool CanStartSampling() => CanControlSampling() && !IsRefreshing && !IsStoppingSampling
        && Devices.Any(row => row.IsChecked && row.Device.IsEnabled && row.Sampling?.IsRunning != true
            && !DeviceRuntime.IsDeletingDevice(row.Device.Id));

    /// <summary>
    /// 由 StopSamplingCommand 调用。
    /// 同时请求停止勾选设备，再等它们各自完成读取和保存收尾。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopSampling))]
    private async Task StopSamplingAsync()
    {
        if (!Devices.Any(row => row.IsChecked)) { PageMessage = "请先勾选要停止的设备。"; return; }
        if (!CanStopSampling()) return;

        // 固定本次要停止的名单，只取勾选且正在运行的设备。
        MonitoringDeviceViewModel[] targets = Devices.Where(row => row.IsChecked && row.Sampling?.IsRunning == true).ToArray();
        // 收尾期间禁止重复发起停止。
        IsStoppingSampling = true;
        try
        {
            var failures = new List<string>();
            // 每台都发停止请求，再一起等它们结束。
            await Task.WhenAll(targets.Select(async row =>
            {
                // 按编号找到原任务，等待它的采集循环结束。
                try { await DeviceRuntime.StopAsync(row.Device.Id); }
                catch (Exception ex) { failures.Add($"{row.Device.Name}：{ex.Message}"); }
            }));
            // 退出时不再更新旧页面的提示。
            if (!_isShuttingDown)
                PageMessage = $"已停止 {targets.Length - failures.Count} 台勾选设备；其他设备不受影响，读数保留最后结果。"
                    + (failures.Count == 0 ? string.Empty : $" 停止失败：{string.Join("；", failures)}");
        }
        // 无论成功还是失败，都解除“正在停止”的标记。
        finally { IsStoppingSampling = false; }
    }

    // 刷新配置期间仍可停止已有任务，不必等查询完成。
    // 由 StopSamplingCommand 判断按钮能不能点时调用。
    private bool CanStopSampling() => CanControlSampling() && !IsStoppingSampling
        && Devices.Any(row => row.IsChecked && row.Sampling?.IsRunning == true);

    // 未退出且用户是已启用的管理员或操作员，才允许启停。
    private bool CanControlSampling() => !_isShuttingDown && !DeviceRuntime.IsShuttingDown && _currentUser.IsEnabled
        && _currentUser.Role is UserRole.Administrator or UserRole.Operator;

    /// <summary>
    /// 由 DeviceRuntime.Sessions 的 CollectionChanged 事件调用。
    /// 管理器的任务集合变化后，按设备编号给显示行接上任务，再刷新读数。
    /// </summary>
    private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isShuttingDown) return;
        // 用相同的设备 Id 对上号，AttachSampling 保存原任务的引用。
        foreach (MonitoringDeviceViewModel row in Devices)
            row.AttachSampling(DeviceRuntime.Sessions.FirstOrDefault(item => item.DeviceId == row.Device.Id));
        // 更新右侧显示，不在这里读取设备。
        RefreshReadingRows();
        NotifySamplingCommands();
    }

    /// <summary>
    /// 由 RefreshCheckedDevices、Sessions_CollectionChanged 和排队的读数刷新调用。
    /// 从勾选设备的采集任务中取出读数行，补上设备名称后放进右侧表格。
    /// 能复用的显示行继续用，不再显示的行解除订阅。
    /// </summary>
    private void RefreshReadingRows()
    {
        // 只汇总勾选且已有任务的设备。
        MonitoringDeviceViewModel[] checkedRows = Devices.Where(row => row.IsChecked && row.Sampling is not null).ToArray();
        // desiredSessions 存这次需要显示的任务，方便和旧订阅比较。
        HashSet<DeviceSamplingViewModel> desiredSessions = checkedRows.Select(row => row.Sampling!).ToHashSet();

        // 不再显示的任务断开通知，新显示的任务接上通知；都不改变采集启停。
        foreach (DeviceSamplingViewModel session in _observedSessions.Where(item => !desiredSessions.Contains(item)).ToArray())
        {
            session.Readings.CollectionChanged -= SessionReadings_CollectionChanged;
            _observedSessions.Remove(session);
        }
        // Add 返回 true 才是新任务，避免重复订阅。
        foreach (DeviceSamplingViewModel session in desiredSessions)
            if (_observedSessions.Add(session)) session.Readings.CollectionChanged += SessionReadings_CollectionChanged;

        // desiredRows 是右表的新名单，desiredReadings 用来判断哪些旧行该清掉。
        var desiredRows = new List<MonitoringReadingViewModel>();
        var desiredReadings = new HashSet<PointReadingViewModel>();
        foreach (MonitoringDeviceViewModel deviceRow in checkedRows)
        {
            foreach (PointReadingViewModel reading in deviceRow.Sampling!.Readings)
            {
                // reading 是任务里的原读数行。
                desiredReadings.Add(reading);
                if (!_readingRows.TryGetValue(reading, out MonitoringReadingViewModel? row))
                {
                    // 没有对应显示行才创建，补上设备名称，数值仍从原行取。
                    row = new MonitoringReadingViewModel(deviceRow.Device, reading);
                    _readingRows.Add(reading, row);
                }
                desiredRows.Add(row);
            }
        }

        // 清掉不再显示的行及订阅，再替换右侧显示名单。
        foreach (PointReadingViewModel reading in _readingRows.Keys.Where(item => !desiredReadings.Contains(item)).ToArray())
        {
            _readingRows[reading].Dispose();
            _readingRows.Remove(reading);
        }
        // 这里只换监控表的行，不清空采集任务中的读数或数据库历史。
        Readings.Clear();
        foreach (MonitoringReadingViewModel row in desiredRows) Readings.Add(row);
    }

    // 任务增删读数行时，稍后统一刷新汇总，避免连续 Add 时反复重建表格。
    // 由设备采集任务 Readings 的 CollectionChanged 事件调用。
    private void SessionReadings_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 已退出或已经排过刷新，就不用再排一次。
        if (_isShuttingDown || _readingRefreshOperation is not null)
            return;

        // 等当前这批 Clear/Add 完成，再在界面线程汇总一次。
        _readingRefreshOperation = _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            // 清掉排队标记，下一次变化还能再次刷新。
            _readingRefreshOperation = null;
            // 等待期间可能退出了，退出后不再回填数据。
            if (!_isShuttingDown)
                RefreshReadingRows();
        }));
    }

    /// <summary>
    /// 由 DeviceRuntime 的 PropertyChanged 事件调用。
    /// 管理器状态变了，更新运行台数和按钮是否可点。
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

    // 收到删除通知后，移除这台设备的显示行和读数，不影响其他设备。
    // 由 DeviceRuntime 的 DeviceDeleted 事件调用。
    private void DeviceRuntime_DeviceDeleted(long deviceId)
    {
        if (_isShuttingDown) return;
        _deviceListVersion++; // 让删除前发出的旧查询作废，避免把已删除设备放回来。
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
    /// 由勾选变化、开始采集、任务变化和退出时的处理方法调用。
    /// 让开始、停止按钮重新检查能否点击；不执行启停操作。
    /// </summary>
    private void NotifySamplingCommands()
    {
        StartSamplingCommand.NotifyCanExecuteChanged();
        StopSamplingCommand.NotifyCanExecuteChanged();
    }

    // 修改对应属性后，工具包生成的 setter 自动调用这里，刷新报警显示。
    partial void OnSelectedAlarmCategoryChanged(string value) => AlarmRecords.Refresh();
    // “仅看勾选”改变后，也自动刷新报警显示。
    partial void OnOnlyCheckedDevicesChanged(bool value) => AlarmRecords.Refresh();

    /// <summary>
    /// 由 AlarmRecords 筛选表格中的报警时自动调用。
    /// 按类别和“仅看勾选设备”判断一条报警是否显示，不删除记录。
    /// </summary>
    private bool MatchesAlarmFilter(object item)
    {
        if (item is not AlarmRecord alarm) return false;
        if (SelectedAlarmCategory == "数值报警" && alarm.Category != AlarmCategory.Value) return false;
        if (SelectedAlarmCategory == "采集异常" && alarm.Category != AlarmCategory.Acquisition) return false;
        return !OnlyCheckedDevices || Devices.Any(row => row.IsChecked && row.Device.Id == alarm.DeviceId);
    }

    /// <summary>
    /// 由 _dataAgeTimer 定时器每秒调用。
    /// 每秒更新设备行的“几秒前”提示，不读取设备。
    /// </summary>
    private void DataAgeTimer_Tick(object? sender, EventArgs e)
    {
        if (_isShuttingDown || DeviceRuntime.IsShuttingDown) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MonitoringDeviceViewModel row in Devices) row.UpdateDataAge(now);
    }

    /// <summary>
    /// 解除旧设备行的通知订阅，不停止采集任务。
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
    /// 取消待执行的刷新，解除读数通知，清掉显示行；不删除数据库历史。
    /// </summary>
    private void ClearReadingRows()
    {
        // 先取消旧刷新，再断开订阅并清空显示。
        _readingRefreshOperation?.Abort();
        _readingRefreshOperation = null;
        foreach (DeviceSamplingViewModel session in _observedSessions) session.Readings.CollectionChanged -= SessionReadings_CollectionChanged;
        _observedSessions.Clear();
        foreach (MonitoringReadingViewModel row in _readingRows.Values) row.Dispose();
        _readingRows.Clear();
        Readings.Clear();
    }

    /// <summary>
    /// 由 MainViewModel.StopBackgroundWorkAsync 在退出前调用。
    /// 退出时等本页操作结束，再解除计时器和事件订阅。
    /// 普通切换页面不调用，采集仍会继续。
    /// </summary>
    public async Task StopOperationsAsync()
    {
        // 先禁止新操作，停掉提示计时器。
        _isShuttingDown = true;
        _dataAgeTimer.Stop();
        _dataAgeTimer.Tick -= DataAgeTimer_Tick;
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        CheckAllDevicesCommand.NotifyCanExecuteChanged();
        ClearDeviceChecksCommand.NotifyCanExecuteChanged();
        NotifySamplingCommands();

        // 等已经发起的刷新和停止结束，再拆掉本页的通知。
        await Task.WhenAll(RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask,
            StopSamplingCommand.ExecutionTask ?? Task.CompletedTask);
        ((INotifyCollectionChanged)DeviceRuntime.Sessions).CollectionChanged -= Sessions_CollectionChanged;
        DeviceRuntime.PropertyChanged -= DeviceRuntime_PropertyChanged;
        DeviceRuntime.DeviceDeleted -= DeviceRuntime_DeviceDeleted;
        DetachDeviceRows();
        ClearReadingRows();
        // 筛选方法也引用本页，退出时一起断开。
        AlarmRecords.Filter = null;
    }
}

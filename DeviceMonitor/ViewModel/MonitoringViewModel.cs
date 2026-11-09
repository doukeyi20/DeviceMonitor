// 选择一台设备查看读数；开始、停止操作不再依赖勾选。
using DeviceMonitor.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

/// <summary>负责选择设备、启停按钮与曲线选择；读数来自任务，不从历史表反查。</summary>
public partial class MonitoringViewModel : PageViewModel
{
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    // 监控页开始退出收尾时打上，拒绝新的刷新和启停操作，晚返回的设备查询也不再更新列表；不再清掉。
    private bool _isClosed;

    // 采集管理器，Sessions保存每台设备的采集对象DeviceSamplingViewModel，同时可以利用采集器协调开始、删除和退出；
    public SamplingTasks SamplingTasks { get; }
    public AlarmMonitor Alarms => SamplingTasks.Alarms;

    // 左侧显示的设备卡片集合，包含设备信息和当前采集任务；不再是单纯设备信息
    /*
     DeviceCardViewModel
        → Sampling：DeviceSamplingViewModel   执行采集的对象,保存设备对象信息和对应的采集对象
            → Readings：多个PointReadingViewModel   采集点的实时显示对象
                → Value：每个点位的实际数值
     */
    public ObservableCollection<DeviceCardViewModel> Devices { get; } = new();
    public LiveTrendViewModel Trend { get; } = new();

    // 采集到的读数在 SelectedDevice.Sampling.Readings 中，曲线和读数列表都从这里取；也就是DevcieCardViewModel.Sampling.Readings 的引用。
    public IEnumerable<PointReadingViewModel> Readings =>
        SelectedDevice?.Sampling?.Readings ?? Enumerable.Empty<PointReadingViewModel>();

    public bool HasReadings => Readings.Any();
    public int ReadingColumns => Math.Clamp(Readings.Count(), 1, 4);
    public int ActiveSamplingCount => SamplingTasks.ActiveSamplingCount;
    public string DeviceTitle => SelectedDevice?.Device.Name ?? "请选择设备";
    public string DeviceDetails => SelectedDevice?.ConnectionDetails ?? "在设备配置中添加设备，再开始监测。";

    // 设备卡片的选择
    [ObservableProperty] private DeviceCardViewModel? _selectedDevice;

    // 选中的点位卡片的选择，绑定到右侧曲线显示
    [ObservableProperty] private PointReadingViewModel? _selectedReading;



    // 正在刷新设备名单的标记：查询前打上，finally 中清掉；期间不重复刷新或发起新的开始采集操作。
    [ObservableProperty] private bool _isRefreshing;

    // 本页正在等待设备停止的标记：点停止或全部停止后打上，暂时挡住刷新和启停，finally 中清掉。
    [ObservableProperty] private bool _isStopping;

    [ObservableProperty] private string _pageMessage = "";

    public MonitoringViewModel(UserAccount currentUser, DeviceStore deviceStore, SamplingTasks deviceTasks)
        : base("实时监控")
    {
        _currentUser = currentUser;
        _deviceStore = deviceStore;
        SamplingTasks = deviceTasks;
        if (!currentUser.IsEnabled)
            throw new UnauthorizedAccessException("当前账户不能查看实时监控。");

        //SamplingTasks.Sessions是设备采集对象的集合，当Sessions里的采集对象有变化时，调用SessionsChanged。
        // 采集对象集合里增加、移除了谁
        ((INotifyCollectionChanged)SamplingTasks.Sessions).CollectionChanged += SessionsChanged;
        // 采集管理器这个对象发出的属性通知执行 TasksChanged
        SamplingTasks.PropertyChanged += TasksChanged;
        // 采集管理器发出的设备删除事件通知执行 DeviceDeleted
        SamplingTasks.DeviceDeleted += DeviceDeleted;

    }

    /// <summary>
    /// 刷新设备列表
    /// </summary>
    /// <returns></returns>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshDevicesAsync()
    {
        IsRefreshing = true;
        try
        {
            // 先查询设备卡片列表，然后清除旧的列表
            var devices = await Task.Run(() => _deviceStore.GetDevices(_currentUser));
            if (_isClosed)
                return;
            long? selectedId = SelectedDevice?.Device.Id;
            DetachRows();
            Devices.Clear();
            // 为设备卡片集合组装 DeviceCardViewModel，添加进去，需要DeviceInfo和对应的采集对象DeviceSamplingViewModel
            // SamplingTasks.Sessions就是所有设备的采集对象集合，按设备编号匹配，从这里面找对应的采集对象
            foreach (DeviceInfo device in devices)
                Devices.Add(new DeviceCardViewModel(device,
                    SamplingTasks.Sessions.FirstOrDefault(task => task.DeviceId == device.Id)));

            SelectedDevice = Devices.FirstOrDefault(row => row.Device.Id == selectedId) ?? Devices.FirstOrDefault();
            PageMessage = Devices.Count == 0 ? "暂无设备，请先添加设备配置。" : "";
        }
        catch (Exception ex)
        {
            PageMessage = $"加载设备失败：{ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private bool CanRefresh() => !_isClosed && !SamplingTasks.IsShuttingDown && !IsRefreshing && !IsStopping;

    partial void OnIsRefreshingChanged(bool value) => NotifyCommands();
    partial void OnIsStoppingChanged(bool value) => NotifyCommands();

    /// <summary>
    /// 切换设备时触发，退订旧设备的通知，订阅新设备的通知，并更新右侧显示和按钮状态
    /// </summary>
    /// <param name="oldValue">旧选择设备卡片</param>
    /// <param name="newValue">新选择设备卡片</param>
    partial void OnSelectedDeviceChanged(DeviceCardViewModel? oldValue, DeviceCardViewModel? newValue)
    {
        // 退订旧设备订阅
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= SelectedDeviceChanged;
            if (oldValue.Sampling is not null)
                oldValue.Sampling.Readings.CollectionChanged -= ReadingsChanged;
        }
        // 订阅新设备
        if (newValue is not null)
        {
            newValue.PropertyChanged += SelectedDeviceChanged;
            if (newValue.Sampling is not null)
                newValue.Sampling.Readings.CollectionChanged += ReadingsChanged;
        }
        // 通知右侧文字重新取值
        OnPropertyChanged(nameof(DeviceTitle));
        OnPropertyChanged(nameof(DeviceDetails));

        // 更新读数区域和按钮
        UpdateReadings();
        NotifyCommands();
    }

    /// <summary>
    /// 被newValue.PropertyChanged订阅，当前选择的设备卡片的属性变了就触发，如果是SamplingStateText属性就执行NotifyCommands()
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void SelectedDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceCardViewModel.SamplingStateText))
            NotifyCommands();
    }

    /// <summary>
    /// 被newValue.Sampling.Readings.CollectionChanged订阅，Readings集合的内容（pointreadingviewmodel）变化会调用
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void ReadingsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateReadings();

    /// <summary>
    /// 重新显示当前设备的点位，并调整选择
    /// </summary>
    private void UpdateReadings()
    {
        // 先记住原来选中了哪个点位。没有选择时，得到 null。
        long? pointId = SelectedReading?.Point.Id;

        OnPropertyChanged(nameof(Readings));
        OnPropertyChanged(nameof(HasReadings));
        OnPropertyChanged(nameof(ReadingColumns));
        // 选中的点位恢复
        SelectedReading = Readings.FirstOrDefault(row => row.Point.Id == pointId) ?? Readings.FirstOrDefault();
    }

    /// <summary>
    /// 切换点位卡片时触发，用户点击另一张点位卡片，绑定写入 SelectedReading。选中的点位改变，切换曲线
    /// </summary>
    /// <param name="value"></param>
    partial void OnSelectedReadingChanged(PointReadingViewModel? value) => Trend.Select(value);

    /// <summary>
    /// 开始采集,这里是选择哪个设备就开始哪个采集，像采集多个得每个都点一遍采集按钮
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartSampling()
    {
        try
        {
            SamplingTasks.Start(SelectedDevice!.Device.Id);
            PageMessage = "";
        }
        catch (Exception ex)
        {
            PageMessage = $"无法开始采集：{ex.Message}";
        }
        NotifyCommands();
    }

    private bool CanStart() => CanRefresh() && SelectedDevice is not null
        && SelectedDevice.Sampling?.IsRunning != true
        && SamplingTasks.SingleReadDeviceId != SelectedDevice.Device.Id
        && !SamplingTasks.IsDeletingDevice(SelectedDevice.Device.Id);

    /// <summary>发停止信号以后，等当前设备的读写和保存真正结束。</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopSamplingAsync() => StopTasksAsync(SelectedDevice?.Sampling is { } task ? [task] : []);

    private bool CanStop() => !_isClosed && !IsStopping && SelectedDevice?.Sampling?.IsRunning == true;

    [RelayCommand(CanExecute = nameof(CanStopAll))]
    private Task StopAllSamplingAsync() =>
        StopTasksAsync(SamplingTasks.Sessions.Where(task => task.IsRunning).ToArray());

    private bool CanStopAll() => !_isClosed && !IsStopping && ActiveSamplingCount > 0;

    private async Task StopTasksAsync(IReadOnlyList<DeviceSamplingViewModel> tasks)
    {
        IsStopping = true;
        try
        {
            await Task.WhenAll(tasks.Select(task => task.StopAsync()));
            PageMessage = "采集已停止，历史记录已保留。";
        }
        catch (Exception ex)
        {
            PageMessage = $"停止采集失败：{ex.Message}";
        }
        finally
        {
            IsStopping = false;
        }
    }

    /// <summary>点一下报警，定位到对应设备与点位；报警本身不会自动切换画面。</summary>
    [RelayCommand]
    private void LocateAlarm(AlarmRecord? alarm)
    {
        if (alarm is null)
            return;
        SelectedDevice = Devices.FirstOrDefault(row => row.Device.Id == alarm.DeviceId) ?? SelectedDevice;
        SelectedReading = Readings.FirstOrDefault(row => row.Point.Id == alarm.PointId)
            ?? Readings.FirstOrDefault();
    }

    private void SessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 只有新建任务时才重接一次集合通知，普通轮询直接更新原点位对象。
        // SelectedDevice.Sampling是DeviceSamplingViewModel
        var oldTask = SelectedDevice?.Sampling;
        if (oldTask is not null)
            oldTask.Readings.CollectionChanged -= ReadingsChanged;
        foreach (var row in Devices)
            row.AttachSampling(SamplingTasks.Sessions.FirstOrDefault(task => task.DeviceId == row.Device.Id));
        if (SelectedDevice?.Sampling is { } newTask)
            newTask.Readings.CollectionChanged += ReadingsChanged;
        UpdateReadings();
        NotifyCommands();
    }

    private void TasksChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SamplingTasks.ActiveSamplingCount))
            OnPropertyChanged(nameof(ActiveSamplingCount));
        NotifyCommands();
    }

    private void DeviceDeleted(long id)
    {
        var row = Devices.FirstOrDefault(item => item.Device.Id == id);
        if (row is null)
            return;
        if (ReferenceEquals(row, SelectedDevice))
            SelectedDevice = null;
        row.DetachSampling();
        Devices.Remove(row);
        SelectedDevice ??= Devices.FirstOrDefault();
    }

    private void NotifyCommands()
    {
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        StartSamplingCommand.NotifyCanExecuteChanged();
        StopSamplingCommand.NotifyCanExecuteChanged();
        StopAllSamplingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>先退订当前选择，再断开设备卡片的通知，避免刷新后重复接收旧对象变化。</summary>
    private void DetachRows()
    {
        SelectedDevice = null;
        foreach (var row in Devices)
            row.DetachSampling();
    }

    /// <summary>退出前等待页面操作，然后解除任务与曲线通知。</summary>
    public override async Task StopOperationsAsync()
    {
        _isClosed = true;
        NotifyCommands();
        await Task.WhenAll(
            RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask,
            StopSamplingCommand.ExecutionTask ?? Task.CompletedTask,
            StopAllSamplingCommand.ExecutionTask ?? Task.CompletedTask);
        ((INotifyCollectionChanged)SamplingTasks.Sessions).CollectionChanged -= SessionsChanged;
        SamplingTasks.PropertyChanged -= TasksChanged;
        SamplingTasks.DeviceDeleted -= DeviceDeleted;
        DetachRows();
        Trend.Dispose();
    }
}

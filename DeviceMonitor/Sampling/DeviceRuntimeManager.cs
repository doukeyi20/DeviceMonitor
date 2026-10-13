using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Sampling;

/// <summary>
/// 全部设备共用的运行管理器，按 DeviceId 保存各自的采集任务。
/// 负责启停任务；删除设备时，先停任务再删配置。
/// </summary>
public sealed class DeviceRuntimeManager : ObservableObject
{
    // 沿用主窗口传来的账户和数据库操作对象。
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    private readonly HistoryStore _historyStore;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // 字典按设备编号找任务，集合供监控查看；两处存的是同一批对象。
    private readonly Dictionary<long, DeviceSamplingViewModel> _sessionsByDevice = new();
    private readonly ObservableCollection<DeviceSamplingViewModel> _sessions = new();
    // 记录正在删除的设备编号，等待删除时禁止它重新启动。
    private readonly HashSet<long> _deletingDeviceIds = new();
    private Task? _stopAllTask; // 记住关窗时的停止任务，重复关窗也只执行一次。

    // 删完配置后通知页面移除这台设备。
    public event Action<long>? DeviceDeleted;
    public bool IsDeletingDevices => _deletingDeviceIds.Count > 0;

    // 对外只允许查看集合，增删任务统一由本类处理。
    public ReadOnlyObservableCollection<DeviceSamplingViewModel> Sessions { get; }
    public int ActiveSamplingCount => _sessionsByDevice.Values.Count(session => session.IsRunning);
    // 所有设备和监控页共用的报警对象。
    public AlarmTracker Alarms { get; }

    private bool _isShuttingDown;
    public bool IsShuttingDown
    {
        get => _isShuttingDown;
        private set => SetProperty(ref _isShuttingDown, value);
    }

    /// <summary>
    /// 由 MainViewModel 构造方法创建，供各页面共用。
    /// 主窗口创建时准备管理器和报警对象，此时不创建采集任务，也不自动采集。
    /// </summary>
    public DeviceRuntimeManager(UserAccount currentUser, DeviceStore deviceStore, HistoryStore historyStore,
        AlarmStore? alarmStore = null)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        if (!_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("当前账户不能使用采集任务。");

        Sessions = new ReadOnlyObservableCollection<DeviceSamplingViewModel>(_sessions);
        Alarms = new AlarmTracker(currentUser, alarmStore ?? new AlarmStore(historyStore.DatabasePath));
    }

    /// <summary>
    /// 由 MonitoringViewModel.StartSampling 调用。
    /// 按设备编号找到或新建任务，然后启动；管理员和操作员都能手动启停。
    /// </summary>
    public void Start(long deviceId)
    {
        // 必须在界面线程操作任务集合。
        _dispatcher.VerifyAccess();

        // 正在退出或无权限时，不允许开始。
        ThrowIfShuttingDown();
        CheckControlPermission();

        // 先拿到负责这台设备的任务对象，再调用它的 Start 开始循环。
        GetOrCreateSession(deviceId).Start();
    }

    /// <summary>
    /// 由本类 Start 调用。
    /// 按设备编号找采集任务，有就返回原对象，没有就新建并记入字典和集合。
    /// 这里只取得对象，调用它的 Start 才开始采集。
    /// </summary>
    public DeviceSamplingViewModel GetOrCreateSession(long deviceId)
    {
        // 检查线程、设备编号和退出状态。
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ThrowIfShuttingDown();

        // 正在删除的设备不能重新创建或启动任务。
        if (IsDeletingDevice(deviceId))
            throw new InvalidOperationException("这台设备正在删除，请等待操作结束。");

        // deviceId 是数据库编号，不是 Modbus 的 Unit ID；找到旧任务就直接返回。
        if (_sessionsByDevice.TryGetValue(deviceId, out DeviceSamplingViewModel? session))
            return session;

        // 只新建这台设备的任务，Store 和报警对象仍用共用的那份。
        session = new DeviceSamplingViewModel(_currentUser, _deviceStore, _historyStore, deviceId, Alarms);
        
        // 监听任务启停，及时更新“正在采集”的台数。
        session.PropertyChanged += Session_PropertyChanged;

        // 同一个任务同时放入字典和集合，不是创建两份任务。
        _sessionsByDevice.Add(deviceId, session);
        _sessions.Add(session);

        // 把对象交给调用方，是否开始采集由调用方决定。
        return session;
    }

    /// <summary>
    /// 由 MonitoringViewModel.StopSamplingAsync 或本类 DeleteDeviceAsync 调用。
    /// 停止指定设备并返回等待任务；没创建过采集任务就直接结束。
    /// </summary>
    public Task StopAsync(long deviceId)
    {
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        CheckControlPermission();

        // 只找已有任务，把停止的 Task 返回给外面等待。
        if (_sessionsByDevice.TryGetValue(deviceId, out DeviceSamplingViewModel? session))
            return session.StopAsync();
        // 没有任务就没有要停止的工作，返回已完成的 Task。
        return Task.CompletedTask;
    }

    /// <summary>
    /// 由 AcquisitionViewModel 判断能否单次测试时调用。
    /// 判断这台设备是否正在采集；只查已有任务，不创建新对象。
    /// </summary>
    public bool IsRunning(long deviceId)
    {
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        return _sessionsByDevice.TryGetValue(deviceId, out DeviceSamplingViewModel? session)
            && session.IsRunning;
    }

    /// <summary>
    /// 判断设备编号是否在“正在删除”的集合里。
    /// </summary>
    public bool IsDeletingDevice(long deviceId) => _deletingDeviceIds.Contains(deviceId);

    /// <summary>
    /// 由 DeviceManagementViewModel.DeleteDeviceAsync 调用。
    /// 先停这台设备的采集，再删设备和点位配置，并通知页面清掉旧行。
    /// 其他设备继续采集；历史读数和报警记录不删除。
    /// </summary>
    public async Task<bool> DeleteDeviceAsync(long deviceId)
    {
        // 检查线程和设备编号。
        _dispatcher.VerifyAccess();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);

        // 正在退出、无权限或重复删除时，不继续执行。
        ThrowIfShuttingDown();

        if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("只有已启用的管理员可以删除设备。");
        // Add 返回 false 表示这个编号已经在集合中，也就是已经有人在删它。
        if (!_deletingDeviceIds.Add(deviceId))
            throw new InvalidOperationException("这台设备已经在删除中，请勿重复操作。");
        
        OnPropertyChanged(nameof(IsDeletingDevices));
        
        try
        {
            // 先等采集和已开始的保存收尾，再删配置。
            await StopAsync(deviceId);

            // 后台删除数据库配置；false 表示设备本来就不存在。
            bool deleted = await Task.Run(() => _deviceStore.TryDeleteDevice(_currentUser, deviceId));

            // 有旧任务就从字典和集合移除，并解除订阅；没有就跳过。
            if (_sessionsByDevice.Remove(deviceId, out DeviceSamplingViewModel? session))
            {
                session.PropertyChanged -= Session_PropertyChanged;
                _sessions.Remove(session);
                OnPropertyChanged(nameof(ActiveSamplingCount));
            }

            // 通知 MonitoringViewModel.DeviceRuntime_DeviceDeleted 清掉监控行和读数。
            // 同时通知 AcquisitionViewModel.DeviceRuntimeManager_DeviceDeleted 清掉配置页的设备和点位。
            DeviceDeleted?.Invoke(deviceId);
            return deleted;
        }
        finally
        {
            // 成功或失败都解除“正在删除”的限制。
            _deletingDeviceIds.Remove(deviceId);
            OnPropertyChanged(nameof(IsDeletingDevices));
        }
    }

    /// <summary>
    /// 由设备采集任务的 PropertyChanged 事件调用。
    /// 任务运行状态变了，就通知界面更新正在采集的台数。
    /// </summary>
    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 这里只关心 IsRunning，其他属性变化不影响运行台数。
        if (e.PropertyName == nameof(DeviceSamplingViewModel.IsRunning))
            OnPropertyChanged(nameof(ActiveSamplingCount));
    }

    /// <summary>
    /// 只允许已启用的管理员或操作员启停采集，不代表能修改配置。
    /// </summary>
    private void CheckControlPermission()
    {
        if (!_currentUser.IsEnabled ||
            _currentUser.Role is not (UserRole.Administrator or UserRole.Operator))
            throw new UnauthorizedAccessException("当前账户不能开始或停止采集。");
    }

    /// <summary>
    /// 主窗口已开始退出时，拒绝发起新操作。
    /// </summary>
    private void ThrowIfShuttingDown()
    {
        if (IsShuttingDown)
            throw new InvalidOperationException("主窗口正在关闭，不能再创建或启动采集任务。");
    }

    /// <summary>
    /// 由 MainViewModel.StopBackgroundWorkAsync 在退出前调用。
    /// 退出时禁止新任务，再等所有设备停止；重复调用共用同一次停止任务。
    /// 这是关窗收尾，不用于普通的“停止采集”，调用后本管理器不再允许启动。
    /// </summary>
    public Task StopAllAsync()
    {
        _dispatcher.VerifyAccess();
        // 退出收尾已经开始，就返回原来的等待任务。
        if (_stopAllTask is not null)
            return _stopAllTask;

        IsShuttingDown = true;
        _stopAllTask = StopAllSessionsAsync();
        return _stopAllTask;
    }

    /// <summary>
    /// 由本类 StopAllAsync 调用。
    /// 等所有设备停止，补存未保存的报警，最后解除任务状态订阅。
    /// </summary>
    private async Task StopAllSessionsAsync()
    {
        // 一起请求停止，再等每台设备都完成读取和保存收尾。
        await Task.WhenAll(_sessionsByDevice.Values.Select(session => session.StopAsync()));

        // 退出前再尝试保存一次积压的报警。
        await Alarms.FlushAsync();
        foreach (DeviceSamplingViewModel session in _sessionsByDevice.Values)
            session.PropertyChanged -= Session_PropertyChanged;
        // 保留最后读数，只断开通知，不删除数据库历史。
    }
}

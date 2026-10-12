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
/// 管各台设备的任务，安排启动、停止、删除顺序：按数据库 DeviceId 保存任务，一个 Id 只建一份任务对象。
/// Start 找到并启动采集任务，StopAsync 停一台；删除设备时，先停任务再让 DeviceStore 删配置。
/// </summary>
public sealed class DeviceRuntimeManager : ObservableObject
{
    // 继续使用登录时传下来的账户和两个 Store，不在这里重新找数据库路径。
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    private readonly HistoryStore _historyStore;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // 字典方便按设备编号找任务；集合方便后面的监控页面绑定和查看。
    // 两处放的是同一个任务对象，不是两份采集循环。
    private readonly Dictionary<long, DeviceSamplingViewModel> _sessionsByDevice = new();
    private readonly ObservableCollection<DeviceSamplingViewModel> _sessions = new();
    // 删除需要先等任务结束；等待期间不能又从监控页把同一台设备启动。
    private readonly HashSet<long> _deletingDeviceIds = new();
    private Task? _stopAllTask;

    // 删除配置成功后，监控页用这个通知移除旧行，不必等用户手动刷新。
    public event Action<long>? DeviceDeleted;
    public bool IsDeletingDevices => _deletingDeviceIds.Count > 0;

    // 页面只能查看集合，不能自行 Add/Remove，避免把字典和列表改得不一致。
    public ReadOnlyObservableCollection<DeviceSamplingViewModel> Sessions { get; }
    public int ActiveSamplingCount => _sessionsByDevice.Values.Count(session => session.IsRunning);
    // 所有设备共用同一份报警记录与保存队列，监控页也绑定这一份，不另做一套判断。
    public AlarmTracker Alarms { get; }

    private bool _isShuttingDown;
    public bool IsShuttingDown
    {
        get => _isShuttingDown;
        private set => SetProperty(ref _isShuttingDown, value);
    }

    /// <summary>
    /// MainViewModel 在创建页面之前调用。这里只接住现成的对象、准备空集合。
    /// 不查库、不连接设备，也不因为设备配置为“启用”就自动开始采集。
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
    /// 监控页的 StartSampling 命令调用。按设备编号取同一个任务，再让它 Start。
    /// 已启用的管理员和操作员都能手动启停；修改设备或点位配置仍由各 Store 检查管理员权限。
    /// </summary>
    public void Start(long deviceId)
    {
        _dispatcher.VerifyAccess();
        ThrowIfShuttingDown();
        CheckControlPermission();

        // Start 自己也会检查 IsRunning，重复点击不会创建第二份循环。
        GetOrCreateSession(deviceId).Start();
    }

    /// <summary>
    /// Start 按数据库设备 Id 调用。先查字典，有就返回原对象，没有才 new DeviceSamplingViewModel 并同时放进字典/集合。
    /// 两处指向同一个任务；新对象还没有开始读，由 Start 再启动。deviceId 是数据库编号，不是 Unit ID。
    /// </summary>
    public DeviceSamplingViewModel GetOrCreateSession(long deviceId)
    {
        // 页面操作和绑定集合都在界面线程；后台只负责数据库和网络，不能来改这个字典。
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ThrowIfShuttingDown();
        if (IsDeletingDevice(deviceId))
            throw new InvalidOperationException("这台设备正在删除，请等待操作结束。");
        if (_sessionsByDevice.TryGetValue(deviceId, out DeviceSamplingViewModel? session))
            return session;

        session = new DeviceSamplingViewModel(_currentUser, _deviceStore, _historyStore, deviceId, Alarms);
        session.PropertyChanged += Session_PropertyChanged;
        _sessionsByDevice.Add(deviceId, session);
        _sessions.Add(session);
        return session;
    }

    /// <summary>
    /// 监控页的 StopSamplingAsync 调用，只停指定设备，其他设备继续采集。
    /// 找不到任务就直接结束，不为了“停止”反而创建一个新任务。
    /// </summary>
    public Task StopAsync(long deviceId)
    {
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        CheckControlPermission();
        if (_sessionsByDevice.TryGetValue(deviceId, out DeviceSamplingViewModel? session))
            return session.StopAsync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 采集管理页在单次测试前调用，只问设备是否正在连续采集，不创建任务。
    /// 找不到任务就是没在运行，不能为了判断状态反而 new 一个任务。
    /// </summary>
    public bool IsRunning(long deviceId)
    {
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        return _sessionsByDevice.TryGetValue(deviceId, out DeviceSamplingViewModel? session)
            && session.IsRunning;
    }

    /// <summary>
    /// 监控/配置页判断按钮、管理器判断能否启动时调用。只查删除中的编号集合，不访问数据库。
    /// </summary>
    public bool IsDeletingDevice(long deviceId) => _deletingDeviceIds.Contains(deviceId);

    /// <summary>
    /// 设备管理的删除命令调用：暂时阻止本设备启动，先停采集，再删除数据库配置。
    /// 其他设备继续采集；历史读数和报警记录不删除。
    /// </summary>
    public async Task<bool> DeleteDeviceAsync(long deviceId)
    {
        // 检查是不是在界面线程执行
        _dispatcher.VerifyAccess();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);

        // 检查主窗口是不是正在退出。已经开始退出，就不允许再发起删除
        ThrowIfShuttingDown();

        if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("只有已启用的管理员可以删除设备。");
        if (!_deletingDeviceIds.Add(deviceId))
            throw new InvalidOperationException("这台设备已经在删除中，请勿重复操作。");
        
        OnPropertyChanged(nameof(IsDeletingDevices));
        
        try
        {
            // 不能先删配置再等保存：本轮已经开始的读取、历史保存要先安全收尾。
            await StopAsync(deviceId);

            // 数据库操作可能耗时，放到后台线程执行。返回 false 表示配置本就不存在。
            bool deleted = await Task.Run(() => _deviceStore.TryDeleteDevice(_currentUser, deviceId));

            // 现在数据库操作已经成功。移除旧任务，不能把它带到以后新建的设备上。
            if (_sessionsByDevice.Remove(deviceId, out DeviceSamplingViewModel? session))
            {
                session.PropertyChanged -= Session_PropertyChanged;
                _sessions.Remove(session);
                OnPropertyChanged(nameof(ActiveSamplingCount));
            }
            // 返回 false 表示配置本就不存在，同样让页面清掉失效的旧行。
            // 把 deviceId 传给两个已经订阅的方法，不是再次删除数据库：
            // MonitoringViewModel.cs 的 DeviceRuntime_DeviceDeleted：移除监控页中这台设备的行和读数。
            // AcquisitionViewModel.cs 的 DeviceRuntimeManager_DeviceDeleted：移除配置页中的设备，若正选着它就清空点位。
            DeviceDeleted?.Invoke(deviceId);
            return deleted;
        }
        finally
        {
            // 删除失败也要解除临时限制，保留的设备可以修正问题后重新开始。
            _deletingDeviceIds.Remove(deviceId);
            OnPropertyChanged(nameof(IsDeletingDevices));
        }
    }

    /// <summary>
    /// 创建任务时订阅了这里。任务开始/结束后，它的 IsRunning 通知会到这里。
    /// 这里再通知 ActiveSamplingCount，页面看到的运行数量才能一起更新。
    /// </summary>
    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceSamplingViewModel.IsRunning))
            OnPropertyChanged(nameof(ActiveSamplingCount));
    }

    /// <summary>
    /// Start、StopAsync 调用。启用的管理员或操作员都能启停；这不赋予操作员修改设备/点位的权限。
    /// </summary>
    private void CheckControlPermission()
    {
        if (!_currentUser.IsEnabled ||
            _currentUser.Role is not (UserRole.Administrator or UserRole.Operator))
            throw new UnauthorizedAccessException("当前账户不能开始或停止采集。");
    }

    /// <summary>
    /// 新建/启动任务或删除设备前调用。主窗口已开始退出就拒绝新操作。
    /// </summary>
    private void ThrowIfShuttingDown()
    {
        if (IsShuttingDown)
            throw new InvalidOperationException("主窗口正在关闭，不能再创建或启动采集任务。");
    }

    /// <summary>
    /// 关主窗口时调用：先禁止新建/启动任务，再通知所有任务停止，并等它们真正结束。
    /// 重复调用返回同一次停止任务；普通停止一台设备不要用这里，否则本窗口将不能再启动采集。
    /// </summary>
    public Task StopAllAsync()
    {
        _dispatcher.VerifyAccess();
        if (_stopAllTask is not null)
            return _stopAllTask;

        IsShuttingDown = true;
        _stopAllTask = StopAllSessionsAsync();
        return _stopAllTask;
    }

    /// <summary>
    /// StopAllAsync 调用。并行请求所有原任务停止，等读取取消和保存结束，再重试报警保存，解除任务状态订阅。
    /// </summary>
    private async Task StopAllSessionsAsync()
    {
        // 不先清空字典；StopAsync 还需要等网络取消、当前那一条历史保存完成。
        await Task.WhenAll(_sessionsByDevice.Values.Select(session => session.StopAsync()));
        // 每个任务的 finally 已结束本设备报警监测；退出前再重试一次仍未保存的记录。
        await Alarms.FlushAsync();
        foreach (DeviceSamplingViewModel session in _sessionsByDevice.Values)
            session.PropertyChanged -= Session_PropertyChanged;
        // 保留最后读数，便于确认停完了什么；这里只解除状态订阅，不删除历史记录。
    }
}

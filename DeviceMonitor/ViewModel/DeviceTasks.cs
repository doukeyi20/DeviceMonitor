// 按设备管理采集任务，协调启停和配置修改。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public sealed class DeviceTasks : ObservableObject
{
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    private readonly HistoryStore _historyStore;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // 一项对应一台设备的完整采集循环，页面切换不重建任务。
    private readonly ObservableCollection<DeviceSamplingViewModel> _sessions = new();

    private readonly HashSet<long> _deletingDeviceIds = new();

    // 重复退出请求共用一个收尾任务，避免重复关闭资源。
    private Task? _shutdownTask;

    public event Action<long>? DeviceDeleted;
    public bool IsDeletingDevices => _deletingDeviceIds.Count > 0;

    // 单次测试也登记目标；测试收尾结束前，其他页面不能删除这台设备。
    private long? _singleReadDeviceId;
    public long? SingleReadDeviceId
    {
        get => _singleReadDeviceId;
        internal set => SetProperty(ref _singleReadDeviceId, value);
    }

    public ReadOnlyObservableCollection<DeviceSamplingViewModel> Sessions { get; }

    public int ActiveSamplingCount => _sessions.Count(session => session.IsRunning);

    public AlarmMonitor Alarms { get; }

    private bool _isShuttingDown;
    public bool IsShuttingDown
    {
        get => _isShuttingDown;
        private set => SetProperty(ref _isShuttingDown, value);
    }

    public DeviceTasks(UserAccount currentUser, DeviceStore deviceStore, HistoryStore historyStore,
    AlarmStore? alarmStore = null)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        if (!_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("当前账户不能使用采集任务。");

        Sessions = new ReadOnlyObservableCollection<DeviceSamplingViewModel>(_sessions);
        Alarms = new AlarmMonitor(currentUser, alarmStore ?? new AlarmStore(historyStore.DatabasePath));
    }

    /// <summary>按设备编号找到或新建任务，然后启动；管理员和操作员都能手动启停。</summary>
    public void Start(long deviceId)
    {
        _dispatcher.VerifyAccess();

        CheckControlPermission();

        // 获取任务时统一检查退出状态，不在同一调用链重复判断。
        GetOrCreateSession(deviceId).Start();
    }

    /// <summary>按设备编号找采集任务，有就返回原对象，没有就新建并放入集合。</summary>
    public DeviceSamplingViewModel GetOrCreateSession(long deviceId)
    {
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ThrowIfShuttingDown();

        if (IsDeletingDevice(deviceId))
            throw new InvalidOperationException("这台设备正在删除，请等待操作结束。");

        DeviceSamplingViewModel? session = _sessions.FirstOrDefault(item => item.DeviceId == deviceId);

        // 同一设备只保留一个任务，重复开始交由原任务判断。
        if (session is not null)
            return session;

        session = new DeviceSamplingViewModel(_currentUser, _deviceStore, _historyStore, deviceId, Alarms);

        session.PropertyChanged += SamplingTask_PropertyChanged;

        _sessions.Add(session);

        return session;
    }

    /// <summary>停止指定设备并返回等待任务；没创建过采集任务就直接结束。</summary>
    public Task StopAsync(long deviceId)
    {
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        CheckControlPermission();

        var session = _sessions.FirstOrDefault(item => item.DeviceId == deviceId);

        return session?.StopAsync() ?? Task.CompletedTask;
    }

    /// <summary>判断这台设备的任务是否还在运行或收尾；只查已有任务，不创建新对象。</summary>
    public bool IsRunning(long deviceId)
    {
        _dispatcher.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        return _sessions.Any(session => session.DeviceId == deviceId && session.IsRunning);
    }

    /// <summary>判断设备编号是否在“正在删除”的集合里。</summary>
    public bool IsDeletingDevice(long deviceId) => _deletingDeviceIds.Contains(deviceId);

    /// <summary>用户先手动停止采集和测试；这里删设备和点位配置，再通知页面清掉旧行。</summary>
    public async Task<bool> DeleteDeviceAsync(long deviceId)
    {
        _dispatcher.VerifyAccess();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);

        ThrowIfShuttingDown();

        if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("只有已启用的管理员可以删除设备。");
        if (IsRunning(deviceId) || SingleReadDeviceId == deviceId)
            throw new InvalidOperationException("请先手动停止这台设备的采集或测试，等结束后再删除。");

        if (!_deletingDeviceIds.Add(deviceId))
            throw new InvalidOperationException("这台设备已经在删除中，请勿重复操作。");

        OnPropertyChanged(nameof(IsDeletingDevices));

        try
        {
            // 先完成数据库删除，再解除旧任务订阅并通知各页面刷新。
            bool deleted = await Task.Run(() => _deviceStore.TryDeleteDevice(_currentUser, deviceId));

            var session = _sessions.FirstOrDefault(item => item.DeviceId == deviceId);
            if (session is not null)
            {
                session.StopReadingNotifications();
                session.PropertyChanged -= SamplingTask_PropertyChanged;
                _sessions.Remove(session);
                OnPropertyChanged(nameof(ActiveSamplingCount));
            }

            DeviceDeleted?.Invoke(deviceId);
            return deleted;
        }
        finally
        {
            _deletingDeviceIds.Remove(deviceId);
            OnPropertyChanged(nameof(IsDeletingDevices));
        }
    }

    /// <summary>任务运行状态变了，就通知界面更新正在采集的台数。</summary>
    private void SamplingTask_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceSamplingViewModel.IsRunning))
            OnPropertyChanged(nameof(ActiveSamplingCount));
    }

    /// <summary>只允许已启用的管理员或操作员启停采集，不代表能修改配置。</summary>
    private void CheckControlPermission()
    {
        if (!_currentUser.IsEnabled ||
            _currentUser.Role is not (UserRole.Administrator or UserRole.Operator))
            throw new UnauthorizedAccessException("当前账户不能开始或停止采集。");
    }

    /// <summary>主窗口已开始退出时，拒绝发起新操作。</summary>
    private void ThrowIfShuttingDown()
    {
        if (IsShuttingDown)
            throw new InvalidOperationException("主窗口正在关闭，不能再创建或启动采集任务。");
    }

    /// <summary>禁止启动新任务，并等待全部设备停止。</summary>
    public Task StopAllAsync()
    {
        _dispatcher.VerifyAccess();

        if (_shutdownTask is not null)
            return _shutdownTask;

        IsShuttingDown = true;
        _shutdownTask = StopAllSessionsAsync();
        return _shutdownTask;
    }

    /// <summary>等所有设备停止，补存未保存的报警，最后解除任务状态订阅。</summary>
    private async Task StopAllSessionsAsync()
    {
        try
        {
            await Task.WhenAll(_sessions.Select(session => session.StopAsync()));
        }
        finally
        {
            try
            {
                await Alarms.FlushAsync();
            }
            finally
            {
                foreach (DeviceSamplingViewModel session in _sessions)
                {
                    session.StopReadingNotifications();
                    session.PropertyChanged -= SamplingTask_PropertyChanged;
                }
            }
        }
    }
}

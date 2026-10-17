using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Communication;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Sampling;
using DeviceMonitor.Core.Diagnostics;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 一台设备的采集任务：开始时查一次配置，再循环读全部点位、更新显示和保存历史。
/// </summary>
public sealed class DeviceSamplingViewModel : ObservableObject
{
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    private readonly HistoryStore _historyStore;
    private readonly AlarmTracker? _alarms;
    private string _lastDeviceName;
    private readonly ModbusPointReader _pointReader = new();
    private CancellationTokenSource? _cancellation; // 给这一次运行发停止信号。
    private Task _runningTask = Task.CompletedTask; // 整个采集循环的任务，不是某一个点的一次读取。
    private string _lastSaveError = string.Empty;

    public long DeviceId { get; }

    // 每一项是一个点位的显示行，包含配置、最新数值、状态和时间。
    public ObservableCollection<PointReadingViewModel> Readings { get; } = new();

    // 循环是否还在运行，不代表设备已经连接成功。
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    // 任意一个点最后读成功的时间，供监控计算数据多久没更新；不代表所有点都正常。
    private DateTimeOffset? _lastSuccessfulReadAt;
    public DateTimeOffset? LastSuccessfulReadAt
    {
        get => _lastSuccessfulReadAt;
        private set => SetProperty(ref _lastSuccessfulReadAt, value);
    }

    private string _statusText = "尚未开始连续采集。";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // 所有点都尝试读过一次，才算完成一轮。
    private int _completedRounds;
    public int CompletedRounds
    {
        get => _completedRounds;
        private set => SetProperty(ref _completedRounds, value);
    }

    // 只统计本次 Start 后保存的条数，重新开始会归零；数据库历史不清空。
    private int _savedRecords;
    public int SavedRecords
    {
        get => _savedRecords;
        private set
        {
            if (SetProperty(ref _savedRecords, value))
                OnPropertyChanged(nameof(HistoryStatusText));
        }
    }

    // 本次运行中，历史记录保存失败的条数。
    private int _saveFailures;
    public int SaveFailures
    {
        get => _saveFailures;
        private set
        {
            if (SetProperty(ref _saveFailures, value))
                OnPropertyChanged(nameof(HistoryStatusText));
        }
    }

    // 上面的条数变化会刷新这段说明；保存失败与设备读取失败分开显示。
    public string HistoryStatusText =>
        $"本次已保存 {SavedRecords} 条，保存失败 {SaveFailures} 条。" +
        (string.IsNullOrEmpty(_lastSaveError) ? string.Empty : $" 最近一次保存失败：{_lastSaveError}");

    /// <summary>
    /// 由 DeviceRuntimeManager.GetOrCreateSession 第一次为设备创建任务时调用。
    /// 管理器第一次需要这台设备的任务时创建；这里只保存所需对象，不开始读取。
    /// </summary>
    public DeviceSamplingViewModel(UserAccount currentUser, DeviceStore deviceStore,
        HistoryStore historyStore, long deviceId, AlarmTracker? alarms = null)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        // 这个任务只负责此编号的设备，不随页面选中项改变。
        DeviceId = deviceId;
        // 没查到名称前先用编号，查到配置后再替换。
        _lastDeviceName = $"设备 #{deviceId}";
        _alarms = alarms; // 多台设备共用管理器传来的报警对象，允许不传。
    }

    /// <summary>
    /// 由 DeviceRuntimeManager.Start 调用。
    /// 重置本次显示和统计，启动 RunAsync 采集循环；正在运行时不重复启动。
    /// </summary>
    public void Start()
    {
        if (IsRunning)
            return; // 同一台设备不能重复启动两份循环。

        // 为本次运行准备“停止开关”，StopAsync 会对它发出取消信号。
        _cancellation = new CancellationTokenSource();
        // 清掉上次的显示和统计，不删除数据库历史。
        CompletedRounds = 0;
        LastSuccessfulReadAt = null;
        _lastSaveError = string.Empty;
        SavedRecords = 0;
        SaveFailures = 0;
        OnPropertyChanged(nameof(HistoryStatusText));
        Readings.Clear();

        // 标记循环开始，此时还不一定读到了数据。
        IsRunning = true;
        
        AppLog.Info("开始设备采集", $"UserId={_currentUser.Id}; DeviceId={DeviceId}");
        
        // RunAsync 是真正的采集循环；记住返回的 Task，停止时用它等待收尾。
        _runningTask = RunAsync(_cancellation.Token);
    }

    /// <summary>
    /// 由本类 Start 启动。
    /// 采集循环的主方法。
    /// 开始时加载配置；每轮逐点读取、检查报警、保存历史，再等一段时间继续。
    /// 本次采集一直使用这份配置；要改设备或点位，先停止，再重新开始。
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusText = "正在加载本次采集要用的设备和点位配置…";

            // 只在开始时查一次。config 里是配置，不是读到的数值。
            var config = await Task.Run(LoadConfiguration, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _lastDeviceName = config.Device.Name;

            // 停止期间可能改过点位或上下限，开始时处理一次旧报警。
            if (_alarms is not null)
                await _alarms.ReconcileConfigurationAsync(config.Device, config.Points);
            cancellationToken.ThrowIfCancellationRequested();

            // 每个点建一行，本次采集不再比较配置或重建行。
            PrepareRows(config.Points);

            // 一轮接一轮采集，直到收到停止信号。
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 只统计这一轮读成功的点数。
                int succeeded = 0;

                // 逐个读这台设备的全部点位，不是只读页面选中的点。
                foreach (PointReadingViewModel row in Readings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // row 是一个点的显示行，先清掉旧值并显示“读取中”。
                    row.BeginRead();
                    StatusText = $"正在采集“{config.Device.Name} / {row.Point.Name}”…";
                    
                    // 接住本次读取结果：点位、数值和读取时间。
                    PointReadResult result;
                    
                    try
                    {
                        // Device 决定连哪台设备，Point 决定读哪个地址，token 用于停止。
                        // 等这个点读完、处理并保存后，才读下一个点。
                        result = await _pointReader.ReadAsync(
                            config.Device, row.Point, cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();
                        
                        // result的第一个作用，更新显示
                        // 把结果填到这一行的数值、时间、状态属性中；实现更新显示。
                        row.ShowResult(result);
                        
                        // 记录成功时间和点数；后面保存失败也不算读取失败。
                        LastSuccessfulReadAt = result.ReadAt;
                        succeeded++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // 主动停止不算故障，更新显示后把取消异常交给外层。
                        row.ShowFailure("读取已取消");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("采集点读取失败", $"DeviceId={DeviceId}; PointId={row.Point.Id}", ex,
                            $"read-{DeviceId}-{row.Point.Id}");
                        // 显示失败原因；跳过这个点，下一轮还会再试。
                        row.ShowFailure($"失败：{ex.Message}");
                        // 真正读取失败才记异常，主动停止不会走到这里。
                        if (_alarms is not null)
                            await _alarms.ReportPointFailureAsync(config.Device, row.Point, ex.Message);
                        continue; // 没有有效读数，不保存这次历史。
                    }

                    // 只有上面的读取成功，才走到这里。

                    // result的第二个作用，检查报警
                    // 有报警对象，就用这次读数检查报警。
                    if (_alarms is not null)
                        await _alarms.ReportReadingAsync(config.Device, result);

                    // result的第三个作用，保存历史记录
                    // 把这个点本次读到的值，保存成一条历史记录。
                    await SaveReadingAsync(config.Device, result);
                }

                // 所有点都尝试过了，本轮结束；个别点失败也计一轮。
                CompletedRounds++;
                StatusText = $"已完成第 {CompletedRounds} 轮，成功 {succeeded}/{Readings.Count} 个点；" +
                    $"等待 {config.Device.SamplingIntervalMilliseconds} ms 后继续。";
                // 这一轮结束后再等采集间隔；异步等待不卡界面，也能被停止信号取消。
                await Task.Delay(config.Device.SamplingIntervalMilliseconds, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "已停止采集，连接已关闭；表格保留停止前的最后结果。";
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 停止后才返回的查询错误，也按停止处理，不再新增故障报警。
            StatusText = "已停止采集，连接已关闭；表格保留停止前的最后结果。";
        }
        catch (Exception ex)
        {
            AppLog.Error("设备采集任务无法继续", $"DeviceId={DeviceId}", ex);
            // 开始时配置不可用，或发生无法继续的错误，就结束这台设备的任务。
            StatusText = $"采集已结束：{ex.Message}";
            if (_alarms is not null)
                await _alarms.ReportDeviceFailureAsync(DeviceId, _lastDeviceName, ex.Message);
        }
        finally
        {
            // 等本次串口收发结束，再退出自己的占用；同 COM 下其他设备仍可继续采集。
            await _pointReader.CloseAsync();
            // 无论停止还是出错，都释放本次“停止开关”。
            _cancellation?.Dispose();
            _cancellation = null;
            // 标记“不再监测”，不把仍有问题的点误标为正常。
            if (_alarms is not null)
                await _alarms.EndDeviceMonitoringAsync(DeviceId);
            // 收尾完成后再标记任务已停止。
            IsRunning = false;
            AppLog.Info("设备采集已结束", $"DeviceId={DeviceId}; Rounds={CompletedRounds}; Saved={SavedRecords}; SaveFailures={SaveFailures}");
        }
    }

    /// <summary>
    /// 由 RunAsync 开始时调用一次。
    /// 查出本次要用的设备和全部点位；设备不可用或没有点位时不开始读取。
    /// </summary>
    private (DeviceInfo Device, IReadOnlyList<PointInfo> Points) LoadConfiguration()
    {
        // 按编号查这一台；enabledOnly: true 表示停用设备也当作查不到。
        DeviceInfo? device = _deviceStore.GetDevice(_currentUser, DeviceId, enabledOnly: true);
        if (device is null)
            throw new InvalidOperationException("设备已停用或已删除，请刷新设备列表。");

        // 用 DeviceId 查属于它的全部点位。
        var points = _deviceStore.GetPoints(_currentUser, DeviceId);
        if (points.Count == 0)
            throw new InvalidOperationException("设备没有采集点，请先让管理员配置采集点。");

        // 一起返回设备对象 device 和点位集合 points，集合中每项是一个 PointInfo。
        return (device, points);
    }

    /// <summary>
    /// 由 RunAsync 加载配置后调用一次。
    /// 为本次采集准备表格行，数值等下面读取成功后再填。
    /// points 中每项是 PointInfo 配置，Readings 中每项是 PointReadingViewModel 显示行。
    /// </summary>
    private void PrepareRows(IReadOnlyList<PointInfo> points)
    {
        // 每份 PointInfo 配置对应一个显示行，不改数据库里的点位和历史。
        Readings.Clear();
        foreach (PointInfo point in points)
            Readings.Add(new PointReadingViewModel(point));
    }

    /// <summary>
    /// 由 RunAsync 在一个点读取成功后调用。
    /// 把这个点本次读到的值，保存成一条历史记录。
    /// </summary>
    private async Task SaveReadingAsync(DeviceInfo device, PointReadResult result)
    {
        try
        {
            // 把设备、点位、数值和时间装成一条历史记录。
            var record = new HistoryRecord(device.Id, device.Name,
                result.Point.Id, result.Point.Name, result.Value, result.ReadAt);

            // 放到后台写数据库，避免卡住界面。
            // 等这条保存结束再继续；点停止也会等这次保存结束。
            await Task.Run(() => _historyStore.SaveRecord(_currentUser, record));

            // 保存成功，已保存条数加 1。
            SavedRecords++;
        }
        catch (Exception ex)
        {
            // 保存失败，记下日志、失败原因和失败条数；读到的值仍然保留。
            AppLog.Error("读数历史保存失败", $"DeviceId={DeviceId}; PointId={result.Point.Id}", ex,
                $"history-save-{DeviceId}");
            _lastSaveError = ex.Message;
            SaveFailures++;
            // 这次不自动重试；后面的点和下一轮仍会继续尝试保存。
        }
    }

    /// <summary>
    /// 由 DeviceRuntimeManager.StopAsync 或 StopAllSessionsAsync 调用。
    /// 发出停止信号，再等采集循环和保存收尾；Cancel 不等于任务已经结束。
    /// </summary>
    public async Task StopAsync()
    {
        if (IsRunning)
        {
            StatusText = "正在停止，取消读取或等待；若正在保存历史，则先等这一条保存结束…";
            // 这里只发信号，真正结束要等下面的 Task。
            _cancellation?.Cancel();
        }

        // 等循环和 finally 结束，已开始的历史保存也会等它写完。
        await _runningTask;
    }
}

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
/// 一台设备的一份采集任务：记住 DeviceId，循环读取该设备的全部点位，并保存成功读数。
/// 先看 Start → RunAsync；同轮的配置/读数显示/保存分别看紧接着的三个方法，最后看 StopAsync。
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
    public ObservableCollection<PointReadingViewModel> Readings { get; } = new();

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    // 这台设备任意一个点最近一次读取成功的时间，不是“所有点都正常”的证明。
    // 保留真正的时间对象供监控计算“多久以前”，不要从显示用的字符串反推时间。
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

    private int _completedRounds;
    public int CompletedRounds
    {
        get => _completedRounds;
        private set => SetProperty(ref _completedRounds, value);
    }

    // 统计的是这次 Start 之后保存的条数，不是整个数据库的总条数。
    // 只让本任务修改，所以保留手写的 private set。
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

    // 界面读取这个说明。条数变化时，上面的 setter 会通知界面重新读取它。
    // 保存错误单独显示，不覆盖点位表格里真实的读取值和读取状态。
    public string HistoryStatusText =>
        $"本次已保存 {SavedRecords} 条，保存失败 {SaveFailures} 条。" +
        (string.IsNullOrEmpty(_lastSaveError) ? string.Empty : $" 最近一次保存失败：{_lastSaveError}");

    /// <summary>
    /// 由 DeviceRuntimeManager.GetOrCreateSession 在这台设备第一次需要任务时创建。
    /// 接住用户、两个 Store、设备 Id 和共用报警对象；此时还不查配置、不读取、不保存。
    /// </summary>
    public DeviceSamplingViewModel(UserAccount currentUser, DeviceStore deviceStore,
        HistoryStore historyStore, long deviceId, AlarmTracker? alarms = null)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        DeviceId = deviceId;
        _lastDeviceName = $"设备 #{deviceId}";
        _alarms = alarms; // 管理器传来的同一个报警对象；直接做旧读取测试时可以不传。
    }

    /// <summary>
    /// DeviceRuntimeManager.Start 调用。为这次运行创建取消对象、重置显示统计，然后启动 RunAsync 并记住它返回的 Task。
    /// 不是 new 任务时就开始读；重复 Start 会被 IsRunning 挡住。
    /// </summary>
    public void Start()
    {
        if (IsRunning)
            return; // 同一台设备不能重复启动两份循环。

        _cancellation = new CancellationTokenSource();
        CompletedRounds = 0;
        LastSuccessfulReadAt = null; // 新一轮 Start 不把上次运行的成功时间冒充为这次成功。
        _lastSaveError = string.Empty;
        SavedRecords = 0;
        SaveFailures = 0;
        OnPropertyChanged(nameof(HistoryStatusText));
        // 这里只重置本次统计，没有执行 DELETE，数据库里的旧历史仍然保留。
        Readings.Clear();
        IsRunning = true;
        AppLog.Info("开始设备采集", $"UserId={_currentUser.Id}; DeviceId={DeviceId}");
        // 记住整个循环的 Task。停止时不只发通知，还要等它真正结束。
        // RunAsync 内会处理错误，不让后台任务的异常把程序关闭。
        _runningTask = RunAsync(_cancellation.Token);
    }

    /// <summary>
    /// Start 启动的循环：每轮查当前设备/点位配置，准备显示行，然后逐点读取、判断报警、保存成功值。
    /// 不同设备有各自循环；一台设备内逐点等待。一个点失败可继续，停止信号或配置不可用才结束任务。
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusText = "正在读取这一轮要用的设备和采集点配置…";

                // 数据库查询是同步方法，放到后台做；集合和提示仍在 await 后更新。
                // 每轮重新查询，下一轮就能发现设备停用、点位增删或配置修改。
                var config = await Task.Run(LoadConfiguration, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _lastDeviceName = config.Device.Name;
                if (_alarms is not null)
                    await _alarms.ReconcileConfigurationAsync(config.Device, config.Points);
                cancellationToken.ThrowIfCancellationRequested();
                UpdateRows(config.Points);
                int succeeded = 0;

                // 同一台设备内逐个读取，别同时向它发多个请求。
                // 不使用页面的 SelectedPoint：整台设备的所有已保存点位都要读。
                foreach (PointReadingViewModel row in Readings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    row.BeginRead();
                    StatusText = $"正在采集“{config.Device.Name} / {row.Point.Name}”…";
                    PointReadResult result;
                    try
                    {
                        result = await _pointReader.ReadAsync(
                            config.Device, row.Point, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        row.ShowResult(result);
                        // 读取成功就记录；后面即使保存历史失败，也不改变设备确实读成功的事实。
                        LastSuccessfulReadAt = result.ReadAt;
                        succeeded++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        row.ShowFailure("读取已取消");
                        throw; // 交给外层结束整台设备的循环。
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("采集点读取失败", $"DeviceId={DeviceId}; PointId={row.Point.Id}", ex,
                            $"read-{DeviceId}-{row.Point.Id}");
                        // 一个点失败，显示它的原因，再继续读下一个点。
                        // 下一轮还会重试，不因为一次超时就永远停止采集。
                        row.ShowFailure($"失败：{ex.Message}");
                        // 真正失败才记采集异常。上面的取消分支已经跳走，不会误记用户主动停止。
                        if (_alarms is not null)
                            await _alarms.ReportPointFailureAsync(config.Device, row.Point, ex.Message);
                        continue; // 这次没有有效读数，不能把失败或旧值存进历史。
                    }

                    // 只有上面的读取成功，才走到这里。显示读数和保存历史是两件事。
                    // 等这一条保存结束，再读取下一个点；不是启动一个没人等待的后台任务。
                    // 只有新读数有效，才判断上下限。报警保存失败会单独提示，不改成通信失败。
                    if (_alarms is not null)
                        await _alarms.ReportReadingAsync(config.Device, result);
                    await SaveReadingAsync(config.Device, result);
                }

                CompletedRounds++;
                StatusText = $"已完成第 {CompletedRounds} 轮，成功 {succeeded}/{Readings.Count} 个点；" +
                    $"等待 {config.Device.SamplingIntervalMilliseconds} ms 后继续。";
                // 这里是异步等待，不用 Thread.Sleep，也不会堵住界面。
                // 间隔表示一轮全部读完后再等多久，不保证每轮总耗时等于这个数字。
                await Task.Delay(config.Device.SamplingIntervalMilliseconds, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "已停止采集，连接已关闭；表格保留停止前的最后结果。";
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 查数据库是同步操作，停止后它仍可能才返回错误。
            // 用户已经要求停止，这时不再补出一条“采集异常”。
            StatusText = "已停止采集，连接已关闭；表格保留停止前的最后结果。";
        }
        catch (Exception ex)
        {
            AppLog.Error("设备采集任务无法继续", $"DeviceId={DeviceId}", ex);
            // 设备停用、没有点位、查询失败等情况，结束这台设备的任务。
            StatusText = $"采集已结束：{ex.Message}";
            if (_alarms is not null)
                await _alarms.ReportDeviceFailureAsync(DeviceId, _lastDeviceName, ex.Message);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            // 不把还在越限/失败的点说成已经正常，只标记当前不再监测；关窗会等保存完成。
            if (_alarms is not null)
                await _alarms.EndDeviceMonitoringAsync(DeviceId);
            IsRunning = false;
            AppLog.Info("设备采集已结束", $"DeviceId={DeviceId}; Rounds={CompletedRounds}; Saved={SavedRecords}; SaveFailures={SaveFailures}");
        }
    }

    /// <summary>
    /// RunAsync 每轮通过 Task.Run 调用。按本任务 DeviceId 查最新启用设备和点位，不看页面当前选中了谁。
    /// 设备停用/删除或没有点位时抛异常，让这一台任务结束。
    /// </summary>
    private (DeviceInfo Device, IReadOnlyList<PointInfo> Points) LoadConfiguration()
    {
        // 已经知道设备编号，只查这一台；多台设备采集时不用每轮反复拿回全部设备。
        DeviceInfo? device = _deviceStore.GetDevice(_currentUser, DeviceId, enabledOnly: true);
        if (device is null)
            throw new InvalidOperationException("设备已停用或已删除，请刷新设备列表。");

        var points = _deviceStore.GetPoints(_currentUser, DeviceId);
        if (points.Count == 0)
            throw new InvalidOperationException("设备没有采集点，请先让管理员配置采集点。");
        return (device, points);
    }

    /// <summary>
    /// RunAsync 查到配置后调用。配置不变就保留原行；变了才把 PointInfo 放进新的 PointReadingViewModel。
    /// 这里只准备显示行；PointInfo 是配置，读数由后面的 ShowResult 填入。
    /// </summary>
    private void UpdateRows(IReadOnlyList<PointInfo> points)
    {
        bool changed = Readings.Count != points.Count;
        if (!changed)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (!Readings[i].HasSameConfiguration(points[i]))
                {
                    changed = true;
                    break;
                }
            }
        }
        if (!changed)
            return;

        Readings.Clear();
        foreach (PointInfo point in points)
            Readings.Add(new PointReadingViewModel(point));
    }

    /// <summary>
    /// RunAsync 读取成功后调用，把本次设备、点位、值和时间组装成 HistoryRecord，交给 HistoryStore.SaveRecord。
    /// 保存失败单独计数和记日志，不把有效读数冒充通信失败，也不把失败值写成 0。
    /// </summary>
    private async Task SaveReadingAsync(DeviceInfo device, PointReadResult result)
    {
        try
        {
            // 名称和值都用这次实际读取的对象，不去读页面当前选中的设备或点位。
            // 所以采集中切换选择，不会把 A 的读数误存到 B 名下。
            var record = new HistoryRecord(device.Id, device.Name,
                result.Point.Id, result.Point.Name, result.Value, result.ReadAt);

            // SQLite 保存是同步方法，放到后台，避免写数据库时卡住界面。
            // 一条有效读数已经产生，就等待保存完成；不因“停止采集”而中途丢弃它。
            // StopAsync 等待的是整个 RunAsync，因此关窗也会等这次保存结束。
            await Task.Run(() => _historyStore.SaveRecord(_currentUser, record));

            // await 后回到界面线程，再更新界面绑定的统计。
            SavedRecords++;
        }
        catch (Exception ex)
        {
            AppLog.Error("读数历史保存失败", $"DeviceId={DeviceId}; PointId={result.Point.Id}", ex,
                $"history-save-{DeviceId}");
            _lastSaveError = ex.Message;
            SaveFailures++;
            // 不调用 row.ShowFailure：设备已读成功，只是这一条历史没有存进去。
            // 本次不自动重试，避免不确定是否写入时重复插入；下一点和下一轮仍会尝试保存。
        }
    }

    /// <summary>
    /// DeviceRuntimeManager 停一台或关窗停全部时调用。取消网络/等待间隔，然后 await 整个 RunAsync。
    /// 已开始保存的有效记录仍等它写完；发出 Cancel 不等于任务已经结束。
    /// </summary>
    public async Task StopAsync()
    {
        if (IsRunning)
        {
            StatusText = "正在停止，取消读取或等待；若正在保存历史，则先等这一条保存结束…";
            _cancellation?.Cancel();
        }
        await _runningTask;
    }
}

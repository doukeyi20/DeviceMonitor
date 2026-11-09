// 管理采集任务。
using DeviceMonitor.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Model;
using DeviceMonitor;

namespace DeviceMonitor.ViewModel;

/// <summary>每台设备对应的采集对象，有对应的设备编号：加载配置一次，然后反复读取、显示、报警、保存。</summary>
public sealed class DeviceSamplingViewModel : ObservableObject
{
    // 保存本设备循环需要的业务对象，界面不直接持有通信连接。
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _deviceStore;
    private readonly HistoryStore _historyStore;
    private readonly AlarmMonitor? _alarms;

    // 正常创建读取对象；回归检查可替换创建方法，避免访问真实设备。
    private Func<DeviceInfo, IReadOnlyList<PointInfo>, ModbusPointReader> _createReader =
        (device, points) => new ModbusPointReader(device, points);

    // 取消源负责发停止信号，任务对象负责等待真正结束。
    // 本轮采集的取消源：每次开始时新建，停止时用它发取消信号，循环最后释放并设回 null。
    // 发过取消信号不等于已经停完，还要等下面的 _samplingTask。
    private CancellationTokenSource? _stopSource;

    // 保存本轮完整采集任务，停止或退出时 await 它，等循环及其收尾真正结束；下一次开始换新任务。
    private Task _samplingTask = Task.CompletedTask;

    // 历史保存故障单独显示，不与通信故障或重连状态混在一起。
    private string _lastHistorySaveError = string.Empty;

    public long DeviceId { get; }

    // 采集点的显示行
    public ObservableCollection<PointReadingViewModel> Readings { get; } = new();

    // 这台设备已启动采集的标记：开始时打上，断线重试、停止收尾期间也保持 true，不代表连接正常。
    // 循环最后才清掉，用它拦住重复开始和运行中修改配置。
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    // 这台设备正在处理停止的标记：收到停止请求或循环进入最后收尾时打上，告诉界面还在等结束。
    // 最后收尾结束时清掉；它本身不发送取消信号，也不代表已经停完。
    private bool _isStopping;
    public bool IsStopping
    {
        get => _isStopping;
        private set => SetProperty(ref _isStopping, value);
    }

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

    private int _savedRecords;
    public int SavedRecords
    {
        get => _savedRecords;
        private set
        {
            SetProperty(ref _savedRecords, value);
        }
    }

    private int _saveFailures;
    public int SaveFailures
    {
        get => _saveFailures;
        private set
        {
            SetProperty(ref _saveFailures, value);
        }
    }

    public string HistoryErrorText => _lastHistorySaveError.Length == 0 ? "" : $"历史保存失败：{_lastHistorySaveError}";

    public DeviceSamplingViewModel(UserAccount currentUser, DeviceStore deviceStore,
        HistoryStore historyStore, long deviceId, AlarmMonitor? alarms = null)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);

        DeviceId = deviceId;
        _alarms = alarms;
    }

    /// <summary>重置本次状态并启动采集循环。</summary>
    public void Start()
    {
        if (IsRunning)
            return;

        _stopSource = new CancellationTokenSource();

        // 重新开始只清理本次显示和统计，不删除数据库历史。
        CompletedRounds = 0;
        LastSuccessfulReadAt = null;
        _lastHistorySaveError = string.Empty;
        OnPropertyChanged(nameof(HistoryErrorText));
        SavedRecords = 0;
        SaveFailures = 0;
        StopReadingNotifications();
        Readings.Clear();

        IsRunning = true;
        IsStopping = false;

        AppLog.Info("开始采集", $"设备={DeviceId} 用户={_currentUser.Id}");

        // 保存整段循环的任务，停止和退出时才能等待收尾。
        _samplingTask = RunAsync(_stopSource.Token);
    }

    /// <summary>
    /// 采集循环，调用了读取方法，设置轮询，处理读取方法的异常和分配结果流向，所有的通信异常封装在Reader的读取方法中
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // 本次采集的秒表，同一台设备的所有点位共用这个起点。
        var trendClock = Stopwatch.StartNew();
        string deviceName = $"设备 #{DeviceId}";
        try
        {
            StatusText = "正在加载本次采集要用的设备和点位配置…";
            // 开始时加载一次配置，后续轮询和故障重连都使用这份配置。
            var config = await Task.Run(LoadConfiguration, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            deviceName = config.Device.Name;

            // 读取对象在循环外创建，正常轮次复用连接；退出 try 时先释放，再进入外层 finally。
            // 这里得到reader对象，真正存读取方法的地方
            await using var reader = _createReader(config.Device, config.Points);

            foreach (PointInfo point in config.Points)
                Readings.Add(new PointReadingViewModel(config.Device, point, _alarms, this));

            // 循环读取和对采集时的异常的处理。
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 更新读取状态，但不清空上一轮显示，避免周期性闪烁。
                foreach (PointReadingViewModel row in Readings)
                    row.BeginRead();
                StatusText = $"正在批量采集“{config.Device.Name}”的 {Readings.Count} 个点…";

                IReadOnlyList<PointReadResult> results;

                try
                {
                    // 读完这一轮,读完全部点位，才返回 results，每条都有点位、原始值、换算值和读取时间。
                    // 在内部执行连接
                    results = await reader.ReadAsync(cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();
                }

                // 停止请求退出整个循环，不作为故障重试。
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TimeSpan canceledAt = trendClock.Elapsed;
                    foreach (PointReadingViewModel row in Readings)
                        row.ShowCanceled(canceledAt);

                    throw;
                }

                // ModbusPointReader通过CanRetry方法区分好了可以进行重连的异常，并且在ReadAsync方法也捕捉了一次，先清理旧连接，然后传给这里进行重新执行，实现重连
                catch (Exception ex) when (ModbusPointReader.CanRetry(ex))
                {
                    AppLog.Warning("采集失败，稍后重试", $"设备={DeviceId}", ex, $"read-{DeviceId}");

                    TimeSpan failedAt = trendClock.Elapsed;
                    foreach (PointReadingViewModel row in Readings)
                        row.ShowFailure($"失败：{ex.Message}", failedAt);

                    if (_alarms is not null)
                        await _alarms.ReportDeviceFailureAsync(config.Device.Id, config.Device.Name, ex.Message);

                    CompletedRounds++;

                    // 失败后至少等待 1 秒，避免高频重连。
                    int retryDelay = Math.Max(1000, config.Device.SamplingIntervalMilliseconds);
                    StatusText = $"读取失败：{ex.Message} {retryDelay} ms 后重试。";
                    await Task.Delay(retryDelay, cancellationToken);

                    // 重新执行循环等于进行重新读取
                    continue;
                }

                // 这一轮读成功后，同一批 results 依次交给显示、报警和历史保存。
                // ① 每条结果交给对应的读数卡片，更新数值并追加曲线样本。
                TimeSpan roundElapsed = trendClock.Elapsed;
                for (int i = 0; i < results.Count; i++)
                    Readings[i].ShowResult(results[i], roundElapsed);

                // 这一轮共用一个读取时间，取第一条更新界面的「最近成功」。
                LastSuccessfulReadAt = results[0].ReadAt;

                // ② 把整批读数交给报警模块，判断哪些点位触发或恢复报警。
                if (_alarms is not null)
                    await _alarms.ReportReadingsAsync(config.Device, results);

                // ③ 把同一批读数交给历史保存，写成采集记录。
                await SaveReadingsAsync(config.Device, results);

                CompletedRounds++;
                StatusText = $"已完成第 {CompletedRounds} 轮，成功 {results.Count}/{Readings.Count} 个点；" +
                    $"等待 {config.Device.SamplingIntervalMilliseconds} ms 后继续。";

                // 本轮读、存结束后再等间隔，不会按定时器另起一轮与本轮重叠。
                await Task.Delay(config.Device.SamplingIntervalMilliseconds, cancellationToken);
            }
        }

        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "已停止采集，连接已释放；表格保留最后结果。";
        }
        catch (Exception ex)
        {
            AppLog.Error("采集停止：无法继续", $"设备={DeviceId}", ex);
            TimeSpan failedAt = trendClock.Elapsed;
            foreach (PointReadingViewModel row in Readings)
                row.ShowFailure($"失败：{ex.Message}", failedAt);
            StatusText = $"采集已结束：{ex.Message}";
            if (_alarms is not null)
                await _alarms.ReportDeviceFailureAsync(DeviceId, deviceName, ex.Message);
        }
        finally
        {
            IsStopping = true;

            try
            {
                if (_alarms is not null)
                    await _alarms.EndDeviceMonitoringAsync(DeviceId);
            }
            finally
            {
                // 读取对象已离开 await using，最后再清理停止状态和任务统计。
                _stopSource?.Dispose();
                _stopSource = null;

                IsRunning = false;
                IsStopping = false;
                AppLog.Info("采集结束",
                    $"设备={DeviceId} 轮次={CompletedRounds} 已存={SavedRecords} 存储失败={SaveFailures}");
            }
        }
    }

    /// <summary>查出本次要用的设备和全部点位；设备不存在或没有点位时不开始读取。</summary>
    private (DeviceInfo Device, IReadOnlyList<PointInfo> Points) LoadConfiguration()
    {
        DeviceInfo? device = _deviceStore.GetDevice(_currentUser, DeviceId);
        if (device is null)
            throw new InvalidOperationException("设备已删除，请刷新设备列表。");

        var points = _deviceStore.GetPoints(_currentUser, DeviceId);
        if (points.Count == 0)
            throw new InvalidOperationException("设备没有采集点，请先让管理员配置采集点。");

        return (device, points);
    }

    /// <summary>批量保存本轮成功读数，转成记录给数据库</summary>
    private async Task SaveReadingsAsync(DeviceInfo device, IReadOnlyList<PointReadResult> results)
    {
        try
        {
            // 把从RunAsync得到的结果转HistoryRecord,组成集合
            var records = results.Select(result => new HistoryRecord(device.Id, device.Name,
                result.Point.Id, result.Point.Name, result.Value, result.ReadAt, unit: result.Point.Unit)).ToArray();

            // 一轮一次提交；保存异常留在本方法内，不触发设备重连。
            await Task.Run(() => _historyStore.SaveRecords(_currentUser, records));

            _lastHistorySaveError = "";
            OnPropertyChanged(nameof(HistoryErrorText));
            SavedRecords += records.Length;
        }
        catch (Exception ex)
        {
            AppLog.Error("读数保存失败", $"设备={DeviceId} 条数={results.Count}", ex,
                $"history-save-{DeviceId}");
            _lastHistorySaveError = ex.Message;
            OnPropertyChanged(nameof(HistoryErrorText));
            SaveFailures += results.Count;
        }
    }

    /// <summary>发出取消信号并等待采集任务结束。</summary>
    public async Task StopAsync()
    {
        if (IsRunning)
        {
            IsStopping = true;
            StatusText = "正在停止，等当前库读取完成或超时；已开始的历史保存也会等它结束…";

            _stopSource?.Cancel();
        }

        // Cancel 只是通知，等任务退出后才算停止完成。
        await _samplingTask;
    }

    /// <summary>断开旧行通知，不删除数据库历史。</summary>
    internal void StopReadingNotifications()
    {
        foreach (PointReadingViewModel row in Readings)
            row.Dispose();
    }
}

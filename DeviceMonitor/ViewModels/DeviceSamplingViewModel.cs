// 运行单台设备的采集循环，处理重试、停止、报警和历史保存。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Communication;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Core;

namespace DeviceMonitor.ViewModels;

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
    private CancellationTokenSource? _stopSource;

    private Task _samplingTask = Task.CompletedTask;

    // 历史保存故障单独显示，不与通信故障或重连状态混在一起。
    private string _lastHistorySaveError = string.Empty;

    public long DeviceId
    {
        get;
    }

    public ObservableCollection<PointMonitorViewModel> Readings { get; } = new();

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

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

    public string HistoryStatusText =>
        $"本次已保存 {SavedRecords} 条，保存失败 {SaveFailures} 条。" +
        (string.IsNullOrEmpty(_lastHistorySaveError) ? string.Empty : $" 最近一次保存失败：{_lastHistorySaveError}");

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
        SavedRecords = 0;
        SaveFailures = 0;
        OnPropertyChanged(nameof(HistoryStatusText));
        StopReadingNotifications();
        Readings.Clear();

        IsRunning = true;
        IsStopping = false;

        AppLog.Info("开始采集", $"设备={DeviceId} 用户={_currentUser.Id}");

        // 保存整段循环的任务，停止和退出时才能等待收尾。
        _samplingTask = RunAsync(_stopSource.Token);
    }

    /// <summary>按固定配置轮询，协调结果显示、报警、保存和故障重试。</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        string deviceName = $"设备 #{DeviceId}";
        try
        {
            StatusText = "正在加载本次采集要用的设备和点位配置…";
            // 开始时加载一次配置，后续轮询和故障重连都使用这份配置。
            var config = await Task.Run(LoadConfiguration, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            deviceName = config.Device.Name;

            // 读取对象在循环外创建，正常轮次复用连接。

            await using var reader = _createReader(config.Device, config.Points);

            foreach (PointInfo point in config.Points)
                Readings.Add(new PointMonitorViewModel(config.Device, point, _alarms, this));

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 更新读取状态，但不清空上一轮显示，避免周期性闪烁。
                foreach (PointMonitorViewModel row in Readings)
                    row.BeginRead();
                StatusText = $"正在批量采集“{config.Device.Name}”的 {Readings.Count} 个点…";

                IReadOnlyList<PointReadResult> results;

                try
                {
                    results = await reader.ReadAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // 停止请求退出整个循环，不作为故障重试。
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    foreach (PointMonitorViewModel row in Readings)
                        row.ShowCanceled();

                    throw;
                }

                // 仅通信故障和暂时设备错误等待重试，配置错误交给外层结束。
                catch (Exception ex) when (ModbusPointReader.CanRetry(ex))
                {
                    AppLog.Warning("采集失败，稍后重试", $"设备={DeviceId}", ex, $"read-{DeviceId}");

                    foreach (PointMonitorViewModel row in Readings)
                        row.ShowFailure($"失败：{ex.Message}");

                    if (_alarms is not null)
                        await _alarms.ReportFailuresAsync(config.Device, config.Points, ex.Message);

                    CompletedRounds++;

                    // 失败后至少等待 1 秒，避免高频重连。
                    int retryDelay = Math.Max(1000, config.Device.SamplingIntervalMilliseconds);
                    StatusText = $"读取失败：{ex.Message} {retryDelay} ms 后重试。";
                    await Task.Delay(retryDelay, cancellationToken);

                    // 失败轮次不走成功处理，不保存假读数。
                    continue;
                }

                // 显示、报警和历史共用同一批已换算结果。
                for (int i = 0; i < results.Count; i++)
                    Readings[i].ShowResult(results[i]);

                LastSuccessfulReadAt = results[0].ReadAt;

                if (_alarms is not null)
                    await _alarms.ReportReadingsAsync(config.Device, results);

                await SaveReadingsAsync(config.Device, results);

                CompletedRounds++;
                StatusText = $"已完成第 {CompletedRounds} 轮，成功 {results.Count}/{Readings.Count} 个点；" +
                    $"等待 {config.Device.SamplingIntervalMilliseconds} ms 后继续。";

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
            foreach (PointMonitorViewModel row in Readings)
                row.ShowFailure($"失败：{ex.Message}");
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

    /// <summary>查出本次要用的设备和全部点位；设备不可用或没有点位时不开始读取。</summary>
    private (DeviceInfo Device, IReadOnlyList<PointInfo> Points) LoadConfiguration()
    {
        DeviceInfo? device = _deviceStore.GetDevice(_currentUser, DeviceId, enabledOnly: true);
        if (device is null)
            throw new InvalidOperationException("设备已停用或已删除，请刷新设备列表。");

        var points = _deviceStore.GetPoints(_currentUser, DeviceId);
        if (points.Count == 0)
            throw new InvalidOperationException("设备没有采集点，请先让管理员配置采集点。");

        return (device, points);
    }

    /// <summary>批量保存本轮成功读数，保存失败不影响通信连接。</summary>
    private async Task SaveReadingsAsync(DeviceInfo device, IReadOnlyList<PointReadResult> results)
    {
        try
        {
            var records = results.Select(result => new HistoryRecord(device.Id, device.Name,
                result.Point.Id, result.Point.Name, result.Value, result.ReadAt, unit: result.Point.Unit)).ToArray();

            // 一轮一次提交；保存异常留在本方法内，不触发设备重连。
            await Task.Run(() => _historyStore.SaveRecords(_currentUser, records));

            SavedRecords += records.Length;
        }
        catch (Exception ex)
        {
            AppLog.Error("读数保存失败", $"设备={DeviceId} 条数={results.Count}", ex,
                $"history-save-{DeviceId}");
            _lastHistorySaveError = ex.Message;
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
        foreach (PointMonitorViewModel row in Readings)
            row.Dispose();
    }
}

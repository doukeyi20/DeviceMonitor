using System;
using DeviceMonitor.Core.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.Sampling;

/// <summary>
/// 所有设备共用的报警对象：检查数值越限、记录读取失败，并保存报警。
/// 同一种持续异常更新原记录，不会每轮新增一条；保存失败的记录留着重试。
/// </summary>
public sealed class AlarmTracker : ObservableObject
{
    public const int DisplayLimit = 500; // 最近报警最多保留 500 条；活动报警另有字典保存。
    private const int PendingLimit = 5000; // 待保存事件最多留 5000 个，防止一直失败时无限增长。
    private readonly UserAccount _currentUser;
    private readonly AlarmStore _store;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    // 多台设备共用保存列表，一次只允许一份保存流程执行。
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    // 设备编号 + 点位编号 + 异常类型 → 尚未结束的报警。
    private readonly Dictionary<(long DeviceId, long? PointId, AlarmKind Kind), AlarmRecord> _active = new();
    private readonly ObservableCollection<AlarmRecord> _records = new(); // 最近报警记录，变化后通知监控行更新状态。
    // EventId → 待保存的最新报警版本；按同一编号更新，重试不会重复新增。
    private readonly Dictionary<string, AlarmRecord> _pending = new();
    private int _droppedUpdates; // 保存列表满了以后，没能保留的更新次数。
    private string _persistenceStatusText = string.Empty;

    public ReadOnlyObservableCollection<AlarmRecord> Records { get; } // 每项是一条 AlarmRecord，页面只能查看。
    public string PersistenceStatusText
    {
        get => _persistenceStatusText;
        private set => SetProperty(ref _persistenceStatusText, value);
    }
    public int PendingSaveCount => _pending.Count;
    // 数据库长时间不可写且超过待保存上限时，用它提醒用户已有更新丢失。
    public int DroppedUpdateCount => _droppedUpdates;
    public int ActiveCount => _active.Count; // 还没恢复、也没停止监测的报警数。

    /// <summary>
    /// 保存账户和报警数据库对象，准备显示集合；此时不生成报警。
    /// </summary>
    public AlarmTracker(UserAccount currentUser, AlarmStore store)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Records = new ReadOnlyObservableCollection<AlarmRecord>(_records);
    }

    /// <summary>
    /// 由 MonitoringReadingViewModel 更新点位状态时调用。
    /// 找这个点尚未结束的报警；整台设备的采集异常也算，读取失败优先显示。
    /// </summary>
    public AlarmRecord? GetCurrentAlarm(long deviceId, long pointId)
    {
        _dispatcher.VerifyAccess();
        // 不从最近 500 条里找，避免持续很久的报警被新记录挤掉。
        if (_active.TryGetValue((deviceId, pointId, AlarmKind.AcquisitionFailure), out AlarmRecord? failure)) return failure;
        if (_active.TryGetValue((deviceId, null, AlarmKind.AcquisitionFailure), out AlarmRecord? deviceFailure)) return deviceFailure;
        if (_active.TryGetValue((deviceId, pointId, AlarmKind.HighLimit), out AlarmRecord? high)) return high;
        return _active.TryGetValue((deviceId, pointId, AlarmKind.LowLimit), out AlarmRecord? low) ? low : null;
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 开始时加载配置后调用一次。
    /// 检查重新开始前改过的点位和上下限；停止监测不等于恢复正常。
    /// </summary>
    public async Task ReconcileConfigurationAsync(DeviceInfo device, IReadOnlyList<PointInfo> points)
    {
        _dispatcher.VerifyAccess();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // 只检查这台设备的活动报警；ToArray 避免遍历时修改原字典。
        foreach (AlarmRecord alarm in _active.Values.Where(item => item.DeviceId == device.Id).ToArray())
        {
            if (!alarm.PointId.HasValue) continue;
            PointInfo? point = points.FirstOrDefault(item => item.Id == alarm.PointId.Value);
            bool removed = point is null;
            // 点位没了，或产生报警的那条限值改了，旧报警就不再继续监测。
            bool ruleChanged = alarm.Kind switch
            {
                AlarmKind.HighLimit => point?.UpperLimit != alarm.Limit,
                AlarmKind.LowLimit => point?.LowerLimit != alarm.Limit,
                _ => false
            };
            if (removed || ruleChanged)
                End(alarm, AlarmState.NotMonitoring, now, "点位或限值配置已变更，旧规则停止监测。");
        }
        await FlushAsync();
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在点位读取成功后调用。
    /// 用新读数恢复这个点的读取异常，再检查上下限；检查是否报警
    /// </summary>
    public async Task ReportReadingAsync(DeviceInfo device, PointReadResult result)
    {
        _dispatcher.VerifyAccess();
        PointInfo point = result.Point;
        // 读成功了，之前的读取异常就恢复；数值报警仍需单独比较。
        Recover(device.Id, point.Id, AlarmKind.AcquisitionFailure, result.ReadAt, "此点已重新读取成功。");
        // 检查数值是否大于上限
        EvaluateLimit(device, result, AlarmKind.HighLimit, point.UpperLimit,
            point.UpperLimit.HasValue && result.Value > point.UpperLimit.Value);
        // 检查数值是否小于下限
        EvaluateLimit(device, result, AlarmKind.LowLimit, point.LowerLimit,
            point.LowerLimit.HasValue && result.Value < point.LowerLimit.Value);
        // 立即保存报警更新，避免丢失。
        await FlushAsync();
    }

    // 没越限就恢复同类报警，越限就创建或更新它；上下限共用这里。
    // 由 ReportReadingAsync 分别检查上限、下限时调用。
    private void EvaluateLimit(DeviceInfo device, PointReadResult result, AlarmKind kind, int? limit, bool outside)
    {
        if (!outside)
        {
            // 无越界，调用Recover
            Recover(device.Id, result.Point.Id, kind, result.ReadAt, "有效读数已回到该限值允许的范围。");
            return;
        }
        string direction = kind == AlarmKind.HighLimit ? "高于上限" : "低于下限";
        // 越界了，调用Observe创建或更新报警
        Observe(device.Id, device.Name, result.Point.Id, result.Point.Name, kind,
            $"读数 {result.Value} {direction} {limit}。", result.Value, limit, result.ReadAt);
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在点位读取失败后调用。
    /// 记录这个点的读取异常，不拿旧值判断上下限；主动取消不调用这里。
    /// </summary>
    public async Task ReportPointFailureAsync(DeviceInfo device, PointInfo point, string message)
    {
        _dispatcher.VerifyAccess();
        Observe(device.Id, device.Name, point.Id, point.Name, AlarmKind.AcquisitionFailure,
            message, null, null, DateTimeOffset.UtcNow);
        await FlushAsync();
    }

    // 记录整台设备无法继续采集的原因，PointId 为 null 表示不是某个点的问题。
    // 由 DeviceSamplingViewModel.RunAsync 在整台设备无法继续采集时调用。
    public async Task ReportDeviceFailureAsync(long deviceId, string deviceName, string message)
    {
        _dispatcher.VerifyAccess();
        Observe(deviceId, deviceName, null, string.Empty, AlarmKind.AcquisitionFailure,
            message, null, null, DateTimeOffset.UtcNow);
        await FlushAsync();
    }

    /// <summary>
    /// 由 EvaluateLimit、ReportPointFailureAsync 和 ReportDeviceFailureAsync 调用。
    /// 同类异常已存在就更新次数和数值，否则新建报警；再更新显示和待保存列表。
    /// </summary>
    private void Observe(long deviceId, string deviceName, long? pointId, string pointName,
        AlarmKind kind, string message, int? value, int? limit, DateTimeOffset time)
    {
        // 用这三个值判断是不是同一类持续异常。
        var key = (deviceId, pointId, kind);
        AlarmRecord record;
        if (_active.TryGetValue(key, out AlarmRecord? existing))
        {
            // 电脑时间回拨时，最后发生时间也不能倒退。
            if (time < existing.LastSeenAt) time = existing.LastSeenAt;
            // with 生成更新后的记录，但保留 EventId，仍算同一条报警。
            record = existing with
            {
                LastSeenAt = time,
                Occurrences = existing.Occurrences == int.MaxValue
                    ? int.MaxValue : existing.Occurrences + 1,
                Message = message,
                Value = value
            };
        }
        else
        {
            // 首次出现，分配新 EventId 并记下首次时间、数值和限值。
            record = new AlarmRecord
            {
                EventId = Guid.NewGuid().ToString("N"),
                DeviceId = deviceId,
                DeviceName = deviceName,
                PointId = pointId,
                PointName = pointName,
                Kind = kind,
                State = AlarmState.Active,
                Message = message,
                Value = value,
                Limit = limit,
                StartedAt = time,
                LastSeenAt = time,
                Occurrences = 1
            };
        }

        // 活动字典、界面显示和待保存列表都改成最新记录。
        _active[key] = record;
        Publish(record);
        OnPropertyChanged(nameof(ActiveCount));
    }

    /// <summary>
    /// 由 ReportReadingAsync 和 EvaluateLimit 调用。
    /// 找到同类活动报警就标记恢复；没有旧报警就不做处理。
    /// </summary>
    private void Recover(long deviceId, long? pointId, AlarmKind kind, DateTimeOffset time, string reason)
    {
        if (_active.TryGetValue((deviceId, pointId, kind), out AlarmRecord? record))
            End(record, AlarmState.Recovered, time, reason);
    }

    /// <summary>
    /// 由 Recover、ReconcileConfigurationAsync 和 EndDeviceMonitoringAsync 调用。
    /// 结束一条活动报警：移出活动字典，再更新结束状态、时间和显示。
    /// </summary>
    private void End(AlarmRecord record, AlarmState state, DateTimeOffset time, string reason)
    {
        if (time < record.LastSeenAt) time = record.LastSeenAt;
        _active.Remove((record.DeviceId, record.PointId, record.Kind));
        Publish(record with { State = state, EndedAt = time, LastSeenAt = time, Message = record.Message + " " + reason });
        OnPropertyChanged(nameof(ActiveCount));
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在采集结束时调用。
    /// 设备停止采集后，把未恢复的报警标记为“不再监测”，不是“已恢复”。
    /// </summary>
    public async Task EndDeviceMonitoringAsync(long deviceId)
    {
        _dispatcher.VerifyAccess();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (AlarmRecord record in _active.Values.Where(item => item.DeviceId == deviceId).ToArray())
            End(record, AlarmState.NotMonitoring, now, "采集任务已结束；停止监测，不代表异常已恢复。");
        await FlushAsync();
    }

    /// <summary>
    /// 由 Observe 和 End 更新报警后调用。
    /// 更新最近报警列表，并把这条报警的最新版本放入待保存字典。
    /// </summary>
    private void Publish(AlarmRecord record)
    {
        // 表里已有同一 EventId 就替换，否则按首次发生时间插入。
        int index = -1;
        for (int i = 0; i < _records.Count; i++)
            if (_records[i].EventId == record.EventId) { index = i; break; }
        if (index >= 0) _records[index] = record;
        else
        {
            // 旧报警重新出现也按首次发生时间排序，不当成新报警。
            int insertAt = 0;
            while (insertAt < _records.Count && _records[insertAt].StartedAt >= record.StartedAt) insertAt++;
            _records.Insert(insertAt, record);
        }
        while (_records.Count > DisplayLimit) _records.RemoveAt(_records.Count - 1);

        // 待保存列表满了就丢掉其中一条，并明确记录丢失次数。
        if (!_pending.ContainsKey(record.EventId) && _pending.Count >= PendingLimit)
        {
            _pending.Remove(_pending.Keys.First());
            _droppedUpdates++;
            AppLog.Warning("报警待保存队列已满，旧更新未能保留", $"Dropped={_droppedUpdates}",
                throttleKey: "alarm-pending-full");
        }
        _pending[record.EventId] = record;
        OnPropertyChanged(nameof(PendingSaveCount));
    }

    /// <summary>
    /// 由本类处理报警的方法调用；退出时管理器和主窗口也会调用它补存。
    /// 把待保存的报警写入数据库；失败时保留，下次调用再试，退出时也会补试一次。
    /// </summary>
    public async Task FlushAsync()
    {
        _dispatcher.VerifyAccess();
        // 其他设备正在保存时先等它完成，避免同时处理同一列表。
        await _saveGate.WaitAsync();
        try
        {
            while (_pending.Count > 0)
            {
                // 固定本批名单，后台写数据库；期间还允许收到新的报警更新。
                AlarmRecord[] batch = _pending.Values.ToArray();
                try
                {
                    await Task.Run(() =>
                    {
                        foreach (AlarmRecord record in batch) _store.SaveRecord(_currentUser, record);
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Error("报警历史保存失败", $"Pending={_pending.Count}", ex, "alarm-save");
                    PersistenceStatusText = $"报警保存失败：{ex.Message}。待保存 {_pending.Count} 条；后续会重试。" + DroppedText();
                    return; // 先保留待保存记录，不影响采集和读数。
                }

                // 只移除刚存成功的版本；等待期间产生的新版本还要继续保存。
                foreach (AlarmRecord record in batch)
                    if (_pending.TryGetValue(record.EventId, out AlarmRecord? latest) && ReferenceEquals(latest, record))
                        _pending.Remove(record.EventId);
                OnPropertyChanged(nameof(PendingSaveCount));
            }
            if (_records.Count > 0)
                PersistenceStatusText = "报警记录已保存。" + DroppedText();
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>
    /// 有报警更新丢失时，在保存提示后补上丢失次数。
    /// </summary>
    private string DroppedText() => _droppedUpdates == 0 ? string.Empty
        : $" 数据库长时间不可写，待保存上限已超出，有 {_droppedUpdates} 次记录更新未能保留。";
}

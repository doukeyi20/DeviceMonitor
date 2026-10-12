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
/// 所有设备共用的报警判断与待保存列表：成功读数检查上下限，读取失败另记采集异常。
/// 同一设备/点/类别的持续异常更新原事件；显示集合和待保存集合不同，写库失败仍可重试。
/// </summary>
public sealed class AlarmTracker : ObservableObject
{
    public const int DisplayLimit = 500;
    private const int PendingLimit = 5000;
    private readonly UserAccount _currentUser;
    private readonly AlarmStore _store;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    // 多台设备都可能要求保存报警；一次只让一个 FlushAsync 整理、写入共用的待保存列表。
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    // 同一设备、同一点、同一种异常，只保留一个尚未结束的记录。
    private readonly Dictionary<(long DeviceId, long? PointId, AlarmKind Kind), AlarmRecord> _active = new();
    private readonly ObservableCollection<AlarmRecord> _records = new();
    // 保存失败后保留最新版本；按 EventId 重试覆盖，不会反复 INSERT 出重复报警。
    private readonly Dictionary<string, AlarmRecord> _pending = new();
    private int _droppedUpdates;
    private string _persistenceStatusText = "本次运行尚无报警；过去的记录请到历史记录 → 报警历史查询。";

    public ReadOnlyObservableCollection<AlarmRecord> Records { get; }
    public string PersistenceStatusText
    {
        get => _persistenceStatusText;
        private set => SetProperty(ref _persistenceStatusText, value);
    }
    public int PendingSaveCount => _pending.Count;
    // 数据库长时间不可写且超过待保存上限时，用它提醒用户已有更新丢失。
    public int DroppedUpdateCount => _droppedUpdates;
    public int ActiveCount => _active.Count;

    /// <summary>
    /// DeviceRuntimeManager 创建这里，传当前用户和 AlarmStore。准备只读显示列表；此时不生成报警，也不启动采集。
    /// </summary>
    public AlarmTracker(UserAccount currentUser, AlarmStore store)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Records = new ReadOnlyObservableCollection<AlarmRecord>(_records);
        // 创建对象只准备容器，不查历史、不伪造当前设备的报警状态。
    }

    /// <summary>
    /// DeviceSamplingViewModel 每轮查到最新配置后调用。
    /// 点被删除、限值关闭或改动时，结束旧规则的监测，不能把它当作“数值恢复正常”。
    /// </summary>
    public async Task ReconcileConfigurationAsync(DeviceInfo device, IReadOnlyList<PointInfo> points)
    {
        _dispatcher.VerifyAccess();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (AlarmRecord alarm in _active.Values.Where(item => item.DeviceId == device.Id).ToArray())
        {
            if (!alarm.PointId.HasValue) continue;
            PointInfo? point = points.FirstOrDefault(item => item.Id == alarm.PointId.Value);
            bool removed = point is null;
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
    /// 读取成功后由采集任务调用。只有这条路会比较上下限。
    /// 先恢复该点的采集异常，再检查数值；等于限值不算越限。
    /// </summary>
    public async Task ReportReadingAsync(DeviceInfo device, PointReadResult result)
    {
        _dispatcher.VerifyAccess();
        PointInfo point = result.Point;
        Recover(device.Id, point.Id, AlarmKind.AcquisitionFailure, result.ReadAt, "此点已重新读取成功。");
        EvaluateLimit(device, result, AlarmKind.HighLimit, point.UpperLimit,
            point.UpperLimit.HasValue && result.Value > point.UpperLimit.Value);
        EvaluateLimit(device, result, AlarmKind.LowLimit, point.LowerLimit,
            point.LowerLimit.HasValue && result.Value < point.LowerLimit.Value);
        await FlushAsync();
    }

    // ReportReadingAsync 调用；上下限共用这段，不需要写两套重复的记录逻辑。
    private void EvaluateLimit(DeviceInfo device, PointReadResult result, AlarmKind kind, int? limit, bool outside)
    {
        if (!outside)
        {
            Recover(device.Id, result.Point.Id, kind, result.ReadAt, "有效读数已回到该限值允许的范围。");
            return;
        }
        string direction = kind == AlarmKind.HighLimit ? "高于上限" : "低于下限";
        Observe(device.Id, device.Name, result.Point.Id, result.Point.Name, kind,
            $"读数 {result.Value} {direction} {limit}。", result.Value, limit, result.ReadAt);
    }

    /// <summary>
    /// 某点读取失败时调用。只记采集异常，不使用旧值判断上下限，也不恢复原来的数值报警。
    /// 取消读取不走这里，用户主动停止不产生失败报警。
    /// </summary>
    public async Task ReportPointFailureAsync(DeviceInfo device, PointInfo point, string message)
    {
        _dispatcher.VerifyAccess();
        Observe(device.Id, device.Name, point.Id, point.Name, AlarmKind.AcquisitionFailure,
            message, null, null, DateTimeOffset.UtcNow);
        await FlushAsync();
    }

    // 查配置失败等整台任务无法继续的情况，由 RunAsync 的最外层 catch 调用。
    public async Task ReportDeviceFailureAsync(long deviceId, string deviceName, string message)
    {
        _dispatcher.VerifyAccess();
        Observe(deviceId, deviceName, null, string.Empty, AlarmKind.AcquisitionFailure,
            message, null, null, DateTimeOffset.UtcNow);
        await FlushAsync();
    }

    /// <summary>
    /// 越限或采集失败时由本类调用。按设备 Id/点 Id/报警类型查活动字典，已有就更新次数，没有才创建新事件。
    /// 随后 Publish 更新显示并加入待保存列表，不在这里直接写数据库。
    /// </summary>
    private void Observe(long deviceId, string deviceName, long? pointId, string pointName,
        AlarmKind kind, string message, int? value, int? limit, DateTimeOffset time)
    {
        var key = (deviceId, pointId, kind);
        AlarmRecord record;
        if (_active.TryGetValue(key, out AlarmRecord? existing))
        {
            // 电脑时钟回拨时，记录的最后时间不能跑到首次发生时间前面。
            if (time < existing.LastSeenAt) time = existing.LastSeenAt;
            // with 复制记录并更新少数字段；EventId、首次发生时间不变，所以仍是同一条报警。
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
        _active[key] = record;
        Publish(record);
        OnPropertyChanged(nameof(ActiveCount));
    }

    /// <summary>
    /// 成功读取或数值回到范围时调用。活动字典里有同类异常才结束它，没有则什么都不新增。
    /// </summary>
    private void Recover(long deviceId, long? pointId, AlarmKind kind, DateTimeOffset time, string reason)
    {
        if (_active.TryGetValue((deviceId, pointId, kind), out AlarmRecord? record))
            End(record, AlarmState.Recovered, time, reason);
    }

    /// <summary>
    /// 恢复或停止监测时调用。移出活动字典，带上结束状态/时间，再通过 Publish 更新同一 EventId 的记录。
    /// </summary>
    private void End(AlarmRecord record, AlarmState state, DateTimeOffset time, string reason)
    {
        if (time < record.LastSeenAt) time = record.LastSeenAt;
        _active.Remove((record.DeviceId, record.PointId, record.Kind));
        Publish(record with { State = state, EndedAt = time, LastSeenAt = time, Message = record.Message + " " + reason });
        OnPropertyChanged(nameof(ActiveCount));
    }

    /// <summary>
    /// 采集任务 finally 调用。仍未恢复的报警标记为停止监测，而不是恢复正常。
    /// 已经恢复的记录不再改变；不清空屏幕记录，也不删除数据库历史。
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
    /// 新报警、重复、恢复和停止监测共用这里。更新本次运行的显示列表，并把最新版本放入待保存字典。
    /// 显示最多 500 条，待保存最多 5000 个事件；不是每轮都 INSERT 一条新报警。
    /// </summary>
    private void Publish(AlarmRecord record)
    {
        int index = -1;
        for (int i = 0; i < _records.Count; i++)
            if (_records[i].EventId == record.EventId) { index = i; break; }
        if (index >= 0) _records[index] = record;
        else
        {
            // 已被最近 500 条挤出去的旧报警又更新时，也按首次发生时间插回，不能冒充新报警。
            int insertAt = 0;
            while (insertAt < _records.Count && _records[insertAt].StartedAt >= record.StartedAt) insertAt++;
            _records.Insert(insertAt, record);
        }
        while (_records.Count > DisplayLimit) _records.RemoveAt(_records.Count - 1);
        // 极端情况下数据库一直不可写，待保存集合也不能无限长；有丢失会明确报数，不假装保存成功。
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
    /// 每次发生/更新/结束报警后调用，关窗也会最后重试一次。
    /// 多台设备共用一个保存队列：后台只写数据库，界面集合始终在界面线程修改。
    /// </summary>
    public async Task FlushAsync()
    {
        _dispatcher.VerifyAccess();
        await _saveGate.WaitAsync();
        try
        {
            while (_pending.Count > 0)
            {
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
                    return; // 保存失败不改读数、不结束采集，也不把这次错误再递归记成设备报警。
                }
                foreach (AlarmRecord record in batch)
                    if (_pending.TryGetValue(record.EventId, out AlarmRecord? latest) && ReferenceEquals(latest, record))
                        _pending.Remove(record.EventId);
                OnPropertyChanged(nameof(PendingSaveCount));
            }
            if (_records.Count > 0)
                PersistenceStatusText = $"报警记录已保存；本页保留本次运行最近 {DisplayLimit} 条，完整记录到报警历史查询。" + DroppedText();
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>
    /// 生成保存提示时调用。曾超过待保存上限才追加丢失更新数；它只是说明，不执行重试。
    /// </summary>
    private string DroppedText() => _droppedUpdates == 0 ? string.Empty
        : $" 数据库长时间不可写，待保存上限已超出，有 {_droppedUpdates} 次记录更新未能保留。";
}

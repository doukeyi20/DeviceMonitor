// 维护报警触发、持续、恢复状态，并保存报警更新。
using System;
using DeviceMonitor;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public sealed class AlarmMonitor : ObservableObject
{
    public const int DisplayLimit = 500;
    private const int PendingLimit = 5000;
    private readonly UserAccount _currentUser;
    private readonly AlarmStore _alarmStore;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // 多台设备可能先后提交报警，保存时只允许一批进入数据库。
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    // 按设备、点位和类别合并持续异常，恢复后再结束原事件。
    private readonly Dictionary<(long DeviceId, long? PointId, AlarmKind Kind), AlarmRecord> _activeAlarms = new();

    // 界面最近记录与待保存更新分别限量，不能用界面裁剪代替持久化。
    private readonly ObservableCollection<AlarmRecord> _recentAlarms = new();

    // 同事件只保留最新待存版本；数据库失败时留待后续补试。
    private readonly Dictionary<string, AlarmRecord> _pendingSaves = new();

    private int _droppedUpdateCount;
    private string _persistenceStatusText = string.Empty;

    public ReadOnlyObservableCollection<AlarmRecord> Records
    {
        get;
    }
    public string PersistenceStatusText
    {
        get => _persistenceStatusText;
        private set => SetProperty(ref _persistenceStatusText, value);
    }
    public int PendingSaveCount => _pendingSaves.Count;

    public int DroppedUpdateCount => _droppedUpdateCount;
    public int ActiveCount => _activeAlarms.Count;

    public AlarmMonitor(UserAccount currentUser, AlarmStore store)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _alarmStore = store ?? throw new ArgumentNullException(nameof(store));
        Records = new ReadOnlyObservableCollection<AlarmRecord>(_recentAlarms);
    }

    /// <summary>找这个点尚未结束的报警；整台设备的采集异常也算，读取失败优先显示。</summary>
    public AlarmRecord? GetCurrentAlarm(long deviceId, long pointId)
    {
        _dispatcher.VerifyAccess();

        if (_activeAlarms.TryGetValue((deviceId, pointId, AlarmKind.AcquisitionFailure), out AlarmRecord? failure))
            return failure;
        if (_activeAlarms.TryGetValue((deviceId, null, AlarmKind.AcquisitionFailure), out AlarmRecord? deviceFailure))
            return deviceFailure;
        if (_activeAlarms.TryGetValue((deviceId, pointId, AlarmKind.HighLimit), out AlarmRecord? high))
            return high;
        return _activeAlarms.TryGetValue((deviceId, pointId, AlarmKind.LowLimit), out AlarmRecord? low) ? low : null;
    }

    /// <summary>解除读取故障报警，并检查本轮读数是否越限。</summary>
    public async Task ReportReadingsAsync(DeviceInfo device, IReadOnlyList<PointReadResult> results)
    {
        _dispatcher.VerifyAccess();
        // 成功读取先解除通信异常，再独立判断上下限。
        foreach (PointReadResult result in results)
        {
            PointInfo point = result.Point;
            RecoverAlarm(device.Id, point.Id, AlarmKind.AcquisitionFailure, result.ReadAt, "此点已重新读取成功。");
            EvaluateLimit(device, result, AlarmKind.HighLimit, point.UpperLimit,
                point.UpperLimit.HasValue && result.Value > point.UpperLimit.Value);
            EvaluateLimit(device, result, AlarmKind.LowLimit, point.LowerLimit,
                point.LowerLimit.HasValue && result.Value < point.LowerLimit.Value);
        }
        await FlushAsync();
    }

    private void EvaluateLimit(DeviceInfo device, PointReadResult result, AlarmKind kind, double? limit, bool outside)
    {
        // 回到允许范围只结束对应类别，不影响其他类别的活动报警。
        if (!outside)
        {
            RecoverAlarm(device.Id, result.Point.Id, kind, result.ReadAt, "有效读数已回到该限值允许的范围。");
            return;
        }
        string direction = kind == AlarmKind.HighLimit ? "高于上限" : "低于下限";

        RecordOrUpdateAlarm(device.Id, device.Name, result.Point.Id, result.Point.Name, kind,
            $"读数 {result.ValueText} {direction} {NumericValue.Format(limit!.Value, result.Point.Unit)}。",
            result.Value, limit, result.ReadAt, result.Point.Unit);
    }

    /// <summary>为本批点位登记采集故障并保存报警变化。</summary>
    public async Task ReportFailuresAsync(DeviceInfo device, IReadOnlyList<PointInfo> points, string message)
    {
        _dispatcher.VerifyAccess();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (PointInfo point in points)
            RecordOrUpdateAlarm(device.Id, device.Name, point.Id, point.Name, AlarmKind.AcquisitionFailure,
                message, null, null, now);
        await FlushAsync();
    }

    public async Task ReportDeviceFailureAsync(long deviceId, string deviceName, string message)
    {
        _dispatcher.VerifyAccess();
        RecordOrUpdateAlarm(deviceId, deviceName, null, string.Empty, AlarmKind.AcquisitionFailure,
            message, null, null, DateTimeOffset.UtcNow);
        await FlushAsync();
    }

    /// <summary>同类异常已存在就更新次数和数值，否则新建报警；再更新显示和待保存列表。</summary>
    private void RecordOrUpdateAlarm(long deviceId, string deviceName, long? pointId, string pointName,
        AlarmKind kind, string message, double? value, double? limit, DateTimeOffset time, string unit = "")
    {
        var key = (deviceId, pointId, kind);
        AlarmRecord record;
        if (_activeAlarms.TryGetValue(key, out AlarmRecord? existing))
        {
            if (time < existing.LastSeenAt)
                time = existing.LastSeenAt;

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
                Unit = unit,
                StartedAt = time,
                LastSeenAt = time,
                Occurrences = 1
            };
        }

        _activeAlarms[key] = record;
        UpdateDisplayAndPendingSave(record);
        OnPropertyChanged(nameof(ActiveCount));
    }

    /// <summary>找到同类活动报警就标记恢复；没有旧报警就不做处理。</summary>
    private void RecoverAlarm(long deviceId, long? pointId, AlarmKind kind, DateTimeOffset time, string reason)
    {
        if (_activeAlarms.TryGetValue((deviceId, pointId, kind), out AlarmRecord? record))
            EndAlarm(record, AlarmState.Recovered, time, reason);
    }

    /// <summary>结束一条活动报警：移出活动字典，再更新结束状态、时间和显示。</summary>
    private void EndAlarm(AlarmRecord record, AlarmState state, DateTimeOffset time, string reason)
    {
        if (time < record.LastSeenAt)
            time = record.LastSeenAt;
        _activeAlarms.Remove((record.DeviceId, record.PointId, record.Kind));
        UpdateDisplayAndPendingSave(record with
        {
            State = state,
            EndedAt = time,
            LastSeenAt = time,
            Message = record.Message + " " + reason
        });
        OnPropertyChanged(nameof(ActiveCount));
    }

    /// <summary>设备停止采集后，把未恢复的报警标记为“不再监测”，不是“已恢复”。</summary>
    public async Task EndDeviceMonitoringAsync(long deviceId)
    {
        _dispatcher.VerifyAccess();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (AlarmRecord record in _activeAlarms.Values.Where(item => item.DeviceId == deviceId).ToArray())
            EndAlarm(record, AlarmState.NotMonitoring, now, "采集任务已结束；停止监测，不代表异常已恢复。");
        await FlushAsync();
    }

    /// <summary>更新最近报警列表，并把这条报警的最新版本放入待保存字典。</summary>
    private void UpdateDisplayAndPendingSave(AlarmRecord record)
    {
        int index = -1;
        for (int i = 0; i < _recentAlarms.Count; i++)
        {
            if (_recentAlarms[i].EventId != record.EventId)
                continue;
            index = i;
            break;
        }
        if (index >= 0)
        {
            _recentAlarms[index] = record;
        }
        else
        {
            // 旧事件重新进入最近列表时，仍按首次发生时间排序。
            int insertAt = 0;
            while (insertAt < _recentAlarms.Count && _recentAlarms[insertAt].StartedAt >= record.StartedAt)
                insertAt++;
            _recentAlarms.Insert(insertAt, record);
        }
        while (_recentAlarms.Count > DisplayLimit)
            _recentAlarms.RemoveAt(_recentAlarms.Count - 1);

        if (!_pendingSaves.ContainsKey(record.EventId) && _pendingSaves.Count >= PendingLimit)
        {
            _pendingSaves.Remove(_pendingSaves.Keys.First());
            _droppedUpdateCount++;
            AppLog.Warning("报警缓存已满", $"丢失更新={_droppedUpdateCount}",
                throttleKey: "alarm-pending-full");
        }
        _pendingSaves[record.EventId] = record;
        OnPropertyChanged(nameof(PendingSaveCount));
    }

    /// <summary>保存待处理报警快照；失败保留，成功仅移除已保存版本。</summary>
    public async Task FlushAsync()
    {
        _dispatcher.VerifyAccess();
        if (_pendingSaves.Count == 0)
            return;

        // 已生成的报警仍需尝试保存，因此不使用采集停止信号取消这批写入。
        await _saveGate.WaitAsync();
        try
        {
            // 后台仅保存快照，避免遍历仍在更新的字典。
            AlarmRecord[] batch = _pendingSaves.Values.ToArray();

            if (batch.Length == 0)
                return;

            try
            {
                await Task.Run(() => _alarmStore.SaveRecords(_currentUser, batch));
            }
            catch (Exception ex)
            {
                AppLog.Error("报警保存失败", $"待存={_pendingSaves.Count}", ex, "alarm-save");
                PersistenceStatusText = $"报警保存失败：{ex.Message}。待保存 {_pendingSaves.Count} 条；后续会重试。" + GetDroppedUpdatesMessage();
                return;
            }

            // 仅移除已保存版本，保留等待期间产生的新更新。

            foreach (AlarmRecord record in batch)
            {
                if (_pendingSaves.TryGetValue(record.EventId, out AlarmRecord? latest) && ReferenceEquals(latest, record))
                    _pendingSaves.Remove(record.EventId);
            }

            OnPropertyChanged(nameof(PendingSaveCount));
            PersistenceStatusText = (_pendingSaves.Count == 0 ? "报警记录已保存。"
                : $"本批报警已保存，另有 {_pendingSaves.Count} 条更新待保存。") + GetDroppedUpdatesMessage();
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>有报警更新丢失时，在保存提示后补上丢失次数。</summary>
    private string GetDroppedUpdatesMessage() => _droppedUpdateCount == 0 ? string.Empty
        : $" 数据库长时间不可写，待保存上限已超出，有 {_droppedUpdateCount} 次记录更新未能保留。";
}

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一条报警记录。相同异常持续发生时更新原记录，不每轮新增。
/// </summary>
public sealed record AlarmRecord
{
    public long Id { get; init; }
    // 同一条报警更新时保留这个编号；数据库用它找到原记录。
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public long DeviceId { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public long? PointId { get; init; }
    public string PointName { get; init; } = string.Empty;
    public AlarmKind Kind { get; init; }
    public AlarmState State { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? Value { get; init; }
    public int? Limit { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int Occurrences { get; init; } = 1;

    public AlarmCategory Category => Kind == AlarmKind.AcquisitionFailure
        ? AlarmCategory.Acquisition : AlarmCategory.Value;
    public string CategoryText => Category == AlarmCategory.Value ? "数值报警" : "采集异常";
    public string KindText => Kind switch
    {
        AlarmKind.HighLimit => "超过上限",
        AlarmKind.LowLimit => "低于下限",
        AlarmKind.AcquisitionFailure => "采集失败",
        _ => "未知报警"
    };
    // 历史中的“未恢复”只说明最后保存的结果，不代表设备现在在线或仍在采集。
    public string StateText => State switch
    {
        AlarmState.Active => "未恢复",
        AlarmState.Recovered => "已恢复",
        AlarmState.NotMonitoring => "不再监测",
        _ => "未知状态"
    };
    public DateTime StartedAtLocal => StartedAt.LocalDateTime;
    public DateTime LastSeenAtLocal => LastSeenAt.LocalDateTime;
    public DateTime? EndedAtLocal => EndedAt?.LocalDateTime;
}

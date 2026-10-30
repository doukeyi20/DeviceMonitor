// 定义报警记录、分类、状态和查询条件。
namespace DeviceMonitor.Model;

/// <summary>一条报警记录。相同异常持续发生时更新原记录，不每轮新增。</summary>
public sealed record AlarmRecord
{
    public long Id { get; init; }

    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public long DeviceId { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public long? PointId { get; init; }
    public string PointName { get; init; } = string.Empty;
    public AlarmKind Kind { get; init; }
    public AlarmState State { get; init; }
    public string Message { get; init; } = string.Empty;
    public double? Value { get; init; }
    public double? Limit { get; init; }
    public string Unit { get; init; } = "";
    public string ValueText => Value.HasValue ? NumericValue.Format(Value.Value, Unit) : "—";
    public string LimitText => Limit.HasValue ? NumericValue.Format(Limit.Value, Unit) : "—";
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

/// <summary>记住本次报警查询的条件，翻页和导出继续用这份条件。</summary>
public sealed class AlarmFilter
{
    public long? DeviceId
    {
        get;
    }
    public AlarmCategory? Category
    {
        get;
    }
    public DateTimeOffset? StartUtc { get; }
    public DateTimeOffset? EndUtcExclusive
    {
        get;
    }

    public AlarmFilter(long? deviceId = null, AlarmCategory? category = null,
        DateTimeOffset? startUtc = null, DateTimeOffset? endUtcExclusive = null)
    {
        if (deviceId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(deviceId));
        if (category.HasValue && !Enum.IsDefined(category.Value))
            throw new ArgumentOutOfRangeException(nameof(category));
        if (startUtc.HasValue && endUtcExclusive.HasValue && startUtc >= endUtcExclusive)
            throw new ArgumentException("开始时间必须早于结束时间。");
        DeviceId = deviceId;
        Category = category;
        StartUtc = startUtc?.ToUniversalTime();
        EndUtcExclusive = endUtcExclusive?.ToUniversalTime();
    }
}

/// <summary>一页报警查询结果：本页记录、总数、页码、最大记录编号。</summary>
public sealed class AlarmPage
{
    public IReadOnlyList<AlarmRecord> Records
    {
        get;
    }
    public long TotalRecords
    {
        get;
    }
    public int PageNumber
    {
        get;
    }
    public long SnapshotId
    {
        get;
    }

    public AlarmPage(IReadOnlyList<AlarmRecord> records, long totalRecords, int pageNumber, long snapshotId)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        Records = records;
        TotalRecords = totalRecords;
        PageNumber = pageNumber;
        SnapshotId = snapshotId;
    }
}

/// <summary>报警原因：超过上限、低于下限、采集失败。恢复情况另看 AlarmState。</summary>
public enum AlarmKind
{
    HighLimit = 1,
    LowLimit = 2,
    AcquisitionFailure = 3
}

/// <summary>报警的大类：数值报警、采集异常。具体原因看 AlarmKind。</summary>
public enum AlarmCategory
{
    Value = 1,
    Acquisition = 2
}

/// <summary>报警状态：未恢复、已恢复、停止监测。</summary>
public enum AlarmState
{
    Active = 1,
    Recovered = 2,

    NotMonitoring = 3
}

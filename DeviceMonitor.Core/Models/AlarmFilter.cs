namespace DeviceMonitor.Core.Models;

/// <summary>
/// 记住本次报警查询的条件，翻页和导出继续用这份条件。
/// 时间筛选看报警首次发生时间，不看重复或恢复时间。
/// </summary>
public sealed class AlarmFilter
{
    public long? DeviceId { get; } // null 表示全部设备。
    public AlarmCategory? Category { get; } // null 表示全部类别。
    public DateTimeOffset? StartUtc { get; }
    public DateTimeOffset? EndUtcExclusive { get; } // 只取早于这个时间的报警，null 表示不限制。

    // 检查条件后保存下来，时间统一转为 UTC。
    public AlarmFilter(long? deviceId = null, AlarmCategory? category = null,
        DateTimeOffset? startUtc = null, DateTimeOffset? endUtcExclusive = null)
    {
        if (deviceId is <= 0) throw new ArgumentOutOfRangeException(nameof(deviceId));
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

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 报警查询按钮确定的设备/类别/发生时间范围，翻页和导出继续用它。
/// 时间按首次发生筛选，不是最近重复或恢复时间；本对象不判断报警。
/// </summary>
public sealed class AlarmFilter
{
    public long? DeviceId { get; }
    public AlarmCategory? Category { get; }
    public DateTimeOffset? StartUtc { get; }
    public DateTimeOffset? EndUtcExclusive { get; }

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

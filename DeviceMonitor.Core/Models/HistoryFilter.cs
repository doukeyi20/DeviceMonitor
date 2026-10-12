using System;

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一次查询按钮确定的设备/名称/时间条件，HistoryViewModel 交给 HistoryStore。
/// 翻页、趋势和导出沿用同一份；之后改输入框不会暗中改变这份条件。
/// </summary>
public sealed class HistoryFilter
{
    public long? DeviceId { get; }
    public string PointName { get; }
    public DateTimeOffset? StartUtc { get; }
    public DateTimeOffset? EndUtcExclusive { get; }

    public HistoryFilter(long? deviceId = null, string? pointName = null,
        DateTimeOffset? startUtc = null, DateTimeOffset? endUtcExclusive = null)
    {
        if (deviceId is <= 0) throw new ArgumentOutOfRangeException(nameof(deviceId));
        if (startUtc.HasValue && endUtcExclusive.HasValue && startUtc >= endUtcExclusive)
            throw new ArgumentException("开始时间不能晚于结束时间。");
        DeviceId = deviceId; // null 表示全部设备。
        PointName = pointName?.Trim() ?? string.Empty;
        StartUtc = startUtc?.ToUniversalTime();
        EndUtcExclusive = endUtcExclusive?.ToUniversalTime(); // 只取早于这个时间点的记录。
    }
}

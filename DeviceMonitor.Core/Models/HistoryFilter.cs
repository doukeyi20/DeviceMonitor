using System;

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 记住本次历史查询的条件，翻页、趋势和导出继续用它。
/// 改输入框不会立即改变这份条件，要重新点击查询。
/// </summary>
public sealed class HistoryFilter
{
    public long? DeviceId { get; }
    public string PointName { get; } // 按名称包含这段文字来筛选，空白表示不限点位名。
    public DateTimeOffset? StartUtc { get; }
    public DateTimeOffset? EndUtcExclusive { get; }

    // 检查条件、去掉名称前后空格，再把时间转成 UTC 保存。
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

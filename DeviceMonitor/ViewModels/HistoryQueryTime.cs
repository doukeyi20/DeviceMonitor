using System.Globalization;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 历史查询共用的时间工具：检查输入，
/// 如果有查询条件有时间范围，就需要调用这个类转换时间，然后再生成查询条件对象
/// </summary>
internal static class HistoryQueryTime
{
    // 组合起止日期和时间，检查先后顺序，返回查询范围。
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtcExclusive) ReadRange(
        DateTime? startDate, string startText, DateTime? endDate, string endText)
    {
        DateTime start = ReadLocalTime(startDate, startText, "开始");
        DateTime end = ReadLocalTime(endDate, endText, "结束");
        if (start > end)
            throw new ArgumentException("开始时间不能晚于结束时间。");

        // 结束填 12:00:00 时，12:00:00.500 也要包含：查询用“小于下一秒”。
        return (ToUtc(start), ToUtc(end.AddSeconds(1)));
    }

    // 检查日期和时间格式，再拼成一个本机日期时间。
    private static DateTime ReadLocalTime(DateTime? date, string text, string label)
    {
        if (!date.HasValue)
            throw new ArgumentException($"请选择{label}日期。");
        if (!TimeSpan.TryParseExact(text?.Trim(), new[] { "hh\\:mm\\:ss", "hh\\:mm" },
                CultureInfo.InvariantCulture, out TimeSpan time) || time.TotalDays >= 1)
            throw new ArgumentException($"{label}时间请写成 HH:mm:ss，例如 08:30:00。");
        return DateTime.SpecifyKind(date.Value.Date + time, DateTimeKind.Unspecified);
    }

    // 按本机时区转换为 UTC，方便和数据库时间比较。
    private static DateTimeOffset ToUtc(DateTime local)
    {
        // 夏令时切换可能让一个时间不存在或重复；这时请用户换时间，不擅自猜。
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
            throw new ArgumentException("这个当地时间不存在或有两种解释，请避开夏令时切换时刻。");
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}

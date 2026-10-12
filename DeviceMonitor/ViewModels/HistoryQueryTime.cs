using System.Globalization;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 采集历史和报警历史共用的时间输入检查：本机日期/时间转成 UTC 查询范围。
/// SQL 用左闭右开范围，输入的结束秒也要完整包含；这里不查数据库。
/// </summary>
internal static class HistoryQueryTime
{
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

    private static DateTime ReadLocalTime(DateTime? date, string text, string label)
    {
        if (!date.HasValue)
            throw new ArgumentException($"请选择{label}日期。");
        if (!TimeSpan.TryParseExact(text?.Trim(), new[] { "hh\\:mm\\:ss", "hh\\:mm" },
                CultureInfo.InvariantCulture, out TimeSpan time) || time.TotalDays >= 1)
            throw new ArgumentException($"{label}时间请写成 HH:mm:ss，例如 08:30:00。");
        return DateTime.SpecifyKind(date.Value.Date + time, DateTimeKind.Unspecified);
    }

    private static DateTimeOffset ToUtc(DateTime local)
    {
        // 夏令时切换可能让一个时间不存在或重复；这时请用户换时间，不擅自猜。
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
            throw new ArgumentException("这个当地时间不存在或有两种解释，请避开夏令时切换时刻。");
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}

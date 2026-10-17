namespace DeviceMonitor.Models;

// 实时曲线里的一个点，不是数据库历史记录；Value 为 null 表示这次没读到，要在这里断线。
public sealed record LiveTrendSample(DateTimeOffset ReadAt, double? Value);

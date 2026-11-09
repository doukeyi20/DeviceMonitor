// 界面使用的读取状态和曲线样本；这里不执行设备或数据库操作。

namespace DeviceMonitor.ViewModel;

/// <summary>一次读取的显示状态，单次测试和监控行共用；不表示设备在线，也不控制采集循环。</summary>
public enum SingleReadState
{
    NotRead,
    Reading,
    Succeeded,
    TimedOut,
    Failed,
    Canceled
}

/// <summary>曲线上的一个时间点，Elapsed 从本次采集开始计算，Value 为 null 时留出断点。</summary>
public sealed record LiveTrendSample(TimeSpan Elapsed, double? Value);

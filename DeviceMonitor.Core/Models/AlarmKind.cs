namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一次报警的具体原因：超过上限、低于下限、采集失败；不表示是否已经恢复。
/// </summary>
public enum AlarmKind
{
    HighLimit = 1,
    LowLimit = 2,
    AcquisitionFailure = 3
}

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 报警原因：超过上限、低于下限、采集失败。恢复情况另看 AlarmState。
/// </summary>
public enum AlarmKind
{
    HighLimit = 1,
    LowLimit = 2,
    AcquisitionFailure = 3
}

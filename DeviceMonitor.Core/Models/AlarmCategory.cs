namespace DeviceMonitor.Core.Models;

/// <summary>
/// 报警的大类：数值报警、采集异常。具体原因看 AlarmKind。
/// </summary>
public enum AlarmCategory
{
    Value = 1,
    Acquisition = 2
}

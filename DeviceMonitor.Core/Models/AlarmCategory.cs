namespace DeviceMonitor.Core.Models;

/// <summary>
/// 报警筛选用的大类：数值报警和采集异常。具体上限/下限/失败由 AlarmKind 区分。
/// </summary>
public enum AlarmCategory
{
    Value = 1,
    Acquisition = 2
}

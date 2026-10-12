namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一次报警现在保存的结束状态：仍未恢复、已恢复或停止监测。
/// 停止采集不代表数值恢复；历史状态也不证明设备当前在线。
/// </summary>
public enum AlarmState
{
    Active = 1,
    Recovered = 2,
    // 用户停采不等于数值恢复，单独记为“不再监测”。
    NotMonitoring = 3
}

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 报警状态：未恢复、已恢复、停止监测。
/// 停止监测不等于恢复正常，也不能用历史状态判断设备现在是否在线。
/// </summary>
public enum AlarmState
{
    Active = 1,
    Recovered = 2,
    // 用户停采不等于数值恢复，单独记为“不再监测”。
    NotMonitoring = 3
}

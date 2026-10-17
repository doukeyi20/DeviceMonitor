namespace DeviceMonitor.Models;

/// <summary>单次测试区域的显示状态；不是设备在线状态，也不控制连续采集。</summary>
public enum SingleReadState
{
    NotRead,
    Reading,
    Succeeded,
    TimedOut,
    Failed,
    Canceled
}

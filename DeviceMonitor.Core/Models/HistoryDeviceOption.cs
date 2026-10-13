namespace DeviceMonitor.Core.Models;

/// <summary>设备筛选下拉框的一项，来自历史表，不要求这台设备的配置仍然存在。</summary>
public sealed class HistoryDeviceOption
{
    public long? DeviceId { get; } // null 表示“全部设备”，有编号就按编号查。
    public string DisplayName { get; } // 下拉框显示的设备名称和编号。

    // 保存编号，并把名称拼成下拉框里显示的文字。
    public HistoryDeviceOption(long? deviceId, string name)
    {
        DeviceId = deviceId;
        DisplayName = deviceId.HasValue ? $"{name}（#{deviceId}）" : name;
    }
}

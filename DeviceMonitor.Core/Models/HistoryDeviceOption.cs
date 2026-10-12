namespace DeviceMonitor.Core.Models;

/// <summary>设备筛选下拉框的一项，来自历史表，不要求这台设备的配置仍然存在。</summary>
public sealed class HistoryDeviceOption
{
    public long? DeviceId { get; }
    public string DisplayName { get; }

    public HistoryDeviceOption(long? deviceId, string name)
    {
        DeviceId = deviceId;
        DisplayName = deviceId.HasValue ? $"{name}（#{deviceId}）" : name;
    }
}

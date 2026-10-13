namespace DeviceMonitor.ViewModels;

/// <summary>
/// 临时装设备操作的确认信息，后台把选择写回 Confirmed。
/// 默认 false，用户没确认就不继续。
/// </summary>
public sealed class DeviceConfirmationRequest(string message, string title)
{
    public string Message { get; } = message;
    public string Title { get; } = title;
    public bool Confirmed { get; set; }
}

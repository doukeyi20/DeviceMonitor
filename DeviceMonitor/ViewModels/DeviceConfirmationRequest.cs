namespace DeviceMonitor.ViewModels;

/// <summary>
/// 设备停用/删除前的确认容器：ViewModel 给出提示。
/// Confirmed 默认 false，没人订阅或用户取消时不进行危险操作。
/// </summary>
public sealed class DeviceConfirmationRequest(string message, string title)
{
    public string Message { get; } = message;
    public string Title { get; } = title;
    public bool Confirmed { get; set; }
}

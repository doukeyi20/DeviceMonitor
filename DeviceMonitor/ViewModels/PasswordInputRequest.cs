namespace DeviceMonitor.ViewModels;

/// <summary>
/// 临时装旧密码、新密码和确认密码，供本次保存使用，不用于长期绑定。
/// </summary>
public sealed class PasswordInputRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string Confirmation { get; set; } = string.Empty;
}

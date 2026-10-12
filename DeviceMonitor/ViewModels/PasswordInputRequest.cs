namespace DeviceMonitor.ViewModels;

/// <summary>
/// 密码保存命令临时向 PasswordWindow 要原密码/新密码/确认值的容器。
/// 不是数据表或长期绑定属性；保存结束清引用，窗口成功或关闭时清控件。
/// </summary>
public sealed class PasswordInputRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string Confirmation { get; set; } = string.Empty;
}

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 临时装密码的对象，让 ViewModel 和界面后台来回传数据。
    /// </summary>
    public sealed class UserPasswordsRequest
    {
        public string Password { get; set; } = string.Empty;
        public string Confirmation { get; set; } = string.Empty;
    }

    /// <summary>
    /// 临时装确认信息：ViewModel 放提示，后台弹框后填回 Confirmed。
    /// 默认 false，用户没确认就不修改账户。
    /// </summary>
    public sealed class UserConfirmationRequest
    {
        public string Message { get; }
        public string Title { get; }
        public bool Confirmed { get; set; }

        // 命令先填入提示内容，后台弹窗后再写回 Confirmed。
        public UserConfirmationRequest(string message, string title)
        {
            Message = message;
            Title = title;
        }
    }
}

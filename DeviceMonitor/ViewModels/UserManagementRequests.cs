namespace DeviceMonitor.ViewModels
{

    /// <summary>
    ///定义了两个临时装数据的类，让 ViewModel 和界面后台来回传数据。
    ///第一个装“后台读到的密码”；第二个装“要显示的提示，以及用户是否确认”
    /// </summary>
    public sealed class UserPasswordsRequest
    {
        public string Password { get; set; } = string.Empty;
        public string Confirmation { get; set; } = string.Empty;
    }

    /// <summary>
    /// 用户启停前的确认容器：ViewModel 放提示内容，页面后台弹框后填 Confirmed。
    /// 默认 false；没有订阅者或用户取消时，不继续修改账户。
    /// </summary>
    public sealed class UserConfirmationRequest
    {
        public string Message { get; }
        public string Title { get; }
        public bool Confirmed { get; set; }

        public UserConfirmationRequest(string message, string title)
        {
            Message = message;
            Title = title;
        }
    }
}

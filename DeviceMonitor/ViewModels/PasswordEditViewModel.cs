using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 密码表单共用的数据源：不传操作员是改自己密码，传操作员就是管理员重置。
/// SaveAsync 请求窗口读密码，检查后在后台调用 UserStore；成功再请求窗口关闭。
/// </summary>
public partial class PasswordEditViewModel : ObservableObject
{
    private readonly UserAccount _currentUser;
    private readonly UserStore _userStore;
    private readonly UserAccount? _operatorToReset;
    private bool _isClosing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditInputs))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    [ObservableProperty]
    private string _pageMessage = string.Empty;

    public bool IsReset => _operatorToReset is not null;
    public bool ShowCurrentPassword => !IsReset;
    public bool CanEditInputs => !IsSaving && !_isClosing;
    public bool IsSaved { get; private set; }
    public string TargetUserName => (_operatorToReset ?? _currentUser).UserName;
    public string WindowTitle => IsReset ? "重置操作员密码" : "修改我的密码";
    public string Description => IsReset
        ? "只更换密码，不改变账户启停状态。已登录的会话不会被强制退出。"
        : "请先输入原密码。修改成功后，下次登录使用新密码，当前会话继续。";
    public string SaveButtonText => IsReset ? "重置密码" : "保存密码";

    public event Action<PasswordInputRequest>? PasswordsRequested;
    public event Action? PasswordsClearRequested;
    public event Action? SaveSucceeded;

    public PasswordEditViewModel(UserAccount currentUser, UserStore userStore, UserAccount? operatorToReset = null)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        if (!currentUser.IsEnabled)
            throw new UnauthorizedAccessException("当前账户已停用。");
        if (operatorToReset is not null &&
            (currentUser.Role != UserRole.Administrator || operatorToReset.Role != UserRole.Operator))
            throw new UnauthorizedAccessException("管理员只能在这里重置操作员密码。");
        _operatorToReset = operatorToReset;
    }

    /// <summary>
    /// 保存按钮 → SaveCommand → 这里。先让窗口读密码，再检查，最后交给 UserStore 改数据库。
    /// 哈希计算和数据库写入放后台；保存期间不允许关这个窗口，避免“取消”后其实已写入。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave())
            return;
        var input = new PasswordInputRequest();
        try
        {
            PageMessage = string.Empty;
            if (PasswordsRequested is null)
            {
                PageMessage = "密码窗口尚未准备好。";
                return;
            }
            // 调用 PasswordWindow.xaml.cs 的 ReadPasswords：
            // 从三个密码框读取原密码、新密码、确认密码，填回 input，再继续下面的检查和保存。
            PasswordsRequested.Invoke(input);
            if (!IsReset && string.IsNullOrWhiteSpace(input.OldPassword))
            {
                PageMessage = "请输入原密码。";
                return;
            }
            if (string.IsNullOrWhiteSpace(input.NewPassword) || input.NewPassword.Length < 6)
            {
                PageMessage = "新密码至少需要 6 个字符，不能全是空白。";
                return;
            }
            if (input.NewPassword != input.Confirmation)
            {
                PageMessage = "两次输入的新密码不一致。";
                return;
            }
            if (!IsReset && input.OldPassword == input.NewPassword)
            {
                PageMessage = "新密码不能和原密码相同。";
                return;
            }

            IsSaving = true;
            PageMessage = "正在保存，请稍候…";
            bool changed = await Task.Run(() => IsReset
                ? _userStore.TryResetOperatorPassword(_currentUser, _operatorToReset!.Id, input.NewPassword)
                : _userStore.TryChangePassword(_currentUser, input.OldPassword, input.NewPassword));
            IsSaved = changed;
            if (_isClosing)
                return; // 整个主窗口正在退出时，只等写入完成，不再向旧界面发消息。
            if (!changed)
            {
                PageMessage = IsReset
                    ? "未能重置：账户不存在、角色或管理员权限已变化。请刷新账户后重试。"
                    : "未能修改：原密码不正确，或账户状态、密码已经变化。";
                return;
            }

            PageMessage = "密码已保存，下次登录使用新密码。";
            // 调用 PasswordWindow.xaml.cs 的 ClearPasswords：清空窗口里的三个密码框。
            PasswordsClearRequested?.Invoke();
            IsSaving = false; // 先允许关闭，再发成功事件，否则窗口会拦住自己的关闭。
            // 调用 PasswordWindow.xaml.cs 的 CompleteSave：设置 DialogResult = true，关闭密码窗口。
            // 密码已经在上面通过 UserStore 保存，这里只负责关窗口。
            SaveSucceeded?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("保存密码失败", $"ActorId={_currentUser.Id}; TargetId={(_operatorToReset ?? _currentUser).Id}", ex);
            if (!_isClosing)
                PageMessage = "密码未能保存，请检查数据库是否可写后重试。可从右上角账户菜单查看运行日志。";
        }
        finally
        {
            // 只是释放字符串引用，不代表能把 .NET 字符串从内存中立即擦掉。
            input.OldPassword = input.NewPassword = input.Confirmation = string.Empty;
            IsSaving = false;
        }
    }

    /// <summary>
    /// 保存按钮的条件：窗口没退出、没在保存、也没成功提交过；避免重复写入。
    /// </summary>
    private bool CanSave() => !_isClosing && !IsSaving && !IsSaved;

    /// <summary>
    /// 主窗口收尾时调用。已经开始写数据库就等它完成，不做会导致结果不明确的中途取消。
    /// </summary>
    public async Task StopOperationsAsync()
    {
        _isClosing = true;
        OnPropertyChanged(nameof(CanEditInputs));
        SaveCommand.NotifyCanExecuteChanged();
        if (SaveCommand.ExecutionTask is { } saving)
            await saving;
    }

    /// <summary>
    /// PasswordWindow.OnClosed 调用。标记表单已关闭，旧命令不能再被执行；不取消已完成的保存。
    /// </summary>
    public void MarkClosed()
    {
        _isClosing = true;
        SaveCommand.NotifyCanExecuteChanged();
    }
}

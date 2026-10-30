// 处理个人密码修改和管理员重置密码。
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public partial class PasswordEditViewModel : ObservableObject
{
    private readonly UserAccount _currentUser;
    private readonly UserStore _userStore;
    private readonly UserAccount? _operatorToReset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditInputs))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    [ObservableProperty]
    private string _pageMessage = string.Empty;

    public bool IsReset => _operatorToReset is not null;
    public bool ShowCurrentPassword => !IsReset;
    public bool CanEditInputs => !IsSaving;
    public bool IsSaved
    {
        get; private set;
    }
    public string TargetUserName => (_operatorToReset ?? _currentUser).UserName;
    public string WindowTitle => IsReset ? "重置操作员密码" : "修改我的密码";
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

    /// <summary>读取并检查密码，在后台保存；保存期间不能关闭表单。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave())
            return;
        var input = new PasswordInputRequest();
        try
        {
            PageMessage = string.Empty;
            PasswordsRequested?.Invoke(input);
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
            if (!changed)
            {
                PageMessage = IsReset
                    ? "未能重置：账户不存在、角色或管理员权限已变化。请刷新账户后重试。"
                    : "未能修改：原密码不正确，或账户状态、密码已经变化。";
                return;
            }

            PageMessage = "密码已保存，下次登录使用新密码。";

            PasswordsClearRequested?.Invoke();
            IsSaving = false;

            SaveSucceeded?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("保存密码失败", $"操作者={_currentUser.Id} 目标用户={(_operatorToReset ?? _currentUser).Id}", ex);
            PageMessage = "密码未能保存，请检查数据库是否可写后重试。可从右上角账户菜单查看运行日志。";
        }
        finally
        {
            input.OldPassword = input.NewPassword = input.Confirmation = string.Empty;
            IsSaving = false;
        }
    }

    /// <summary>没在保存、没保存成功过，才允许提交；窗口会等保存结束再关闭。</summary>
    private bool CanSave() => !IsSaving && !IsSaved;
}

using System;
using DeviceMonitor.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 登录页数据源：登录、首次创建管理员。密码在点按钮时才向窗口读取。
    /// </summary>
    public partial class LoginViewModel : ObservableObject
    {
        private readonly UserStore _userStore;

        [ObservableProperty]
        private string _userName = string.Empty;

        [ObservableProperty]
        private string _adminUserName = string.Empty;

        private bool _isAdminSetupVisible;
        public bool IsAdminSetupVisible
        {
            get => _isAdminSetupVisible;
            private set
            {
                // 只有本类能切换面板，界面只读取状态。
                if (SetProperty(ref _isAdminSetupVisible, value))
                    OnPropertyChanged(nameof(IsLoginVisible));
            }
        }
        public bool IsLoginVisible => !IsAdminSetupVisible;

        private bool _canCreateFirstAdministrator;
        public bool CanCreateFirstAdministrator
        {
            get => _canCreateFirstAdministrator;
            private set
            {
                if (SetProperty(ref _canCreateFirstAdministrator, value))
                {
                    ShowAdminSetupCommand.NotifyCanExecuteChanged();
                    CreateFirstAdminCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public event Action<UserPasswordsRequest>? LoginPasswordRequested;
        public event Action<UserPasswordsRequest>? AdminPasswordsRequested;
        public event Action? LoginPasswordClearRequested;
        public event Action? AdminPasswordsClearRequested;
        public event Action<string, string>? MessageRequested;
        public event Action<UserAccount>? LoginSucceeded;

        /// <summary>
        /// 由 LoginWindow 构造方法创建，作为登录窗口的数据源。
        /// 接住 UserStore，检查是否已有账户，决定能否创建首个管理员。
        /// </summary>
        public LoginViewModel(UserStore userStore)
        {
            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
            _canCreateFirstAdministrator = !_userStore.HasAnyUser();
        }

        /// <summary>
        /// 由 LoginCommand 调用。
        /// 读取密码并验证登录，成功后请求打开主窗口。
        /// </summary>
        [RelayCommand]
        private void Login()
        {
            var passwords = new UserPasswordsRequest();
            try
            {
                if (LoginPasswordRequested is null)
                {
                    // 调用 LoginWindow.xaml.cs 的 ShowMessage：弹出下面的提示。
                    MessageRequested?.Invoke("登录页面尚未准备好。", "登录");
                    return;
                }

                // 让 LoginWindow.ReadLoginPassword 读取密码，填回 passwords.Password 再验证。
                LoginPasswordRequested.Invoke(passwords);

                string userName = UserName.Trim();

                if (string.IsNullOrWhiteSpace(userName) ||
                    string.IsNullOrWhiteSpace(passwords.Password))
                {
                    // 调用 LoginWindow.xaml.cs 的 ShowMessage：提示还没填完整。
                    MessageRequested?.Invoke("请输入用户名和密码。", "登录");
                    return;
                }

                UserAccount? account;
                try
                {
                    //得到账户信息
                    account = _userStore.CheckLogin(userName, passwords.Password);
                }
                finally
                {
                    // 让 LoginWindow.ClearLoginPassword 清空密码框，无论验证结果如何都清空。
                    LoginPasswordClearRequested?.Invoke();
                }

                if (account is null)
                {
                    AppLog.Warning("登录验证未通过", throttleKey: "login-rejected");
                    // 调用 LoginWindow.xaml.cs 的 ShowMessage：显示登录未通过的原因。
                    MessageRequested?.Invoke("用户名或密码错误，或账户已停用。", "登录失败");
                    return;
                }

                // 让 LoginWindow.OpenMainWindow 带着 account 打开主窗口，再关闭登录窗口。
                LoginSucceeded?.Invoke(account);
                AppLog.Info("登录成功", $"UserId={account.Id}; Role={account.Role}");
            }
            catch (Exception ex)
            {
                AppLog.Error("登录过程失败", exception: ex);
                // 调用 LoginWindow.xaml.cs 的 ShowMessage：弹出这次登录的错误信息。
                MessageRequested?.Invoke($"登录过程出错：{ex.Message}", "登录失败");
            }
            finally
            {
                passwords.Password = string.Empty;
                passwords.Confirmation = string.Empty;
            }
        }

        /// <summary>
        /// 由 ShowAdminSetupCommand 调用。
        /// 没有任何账户才展开创建管理员面板，不另开窗口。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanOpenAdminSetup))]
        private void ShowAdminSetup()
        {
            if (!CanOpenAdminSetup())
                return;

            try
            {
                // 窗口打开后，另一程序实例也可能已创建账户；再次读取实际状态。
                if (_userStore.HasAnyUser())
                {
                    CanCreateFirstAdministrator = false;
                    return;
                }

                // 让 LoginWindow.ClearLoginPassword 清空原登录密码，再展开创建面板。
                LoginPasswordClearRequested?.Invoke();
                IsAdminSetupVisible = true;
            }
            catch (Exception ex)
            {
                // 调用 LoginWindow.xaml.cs 的 ShowMessage：显示检查账户时的错误。
                MessageRequested?.Invoke($"检查账户失败：{ex.Message}", "操作失败");
            }
        }

        // 判断现在是否还允许创建首个管理员。
        // 由 ShowAdminSetupCommand、CreateFirstAdminCommand 判断按钮能不能点时调用。
        private bool CanOpenAdminSetup() => CanCreateFirstAdministrator;

        /// <summary>
        /// 由 CreateFirstAdminCommand 调用。
        /// 检查用户名和两次密码，创建首个管理员；成功后回登录面板，不自动登录。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanOpenAdminSetup))]
        private void CreateFirstAdmin()
        {
            if (!CanOpenAdminSetup())
                return;

            var passwords = new UserPasswordsRequest();
            try
            {
                // 输入框把用户名传到 AdminUserName，Trim 去掉前后空格。
                string userName = AdminUserName.Trim();

                if (string.IsNullOrWhiteSpace(userName))
                {
                    // 调用 LoginWindow.xaml.cs 的 ShowMessage：提示填写管理员用户名。
                    MessageRequested?.Invoke("请输入用户名。", "创建管理员");
                    return;
                }

                if (AdminPasswordsRequested is null)
                {
                    // 调用 LoginWindow.xaml.cs 的 ShowMessage：弹出下面的提示。
                    MessageRequested?.Invoke("登录页面尚未准备好。", "创建管理员");
                    return;
                }

                // 让 LoginWindow.ReadAdminPasswords 读取两个密码框，填回 passwords 再检查。
                AdminPasswordsRequested.Invoke(passwords);
                if (passwords.Password.Length < 6)
                {
                    // 调用 LoginWindow.xaml.cs 的 ShowMessage：提示密码太短。
                    MessageRequested?.Invoke("密码至少需要 6 个字符。", "创建管理员");
                    return;
                }

                if (passwords.Password != passwords.Confirmation)
                {
                    // 调用 LoginWindow.xaml.cs 的 ShowMessage：提示两次密码不同。
                    MessageRequested?.Invoke("两次输入的密码不一致。", "创建管理员");
                    return;
                }

                // 界面入口判断不能代替数据库检查；原 SQL 仍阻止重复创建首个管理员。
                bool created = _userStore.TryCreateFirstAdministrator(userName, passwords.Password);
                CanCreateFirstAdministrator = false;
                BackToLogin();
                if (!created)
                {
                    // 调用 LoginWindow.xaml.cs 的 ShowMessage：说明没有再次创建管理员。
                    MessageRequested?.Invoke("已有用户，不能再次创建首个管理员。", "创建管理员");
                    return;
                }

                UserName = userName; // 绑定将用户名自动填到登录表单。
                // 调用 LoginWindow.xaml.cs 的 ShowMessage：提示创建成功；这里只提示，不自动登录。
                MessageRequested?.Invoke("管理员创建成功，请登录。", "创建管理员");
            }
            catch (Exception ex)
            {
                // 调用 LoginWindow.xaml.cs 的 ShowMessage：显示创建管理员时的错误。
                MessageRequested?.Invoke($"创建管理员失败：{ex.Message}", "操作失败");
            }
            finally
            {
                // 仅释放临时对象中的引用，不意味着把字符串从内存中擦除。
                passwords.Password = string.Empty;
                passwords.Confirmation = string.Empty;
            }
        }

        /// <summary>
        /// 由 BackToLoginCommand 调用。
        /// 收起创建面板并清空密码框，不删除已创建的账户。
        /// </summary>
        [RelayCommand]
        private void BackToLogin()
        {
            // 让 LoginWindow.ClearAdminPasswords 清空两个密码框，再回登录面板。
            AdminPasswordsClearRequested?.Invoke();
            IsAdminSetupVisible = false;
        }
    }
}

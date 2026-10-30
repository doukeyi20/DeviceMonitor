// 处理登录和首次管理员创建。
using System;
using System.Threading.Tasks;
using DeviceMonitor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel
{
    public partial class LoginViewModel : ObservableObject
    {
        private readonly UserStore _userStore;

        // iswork代表忙的状态
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(ShowAdminSetupCommand), nameof(CreateFirstAdminCommand), nameof(BackToLoginCommand))]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        private bool _isWorking = true;

        public bool CanEdit => !IsWorking;

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
                if (SetProperty(ref _isAdminSetupVisible, value))
                    OnPropertyChanged(nameof(IsLoginVisible));
            }
        }
        public bool IsLoginVisible => !IsAdminSetupVisible;

        // CanCreateFirstAdministrator是判断要不要开创建管理员窗口的属性
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

        public LoginViewModel(UserStore userStore)
        {
            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        }

        /// <summary>检查已有账户，决定是否开放首次管理员创建。</summary>
        public async Task InitializeAsync()
        {
            IsWorking = true;

            try
            {
                // 检查数据库中是否已有账户，决定是否开放首次管理员创建。
                CanCreateFirstAdministrator = !await Task.Run(_userStore.HasAnyUser);
            }
            catch (Exception ex)
            {
                MessageRequested?.Invoke($"检查账户失败：{ex.Message}", "登录");
            }

            finally
            {
                IsWorking = false;
            }
        }

        /// <summary>读取密码并验证登录，成功后请求打开主窗口MainWindow。</summary>
        [RelayCommand(CanExecute = nameof(CanAcceptInput))]
        private async Task LoginAsync()
        {
            // 创建密码请求对象，而不是直接来绑定属性读取
            var passwords = new UserPasswordsRequest();
            try
            {
                // 通过事件来读取控件中的密码并且传入密码请求对象接收
                // 不直接让控件PasswordBox.Password绑定viewmodel属性是因为不允许，username可以
                LoginPasswordRequested?.Invoke(passwords);

                string userName = UserName.Trim();

                if (string.IsNullOrWhiteSpace(userName) ||
                    string.IsNullOrWhiteSpace(passwords.Password))
                {
                    MessageRequested?.Invoke("请输入用户名和密码。", "登录");
                    return;
                }

                // 用户信息对象
                UserAccount? account;
                try
                {
                    IsWorking = true;

                    // 数据库查询和密码计算在后台执行，界面等待结果。
                    account = await Task.Run(() => _userStore.CheckLogin(userName, passwords.Password));
                }
                finally
                {
                    LoginPasswordClearRequested?.Invoke();
                }

                if (account is null)
                {
                    AppLog.Warning("登录验证未通过", throttleKey: "login-rejected");

                    MessageRequested?.Invoke("用户名或密码错误，或账户已停用。", "登录失败");
                    return;
                }

                IsWorking = false;
                // 先解除忙状态，再交给窗口跳转和关闭。进入MainWindow
                LoginSucceeded?.Invoke(account);
                AppLog.Info("登录成功", $"用户={account.Id} 角色={(account.Role == UserRole.Administrator ? "管理员" : "操作员")}");
            }
            catch (Exception ex)
            {
                AppLog.Error("登录过程失败", exception: ex);

                MessageRequested?.Invoke($"登录过程出错：{ex.Message}", "登录失败");
            }
            finally
            {
                // 本次密码不参与长期绑定，操作结束后清除输入引用。
                passwords.Password = string.Empty;
                passwords.Confirmation = string.Empty;
                IsWorking = false;
            }
        }

        /// <summary>没有任何账户才展开创建管理员面板，不另开窗口。</summary>
        [RelayCommand(CanExecute = nameof(CanOpenAdminSetup))]
        private void ShowAdminSetup()
        {
            if (!CanOpenAdminSetup())
                return;

            // 初始化已查过；这里只切面板，真正保存时 Store 仍会检查是否已有账户。
            LoginPasswordClearRequested?.Invoke();
            IsAdminSetupVisible = true;
        }

        // 决定要不要开创建管理员窗口
        private bool CanOpenAdminSetup() => !IsWorking && CanCreateFirstAdministrator;
        private bool CanAcceptInput() => !IsWorking;

        /// <summary>检查用户名和两次密码，创建首个管理员；成功后回登录面板，不自动登录。</summary>
        [RelayCommand(CanExecute = nameof(CanOpenAdminSetup))]
        private async Task CreateFirstAdminAsync()
        {
            if (!CanOpenAdminSetup())
                return;

            var passwords = new UserPasswordsRequest();
            try
            {
                string userName = AdminUserName.Trim();

                if (string.IsNullOrWhiteSpace(userName))
                {
                    MessageRequested?.Invoke("请输入用户名。", "创建管理员");
                    return;
                }

                AdminPasswordsRequested?.Invoke(passwords);
                if (passwords.Password.Length < 6)
                {
                    MessageRequested?.Invoke("密码至少需要 6 个字符。", "创建管理员");
                    return;
                }

                if (passwords.Password != passwords.Confirmation)
                {
                    MessageRequested?.Invoke("两次输入的密码不一致。", "创建管理员");
                    return;
                }

                IsWorking = true;
                bool created = await Task.Run(() => _userStore.TryCreateFirstAdministrator(userName, passwords.Password));
                CanCreateFirstAdministrator = false;
                BackToLogin();
                if (!created)
                {
                    MessageRequested?.Invoke("已有用户，不能再次创建首个管理员。", "创建管理员");
                    return;
                }

                UserName = userName;

                MessageRequested?.Invoke("管理员创建成功，请登录。", "创建管理员");
            }
            catch (Exception ex)
            {
                MessageRequested?.Invoke($"创建管理员失败：{ex.Message}", "操作失败");
            }
            finally
            {
                passwords.Password = string.Empty;
                passwords.Confirmation = string.Empty;
                IsWorking = false;
            }
        }

        /// <summary>收起创建面板并清空密码框，不删除已创建的账户。</summary>
        [RelayCommand(CanExecute = nameof(CanAcceptInput))]
        private void BackToLogin()
        {
            AdminPasswordsClearRequested?.Invoke();
            IsAdminSetupVisible = false;
        }
    }
}

// 处理登录和首次管理员创建。
using System;
using System.Threading.Tasks;
using DeviceMonitor.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels
{
    public partial class LoginViewModel : ObservableObject
    {
        private readonly UserStore _userStore;

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

        /// <summary>读取密码并验证登录，成功后请求打开主窗口。</summary>
        [RelayCommand(CanExecute = nameof(CanAcceptInput))]
        private async Task LoginAsync()
        {
            var passwords = new UserPasswordsRequest();
            try
            {
                if (LoginPasswordRequested is null)
                {
                    MessageRequested?.Invoke("登录页面尚未准备好。", "登录");
                    return;
                }

                LoginPasswordRequested.Invoke(passwords);

                string userName = UserName.Trim();

                if (string.IsNullOrWhiteSpace(userName) ||
                    string.IsNullOrWhiteSpace(passwords.Password))
                {
                    MessageRequested?.Invoke("请输入用户名和密码。", "登录");
                    return;
                }

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
                // 先解除忙状态，再交给窗口跳转和关闭。
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
        private async Task ShowAdminSetupAsync()
        {
            if (!CanOpenAdminSetup())
                return;

            try
            {
                IsWorking = true;
                if (await Task.Run(_userStore.HasAnyUser))
                {
                    CanCreateFirstAdministrator = false;
                    return;
                }

                LoginPasswordClearRequested?.Invoke();
                IsAdminSetupVisible = true;
            }
            catch (Exception ex)
            {
                MessageRequested?.Invoke($"检查账户失败：{ex.Message}", "操作失败");
            }
            finally
            {
                IsWorking = false;
            }
        }

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

                if (AdminPasswordsRequested is null)
                {
                    MessageRequested?.Invoke("登录页面尚未准备好。", "创建管理员");
                    return;
                }

                AdminPasswordsRequested.Invoke(passwords);
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

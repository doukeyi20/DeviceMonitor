// 处理登录和首次管理员创建。
using DeviceMonitor.Services;
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

        // IsWorking 代表正在检查或登录，等待期间禁用提交，避免同一次操作重复执行。
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ShowAdminSetupCommand), nameof(BackToLoginCommand))]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        // 登录页的忙碌标记：初始先为 true，账户检查、登录验证、创建首个管理员期间也打上，拦住重复提交。
        // 这些操作结束后清掉；登录成功时会先清掉，再通知窗口跳转。
        private bool _isWorking = true;

        public bool CanEdit => !IsWorking;

        [ObservableProperty]
        private string _userName = string.Empty;

        [ObservableProperty]
        private string _adminUserName = string.Empty;

        // 面板显示标记：打开首次创建管理员面板时为 true，返回登录面板时清掉；不代表管理员已经创建成功。
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

        // CanCreateFirstAdministrator 是查库得到的结果，决定是否允许展开首次管理员面板。
        // 首次创建入口的允许标记：初始化查到数据库还没有任何账户时打上，尝试创建返回后清掉。
        // 它用于决定能否打开创建面板，真正写数据库时还会再检查，不是当前用户的管理员权限。
        private bool _canCreateFirstAdministrator;
        public bool CanCreateFirstAdministrator
        {
            get => _canCreateFirstAdministrator;
            private set
            {
                if (SetProperty(ref _canCreateFirstAdministrator, value))
                {
                    ShowAdminSetupCommand.NotifyCanExecuteChanged();
                }
            }
        }

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

        /// <summary>窗口直接传入本次密码；验证成功后通知窗口打开主界面。</summary>
        public async Task LoginAsync(string password)
        {
            // 方法也会被窗口直接调用，忙碌时不接受第二次提交。
            if (IsWorking)
                return;

            try
            {
                string userName = UserName.Trim();

                if (string.IsNullOrWhiteSpace(userName) ||
                    string.IsNullOrWhiteSpace(password))
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
                    account = await Task.Run(() => _userStore.CheckLogin(userName, password));
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

                // 先解除忙状态，再交给窗口跳转和关闭。这里进入MainWindow
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
                IsWorking = false;
            }
        }

        /// <summary>
        /// 创建管理员的按钮命令
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanOpenAdminSetup))]
        private void ShowAdminSetup()
        {
            if (!CanOpenAdminSetup())
                return;

            // 初始化已查过；这里只切面板，真正保存时 Store 仍会检查是否已有账户。
            LoginPasswordClearRequested?.Invoke();
            IsAdminSetupVisible = true;
        }

        // 决定能否展开创建管理员面板，不负责检查数据库。
        private bool CanOpenAdminSetup() => !IsWorking && CanCreateFirstAdministrator;
        private bool CanAcceptInput() => !IsWorking;

        /// <summary>检查窗口传来的两次密码，创建首个管理员；成功后回登录面板。</summary>
        public async Task CreateFirstAdminAsync(string password, string confirmation)
        {
            if (!CanOpenAdminSetup())
                return;

            try
            {
                string userName = AdminUserName.Trim();

                if (string.IsNullOrWhiteSpace(userName))
                {
                    MessageRequested?.Invoke("请输入用户名。", "创建管理员");
                    return;
                }

                if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
                {
                    MessageRequested?.Invoke("密码至少需要 6 个字符。", "创建管理员");
                    return;
                }

                if (password != confirmation)
                {
                    MessageRequested?.Invoke("两次输入的密码不一致。", "创建管理员");
                    return;
                }

                IsWorking = true;
                bool created = await Task.Run(() => _userStore.TryCreateFirstAdministrator(userName, password));
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

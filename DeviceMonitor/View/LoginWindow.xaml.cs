// 处理登录窗口、密码控件和登录后的窗口切换。
using DeviceMonitor.Services;
using DeviceMonitor.ViewModel;
using System;
using System.ComponentModel;
using System.Windows;
using DeviceMonitor.Model;

namespace DeviceMonitor.View
{
    public partial class LoginWindow : Window
    {
        private readonly UserStore _userStore;
        private readonly DeviceStore _deviceStore;
        private readonly HistoryStore _historyStore;

        private readonly LoginViewModel _viewModel;

        public LoginWindow(UserStore userStore, DeviceStore deviceStore, HistoryStore historyStore)
        {
            // 接收外部传入的 Store，避免在窗口中直接创建 DbContext，保证每次数据库操作都是独立上下文。
            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));

            try
            {
                InitializeComponent();

                // 设定尺寸
                MaxHeight = SystemParameters.WorkArea.Height;
                MaxWidth = SystemParameters.WorkArea.Width;
                Height = Math.Min(Height, MaxHeight);
                Width = Math.Min(Width, MaxWidth);

                _viewModel = new LoginViewModel(_userStore);
            }
            catch
            {
                Close();
                throw;
            }

            // 读取密码由按钮事件直接完成；这里仅订阅清空控件和显示窗口的通知。
            _viewModel.LoginPasswordClearRequested += ClearLoginPassword;
            _viewModel.AdminPasswordsClearRequested += ClearAdminPasswords;
            // ViewModel 请求显示消息时，由窗口负责弹出 MessageBox。
            _viewModel.MessageRequested += ShowMessage;

            _viewModel.LoginSucceeded += OpenMainWindow;

            DataContext = _viewModel;

            // 本窗口加载完成后，异步初始化 ViewModel，避免阻塞 UI 线程。
            Loaded += async (_, _) =>
            {
                // 窗口加载时，检查数据库中是否已有账户决定首次创建管理员。
                await _viewModel.InitializeAsync();
            };
        }

        /// <summary>直接读取密码框，作为参数交给 ViewModel；窗口不查数据库。</summary>
        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.LoginAsync(PasswordInput.Password);
        }

        /// <summary>
        /// 登陆成功，打开主窗口
        /// </summary>
        /// <param name="account"></param>
        private void OpenMainWindow(UserAccount account)
        {
            // 传入登录成功的账户和共用的 Store，避免在主窗口中直接创建 DbContext，保证每次数据库操作都是独立上下文。
            var mainWindow = new DeviceMonitor.View.MainWindow(account, _userStore, _deviceStore, _historyStore);

            Application application = Application.Current;

            // 保存LoginWindow的引用
            Window previousMainWindow = application.MainWindow;

            application.MainWindow = mainWindow;

            try
            {
                mainWindow.Show();
            }
            catch
            {
                // 如果打开主窗口失败，恢复原来的 MainWindow 并关闭新窗口，避免应用程序没有主窗口而退出。
                // 注意MainWindow是Application的属性，我们这个mainWindow是主界面的名，不一样
                application.MainWindow = previousMainWindow;
                mainWindow.Close();
                throw;
            }

            Close();
        }

        /// <summary>两次密码都是本次调用的参数，不再创建对象来回填。</summary>
        private async void CreateAdminButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.CreateFirstAdminAsync(AdminPasswordInput.Password, ConfirmPasswordInput.Password);
        }

        /// <summary>在当前登录窗口上弹出提示消息。</summary>
        private void ShowMessage(string message, string title)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>清空登录密码框，不修改数据库密码。</summary>
        private void ClearLoginPassword() => PasswordInput.Clear();

        /// <summary>清空创建管理员的两个密码框，不删除账户。</summary>
        private void ClearAdminPasswords()
        {
            AdminPasswordInput.Clear();
            ConfirmPasswordInput.Clear();
        }

        /// <summary>登录或创建还没结束时，先不让关窗，等待当前操作完成。</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (_viewModel?.IsWorking == true)
                e.Cancel = true;
        }

        /// <summary>关窗后清空密码，并解除界面通知订阅。</summary>
        protected override void OnClosed(EventArgs e)
        {
            PasswordInput?.Clear();
            AdminPasswordInput?.Clear();
            ConfirmPasswordInput?.Clear();
            if (_viewModel is not null)
            {
                _viewModel.LoginPasswordClearRequested -= ClearLoginPassword;
                _viewModel.AdminPasswordsClearRequested -= ClearAdminPasswords;
                _viewModel.MessageRequested -= ShowMessage;
                _viewModel.LoginSucceeded -= OpenMainWindow;
            }
            base.OnClosed(e);
        }
    }
}

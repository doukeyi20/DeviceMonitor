using System;
using System.Windows;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 登录窗口自己创建 LoginViewModel，并把它设为 DataContext；不是从主窗口 Pages 里取。
    /// 用户名走绑定；密码、提示、登录成功后的开窗通过本文件订阅的事件处理。
    /// </summary>
    public partial class LoginWindow : Window
    {
        private readonly UserStore _userStore;
        private readonly DeviceStore _deviceStore;
        // 登录本身不保存采集数据；这里只接住对象，登录成功后继续交给主窗口。
        private readonly HistoryStore _historyStore;

        // 登录窗口的viewmodel对象在后台创建
        private readonly LoginViewModel _viewModel;

        /// <summary>
        /// App 启动或 MainWindow 退出登录时调用，接收同一份 UserStore、DeviceStore、HistoryStore。
        /// 创建本窗口的 LoginViewModel，把后台 UI 方法订阅到它的请求事件，再设 DataContext；数据库操作对象仍沿用 App 传来的同一份。
        /// </summary>
        public LoginWindow(UserStore userStore, DeviceStore deviceStore, HistoryStore historyStore)
        {
            //构造方法得传入数据库操作对象，确保登录窗口能访问用户和设备数据。

            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));

            try
            {
                InitializeComponent();

                // 后台创建自己的数据源。它会查询是否已有账户；数据库打不开时，这里也可能失败。
                _viewModel = new LoginViewModel(_userStore);
            }
            catch
            {
                // new 失败时，调用者还拿不到这个窗口。由这里清理，避免留下一个隐藏窗口。
                Close();
                throw;
            }

            // 订阅后台事件
            _viewModel.LoginPasswordRequested += ReadLoginPassword;
            _viewModel.AdminPasswordsRequested += ReadAdminPasswords;
            _viewModel.LoginPasswordClearRequested += ClearLoginPassword;
            _viewModel.AdminPasswordsClearRequested += ClearAdminPasswords;
            _viewModel.MessageRequested += ShowMessage;
            _viewModel.LoginSucceeded += OpenMainWindow;

            // 手动设置 DataContext 为 刚才创建的 ViewModel，使界面绑定到 ViewModel 的属性和命令。
            DataContext = _viewModel;
        }


        // 用户名通过 TextBox 绑定给 ViewModel；密码来自 PasswordBox，由下面的方法按需读取。
        /// <summary>
        /// 登录窗口构造时已订阅到 LoginPasswordRequested；LoginViewModel.Login 触发请求时调用这里。
        /// 从 PasswordInput 这个 PasswordBox 取出密码，写回传来的临时 request；返回后 Login 才拿它去验证，不在这里判断密码对错。
        /// </summary>
        private void ReadLoginPassword(UserPasswordsRequest request)
        {
            request.Password = PasswordInput.Password;
        }

        /// <summary>
        /// 登录窗口构造时已订阅到 LoginSucceeded；LoginViewModel.Login 验证成功后将 UserAccount 传到这里。
        /// 把用户和同一份数据库操作对象传给 MainWindow，设置应用主窗口、Show 显示成功后，再 Close 关闭登录窗口。
        /// </summary>
        private void OpenMainWindow(UserAccount account)
        {
            // 点击登录按钮后，登录成功时，创建主窗口并显示，同时关闭登录窗口。
            var mainWindow = new DeviceMonitor.MainWindow(account, _userStore, _deviceStore, _historyStore);

            Application application = Application.Current;

            Window previousMainWindow = application.MainWindow;

            application.MainWindow = mainWindow;

            try
            {
                mainWindow.Show(); // 真正让主窗口显示在屏幕上。
            }
            catch
            {
                // 显示失败时恢复原主窗口，保留登录窗口以便用户重试。
                application.MainWindow = previousMainWindow;
                mainWindow.Close();
                throw;
            }

            Close(); // 必须在主窗口显示后，才关闭登录窗口。
        }

        /// <summary>
        /// 已订阅到 AdminPasswordsRequested；LoginViewModel.CreateFirstAdmin 需要两个密码时触发这个事件。
        /// 读管理员密码框和确认密码框，填回同一个 request，让 ViewModel 接着检查长度和一致性。
        /// </summary>
        private void ReadAdminPasswords(UserPasswordsRequest request)
        {
            request.Password = AdminPasswordInput.Password;
            request.Confirmation = ConfirmPasswordInput.Password;
        }

        /// <summary>
        /// 已订阅到 LoginViewModel.MessageRequested；登录或创建管理员需要提示时通过事件调用这里。
        /// 以当前登录窗口为所属窗口显示 MessageBox，业务判断已在 ViewModel 中完成。
        /// </summary>
        private void ShowMessage(string message, string title)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 由 LoginPasswordClearRequested 事件调用，或在 OnClosed 关窗时直接调用。
        /// 清空屏幕上的登录密码框；不修改数据库中已保存的密码。
        /// </summary>
        private void ClearLoginPassword() => PasswordInput.Clear();

        /// <summary>
        /// 由 AdminPasswordsClearRequested 事件调用，或 OnClosed 直接调用。
        /// 清空创建管理员区域的两个密码框，不删除已创建的账户。
        /// </summary>
        private void ClearAdminPasswords()
        {
            AdminPasswordInput.Clear();
            ConfirmPasswordInput.Clear();
        }

        /// <summary>
        /// 登录窗口真正关闭后，由 WPF 自动调用；可能是登录成功后关闭，也可能是用户直接关闭。
        /// 清空密码框，并从 LoginViewModel 上移除本窗口订阅的所有 UI 方法。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            // 构造失败也会走到这里，此时控件或 ViewModel 可能还没准备好。
            PasswordInput?.Clear();
            AdminPasswordInput?.Clear();
            ConfirmPasswordInput?.Clear();
            if (_viewModel is not null)
            {
                _viewModel.LoginPasswordRequested -= ReadLoginPassword;
                _viewModel.AdminPasswordsRequested -= ReadAdminPasswords;
                _viewModel.LoginPasswordClearRequested -= ClearLoginPassword;
                _viewModel.AdminPasswordsClearRequested -= ClearAdminPasswords;
                _viewModel.MessageRequested -= ShowMessage;
                _viewModel.LoginSucceeded -= OpenMainWindow;
            }
            base.OnClosed(e);
        }
    }
}

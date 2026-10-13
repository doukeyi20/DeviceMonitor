using System;
using System.Windows;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 登录窗口后台：创建 LoginViewModel，处理取密码、提示和登录后打开主窗口。
    /// </summary>
    public partial class LoginWindow : Window
    {
        private readonly UserStore _userStore;
        private readonly DeviceStore _deviceStore;
        // 登录页不用它保存读数，只在登录成功后继续传给主窗口。
        private readonly HistoryStore _historyStore;

        // 本窗口自己创建的数据源，界面属性和按钮命令都从它取。
        private readonly LoginViewModel _viewModel;

        /// <summary>
        /// 接住共用的 Store，创建登录数据源，订阅界面请求后设置 DataContext。
        /// </summary>
        public LoginWindow(UserStore userStore, DeviceStore deviceStore, HistoryStore historyStore)
        {
            // 保存传来的数据库操作对象，登录成功后继续传给主窗口。

            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));

            try
            {
                InitializeComponent();

                // 创建数据源时会检查是否已有账户，数据库打不开也可能在这里报错。
                _viewModel = new LoginViewModel(_userStore);
            }
            catch
            {
                // 创建失败就清理这个窗口，再把错误交给调用方。
                Close();
                throw;
            }

            // 命令触发请求时，才会调用下面对应的界面方法。
            _viewModel.LoginPasswordRequested += ReadLoginPassword;
            _viewModel.AdminPasswordsRequested += ReadAdminPasswords;
            _viewModel.LoginPasswordClearRequested += ClearLoginPassword;
            _viewModel.AdminPasswordsClearRequested += ClearAdminPasswords;
            _viewModel.MessageRequested += ShowMessage;
            _viewModel.LoginSucceeded += OpenMainWindow;

            // 界面绑定从这个 ViewModel 取属性和命令。
            DataContext = _viewModel;
        }


        /// <summary>
        /// 由 LoginViewModel 的 LoginPasswordRequested 事件调用。
        /// 从登录密码框取密码，写回 request，让 Login 命令继续验证。
        /// </summary>
        private void ReadLoginPassword(UserPasswordsRequest request)
        {
            request.Password = PasswordInput.Password;
        }

        /// <summary>
        /// 由 LoginViewModel 的 LoginSucceeded 事件调用。
        /// 登录成功后，用账户和共用 Store 打开主窗口，再关闭登录窗口。
        /// </summary>
        private void OpenMainWindow(UserAccount account)
        {
            // 创建主窗口，并暂时记住旧主窗口，显示失败时可以恢复。
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
        /// 由 LoginViewModel 的 AdminPasswordsRequested 事件调用。
        /// 取出管理员的密码和确认密码，写回 request 供命令检查。
        /// </summary>
        private void ReadAdminPasswords(UserPasswordsRequest request)
        {
            request.Password = AdminPasswordInput.Password;
            request.Confirmation = ConfirmPasswordInput.Password;
        }

        /// <summary>
        /// 由 LoginViewModel 的 MessageRequested 事件调用。
        /// 在当前登录窗口上弹出提示消息。
        /// </summary>
        private void ShowMessage(string message, string title)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 由 LoginViewModel 的 LoginPasswordClearRequested 事件调用。
        /// 清空登录密码框，不修改数据库密码。
        /// </summary>
        private void ClearLoginPassword() => PasswordInput.Clear();

        /// <summary>
        /// 由 LoginViewModel 的 AdminPasswordsClearRequested 事件调用。
        /// 清空创建管理员的两个密码框，不删除账户。
        /// </summary>
        private void ClearAdminPasswords()
        {
            AdminPasswordInput.Clear();
            ConfirmPasswordInput.Clear();
        }

        /// <summary>
        /// 窗口关闭后自动调用，清空密码并解除事件订阅。
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

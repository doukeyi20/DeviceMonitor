// 处理登录窗口、密码控件和登录后的窗口切换。
using DeviceMonitor.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.Views
{
    public partial class LoginWindow : Window
    {
        private readonly UserStore _userStore;
        private readonly DeviceStore _deviceStore;

        private readonly HistoryStore _historyStore;

        private readonly LoginViewModel _viewModel;

        public LoginWindow(UserStore userStore, DeviceStore deviceStore, HistoryStore historyStore)
        {
            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));

            try
            {
                InitializeComponent();
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

            _viewModel.LoginPasswordRequested += ReadLoginPassword;
            _viewModel.AdminPasswordsRequested += ReadAdminPasswords;
            _viewModel.LoginPasswordClearRequested += ClearLoginPassword;
            _viewModel.AdminPasswordsClearRequested += ClearAdminPasswords;
            _viewModel.MessageRequested += ShowMessage;
            _viewModel.LoginSucceeded += OpenMainWindow;

            DataContext = _viewModel;

            Loaded += async (_, _) => await _viewModel.InitializeAsync();
        }

        /// <summary>从登录密码框取密码，写回 request，让 Login 命令继续验证。</summary>
        private void ReadLoginPassword(UserPasswordsRequest request)
        {
            request.Password = PasswordInput.Password;
        }

        /// <summary>登录成功后，用账户和共用 Store 打开主窗口，再关闭登录窗口。</summary>
        private void OpenMainWindow(UserAccount account)
        {
            var mainWindow = new DeviceMonitor.MainWindow(account, _userStore, _deviceStore, _historyStore);

            Application application = Application.Current;

            Window previousMainWindow = application.MainWindow;

            application.MainWindow = mainWindow;

            try
            {
                mainWindow.Show();
            }
            catch
            {
                application.MainWindow = previousMainWindow;
                mainWindow.Close();
                throw;
            }

            Close();
        }

        /// <summary>取出管理员的密码和确认密码，写回 request 供命令检查。</summary>
        private void ReadAdminPasswords(UserPasswordsRequest request)
        {
            request.Password = AdminPasswordInput.Password;
            request.Confirmation = ConfirmPasswordInput.Password;
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

        /// <summary>清空密码并解除事件订阅。</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (_viewModel?.IsWorking == true)
                e.Cancel = true;
        }

        protected override void OnClosed(EventArgs e)
        {
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

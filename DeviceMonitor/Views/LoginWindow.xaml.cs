using DeviceMonitor.Core.Persistence;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DeviceMonitor.Views
{
    public partial class LoginWindow : Window
    {
        // 得到数据库操作对象，供后续登录验证和管理员账户创建使用。
        private readonly UserStore _userStore;

        public LoginWindow(UserStore userStore)
        {
            InitializeComponent();
            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));

            // 只有一个用户都没有时，才显示首次创建管理员的入口。
            FirstAdminButton.Visibility = _userStore.HasAnyUser()
                ? Visibility.Collapsed  // 隐藏按钮
                : Visibility.Visible;   // 显示按钮
        }

        // 实现界面切换
        
        private void FirstAdminButton_Click(
            object sender, RoutedEventArgs e)
        {
            // 当点击首次创建管理员按钮时，切换界面到管理员创建面板。

            //登录面板隐藏
            LoginPanel.Visibility = Visibility.Collapsed;
            // 管理员面板显示
            AdminSetupPanel.Visibility = Visibility.Visible;
        }

        private void BackToLoginButton_Click(
            object sender, RoutedEventArgs e)
        {
            AdminSetupPanel.Visibility = Visibility.Collapsed;
            LoginPanel.Visibility = Visibility.Visible;
        }

        private void CreateAdminButton_Click(object sender, RoutedEventArgs e)
        {
            string userName = AdminUserNameInput.Text.Trim();
            string password = AdminPasswordInput.Password;

            if (string.IsNullOrWhiteSpace(userName))
            {
                MessageBox.Show("请输入用户名。");
                return;
            }

            if (password.Length < 6)
            {
                MessageBox.Show("密码至少需要 6 个字符。");
                return;
            }

            if (password != ConfirmPasswordInput.Password)
            {
                MessageBox.Show("两次输入的密码不一致。");
                return;
            }

            try
            {
                bool created =
                    _userStore.TryCreateFirstAdministrator(userName, password);

                if (!created)
                {
                    MessageBox.Show("已有用户，不能再次创建首个管理员。");
                    FirstAdminButton.Visibility = Visibility.Collapsed;
                    AdminSetupPanel.Visibility = Visibility.Collapsed;
                    LoginPanel.Visibility = Visibility.Visible;
                    return;
                }

                UserNameInput.Text = userName;
                AdminPasswordInput.Clear();
                ConfirmPasswordInput.Clear();

                FirstAdminButton.Visibility = Visibility.Collapsed;
                AdminSetupPanel.Visibility = Visibility.Collapsed;
                LoginPanel.Visibility = Visibility.Visible;

                MessageBox.Show("管理员创建成功，请登录。");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"创建管理员失败：{ex.Message}");
            }
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string userName = UserNameInput.Text.Trim();
            string passWord = PasswordInput.Password;
            if (string.IsNullOrWhiteSpace(userName) ||
    string.IsNullOrWhiteSpace(passWord))
            {
                MessageBox.Show("请输入用户名和密码。");
                return;
            }
            try
            {
                // 查用户、核对密码；失败或账户被停用都会返回 null。
                var account = _userStore.CheckLogin(userName, passWord);

                PasswordInput.Clear();

                if (account is null)
                {
                    MessageBox.Show("用户名或密码错误，或账户已停用。");
                    return;
                }

                // 账号验证通过后，带着用户信息打开主界面。
                DeviceMonitor.MainWindow mainWindow = new DeviceMonitor.MainWindow(account);
                // 将主窗口设置为应用程序的主窗口，确保关闭登录窗口后应用程序不会退出。
                Application.Current.MainWindow = mainWindow;
                // 显示主窗口，进入主窗口就可以看主窗口的代码了
                mainWindow.Show();
                Close(); // 关闭登录窗口
            }
            catch (Exception ex)
            {
                MessageBox.Show($"登录过程出错：{ex.Message}");
            }
        }
    }
}

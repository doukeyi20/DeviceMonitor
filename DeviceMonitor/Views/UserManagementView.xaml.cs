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
using System.Windows.Navigation;
using System.Windows.Shapes;
using DeviceMonitor.Core.Models;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{
    /// <summary>
    /// UserManagementView.xaml 的交互逻辑
    /// </summary>
    public partial class UserManagementView : UserControl
    {
        public UserManagementView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 创建操作员按钮，调用链路：UserManagementViewModel.TryCreateOperator -> UserStore.TryCreateOperator
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CreateOperatorButton_Click(
    object sender, RoutedEventArgs e)
        {
            // 取得这个页面正在使用的 UserManagementViewModel
            if (DataContext is not UserManagementViewModel viewModel)
            {
                MessageBox.Show("用户管理页面未准备好。");
                return;
            }

            string userName = NewUserNameInput.Text.Trim();
            string password = NewPasswordInput.Password;

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
                bool created = viewModel.TryCreateOperator(userName, password);

                if (!created)
                {
                    MessageBox.Show("用户名已存在。");
                    return;
                }

                NewUserNameInput.Clear();
                NewPasswordInput.Clear();
                ConfirmPasswordInput.Clear();
                MessageBox.Show("操作员创建成功。");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"创建操作员失败：{ex.Message}");
            }
        }



        /// <summary>
        /// 管理启用按钮的状态
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void UsersGrid_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            // 只管理界面按钮状态；真正的权限和目标角色由 UserStore 再检查。
            if (EnableOperatorButton is null || DisableOperatorButton is null)
            {
                return;
            }

            UserAccount? selected = UsersGrid.SelectedItem as UserAccount;
            EnableOperatorButton.IsEnabled =
                selected is { Role: UserRole.Operator, IsEnabled: false };
            DisableOperatorButton.IsEnabled =
                selected is { Role: UserRole.Operator, IsEnabled: true };
        }

        private void EnableOperatorButton_Click(object sender, RoutedEventArgs e)
        {
            ChangeSelectedOperatorStatus(true);
        }

        private void DisableOperatorButton_Click(object sender, RoutedEventArgs e)
        {
            ChangeSelectedOperatorStatus(false);
        }

        /// <summary>
        /// 修改选中操作员的启用状态
        /// </summary>
        /// <param name="enabled"></param>
        private void ChangeSelectedOperatorStatus(bool enabled)
        {
            
            if (DataContext is not UserManagementViewModel viewModel ||
                UsersGrid.SelectedItem is not UserAccount selected ||
                selected.Role != UserRole.Operator ||
                selected.IsEnabled == enabled)
            {
                return;
            }

            string action = enabled ? "启用" : "停用";
            MessageBoxResult answer = MessageBox.Show(
                $"确定{action}操作员“{selected.UserName}”吗？",
                "确认账户操作",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                bool changed = viewModel.TrySetOperatorEnabled(selected.Id, enabled);
                MessageBox.Show(changed
                    ? $"操作员“{selected.UserName}”已{action}。"
                    : "账户不存在或状态已变化，列表已刷新。");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{action}操作员失败：{ex.Message}");
            }
        }
    }
}

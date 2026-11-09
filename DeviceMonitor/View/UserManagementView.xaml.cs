// 处理用户确认、密码输入和重置窗口。
using DeviceMonitor.ViewModel;
using System.Windows;
using System.Windows.Controls;

namespace DeviceMonitor.View
{
    public partial class UserManagementView : UserControl
    {
        private UserManagementViewModel? _subscribedViewModel;

        public UserManagementView()
        {
            InitializeComponent();
        }

        /// <summary>页面显示时，接上数据源的界面请求。</summary>
        private async void UserManagementView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeViewModel();
            if (DataContext is UserManagementViewModel vm && vm.RefreshUsersCommand.CanExecute(null))
                await vm.RefreshUsersCommand.ExecuteAsync(null);
        }

        /// <summary>先断开旧订阅，再订阅当前数据源的确认、提示和清空表单通知。</summary>
        private void SubscribeViewModel()
        {
            UnsubscribeViewModel();
            if (DataContext is UserManagementViewModel viewModel)
            {
                _subscribedViewModel = viewModel;
                viewModel.ConfirmationRequested += ConfirmAction;
                viewModel.MessageRequested += ShowMessage;
                viewModel.FormResetRequested += ClearPasswords;
                viewModel.PasswordEditorRequested += OpenPasswordEditor;
            }
        }

        /// <summary>创建操作员的点击事件，窗口直接传入两次密码；用户列表和数据库操作留在 ViewModel。</summary>
        private async void CreateOperatorButton_Click(object sender, RoutedEventArgs e)
        {
            
            if (DataContext is UserManagementViewModel viewModel)
                await viewModel.CreateOperatorAsync(NewPasswordInput.Password, ConfirmPasswordInput.Password);
        }

        /// <summary>清空两个密码框，不改数据库密码。</summary>
        private void ClearPasswords()
        {
            NewPasswordInput?.Clear();
            ConfirmPasswordInput?.Clear();
        }

        /// <summary>弹出确认框，直接返回是否点了“是”。</summary>
        private bool ConfirmAction(string message, string title)
        {
            Window? owner = Window.GetWindow(this);

            MessageBoxResult answer = owner is not null
                ? MessageBox.Show(owner, message, title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(message, title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
        }

        /// <summary>在页面所属窗口上弹出提示消息。</summary>
        private void ShowMessage(string message, string title)
        {
            Window? owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner, message, title);
            else
                MessageBox.Show(message, title);
        }

        private void OpenPasswordEditor(PasswordEditViewModel viewModel)
        {
            // 修改密码的界面
            var dialog = new PasswordWindow(viewModel) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
        }

        /// <summary>离开页面时解除订阅并清空密码，用户列表仍保留。</summary>
        private void UserManagementView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeViewModel();
            ClearPasswords();
        }

        /// <summary>解除旧数据源的事件订阅，不再响应它的界面请求。</summary>
        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel is not null)
            {
                _subscribedViewModel.ConfirmationRequested -= ConfirmAction;
                _subscribedViewModel.MessageRequested -= ShowMessage;
                _subscribedViewModel.FormResetRequested -= ClearPasswords;
                _subscribedViewModel.PasswordEditorRequested -= OpenPasswordEditor;
                _subscribedViewModel = null;
            }
        }
    }
}

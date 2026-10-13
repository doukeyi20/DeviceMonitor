using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 用户管理页面后台，处理取密码、清空密码、确认弹窗和打开重置密码窗口。
    /// 数据和命令来自 DataContext 中的 UserManagementViewModel。
    /// </summary>
    public partial class UserManagementView : UserControl
    {
        private UserManagementViewModel? _subscribedViewModel; // 记住已订阅的对象，离开时解除它的订阅。

        /// <summary>
        /// 创建页面控件，数据源由主窗口的 DataTemplate 传入。
        /// </summary>
        public UserManagementView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 页面显示时，接上数据源的界面请求。
        /// </summary>
        private void UserManagementView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeViewModel();
        }

        /// <summary>
        /// 数据源换了，先清密码，再接上新对象的事件。
        /// </summary>
        private void UserManagementView_DataContextChanged(
            object sender, DependencyPropertyChangedEventArgs e)
        {
            ClearPasswords();
            if (IsLoaded)
                SubscribeViewModel();
        }

        /// <summary>
        /// 先断开旧订阅，再订阅当前数据源的取密码和弹窗等请求。
        /// </summary>
        private void SubscribeViewModel()
        {
            UnsubscribeViewModel();
            if (DataContext is UserManagementViewModel viewModel)
            {
                _subscribedViewModel = viewModel;
                viewModel.PasswordsRequested += ReadPasswords;
                viewModel.ConfirmationRequested += ConfirmAction;
                viewModel.MessageRequested += ShowMessage;
                viewModel.FormResetRequested += ClearPasswords;
                viewModel.PasswordEditorRequested += OpenPasswordEditor;
            }
        }

        /// <summary>
        /// 由 UserManagementViewModel 的 PasswordsRequested 事件调用。
        /// 从两个密码框取值，写回 request，让创建账户命令继续检查。
        /// </summary>
        private void ReadPasswords(UserPasswordsRequest request)
        {
            // 只取控件的值，检查和保存由命令处理。
            request.Password = NewPasswordInput.Password;
            request.Confirmation = ConfirmPasswordInput.Password;
        }

        /// <summary>
        /// 由 UserManagementViewModel 的 FormResetRequested 事件或本页 UserManagementView_Unloaded 调用。
        /// 清空两个密码框，不改数据库密码。
        /// </summary>
        private void ClearPasswords()
        {
            // 初始化时控件可能尚未创建，?. 避免空对象报错。
            NewPasswordInput?.Clear();
            ConfirmPasswordInput?.Clear();
        }

        /// <summary>
        /// 由 UserManagementViewModel 的 ConfirmationRequested 事件调用。
        /// 弹出确认框，把是否点了“是”写回 request.Confirmed，供命令判断。
        /// </summary>
        private void ConfirmAction(UserConfirmationRequest request)
        {
            Window? owner = Window.GetWindow(this);
            // 默认选“否”，必须明确确认才继续修改。
            MessageBoxResult answer = owner is not null
                ? MessageBox.Show(owner, request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            request.Confirmed = answer == MessageBoxResult.Yes;
        }

        /// <summary>
        /// 由 UserManagementViewModel 的 MessageRequested 事件调用。
        /// 在页面所属窗口上弹出提示消息。
        /// </summary>
        private void ShowMessage(string message, string title)
        {
            Window? owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner, message, title);
            else
                MessageBox.Show(message, title);
        }

        // 用传来的数据源打开密码表单，关闭后再返回。
        // 由 UserManagementViewModel 的 PasswordEditorRequested 事件调用。
        private void OpenPasswordEditor(PasswordEditViewModel viewModel)
        {
            var dialog = new PasswordWindow(viewModel) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
        }

        /// <summary>
        /// 离开页面时解除订阅并清空密码，用户列表仍保留。
        /// </summary>
        private void UserManagementView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeViewModel();
            ClearPasswords(); // 离开页面时不保留密码控件中的输入。
        }

        /// <summary>
        /// 解除旧数据源的事件订阅，不再响应它的界面请求。
        /// </summary>
        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel is not null)
            {
                _subscribedViewModel.PasswordsRequested -= ReadPasswords;
                _subscribedViewModel.ConfirmationRequested -= ConfirmAction;
                _subscribedViewModel.MessageRequested -= ShowMessage;
                _subscribedViewModel.FormResetRequested -= ClearPasswords;
                _subscribedViewModel.PasswordEditorRequested -= OpenPasswordEditor;
                _subscribedViewModel = null;
            }
        }
    }
}

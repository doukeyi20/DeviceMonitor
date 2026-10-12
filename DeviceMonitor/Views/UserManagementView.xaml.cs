using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 用户管理页的后台：从 DataContext 接住已有 UserManagementViewModel，订阅读/清密码、确认和开密码表单。
    /// 用户名走绑定；PasswordBox 只按请求读取；账户验证和数据库修改都在 ViewModel/Store。
    /// </summary>
    public partial class UserManagementView : UserControl
    {
        private UserManagementViewModel? _subscribedViewModel;

        /// <summary>
        /// WPF 根据 DataTemplate 创建用户管理页面时调用这里，只加载 XAML。
        /// 界面继续使用 Pages 中已有的 UserManagementViewModel 作为 DataContext，不在这里重新创建。
        /// </summary>
        public UserManagementView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 页面显示出来时，由 XAML 的 Loaded 事件调用。
        /// 调用 SubscribeViewModel，把读密码、清密码和弹框方法登记到已有 ViewModel 上。
        /// </summary>
        private void UserManagementView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeViewModel();
        }

        /// <summary>
        /// 页面数据源更换时，由 XAML 的 DataContextChanged 事件调用。
        /// 先清掉旧密码；如果页面已加载，再重新订阅新 ViewModel 的请求事件。
        /// </summary>
        private void UserManagementView_DataContextChanged(
            object sender, DependencyPropertyChangedEventArgs e)
        {
            ClearPasswords();
            if (IsLoaded)
                SubscribeViewModel();
        }

        /// <summary>
        /// Loaded 或换数据源时调用。先解除旧订阅，再把读密码、提示、确认、清空和开重置窗口的方法登记上。
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
        /// 已订阅到 PasswordsRequested；UserManagementViewModel.CreateOperator 需要输入的密码时触发事件。
        /// 读两个 PasswordBox，填回临时 request；长度、两次是否一致等检查仍交给 ViewModel。
        /// </summary>
        private void ReadPasswords(UserPasswordsRequest request)
        {
            // 只提供控件当前值，校验和数据库操作都由 ViewModel 负责。
            request.Password = NewPasswordInput.Password;
            request.Confirmation = ConfirmPasswordInput.Password;
        }

        /// <summary>
        /// 由 FormResetRequested 事件在创建成功后调用；页面换数据源或离开时也直接调用。
        /// 只清空两个密码框；控件还没创建时用空值保护，不改数据库。
        /// </summary>
        private void ClearPasswords()
        {
            // 初始化期间 DataContextChanged 可能早于子控件创建，所以使用空值保护。
            NewPasswordInput?.Clear();
            ConfirmPasswordInput?.Clear();
        }

        /// <summary>
        /// 已订阅到 ConfirmationRequested；ViewModel.ChangeSelectedOperatorStatus 修改账户状态前触发事件。
        /// 显示确认框，把是否点了“是”写回 request.Confirmed；ViewModel 收到确认才会修改数据库。
        /// </summary>
        private void ConfirmAction(UserConfirmationRequest request)
        {
            Window? owner = Window.GetWindow(this);
            // 回车默认选“否”，避免只是想关提示，却误改了账户状态。
            MessageBoxResult answer = owner is not null
                ? MessageBox.Show(owner, request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            request.Confirmed = answer == MessageBoxResult.Yes;
        }

        /// <summary>
        /// 已订阅到 ViewModel.MessageRequested；创建账户、启停账户成功或失败时通过事件调用这里。
        /// 以页面所在的主窗口为所属窗口显示提示，不在这里执行业务操作。
        /// </summary>
        private void ShowMessage(string message, string title)
        {
            Window? owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner, message, title);
            else
                MessageBox.Show(message, title);
        }

        // 重置命令触发事件后到这里。沿用传进来的数据源，窗口只管显示和读密码框。
        private void OpenPasswordEditor(PasswordEditViewModel viewModel)
        {
            var dialog = new PasswordWindow(viewModel) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
        }

        /// <summary>
        /// 切换到别的页面、用户管理页面从显示区域移除时，由 XAML 的 Unloaded 事件调用。
        /// 解除后台方法订阅，并清空密码输入；Users 列表仍保留在已有 ViewModel 中。
        /// </summary>
        private void UserManagementView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeViewModel();
            ClearPasswords(); // 离开页面时不保留密码控件中的输入。
        }

        /// <summary>
        /// 离开页面或换数据源前调用，解除本页全部请求订阅；不删除用户，不改变用户启停状态。
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

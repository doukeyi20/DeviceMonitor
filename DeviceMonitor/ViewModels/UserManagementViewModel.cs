using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 用户管理页：查询账户、创建和启停操作员、重置密码。
    /// </summary>
    public partial class UserManagementViewModel : PageViewModel
    {

        // 存储当前登录的用户信息
        private readonly UserAccount _currentUser;
        private readonly UserStore _userStore;
        private PasswordEditViewModel? _passwordEditor; // 当前打开的重置密码表单数据，避免重复开窗。
        private bool _isClosing; // 退出后不再创建、启停或重置账户。

        // 普通输入可以直接绑定；密码不放进长期保存的绑定属性。
        [ObservableProperty]
        private string _newUserName = string.Empty;

        // 用户表当前选中的 UserAccount，启停和重置密码都操作它。
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EnableOperatorCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableOperatorCommand))]
        [NotifyCanExecuteChangedFor(nameof(ResetOperatorPasswordCommand))]
        private UserAccount? _selectedUser;

        public event Action<UserPasswordsRequest>? PasswordsRequested;
        public event Action<UserConfirmationRequest>? ConfirmationRequested;
        public event Action<string, string>? MessageRequested;
        public event Action? FormResetRequested;
        public event Action<PasswordEditViewModel>? PasswordEditorRequested;

        private string _pageMessage = "先选择操作员；管理员不能在这里启停。";
        public string PageMessage
        {
            get => _pageMessage;
            private set => SetProperty(ref _pageMessage, value);
        }

        // 用户表绑定这里，每项是一个 UserAccount，包含名称、角色和启用状态，不含密码。
        public ObservableCollection<UserAccount> Users { get; } = new();


        /// <summary>
        /// 接住当前用户和 UserStore，检查管理员权限并加载账户列表。
        /// </summary>
        public UserManagementViewModel(
            UserAccount currentUser,
            UserStore userStore) : base("用户管理")
        {
            _currentUser = currentUser
                ?? throw new ArgumentNullException(nameof(currentUser));
            _userStore = userStore
                ?? throw new ArgumentNullException(nameof(userStore));

            if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有已启用的管理员可以管理账户。");
            }

            RefreshUsers(); // 打开管理员主界面时，先加载已有账户。
        }


        /// <summary>
        /// 由 RefreshUsersCommand 调用；本类构造方法也会调用一次。
        /// 刷新账户列表，失败时保留旧列表，不清空正在填写的表单。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanManageUsers))]
        private void RefreshUsers()
        {
            if (!CanManageUsers())
                return;

            try
            {
                LoadUsers();
                PageMessage = $"已刷新 {Users.Count} 个账户。";
            }
            catch (Exception ex)
            {
                PageMessage = $"刷新账户失败：{ex.Message}。原列表已保留，请点击“刷新账户”重试。";
            }
        }

        // 刷新按钮、创建成功和启停完成后共用这里；按 Id 尽量恢复选择。
        // 由 RefreshUsers 和 RefreshUsersAfterOperation 调用。
        private void LoadUsers()
        {
            // 先查成功，再替换界面列表，避免查询失败时把原列表清空。
            var latestUsers = _userStore.GetUsers(_currentUser);
            long? selectedId = SelectedUser?.Id;

            Users.Clear();
            foreach (UserAccount user in latestUsers)
            {
                Users.Add(user);
            }

            // 刷新产生新对象；按编号恢复选择，让命令读取最新的角色和启用状态。
            SelectedUser = selectedId.HasValue
                ? Users.FirstOrDefault(user => user.Id == selectedId.Value)
                : null;
        }

        /// <summary>
        /// 由 CreateOperatorCommand 调用。
        /// 读取两次密码，检查并创建操作员；成功后清空密码框并提示。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanManageUsers))]
        private void CreateOperator()
        {
            if (!CanManageUsers())
                return;

            var passwords = new UserPasswordsRequest();
            try
            {
                string userName = NewUserName.Trim();
                if (string.IsNullOrWhiteSpace(userName))
                {
                    // 调用 UserManagementView.xaml.cs 的 ShowMessage：提示填写用户名。
                    MessageRequested?.Invoke("请输入用户名。", "创建操作员");
                    return;
                }

                if (PasswordsRequested is null)
                {
                    // 调用 UserManagementView.xaml.cs 的 ShowMessage：弹出下面的提示。
                    MessageRequested?.Invoke("用户管理页面尚未准备好。", "创建操作员");
                    return;
                }

                // 让 UserManagementView.ReadPasswords 读取两个密码框，填回 passwords 再检查。
                PasswordsRequested.Invoke(passwords);
                if (passwords.Password.Length < 6)
                {
                    // 调用 UserManagementView.xaml.cs 的 ShowMessage：提示密码太短。
                    MessageRequested?.Invoke("密码至少需要 6 个字符。", "创建操作员");
                    return;
                }

                if (passwords.Password != passwords.Confirmation)
                {
                    // 调用 UserManagementView.xaml.cs 的 ShowMessage：提示两次密码不同。
                    MessageRequested?.Invoke("两次输入的密码不一致。", "创建操作员");
                    return;
                }

                bool created = TryCreateOperator(userName, passwords.Password);
                if (!created)
                {
                    // 调用 UserManagementView.xaml.cs 的 ShowMessage：提示用户名被占用。
                    MessageRequested?.Invoke("用户名已存在。", "创建操作员");
                    return;
                }

                NewUserName = string.Empty;
                // 调用 UserManagementView.xaml.cs 的 ClearPasswords：清空创建操作员的两个密码框。
                FormResetRequested?.Invoke();
                // 让 UserManagementView.ShowMessage 显示结果，创建成功和刷新失败要分开说。
                MessageRequested?.Invoke(PageMessage, "创建操作员");
            }
            catch (Exception ex)
            {
                // 调用 UserManagementView.xaml.cs 的 ShowMessage：显示创建操作员时的错误。
                MessageRequested?.Invoke($"创建操作员失败：{ex.Message}", "操作失败");
            }
            finally
            {
                // 释放临时对象对密码字符串的引用；不代表把字符串从内存中擦除。
                passwords.Password = string.Empty;
                passwords.Confirmation = string.Empty;
            }
        }

        /// <summary>
        /// 由 CreateOperator 调用。
        /// 保存操作员账户，成功后刷新列表；刷新失败单独提示。
        /// </summary>
        public bool TryCreateOperator(string userName, string password)
        {
            bool created = _userStore.TryCreateOperator(
                _currentUser, userName, password);

            if (created)
            {
                RefreshUsersAfterOperation("操作员创建成功。");
            }

            return created;
        }

        /// <summary>
        /// 由 EnableOperatorCommand 调用。
        /// 启用选中的操作员，传 true。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEnableOperator))]
        private void EnableOperator()
        {
            ChangeSelectedOperatorStatus(true);
        }

        // 有权限且选中停用的操作员，才能启用。
        // 由 EnableOperatorCommand 判断按钮能不能点时调用。
        private bool CanEnableOperator() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator, IsEnabled: false };

        /// <summary>
        /// 由 DisableOperatorCommand 调用。
        /// 停用选中的操作员，传 false。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDisableOperator))]
        private void DisableOperator()
        {
            ChangeSelectedOperatorStatus(false);
        }

        // 有权限且选中启用的操作员，才能停用。
        // 由 DisableOperatorCommand 判断按钮能不能点时调用。
        private bool CanDisableOperator() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator, IsEnabled: true };

        /// <summary>
        /// 由 EnableOperator 和 DisableOperator 调用。
        /// 确认后保存操作员的启停状态。
        /// </summary>
        private void ChangeSelectedOperatorStatus(bool enabled)
        {
            UserAccount? selected = SelectedUser;
            if (!CanManageUsers() || selected is not { Role: UserRole.Operator } ||
                selected.IsEnabled == enabled)
                return;

            string action = enabled ? "启用" : "停用";
            try
            {
                var confirmation = new UserConfirmationRequest(
                    $"确定{action}操作员“{selected.UserName}”吗？", "确认账户操作");
                // 让 UserManagementView.ConfirmAction 弹框，把是否点“是”写回 confirmation.Confirmed。
                ConfirmationRequested?.Invoke(confirmation);
                if (!confirmation.Confirmed)
                    return;

                TrySetOperatorEnabled(selected.Id, enabled);
                // 调用 UserManagementView.xaml.cs 的 ShowMessage：显示本次账户操作的结果。
                MessageRequested?.Invoke(PageMessage, "账户操作");
            }
            catch (Exception ex)
            {
                // 调用 UserManagementView.xaml.cs 的 ShowMessage：显示启用/停用失败的原因。
                MessageRequested?.Invoke($"{action}操作员失败：{ex.Message}", "操作失败");
            }
        }

        /// <summary>
        /// 由 ChangeSelectedOperatorStatus 调用。
        /// 保存操作员启停状态，再刷新列表显示实际状态。
        /// </summary>
        public bool TrySetOperatorEnabled(long operatorId, bool enabled)
        {
            string target = Users.FirstOrDefault(user => user.Id == operatorId)?.UserName
                ?? $"编号 {operatorId}";
            bool changed = _userStore.TrySetOperatorEnabled(
                _currentUser, operatorId, enabled);

            // 无论是否修改成功，都重新读取数据库中的实际状态。
            RefreshUsersAfterOperation(changed
                ? $"操作员“{target}”已{(enabled ? "启用" : "停用")}。"
                : "账户不存在或状态已变化。");
            return changed;
        }

        /// <summary>
        /// 由 ResetOperatorPasswordCommand 调用。
        /// 打开选中操作员的密码重置表单，不读取旧密码，也不重新启用账户。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanResetOperatorPassword))]
        private void ResetOperatorPassword()
        {
            if (!CanResetOperatorPassword() || PasswordEditorRequested is null)
                return;
            var editor = new PasswordEditViewModel(_currentUser, _userStore, SelectedUser);
            _passwordEditor = editor;
            NotifyUserCommands();
            try
            {
                // 让 UserManagementView.OpenPasswordEditor 显示 editor，关闭后检查是否保存成功。
                PasswordEditorRequested.Invoke(editor);
                if (editor.IsSaved && !_isClosing)
                    PageMessage = "操作员密码已重置，下次登录使用新密码。账户启停状态未改变，已登录会话不会被强制退出。";
            }
            finally
            {
                _passwordEditor = null;
                NotifyUserCommands();
            }
        }

        /// <summary>
        /// 由 ResetOperatorPasswordCommand 判断按钮能不能点时调用。
        /// 有权限且选中操作员才能重置密码，停用的操作员也可以。
        /// </summary>
        private bool CanResetOperatorPassword() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator };

        // 写入已经完成后，刷新是第二件事。刷新出错要单独提示，不能让用户以为账户没创建。
        // 由 TryCreateOperator 和 TrySetOperatorEnabled 保存成功后调用。
        private void RefreshUsersAfterOperation(string resultMessage)
        {
            try
            {
                LoadUsers();
                PageMessage = resultMessage;
            }
            catch (Exception ex)
            {
                PageMessage = $"{resultMessage} 但刷新列表失败：{ex.Message}。" +
                    "当前表格可能仍是旧数据，请点击“刷新账户”重新加载。";
            }
        }

        /// <summary>
        /// 由 RefreshUsersCommand、CreateOperatorCommand 判断按钮能不能点时调用。
        /// 账户已启用、是管理员，且没在退出或重置密码，才能操作。
        /// </summary>
        private bool CanManageUsers() =>
            !_isClosing && _passwordEditor is null &&
            _currentUser.Role == UserRole.Administrator && _currentUser.IsEnabled;

        /// <summary>
        /// 由 ResetOperatorPassword 和 StopOperationsAsync 调用。
        /// 让账户按钮重新判断能不能点，不执行账户操作。
        /// </summary>
        private void NotifyUserCommands()
        {
            RefreshUsersCommand.NotifyCanExecuteChanged();
            CreateOperatorCommand.NotifyCanExecuteChanged();
            EnableOperatorCommand.NotifyCanExecuteChanged();
            DisableOperatorCommand.NotifyCanExecuteChanged();
            ResetOperatorPasswordCommand.NotifyCanExecuteChanged();
        }

        // 由 MainViewModel.StopBackgroundWorkAsync 在退出前调用。
        // 等密码写入完成，不能关窗后还在改账户。
        public async Task StopOperationsAsync()
        {
            _isClosing = true;
            NotifyUserCommands();
            if (_passwordEditor is not null)
                await _passwordEditor.StopOperationsAsync();
        }
    }
}

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
    /// 用户管理页的数据源：刷新账户、创建操作员、启停操作员和请求重置密码。
    /// 读密码/确认/开窗通过事件回到页面后台；真正改账户由同一个 UserStore 完成。
    /// </summary>
    public partial class UserManagementViewModel : PageViewModel
    {

        // 存储当前登录的用户信息
        private readonly UserAccount _currentUser;
        private readonly UserStore _userStore;
        private PasswordEditViewModel? _passwordEditor;
        private bool _isClosing;

        // 普通输入可以直接绑定；密码不放进长期保存的绑定属性。
        [ObservableProperty]
        private string _newUserName = string.Empty;

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

        // 页面展示的用户列表。增删列表项时，WPF 会收到更新通知。
        public ObservableCollection<UserAccount> Users { get; } = new();


        /// <summary>
        /// 由 MainViewModel 为管理员创建，传入已登录用户和同一个 UserStore。
        /// 保存这两个对象，检查角色，并调用 RefreshUsers 先加载用户列表。
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
        /// 构造函数和“刷新账户”按钮调用这里；失败时保留旧表格，并给出可以重试的提示。
        /// 不读取密码，也不会清空正在填写的新账户表单。
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
        /// 点击“创建操作员”后，CreateOperatorCommand 调用这里。
        /// 触发 PasswordsRequested，让 UserManagementView.ReadPasswords 读两个密码框；检查通过后调用 TryCreateOperator。
        /// 成功后发出清空密码和显示提示的事件，由页面后台执行。
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

                // 调用 UserManagementView.xaml.cs 的 ReadPasswords：
                // 把两个密码框的值填回 passwords，回来后继续检查，再决定是否创建账户。
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
                // 调用 UserManagementView.xaml.cs 的 ShowMessage：显示 PageMessage 中的完整结果。
                // 若账户已创建、只是刷新失败，提示中也会保留这两件事。
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
        /// 由 CreateOperator 在输入检查通过后调用。
        /// 把用户名、密码和当前管理员交给 UserStore.TryCreateOperator；创建成功再刷新表格，刷新失败单独提示。
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
        /// 点击“启用操作员”后，EnableOperatorCommand 调用这里，再把 true 交给 ChangeSelectedOperatorStatus。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEnableOperator))]
        private void EnableOperator()
        {
            ChangeSelectedOperatorStatus(true);
        }

        // EnableOperatorCommand 判断按钮能不能点时调用：当前有权限，并且选中的是停用的操作员，不是管理员。
        private bool CanEnableOperator() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator, IsEnabled: false };

        /// <summary>
        /// 点击“停用操作员”后，DisableOperatorCommand 调用这里，再把 false 交给 ChangeSelectedOperatorStatus。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDisableOperator))]
        private void DisableOperator()
        {
            ChangeSelectedOperatorStatus(false);
        }

        // DisableOperatorCommand 判断按钮能不能点时调用：当前有权限，并且选中的是启用的操作员，不是管理员。
        private bool CanDisableOperator() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator, IsEnabled: true };

        /// <summary>
        /// 由 EnableOperator 和 DisableOperator 共用。
        /// 先触发 ConfirmationRequested，让 UserManagementView.ConfirmAction 弹框并把选择写回；确认后才调用 TrySetOperatorEnabled。
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
                // 调用 UserManagementView.xaml.cs 的 ConfirmAction：弹出启用/停用确认框。
                // 后台把是否点“是”填入 confirmation.Confirmed，回来后才决定要不要改数据库。
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
        /// 由 ChangeSelectedOperatorStatus 在用户确认后调用。
        /// 调用 UserStore.TrySetOperatorEnabled 改数据库，再刷新列表显示最新状态；取消确认不会走到这里。
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
        /// 选中操作员后点“重置密码”：只打开密码表单，不重新启用账户、不读取原密码。
        /// PasswordWindow 仍通过 SaveCommand → UserStore 写入，回到这里后只更新操作提示。
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
                // 调用 UserManagementView.xaml.cs 的 OpenPasswordEditor：
                // 把 editor 作为密码窗口的数据源并显示窗口；窗口关闭后，才继续检查 IsSaved。
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
        /// 重置按钮的条件：当前可管理，并且选中的是操作员；停用操作员也能重置，但不会被启用。
        /// </summary>
        private bool CanResetOperatorPassword() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator };

        // 写入已经完成后，刷新是第二件事。刷新出错要单独提示，不能让用户以为账户没创建。
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
        /// 账户操作共用条件：已启用管理员，没在退出，也没打开重置密码表单。
        /// </summary>
        private bool CanManageUsers() =>
            !_isClosing && _passwordEditor is null &&
            _currentUser.Role == UserRole.Administrator && _currentUser.IsEnabled;

        /// <summary>
        /// 打开/关闭重置窗口或退出时调用，让账户按钮重新判断能不能点，不会执行按钮业务。
        /// </summary>
        private void NotifyUserCommands()
        {
            RefreshUsersCommand.NotifyCanExecuteChanged();
            CreateOperatorCommand.NotifyCanExecuteChanged();
            EnableOperatorCommand.NotifyCanExecuteChanged();
            DisableOperatorCommand.NotifyCanExecuteChanged();
            ResetOperatorPasswordCommand.NotifyCanExecuteChanged();
        }

        // MainViewModel 关窗/退出登录时调用，等密码写入完成，不能关窗后还在改账户。
        public async Task StopOperationsAsync()
        {
            _isClosing = true;
            NotifyUserCommands();
            if (_passwordEditor is not null)
                await _passwordEditor.StopOperationsAsync();
        }
    }
}

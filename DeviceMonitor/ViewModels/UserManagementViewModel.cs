// 管理操作员账户、启用状态和密码重置。
using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core;
using DeviceMonitor.Core.Persistence;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace DeviceMonitor.ViewModels
{
    public partial class UserManagementViewModel : PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly UserStore _userStore;
        private PasswordEditViewModel? _passwordEditor;
        private bool _isWorking;
        private bool _isClosing;

        public bool CanEditNewUser => CanManageUsers();
        public string CreateButtonText => _isWorking ? "处理中…" : "创建操作员";
        public bool CanChooseUser => !_isWorking && !_isClosing;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CreateOperatorCommand))]
        private string _newUserName = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EnableOperatorCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableOperatorCommand))]
        [NotifyCanExecuteChangedFor(nameof(ResetOperatorPasswordCommand))]
        private UserAccount? _selectedUser;

        public event Action<UserPasswordsRequest>? PasswordsRequested;
        public event Action<ConfirmationRequest>? ConfirmationRequested;
        public event Action<string, string>? MessageRequested;
        public event Action? FormResetRequested;
        public event Action<PasswordEditViewModel>? PasswordEditorRequested;

        private string _pageMessage = string.Empty;
        public string PageMessage
        {
            get => _pageMessage;
            private set => SetProperty(ref _pageMessage, value);
        }

        public ObservableCollection<UserAccount> Users { get; } = new();

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
        }

        /// <summary>刷新账户列表，失败时保留旧列表，不清空正在填写的表单。</summary>
        [RelayCommand(CanExecute = nameof(CanManageUsers))]
        private async Task RefreshUsersAsync()
        {
            if (!CanManageUsers())
                return;

            try
            {
                _isWorking = true;
                NotifyUserCommands();
                await LoadUsersAsync();

                PageMessage = $"已刷新 {Users.Count} 个账户。";
            }
            catch (Exception ex)
            {
                PageMessage = $"刷新账户失败：{ex.Message}。原列表已保留，请点击“刷新账户”重试。";
            }
            finally
            {
                _isWorking = false;
                NotifyUserCommands();
            }
        }

        private async Task LoadUsersAsync()
        {
            var latestUsers = await Task.Run(() => _userStore.GetUsers(_currentUser));

            if (_isClosing)
                return;

            long? selectedId = SelectedUser?.Id;

            Users.Clear();
            foreach (UserAccount user in latestUsers)
            {
                Users.Add(user);
            }

            SelectedUser = Users.FirstOrDefault(user => user.Id == selectedId)
                ?? Users.FirstOrDefault();
        }

        /// <summary>读取两次密码，检查并创建操作员；成功后清空密码框并提示。</summary>
        [RelayCommand(CanExecute = nameof(CanCreateOperator))]
        private async Task CreateOperatorAsync()
        {
            if (!CanManageUsers())
                return;

            var passwords = new UserPasswordsRequest();
            try
            {
                string userName = NewUserName.Trim();
                if (string.IsNullOrWhiteSpace(userName))
                {
                    MessageRequested?.Invoke("请输入用户名。", "创建操作员");
                    return;
                }

                if (PasswordsRequested is null)
                {
                    MessageRequested?.Invoke("用户管理页面尚未准备好。", "创建操作员");
                    return;
                }

                PasswordsRequested.Invoke(passwords);
                if (passwords.Password.Length < 6)
                {
                    MessageRequested?.Invoke("密码至少需要 6 个字符。", "创建操作员");
                    return;
                }

                if (passwords.Password != passwords.Confirmation)
                {
                    MessageRequested?.Invoke("两次输入的密码不一致。", "创建操作员");
                    return;
                }

                _isWorking = true;
                NotifyUserCommands();
                PageMessage = "正在创建操作员…";

                string password = passwords.Password;
                bool created = await Task.Run(() => _userStore.TryCreateOperator(_currentUser, userName, password));
                if (_isClosing)
                    return;
                if (!created)
                {
                    MessageRequested?.Invoke("用户名已存在。", "创建操作员");
                    return;
                }

                await RefreshUsersAfterOperationAsync("操作员创建成功。");
                SelectedUser = Users.FirstOrDefault(user =>
                    string.Equals(user.UserName, userName, StringComparison.Ordinal))
                    ?? SelectedUser;

                NewUserName = string.Empty;

                FormResetRequested?.Invoke();

                MessageRequested?.Invoke(PageMessage, "创建操作员");
            }
            catch (Exception ex)
            {
                AppLog.Warning("创建操作员失败", exception: ex);
                if (!_isClosing)
                    MessageRequested?.Invoke($"创建操作员失败：{ex.Message}", "操作失败");
            }
            finally
            {
                passwords.Password = string.Empty;
                passwords.Confirmation = string.Empty;
                _isWorking = false;
                NotifyUserCommands();
            }
        }

        /// <summary>启用选中的操作员，传 true。</summary>
        [RelayCommand(CanExecute = nameof(CanEnableOperator))]
        private async Task EnableOperatorAsync()
        {
            await ChangeSelectedOperatorStatusAsync(true);
        }

        private bool CanEnableOperator() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator, IsEnabled: false };

        /// <summary>停用选中的操作员，传 false。</summary>
        [RelayCommand(CanExecute = nameof(CanDisableOperator))]
        private async Task DisableOperatorAsync()
        {
            await ChangeSelectedOperatorStatusAsync(false);
        }

        private bool CanDisableOperator() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator, IsEnabled: true };

        /// <summary>确认后保存操作员的启停状态。</summary>
        private async Task ChangeSelectedOperatorStatusAsync(bool enabled)
        {
            UserAccount? selected = SelectedUser;
            if (!CanManageUsers() || selected is not { Role: UserRole.Operator } ||
                selected.IsEnabled == enabled)
                return;

            string action = enabled ? "启用" : "停用";
            try
            {
                var confirmation = new ConfirmationRequest(
                    $"确定{action}操作员“{selected.UserName}”吗？", "确认账户操作");

                ConfirmationRequested?.Invoke(confirmation);
                if (!confirmation.Confirmed)
                    return;

                await TrySetOperatorEnabledAsync(selected.Id, enabled);

                if (!_isClosing)
                    MessageRequested?.Invoke(PageMessage, "账户操作");
            }
            catch (Exception ex)
            {
                MessageRequested?.Invoke($"{action}操作员失败：{ex.Message}", "操作失败");
            }
        }

        /// <summary>保存操作员启停状态，再刷新列表显示实际状态。</summary>
        public async Task<bool> TrySetOperatorEnabledAsync(long operatorId, bool enabled)
        {
            if (!CanManageUsers())
                return false;
            string target = Users.FirstOrDefault(user => user.Id == operatorId)?.UserName
                ?? $"编号 {operatorId}";
            _isWorking = true;
            NotifyUserCommands();
            try
            {
                bool changed = await Task.Run(() => _userStore.TrySetOperatorEnabled(_currentUser, operatorId, enabled));

                if (!_isClosing)
                    await RefreshUsersAfterOperationAsync(changed
                    ? $"操作员“{target}”已{(enabled ? "启用" : "停用")}。"
                    : "账户不存在或状态已变化。");
                return changed;
            }
            finally
            {
                _isWorking = false;
                NotifyUserCommands();
            }
        }

        /// <summary>打开选中操作员的密码重置表单，不读取旧密码，也不重新启用账户。</summary>
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

        /// <summary>有权限且选中操作员才能重置密码，停用的操作员也可以。</summary>
        private bool CanResetOperatorPassword() =>
            CanManageUsers() && SelectedUser is { Role: UserRole.Operator };

        private async Task RefreshUsersAfterOperationAsync(string resultMessage)
        {
            try
            {
                await LoadUsersAsync();
                PageMessage = resultMessage;
            }
            catch (Exception ex)
            {
                PageMessage = $"{resultMessage} 但刷新列表失败：{ex.Message}。" +
                    "当前表格可能仍是旧数据，请点击“刷新账户”重新加载。";
            }
        }

        /// <summary>没在退出、创建账户或重置密码，才接受下一次操作。</summary>

        private bool CanManageUsers() =>
            !_isClosing && !_isWorking && _passwordEditor is null;

        private bool CanCreateOperator() => CanManageUsers() && !string.IsNullOrWhiteSpace(NewUserName);

        /// <summary>让账户按钮重新判断能不能点，不执行账户操作。</summary>
        private void NotifyUserCommands()
        {
            OnPropertyChanged(nameof(CanEditNewUser));
            OnPropertyChanged(nameof(CreateButtonText));
            OnPropertyChanged(nameof(CanChooseUser));
            RefreshUsersCommand.NotifyCanExecuteChanged();
            CreateOperatorCommand.NotifyCanExecuteChanged();
            EnableOperatorCommand.NotifyCanExecuteChanged();
            DisableOperatorCommand.NotifyCanExecuteChanged();
            ResetOperatorPasswordCommand.NotifyCanExecuteChanged();
        }

        public async Task StopOperationsAsync()
        {
            _isClosing = true;
            NotifyUserCommands();
            await Task.WhenAll(CreateOperatorCommand.ExecutionTask ?? Task.CompletedTask,
                RefreshUsersCommand.ExecutionTask ?? Task.CompletedTask,
                EnableOperatorCommand.ExecutionTask ?? Task.CompletedTask,
                DisableOperatorCommand.ExecutionTask ?? Task.CompletedTask,
                _passwordEditor?.StopOperationsAsync() ?? Task.CompletedTask);
        }
    }
}

// 管理操作员账户、启用状态和密码重置。
using DeviceMonitor.Services;
using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;
using DeviceMonitor;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace DeviceMonitor.ViewModel
{
    public partial class UserManagementViewModel : PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly UserStore _userStore;
        // 账户页的忙碌标记：刷新用户、创建账户、启停账户时打上，拦住重复操作和切换用户；finally 中清掉。
        private bool _isWorking;
        // 账户页开始退出收尾时打上，拒绝新账户操作；加载等异步结果回来时也用它判断是否还要更新页面。
        // 这个页面对象以后不再使用，所以不清回 false。
        private bool _isClosing;
        // 创建账户改为直接接收密码参数；退出时仍要等这次保存真正结束。
        // 保存最近一次创建操作员的 Task；退出时等它真正结束，避免窗口关了但账户还在写入；下次创建换新任务。
        private Task _createOperatorTask = Task.CompletedTask;

        // 上方创建输入区能否操作
        public bool CanEditNewUser => CanManageUsers();
        public string CreateButtonText => _isWorking ? "处理中…" : "创建操作员";
        public bool CanChooseUser => CanManageUsers();
        public bool CanCreateOperator => CanManageUsers() && !string.IsNullOrWhiteSpace(NewUserName);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanCreateOperator))]
        private string _newUserName = string.Empty;

        /*
        启用／停用命令重新检查条件。
        按钮文字重新取值。
        重置密码命令重新检查条件。*/
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleOperatorCommand))]
        [NotifyPropertyChangedFor(nameof(OperatorActionText))]
        [NotifyCanExecuteChangedFor(nameof(ResetOperatorPasswordCommand))]
        private UserAccount? _selectedUser;

        public string OperatorActionText => SelectedUser?.IsEnabled == true ? "停用账户" : "启用账户";

        public event Func<string, string, bool>? ConfirmationRequested;
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

        /// <summary>
        /// 加载用户列表
        /// </summary>
        /// <returns></returns>
        private async Task LoadUsersAsync()
        {
            // 得到用户列表
            var latestUsers = await Task.Run(() => _userStore.GetUsers(_currentUser));

            if (_isClosing)
                return;

            // 如果有选中用户，先保存
            long? selectedId = SelectedUser?.Id;

            Users.Clear();
            foreach (UserAccount user in latestUsers)
            {
                Users.Add(user);
            }

            // 恢复选择
            SelectedUser = Users.FirstOrDefault(user => user.Id == selectedId)
                ?? Users.FirstOrDefault();
        }

        /// <summary>由创建操作员的后台事件调用，这里没用命令。</summary>
        public Task CreateOperatorAsync(string password, string confirmation)
        {
            if (!CanManageUsers())
                return Task.CompletedTask;

            _createOperatorTask = CreateOperatorCoreAsync(password, confirmation);
            return _createOperatorTask;
        }

        /// <summary>真正校验并创建账户，检查输入后创建操作员；保存期间不接受第二次账户操作。</summary>
        private async Task CreateOperatorCoreAsync(string password, string confirmation)
        {
            try
            {
                string userName = NewUserName.Trim();
                if (string.IsNullOrWhiteSpace(userName))
                {
                    MessageRequested?.Invoke("请输入用户名。", "创建操作员");
                    return;
                }

                if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
                {
                    MessageRequested?.Invoke("密码至少需要 6 个字符。", "创建操作员");
                    return;
                }

                if (password != confirmation)
                {
                    MessageRequested?.Invoke("两次输入的密码不一致。", "创建操作员");
                    return;
                }

                _isWorking = true;
                NotifyUserCommands();
                PageMessage = "正在创建操作员…";

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

                // 创建结果已显示在 PageMessage，只通知界面清空密码框，不重复弹成功消息。
                FormResetRequested?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Warning("创建操作员失败", exception: ex);
                if (!_isClosing)
                    MessageRequested?.Invoke($"创建操作员失败：{ex.Message}", "操作失败");
            }
            finally
            {
                // 任务会被记住用于退出等待，结束时清掉本次输入引用。
                password = confirmation = string.Empty;
                _isWorking = false;
                NotifyUserCommands();
            }
        }

        /// <summary>
        /// 确认后启用或停用账户
        /// </summary>
        /// <returns></returns>
        [RelayCommand(CanExecute = nameof(CanToggleOperator))]
        private async Task ToggleOperatorAsync()
        {
            UserAccount selected = SelectedUser!;
            bool enabled = !selected.IsEnabled;
            // 判断启用还是停用
            string action = enabled ? "启用" : "停用";
            // 窗口直接返回是否确认，不创建对象再回填 Confirmed。
            bool confirmed = ConfirmationRequested?.Invoke(
                $"确定{action}操作员“{selected.UserName}”吗？", "账户状态") == true;
            if (!confirmed)
                return;

            _isWorking = true;
            NotifyUserCommands();
            try
            {
                // 
                bool saved = await Task.Run(() => _userStore.TrySetOperatorEnabled(_currentUser, selected.Id, enabled));
                if (!_isClosing)
                    await RefreshUsersAfterOperationAsync(saved
                        ? $"已{action}“{selected.UserName}”。" : "账户不存在或状态已变化。");
            }
            catch (Exception ex)
            {
                PageMessage = $"账户状态保存失败：{ex.Message}";
                AppLog.Error("账户状态保存失败", $"账户={selected.Id}", ex);
            }
            finally
            {
                _isWorking = false;
                NotifyUserCommands();
            }
        }

        // 通过角色和状态判断能否启停账户
        private bool CanToggleOperator() => CanManageUsers() && SelectedUser is { Role: UserRole.Operator };

        /// <summary>打开选中操作员的密码重置表单，不读取旧密码，也不重新启用账户。</summary>
        [RelayCommand(CanExecute = nameof(CanResetOperatorPassword))]
        private void ResetOperatorPassword()
        {
            if (!CanResetOperatorPassword() || PasswordEditorRequested is null)
                return;
            var editor = new PasswordEditViewModel(_currentUser, _userStore, SelectedUser);
            // 打开修改窗口。
            PasswordEditorRequested.Invoke(editor);
            if (editor.IsSaved && !_isClosing)
                PageMessage = "操作员密码已重置，下次登录使用新密码。账户启停状态未改变，已登录会话不会被强制退出。";
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

        /// <summary>没在退出或执行账户操作，才接受下一次操作。</summary>
        private bool CanManageUsers() =>
            !_isClosing && !_isWorking;

        /// <summary>让账户按钮重新判断能不能点，不执行账户操作。</summary>
        private void NotifyUserCommands()
        {
            OnPropertyChanged(nameof(CanEditNewUser));
            OnPropertyChanged(nameof(CreateButtonText));
            OnPropertyChanged(nameof(CanChooseUser));
            RefreshUsersCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanCreateOperator));
            ToggleOperatorCommand.NotifyCanExecuteChanged();
            ResetOperatorPasswordCommand.NotifyCanExecuteChanged();
        }

        public override async Task StopOperationsAsync()
        {
            _isClosing = true;
            NotifyUserCommands();
            await Task.WhenAll(_createOperatorTask,
                RefreshUsersCommand.ExecutionTask ?? Task.CompletedTask,
                ToggleOperatorCommand.ExecutionTask ?? Task.CompletedTask);
        }
    }
}

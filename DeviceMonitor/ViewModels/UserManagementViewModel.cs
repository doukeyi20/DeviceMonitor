using System;
using System.Collections.Generic;
using System.Text;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using System.Collections.ObjectModel;

namespace DeviceMonitor.ViewModels
{
    public class UserManagementViewModel : PageViewModel
    {

        // 存储当前登录的用户信息
        private readonly UserAccount _currentUser;
        private readonly UserStore _userStore;


        // 页面展示的用户列表。增删列表项时，WPF 会收到更新通知。
        public ObservableCollection<UserAccount> Users { get; } = new();


        public UserManagementViewModel(
            UserAccount currentUser,
            UserStore userStore) : base("用户管理")
        {
            _currentUser = currentUser
                ?? throw new ArgumentNullException(nameof(currentUser));
            _userStore = userStore
                ?? throw new ArgumentNullException(nameof(userStore));

            if (_currentUser.Role != UserRole.Administrator)
            {
                throw new UnauthorizedAccessException("只有管理员可以管理账户。");
            }

            RefreshUsers(); // 打开管理员主界面时，先加载已有账户。
        }

        public bool TryCreateOperator(string userName, string password)
        {
            bool created = _userStore.TryCreateOperator(
                _currentUser, userName, password);

            if (created)
            {
                RefreshUsers(); // 创建成功后，立即让列表显示新账户。
            }

            return created;
        }

        /// <summary>
        /// 修改启用状态
        /// </summary>
        public bool TrySetOperatorEnabled(long operatorId, bool enabled)
        {
            bool changed = _userStore.TrySetOperatorEnabled(
                _currentUser, operatorId, enabled);

            // 无论是否修改成功，都重新读取数据库中的实际状态。
            RefreshUsers();
            return changed;
        }


        /// <summary>
        /// 刷新用户列表方法
        /// </summary>
        private void RefreshUsers()
        {
            // 先查成功，再替换界面列表，避免查询失败时把原列表清空。
            var latestUsers = _userStore.GetUsers(_currentUser);

            Users.Clear();
            foreach (UserAccount user in latestUsers)
            {
                Users.Add(user);
            }
        }

    }
}

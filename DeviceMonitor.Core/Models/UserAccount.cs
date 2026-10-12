using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 登录成功或账户列表查询得到的用户基本信息：Id、用户名、角色和启停状态。
    /// 不携带密码/盐/哈希；这是当时查到的状态，不会自动感知其他程序实例改了账户。
    /// </summary>
    public class UserAccount
    {
        // 数据库中用户的唯一编号。
        public long Id { get; }

        // 登录时使用的用户名。
        public string UserName { get; }

        // 用户角色。
        public UserRole Role { get; }

        // 是否允许该账户使用系统。
        public bool IsEnabled { get; }

        public UserAccount(
                long id,
                string userName,
                UserRole role,
                bool isEnabled)
        {
            if (id <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(id),
                    "用户编号必须大于0。");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(userName);

            if (role != UserRole.Operator &&
                role != UserRole.Administrator)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(role),
                    "不支持的用户角色。");
            }

            Id = id;
            UserName = userName;
            Role = role;
            IsEnabled = isEnabled;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 用户基本信息：编号、用户名、角色、是否启用，不包含密码信息。
    /// 这里只保存当时查到的状态，不会自动跟随数据库变化。
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

        // 把查到的账户信息装成对象，供登录后的权限判断和用户表显示。
        public UserAccount(
                long id,
                string userName,
                UserRole role,
                bool isEnabled)
        {
            // 检查编号、用户名和角色，不合法就拒绝创建这个对象。
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


            // 保存传来的值，之后通过只读属性查看。
            Id = id;
            UserName = userName;
            Role = role;
            IsEnabled = isEnabled;
        }
    }
}

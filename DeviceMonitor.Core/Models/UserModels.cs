// 定义账户信息和角色，不包含密码验证数据。
using System.Collections.Generic;
using System.Text;
using System;

namespace DeviceMonitor.Core.Models;

public class UserAccount
{
    public long Id { get; }

    public string UserName { get; }

    public UserRole Role { get; }

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

public enum UserRole
{
    Operator = 0,
    Administrator = 1
}

using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DeviceMonitor.Core.Persistence;

/// <summary>账户的 EF 数据库操作。页面仍得到 UserAccount，不会得到密码盐和哈希。</summary>
public sealed class UserStore
{
    private readonly MonitorDbContextFactory _database;

    public UserStore(string databasePath) => _database = new(databasePath);

    /// <summary>登录页准备界面时调用；只检查是否已有任何账户。</summary>
    public bool HasAnyUser()
    {
        using var db = _database.Create();
        return db.Users.Any(); // Any 在数据库里检查是否存在，不把整张表查回来。
    }

    /// <summary>LoginViewModel.Login 调用；停用、用户名或密码不对时返回 null。</summary>
    public UserAccount? CheckLogin(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) return null;
        UserRow? row;
        using (var db = _database.Create())
        {
            string name = userName.Trim();
            row = db.Users.AsNoTracking().FirstOrDefault(x => x.UserName == name);
        }
        // 查询结束就释放上下文，之后再进行比较费时的密码验证。
        if (row is null || !row.IsEnabled ||
            !PasswordHasher.Verify(password, row.PasswordSalt, row.PasswordHash, row.PasswordIterations)) return null;
        return new UserAccount(row.Id, row.UserName, row.Role, row.IsEnabled);
    }

    /// <summary>登录页创建首次管理员时调用；数据库已有任何账户，就不再创建。</summary>
    public bool TryCreateFirstAdministrator(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ValidateNewPassword(password);
        var data = PasswordHasher.Hash(password);
        using var db = _database.Create();
        // 检查空库和插入放在同一写事务中，两个程序同时启动也只能创建一次。
        using var transaction = db.Database.BeginTransaction();
        if (db.Users.Any()) return false;
        db.Users.Add(new UserRow
        {
            UserName = userName.Trim(), PasswordSalt = data.Salt, PasswordHash = data.Hash,
            PasswordIterations = data.Iterations, Role = UserRole.Administrator, IsEnabled = true
        });
        db.SaveChanges();
        transaction.Commit();
        return true;
    }

    /// <summary>用户管理刷新时调用；列表只含基本账户信息，不查询密码列。</summary>
    public IReadOnlyList<UserAccount> GetUsers(UserAccount currentUser)
    {
        CheckAdministrator(currentUser, "只有管理员可以查看账户。");
        using var db = _database.Create();
        return db.Users.AsNoTracking().OrderByDescending(x => x.Role)
            .ThenBy(x => EF.Functions.Collate(x.UserName, "NOCASE"))
            .Select(x => new UserAccount(x.Id, x.UserName, x.Role, x.IsEnabled)).ToList();
    }

    /// <summary>用户管理创建操作员时调用；同名返回 false，密码仍只保存盐和哈希。</summary>
    public bool TryCreateOperator(UserAccount currentUser, string userName, string password)
    {
        CheckAdministrator(currentUser, "只有管理员可以创建操作员。");
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ValidateNewPassword(password);
        var data = PasswordHasher.Hash(password);
        using var db = _database.Create();
        using var transaction = db.Database.BeginTransaction();
        string name = userName.Trim();
        if (db.Users.Any(x => x.UserName == name)) return false;
        db.Users.Add(new UserRow
        {
            UserName = name, PasswordSalt = data.Salt, PasswordHash = data.Hash,
            PasswordIterations = data.Iterations, Role = UserRole.Operator, IsEnabled = true
        });
        db.SaveChanges();
        transaction.Commit();
        return true;
    }

    /// <summary>用户管理启停账户时调用；只允许改变操作员，不改变管理员。</summary>
    public bool TrySetOperatorEnabled(UserAccount currentUser, long operatorId, bool enabled)
    {
        CheckAdministrator(currentUser, "只有管理员可以修改操作员状态。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorId);
        using var db = _database.Create();
        return db.Users.Where(x => x.Id == operatorId && x.Role == UserRole.Operator && x.IsEnabled != enabled)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.IsEnabled, enabled)) == 1;
    }

    /// <summary>密码窗口重置操作员密码时调用；不需要旧密码，不改变账户启停状态。</summary>
    public bool TryResetOperatorPassword(UserAccount currentUser, long operatorId, string newPassword)
    {
        CheckAdministrator(currentUser, "只有已启用的管理员可以重置操作员密码。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorId);
        ValidateNewPassword(newPassword);
        var data = PasswordHasher.Hash(newPassword);
        using var db = _database.Create();
        // 权限检查放进同一条 UPDATE，避免只相信登录时的旧账户对象。
        int count = db.Users.Where(x => x.Id == operatorId && x.Role == UserRole.Operator &&
            db.Users.Any(actor => actor.Id == currentUser.Id && actor.Role == UserRole.Administrator && actor.IsEnabled))
            .ExecuteUpdate(setters => setters
                .SetProperty(x => x.PasswordSalt, data.Salt)
                .SetProperty(x => x.PasswordHash, data.Hash)
                .SetProperty(x => x.PasswordIterations, data.Iterations));
        if (count == 1) AppLog.Info("重置操作员密码成功", $"ActorId={currentUser.Id}; TargetId={operatorId}");
        return count == 1;
    }

    /// <summary>密码窗口修改自己的密码时调用；验证旧密码，不改变用户名、角色和启停状态。</summary>
    public bool TryChangePassword(UserAccount currentUser, string oldPassword, string newPassword)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ValidateNewPassword(newPassword);
        if (!currentUser.IsEnabled || string.IsNullOrWhiteSpace(oldPassword)) return false;
        if (oldPassword == newPassword) throw new ArgumentException("新密码不能和原密码相同。");
        UserRow? old;
        using (var db = _database.Create())
            old = db.Users.AsNoTracking().FirstOrDefault(x => x.Id == currentUser.Id && x.IsEnabled);
        if (old is null || !PasswordHasher.Verify(oldPassword, old.PasswordSalt, old.PasswordHash, old.PasswordIterations))
            return false;

        // 哈希计算在数据库操作之外进行；ViewModel 仍把整个方法放到后台执行。
        var data = PasswordHasher.Hash(newPassword);
        using var writeDb = _database.Create();
        // 旧哈希也必须对上：等待期间若被重置过密码，本次不能覆盖管理员的新密码。
        int count = writeDb.Users.Where(x => x.Id == currentUser.Id && x.IsEnabled &&
            x.PasswordSalt.SequenceEqual(old.PasswordSalt) && x.PasswordHash.SequenceEqual(old.PasswordHash) &&
            x.PasswordIterations == old.PasswordIterations)
            .ExecuteUpdate(setters => setters
                .SetProperty(x => x.PasswordSalt, data.Salt)
                .SetProperty(x => x.PasswordHash, data.Hash)
                .SetProperty(x => x.PasswordIterations, data.Iterations));
        if (count == 1) AppLog.Info("修改自己的密码成功", $"UserId={currentUser.Id}");
        return count == 1;
    }

    private static void CheckAdministrator(UserAccount user, string message)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Role != UserRole.Administrator || !user.IsEnabled) throw new UnauthorizedAccessException(message);
    }

    /// <summary>创建、修改和重置密码共用的输入检查。</summary>
    private static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new ArgumentException("密码至少需要 6 个字符，不能全是空白。");
    }
}

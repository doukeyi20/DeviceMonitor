// 管理账户持久化、密码哈希和验证。
using System.Security.Cryptography;
using DeviceMonitor.Core;
using DeviceMonitor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DeviceMonitor.Core.Persistence;

/// <summary>账户的 EF 数据库操作。页面仍得到 UserAccount，不会得到密码盐和哈希。</summary>
public sealed class UserStore
{
    private readonly MonitorDbContextFactory _database;

    public UserStore(string databasePath) => _database = new(databasePath);

    /// <summary>只检查是否已有任何账户。</summary>
    public bool HasAnyUser()
    {
        using var db = _database.Create();
        return db.Users.Any();
    }

    /// <summary>停用、用户名或密码不对时返回 null。</summary>
    public UserAccount? CheckLogin(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            return null;
        UserRow? row;
        using (var db = _database.Create())
        {
            string name = userName.Trim();

            // 只读取账户验证所需信息，不跟踪查询结果。
            row = db.Users.AsNoTracking().FirstOrDefault(x => x.UserName == name);
        }

        if (row is null || !row.IsEnabled ||
            !PasswordHasher.Verify(password, row.PasswordSalt, row.PasswordHash, row.PasswordIterations))
            return null;
        return new UserAccount(row.Id, row.UserName, row.Role, row.IsEnabled);
    }

    /// <summary>数据库已有任何账户，就不再创建。</summary>
    public bool TryCreateFirstAdministrator(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        ValidateNewPassword(password);

        var data = PasswordHasher.Hash(password);
        using var db = _database.Create();
        // 首次管理员检查和创建使用同一写事务。
        using var transaction = db.Database.BeginTransaction();
        if (db.Users.Any())
            return false;
        db.Users.Add(new UserRow
        {
            UserName = userName.Trim(),
            PasswordSalt = data.Salt,
            PasswordHash = data.Hash,
            PasswordIterations = data.Iterations,
            Role = UserRole.Administrator,
            IsEnabled = true
        });
        db.SaveChanges();
        // 提交首次管理员创建事务。
        transaction.Commit();
        return true;
    }

    /// <summary>列表只含基本账户信息，不查询密码列。</summary>
    public IReadOnlyList<UserAccount> GetUsers(UserAccount currentUser)
    {
        CheckAdministrator(currentUser, "只有管理员可以查看账户。");
        using var db = _database.Create();
        return db.Users.AsNoTracking().OrderByDescending(x => x.Role)
            .ThenBy(x => EF.Functions.Collate(x.UserName, "NOCASE"))
            .Select(x => new UserAccount(x.Id, x.UserName, x.Role, x.IsEnabled)).ToList();
    }

    /// <summary>同名返回 false，密码仍只保存盐和哈希。</summary>
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
        if (db.Users.Any(x => x.UserName == name))
            return false;
        db.Users.Add(new UserRow
        {
            UserName = name,
            PasswordSalt = data.Salt,
            PasswordHash = data.Hash,
            PasswordIterations = data.Iterations,
            Role = UserRole.Operator,
            IsEnabled = true
        });
        db.SaveChanges();
        transaction.Commit();
        return true;
    }

    /// <summary>只允许改变操作员，不改变管理员。</summary>
    public bool TrySetOperatorEnabled(UserAccount currentUser, long operatorId, bool enabled)
    {
        CheckAdministrator(currentUser, "只有管理员可以修改操作员状态。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorId);
        using var db = _database.Create();
        return db.Users.Where(x => x.Id == operatorId && x.Role == UserRole.Operator && x.IsEnabled != enabled)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.IsEnabled, enabled)) == 1;
    }

    /// <summary>不需要旧密码，不改变账户启停状态。</summary>
    public bool TryResetOperatorPassword(UserAccount currentUser, long operatorId, string newPassword)
    {
        CheckAdministrator(currentUser, "只有已启用的管理员可以重置操作员密码。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorId);
        ValidateNewPassword(newPassword);
        var data = PasswordHasher.Hash(newPassword);
        using var db = _database.Create();

        int count = db.Users.Where(x => x.Id == operatorId && x.Role == UserRole.Operator &&
            db.Users.Any(actor => actor.Id == currentUser.Id && actor.Role == UserRole.Administrator && actor.IsEnabled))
            .ExecuteUpdate(setters => setters
                .SetProperty(x => x.PasswordSalt, data.Salt)
                .SetProperty(x => x.PasswordHash, data.Hash)
                .SetProperty(x => x.PasswordIterations, data.Iterations));
        if (count == 1)
            AppLog.Info("重置密码成功", $"操作者={currentUser.Id} 目标用户={operatorId}");
        return count == 1;
    }

    /// <summary>验证旧密码，不改变用户名、角色和启停状态。</summary>
    public bool TryChangePassword(UserAccount currentUser, string oldPassword, string newPassword)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ValidateNewPassword(newPassword);
        if (!currentUser.IsEnabled || string.IsNullOrWhiteSpace(oldPassword))
            return false;
        if (oldPassword == newPassword)
            throw new ArgumentException("新密码不能和原密码相同。");
        UserRow? old;
        using (var db = _database.Create())
            old = db.Users.AsNoTracking().FirstOrDefault(x => x.Id == currentUser.Id && x.IsEnabled);
        if (old is null || !PasswordHasher.Verify(oldPassword, old.PasswordSalt, old.PasswordHash, old.PasswordIterations))
            return false;

        var data = PasswordHasher.Hash(newPassword);
        using var writeDb = _database.Create();
        // 核对旧哈希，避免覆盖并发重置后的密码。
        int count = writeDb.Users.Where(x => x.Id == currentUser.Id && x.IsEnabled &&
            x.PasswordSalt.SequenceEqual(old.PasswordSalt) && x.PasswordHash.SequenceEqual(old.PasswordHash) &&
            x.PasswordIterations == old.PasswordIterations)
            .ExecuteUpdate(setters => setters
                .SetProperty(x => x.PasswordSalt, data.Salt)
                .SetProperty(x => x.PasswordHash, data.Hash)
                .SetProperty(x => x.PasswordIterations, data.Iterations));
        if (count == 1)
            AppLog.Info("修改密码成功", $"用户={currentUser.Id}");
        return count == 1;
    }

    private static void CheckAdministrator(UserAccount user, string message)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Role != UserRole.Administrator || !user.IsEnabled)
            throw new UnauthorizedAccessException(message);
    }

    /// <summary>创建、修改和重置密码共用的输入检查。</summary>
    private static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new ArgumentException("密码至少需要 6 个字符，不能全是空白。");
    }
}

/// <summary>Hash 生成密码的保存信息，Verify 检查输入密码是否正确，不保存明文密码。</summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 600_000;
    private const int MaxVerificationIterations = 2_000_000;

    /// <summary>生成随机盐、密码哈希和计算次数，供数据库保存。</summary>
    public static (byte[] Salt, byte[] Hash, int Iterations)
        Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // 每次生成随机盐，不存明文，同一密码也不会固定得到同一保存值。
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);

        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return (salt, hash, Iterations);
    }

    /// <summary>用原来的盐和次数计算输入密码，再比较哈希是否一致。</summary>
    public static bool Verify(
        string password,
        byte[] salt,
        byte[] storedHash,
        int iterations)
    {
        if (string.IsNullOrWhiteSpace(password) ||
            salt is null || salt.Length != SaltSize ||
            storedHash is null || storedHash.Length != HashSize ||
            iterations <= 0 || iterations > MaxVerificationIterations)
        {
            return false;
        }

        byte[] currentHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        // 使用固定时间比较，避免根据提前退出时间暴露比较进度。
        return CryptographicOperations.FixedTimeEquals(currentHash, storedHash);
    }
}

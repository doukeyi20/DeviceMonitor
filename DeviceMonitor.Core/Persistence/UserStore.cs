using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using System;
using DeviceMonitor.Core.Security;
using System.Collections.Generic;
using DeviceMonitor.Core.Diagnostics;

namespace DeviceMonitor.Core.Persistence
{

    /// <summary>
    /// 只操作 Users 表：首次建管理员、登录验证、操作员管理和修改/重置密码。
    /// 窗口一路传来同一个 UserStore；每个方法临时开自己的连接，不保存明文密码，也不打开 UI。
    /// </summary>
    public sealed class UserStore
    {
        // 连接数据库所需的设置配置字符串。只读字段，构造函数中初始化。
        private readonly string _connectionString;

        /// <summary>
        /// 由 App.OnStartup 创建，使用 DatabaseInitializer 准备好的数据库路径，后续窗口沿用同一个对象。
        /// 这里只保存连接配置；真正查询或写入时，下面的方法才打开连接。
        /// </summary>
        public UserStore(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();
        }


        /// <summary>
        /// LoginViewModel 构造时和 ShowAdminSetup 打开首次创建面板前调用这里。
        /// 查询 Users 表是否有任意账户；有返回 true，没有返回 false。它不判断当前输入的用户名是否正确。
        /// </summary>
        public bool HasAnyUser()
        {
            using SqliteConnection connection =
                new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM Users);";

            // EXISTS 返回 1 表示已有账户，返回 0 表示还没有账户。
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }



        /// <summary>
        /// 由 LoginViewModel.Login 调用，传入用户名和这次从密码框读到的密码。
        /// 先查用户名对应的记录，再用 PasswordHasher.Verify 验证；账户启用且密码匹配才返回 UserAccount，否则返回 null。
        /// 返回的用户对象不包含密码；是否打开主窗口由外面的登录流程决定。
        /// </summary>
        public UserAccount? CheckLogin(string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(userName) ||
                string.IsNullOrWhiteSpace(password))
            {
                return null;
            }

            long id;
            string savedUserName;
            byte[] salt;
            byte[] storedHash;
            int iterations;
            UserRole role;
            bool isEnabled;

            // 只查找用户名对应的一条记录；读完后关闭数据库连接。
            using (SqliteConnection connection = new SqliteConnection(_connectionString))
            {
                connection.Open();

                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT Id, UserName, PasswordSalt, PasswordHash,
                           PasswordIterations, Role, IsEnabled
                    FROM Users
                    WHERE UserName = $userName
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$userName", userName.Trim());

                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                id = reader.GetInt64(0);
                savedUserName = reader.GetString(1);
                salt = reader.GetFieldValue<byte[]>(2);
                storedHash = reader.GetFieldValue<byte[]>(3);
                iterations = reader.GetInt32(4);
                role = (UserRole)reader.GetInt32(5);
                isEnabled = reader.GetInt64(6) == 1;
            }

            // 用户停用或密码不匹配，都视为登录失败。
            if (!isEnabled ||
                !PasswordHasher.Verify(password, salt, storedHash, iterations))
            {
                return null;
            }

            // 返回给界面的用户信息中不包含密码或哈希。
            return new UserAccount(id, savedUserName, role, isEnabled);
        }

        /// <summary>
        /// 由 LoginViewModel.CreateFirstAdmin 在输入检查通过后调用。
        /// 先用 PasswordHasher.Hash 生成保存用的密码信息，再向 Users 插入管理员；只有表中没有任何账户时才允许插入，成功返回 true。
        /// </summary>
        public bool TryCreateFirstAdministrator(string userName, string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);
            ArgumentException.ThrowIfNullOrWhiteSpace(password);

            if (password.Length < 6)
            {
                throw new ArgumentException(
                    "密码至少需要 6 个字符。",
                    nameof(password));
            }

            var passwordData = PasswordHasher.Hash(password);

            using SqliteConnection connection = new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
        INSERT INTO Users
            (UserName, PasswordSalt, PasswordHash,
             PasswordIterations, Role, IsEnabled)
        SELECT
            $userName, $salt, $hash,
            $iterations, $role, 1
        WHERE NOT EXISTS (SELECT 1 FROM Users);
        """;

            command.Parameters.AddWithValue("$userName", userName.Trim());
            command.Parameters.AddWithValue("$salt", passwordData.Salt);
            command.Parameters.AddWithValue("$hash", passwordData.Hash);
            command.Parameters.AddWithValue("$iterations", passwordData.Iterations);
            command.Parameters.AddWithValue("$role", (int)UserRole.Administrator);

            return command.ExecuteNonQuery() == 1;

        }

        /// <summary>
        /// UserManagementViewModel.LoadUsers 调用，查询账户基本信息并返回 UserAccount 列表。
        /// 不返回密码信息；由 ViewModel 再把列表放进界面绑定的 Users。
        /// </summary>
        public IReadOnlyList<UserAccount> GetUsers(UserAccount currentUser)
        {
            ArgumentNullException.ThrowIfNull(currentUser);

            // 查询用户也属于管理操作，不能只依赖界面隐藏菜单。
            if (currentUser.Role != UserRole.Administrator ||
                !currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有管理员可以查看账户。");
            }

            using SqliteConnection connection =
                new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
        SELECT Id, UserName, Role, IsEnabled
        FROM Users
        ORDER BY Role DESC, UserName COLLATE NOCASE;
        """;

            using SqliteDataReader reader = command.ExecuteReader();
            var users = new List<UserAccount>();

            // 数据库每读到一行，就还原成一个 UserAccount。
            while (reader.Read())
            {
                users.Add(new UserAccount(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    (UserRole)reader.GetInt32(2),
                    reader.GetInt64(3) == 1));
            }

            return users;
        }




        /// <summary>
        /// 由 UserManagementViewModel.TryCreateOperator 调用。
        /// 检查当前管理员权限，用 PasswordHasher.Hash 处理密码，再向 Users 插入操作员；成功返回 true，用户名重复返回 false。
        /// </summary>
        public bool TryCreateOperator(
            UserAccount currentUser,
            string userName,
            string password)
        {
            ArgumentNullException.ThrowIfNull(currentUser);

            // 真正执行写入的地方也检查角色，不能只靠隐藏菜单。
            if (currentUser.Role != UserRole.Administrator ||
                !currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有管理员可以创建操作员。");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(userName);
            ArgumentException.ThrowIfNullOrWhiteSpace(password);

            if (password.Length < 6)
            {
                throw new ArgumentException("密码至少需要 6 个字符。");
            }

            var passwordData = PasswordHasher.Hash(password);

            using SqliteConnection connection =
                new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
        INSERT INTO Users
            (UserName, PasswordSalt, PasswordHash,
             PasswordIterations, Role, IsEnabled)
        VALUES
            ($userName, $salt, $hash,
             $iterations, $role, 1)
        ON CONFLICT(UserName) DO NOTHING;
        """;

            command.Parameters.AddWithValue("$userName", userName.Trim());
            command.Parameters.AddWithValue("$salt", passwordData.Salt);
            command.Parameters.AddWithValue("$hash", passwordData.Hash);
            command.Parameters.AddWithValue("$iterations", passwordData.Iterations);
            command.Parameters.AddWithValue("$role", (int)UserRole.Operator);

            // 插入 1 行为成功；用户名重复时不插入，返回 false。
            return command.ExecuteNonQuery() == 1;
        }

        /// <summary>
        /// 由 UserManagementViewModel.TrySetOperatorEnabled 调用，外层已经过确认框。
        /// 只修改指定操作员的 IsEnabled；不能用这里停用管理员。成功返回 true，目标不符合或无需改变时返回 false。
        /// </summary>
        public bool TrySetOperatorEnabled(
    UserAccount currentUser, long operatorId, bool enabled)
        {
            ArgumentNullException.ThrowIfNull(currentUser);

            // 修改账户必须由管理员发起。
            if (currentUser.Role != UserRole.Administrator ||
                !currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有管理员可以修改操作员状态。");
            }

            if (operatorId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(operatorId));
            }

            using SqliteConnection connection =
                new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
        UPDATE Users
        SET IsEnabled = $enabled
        WHERE Id = $operatorId
          AND Role = $operatorRole
          AND IsEnabled <> $enabled;
        """;

            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$operatorId", operatorId);
            command.Parameters.AddWithValue("$operatorRole", (int)UserRole.Operator);

            // 只有确实修改了一个操作员，才返回 true。
            return command.ExecuteNonQuery() == 1;
        }

        /// <summary>
        /// 管理员重置操作员密码时调用，不需要操作员的旧密码。
        /// 停用账户也能重置，但不会因此变成已启用，更不能借此修改管理员密码。
        /// </summary>
        public bool TryResetOperatorPassword(UserAccount currentUser, long operatorId, string newPassword)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            if (currentUser.Role != UserRole.Administrator || !currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以重置操作员密码。");
            if (operatorId <= 0)
                throw new ArgumentOutOfRangeException(nameof(operatorId));
            ValidateNewPassword(newPassword);
            var passwordData = PasswordHasher.Hash(newPassword);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Users
                SET PasswordSalt = $salt, PasswordHash = $hash, PasswordIterations = $iterations
                WHERE Id = $targetId AND Role = $operatorRole
                  AND EXISTS (SELECT 1 FROM Users AS actor
                              WHERE actor.Id = $actorId AND actor.Role = $adminRole AND actor.IsEnabled = 1);
                """;
            command.Parameters.AddWithValue("$salt", passwordData.Salt);
            command.Parameters.AddWithValue("$hash", passwordData.Hash);
            command.Parameters.AddWithValue("$iterations", passwordData.Iterations);
            command.Parameters.AddWithValue("$targetId", operatorId);
            command.Parameters.AddWithValue("$operatorRole", (int)UserRole.Operator);
            command.Parameters.AddWithValue("$actorId", currentUser.Id);
            command.Parameters.AddWithValue("$adminRole", (int)UserRole.Administrator);
            // 在同一条 UPDATE 中核对数据库里的最新权限，不只相信登录时的用户对象。
            bool changed = command.ExecuteNonQuery() == 1;
            if (changed)
                AppLog.Info("重置操作员密码成功", $"ActorId={currentUser.Id}; TargetId={operatorId}");
            return changed;
        }





        /// <summary>
        /// 修改密码窗口的 SaveAsync 调用：自己改密码必须先验证旧密码。
        /// 只替换密码对应的盐、哈希、迭代次数，不改用户名、角色、启停状态。
        /// </summary>
        public bool TryChangePassword(UserAccount currentUser, string oldPassword, string newPassword)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            ValidateNewPassword(newPassword);
            if (!currentUser.IsEnabled || string.IsNullOrWhiteSpace(oldPassword))
                return false;
            if (oldPassword == newPassword)
                throw new ArgumentException("新密码不能和原密码相同。");

            byte[] oldSalt;
            byte[] oldHash;
            int oldIterations;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT PasswordSalt, PasswordHash, PasswordIterations
                    FROM Users WHERE Id = $id AND IsEnabled = 1;
                    """;
                command.Parameters.AddWithValue("$id", currentUser.Id);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return false;
                oldSalt = reader.GetFieldValue<byte[]>(0);
                oldHash = reader.GetFieldValue<byte[]>(1);
                oldIterations = reader.GetInt32(2);
            }

            // 验证和计算新哈希比较费时。ViewModel 会把整个方法放到后台执行。
            if (!PasswordHasher.Verify(oldPassword, oldSalt, oldHash, oldIterations))
                return false;
            var passwordData = PasswordHasher.Hash(newPassword);
            using var writeConnection = new SqliteConnection(_connectionString);
            writeConnection.Open();
            using var update = writeConnection.CreateCommand();
            update.CommandText = """
                UPDATE Users
                SET PasswordSalt = $salt, PasswordHash = $hash, PasswordIterations = $iterations
                WHERE Id = $id AND IsEnabled = 1
                  AND PasswordSalt = $oldSalt AND PasswordHash = $oldHash
                  AND PasswordIterations = $oldIterations;
                """;
            update.Parameters.AddWithValue("$id", currentUser.Id);
            update.Parameters.AddWithValue("$salt", passwordData.Salt);
            update.Parameters.AddWithValue("$hash", passwordData.Hash);
            update.Parameters.AddWithValue("$iterations", passwordData.Iterations);
            update.Parameters.AddWithValue("$oldSalt", oldSalt);
            update.Parameters.AddWithValue("$oldHash", oldHash);
            update.Parameters.AddWithValue("$oldIterations", oldIterations);
            // 等待期间若被管理员重置过密码，旧密码已作废，本次不能把它覆盖回去。
            bool changed = update.ExecuteNonQuery() == 1;
            if (changed)
                AppLog.Info("修改自己的密码成功", $"UserId={currentUser.Id}");
            return changed;
        }

        /// <summary>
        /// 修改/重置密码时共用的底线检查。至少 6 个字符且不能全是空白；不合格就拒绝写库。
        /// </summary>
        private static void ValidateNewPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
                throw new ArgumentException("密码至少需要 6 个字符，不能全是空白。");
        }
    }
}

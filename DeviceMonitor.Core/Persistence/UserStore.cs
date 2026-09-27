using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using System;
using DeviceMonitor.Core.Security;
using System.Collections.Generic;

namespace DeviceMonitor.Core.Persistence
{
    /// <summary>
    /// 用户记录存储，操作数据库，进行登录功能各种验证的功能类
    /// </summary>
    public sealed class UserStore
    {
        // 连接数据库所需的设置配置字符串。只读字段，构造函数中初始化。
        private readonly string _connectionString;

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
        /// 检查数据库中是否已有用户账户的方法。用于首次启动时判断是否需要创建管理员。
        /// </summary>
        /// <returns></returns>
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
        /// 创建第一个管理员的方法
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="password"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
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
        /// 检查用户名和密码是否匹配的方法。匹配成功返回 UserAccount 对象，失败返回 null。
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="password"></param>
        /// <returns></returns>
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
        /// 创建操作员账户的方法。只有管理员可以创建操作员，且用户名不能重复。
        /// </summary>
        /// <param name="currentUser"></param>
        /// <param name="userName"></param>
        /// <param name="password"></param>
        /// <returns></returns>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
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
        /// 调用当时的账户列表
        /// </summary>
        /// <param name="currentUser"></param>
        /// <returns></returns>
        /// <exception cref="UnauthorizedAccessException"></exception>
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
        /// 修改指定操作员的启用状态
        /// </summary>
        /// <param name="currentUser"></param>
        /// <param name="operatorId"></param>
        /// <param name="enabled"></param>
        /// <returns></returns>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
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
    }
}

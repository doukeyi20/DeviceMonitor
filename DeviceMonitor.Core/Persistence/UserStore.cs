using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using System;
using DeviceMonitor.Core.Security;

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


        // 检查数据库中是否已有用户记录。= 检查是否有用户
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


        // 添加用户
        public void AddUser(
            string userName,
            UserRole role,
            string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);

            if (role != UserRole.Operator &&
                role != UserRole.Administrator)
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            // 密码只用于生成盐和哈希，不把原文写入数据库。
            var passwordData = PasswordHasher.Hash(password);

            // 打开数据库；方法结束时 using 会关闭连接。
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
                     $iterations, $role, 1);
                """;

            // 给 SQL 中的占位符填值。
            command.Parameters.AddWithValue("$userName", userName.Trim());
            command.Parameters.AddWithValue("$salt", passwordData.Salt);
            command.Parameters.AddWithValue("$hash", passwordData.Hash);
            command.Parameters.AddWithValue("$iterations", passwordData.Iterations);
            command.Parameters.AddWithValue("$role", (int)role);

            // 真正插入一条用户记录。
            command.ExecuteNonQuery();
        }

        // 创建管理员时判断并写入数据库
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
    }
}

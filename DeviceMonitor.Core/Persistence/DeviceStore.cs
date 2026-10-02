using System;
using System.Collections.Generic;
using System.Text;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;


namespace DeviceMonitor.Core.Persistence
{
    /// <summary>
    /// 负责将设备配置,操作后的数据保存到数据库。
    /// </summary>
    public class DeviceStore
    {
        private readonly string _connectionString;

        public DeviceStore(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            // 保存“怎样打开数据库”的配置；此时还没有打开连接。
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();
        }


        /// <summary>
        /// 新增设备。成功返回 true；设备名称重复返回 false。
        /// </summary>
        /// <param name="currentUser"></param>
        /// <param name="input"></param>
        /// <returns></returns>
        public bool TryAddDevice(UserAccount currentUser,DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            ArgumentNullException.ThrowIfNull(input);

            // 即使界面只给管理员显示按钮，真正写入时仍要检查权限。
            if (currentUser.Role != UserRole.Administrator ||
                !currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有管理员可以新增设备。");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Host);

            if (input.Port < 1 || input.Port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input.Port), "端口必须在 1 到 65535 之间。");
            }

            if (input.SamplingIntervalMilliseconds <= 0 ||
                input.ConnectTimeoutMilliseconds <= 0 ||
                input.ReadTimeoutMilliseconds <= 0 ||
                input.WriteTimeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input), "采集间隔和各项超时时间必须大于 0。");
            }

            using SqliteConnection connection = new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Devices
                    (Name, Host, Port, UnitId,
                     SamplingIntervalMilliseconds,
                     ConnectTimeoutMilliseconds,
                     ReadTimeoutMilliseconds,
                     WriteTimeoutMilliseconds, IsEnabled)
                VALUES
                    ($name, $host, $port, $unitId,
                     $samplingInterval, $connectTimeout,
                     $readTimeout, $writeTimeout, 0)
                ON CONFLICT(Name) DO NOTHING;
                """;

            // 新设备先停用；之后由管理员单独启用，不在新增时自动开始采集。
            // 用参数传值，不把用户输入直接拼进 SQL。
            command.Parameters.AddWithValue("$name", input.Name.Trim());
            command.Parameters.AddWithValue("$host", input.Host.Trim());
            command.Parameters.AddWithValue("$port", input.Port);
            command.Parameters.AddWithValue("$unitId", (int)input.UnitId);
            command.Parameters.AddWithValue(
                "$samplingInterval", input.SamplingIntervalMilliseconds);
            command.Parameters.AddWithValue(
                "$connectTimeout", input.ConnectTimeoutMilliseconds);
            command.Parameters.AddWithValue(
                "$readTimeout", input.ReadTimeoutMilliseconds);
            command.Parameters.AddWithValue(
                "$writeTimeout", input.WriteTimeoutMilliseconds);

            // 插入 1 行就是成功；名称重复时不插入，结果为 0。
            return command.ExecuteNonQuery() == 1;
        }


        /// <summary>
        /// 读取数据库中的设备配置；默认返回全部，采集页面可指定只取已启用设备。
        /// </summary>
        public IReadOnlyList<DeviceInfo> GetDevices(
            UserAccount currentUser, bool enabledOnly = false)
        {
            ArgumentNullException.ThrowIfNull(currentUser);

            // 管理员和操作员都需要查看设备；未启用账户不能读取。
            if (!currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("当前账户不能查看设备。");
            }

            using SqliteConnection connection =
                new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
        SELECT Id, Name, Host, Port, UnitId,
               SamplingIntervalMilliseconds,
               ConnectTimeoutMilliseconds,
               ReadTimeoutMilliseconds,
               WriteTimeoutMilliseconds, IsEnabled
        FROM Devices
        WHERE ($enabledOnly = 0 OR IsEnabled = 1)
        ORDER BY Name COLLATE NOCASE;
        """;

            // 默认 0 返回全部，保证设备管理仍能显示并启用停用的设备。
            // 采集页面传 true 时使用 1，只返回 IsEnabled = 1 的记录。
            command.Parameters.AddWithValue("$enabledOnly", enabledOnly ? 1 : 0);

            using SqliteDataReader reader = command.ExecuteReader();

            var devices = new List<DeviceInfo>();

            // 每读到数据库中的一行，就创建一个 DeviceInfo 放进列表。
            while (reader.Read())
            {
                devices.Add(new DeviceInfo(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    checked((byte)reader.GetInt32(4)),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.GetBoolean(9)));
            }

            return devices;
        }

        /// <summary>
        /// 根据设备编号修改已有设备配置。
        /// 成功修改返回 true；设备不存在或名称冲突返回 false。
        /// </summary>
        public bool TryUpdateDevice(
            UserAccount currentUser,
            long deviceId,
            DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            ArgumentNullException.ThrowIfNull(input);

            // 真正执行修改的位置也要检查管理员权限。
            if (currentUser.Role != UserRole.Administrator ||
                !currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有管理员可以修改设备。");
            }

            if (deviceId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deviceId),
                    "设备编号必须大于 0。");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Host);

            if (input.Port < 1 || input.Port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input.Port),
                    "端口必须在 1 到 65535 之间。");
            }

            if (input.SamplingIntervalMilliseconds <= 0 ||
                input.ConnectTimeoutMilliseconds <= 0 ||
                input.ReadTimeoutMilliseconds <= 0 ||
                input.WriteTimeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input),
                    "采集间隔和各项超时时间必须大于 0。");
            }

            using SqliteConnection connection =
                new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            // 编辑连接配置时不修改 IsEnabled，保留原来的启停设置。
            command.CommandText = """
        UPDATE OR IGNORE Devices
        SET Name = $name,
            Host = $host,
            Port = $port,
            UnitId = $unitId,
            SamplingIntervalMilliseconds = $samplingInterval,
            ConnectTimeoutMilliseconds = $connectTimeout,
            ReadTimeoutMilliseconds = $readTimeout,
            WriteTimeoutMilliseconds = $writeTimeout
        WHERE Id = $deviceId;
        """;

            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.AddWithValue("$name", input.Name.Trim());
            command.Parameters.AddWithValue("$host", input.Host.Trim());
            command.Parameters.AddWithValue("$port", input.Port);
            command.Parameters.AddWithValue("$unitId", (int)input.UnitId);
            command.Parameters.AddWithValue(
                "$samplingInterval",
                input.SamplingIntervalMilliseconds);
            command.Parameters.AddWithValue(
                "$connectTimeout",
                input.ConnectTimeoutMilliseconds);
            command.Parameters.AddWithValue(
                "$readTimeout",
                input.ReadTimeoutMilliseconds);
            command.Parameters.AddWithValue(
                "$writeTimeout",
                input.WriteTimeoutMilliseconds);

            // 修改到一行返回 true；找不到 Id 或名称冲突时返回 false。
            return command.ExecuteNonQuery() == 1;
        }

        /// <summary>
        /// 修改指定设备是否允许启动，仅管理员可以执行。
        /// 修改成功返回 true；设备不存在或已是目标状态时返回 false。
        /// </summary>
        public bool TrySetDeviceEnabled(
            UserAccount currentUser,
            long deviceId,
            bool enabled)
        {
            ArgumentNullException.ThrowIfNull(currentUser);

            // 权限检查放在真正写数据库的位置，不能只靠界面隐藏按钮。
            if (currentUser.Role != UserRole.Administrator ||
                !currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有管理员可以修改设备启停状态。");
            }

            if (deviceId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deviceId), "设备编号必须大于 0。");
            }

            using SqliteConnection connection = new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Devices
                SET IsEnabled = $enabled
                WHERE Id = $deviceId AND IsEnabled <> $enabled;
                """;

            command.Parameters.AddWithValue("$deviceId", deviceId);
            // SQLite 用整数保存布尔值：1 是启用，0 是停用。
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);

            // 这里只保存配置，不负责连接设备；界面需要重新读取列表来显示新状态。
            return command.ExecuteNonQuery() == 1;
        }

    }
}

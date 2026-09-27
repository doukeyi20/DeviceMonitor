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
                     WriteTimeoutMilliseconds)
                VALUES
                    ($name, $host, $port, $unitId,
                     $samplingInterval, $connectTimeout,
                     $readTimeout, $writeTimeout)
                ON CONFLICT(Name) DO NOTHING;
                """;

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
        /// 读取数据库中保存的所有设备配置,返回设备对象。
        /// </summary>
        public IReadOnlyList<DeviceInfo> GetDevices(UserAccount currentUser)
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
               WriteTimeoutMilliseconds
        FROM Devices
        ORDER BY Name COLLATE NOCASE;
        """;

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
                    reader.GetInt32(8)));
            }

            return devices;
        }

    }
}

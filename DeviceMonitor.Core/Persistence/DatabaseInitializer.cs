using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DeviceMonitor.Core.Persistence
{
    // 用于初始化数据库
    public class DatabaseInitializer
    {
        // 保存数据库所在的文件夹路径。
        private readonly string _databaseDirectory;

        // 完整的数据库文件路径，供后续初始化数据库时使用。
        public string DatabasePath { get; }
        
        public DatabaseInitializer()
        {
            // 获取当前 Windows 用户的本地应用数据目录，
            // 例如 C:\Users\用户名\AppData\Local。
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("无法获取应用数据目录。");
            }

            // Path.Combine 负责拼接路径
            _databaseDirectory = Path.Combine(localAppData, "DeviceMonitor");
            DatabasePath = Path.Combine(_databaseDirectory, "device-monitor.db");
        }

        // 创建库所在文件夹并连接数据库并建User表
        public void Initialize()
        {
            // 文件夹不存在就创建；已存在也可以安全调用。
            Directory.CreateDirectory(_databaseDirectory);

            // 指定数据库文件，以及“文件不存在时创建”的打开方式。= 保存配置
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            // 打开连接时才会创建数据库文件；方法结束时 using 会关闭连接。= 传入配置，得到连接对象
            using SqliteConnection connection = new SqliteConnection(connectionString);
            connection.Open();

            // 创建与当前连接关联的 SQL 命令。 = 得到能执行命令的命令对象
            using SqliteCommand command = connection.CreateCommand();

            // 三个引号包住的是一段多行字符串，里面写的是 SQLite 建表语句。
            command.CommandText = """
    CREATE TABLE IF NOT EXISTS Users (
        Id INTEGER PRIMARY KEY,
        UserName TEXT NOT NULL COLLATE NOCASE UNIQUE,
        PasswordSalt BLOB NOT NULL,
        PasswordHash BLOB NOT NULL,
        PasswordIterations INTEGER NOT NULL CHECK (PasswordIterations > 0),
        Role INTEGER NOT NULL CHECK (Role IN (0, 1)),
        IsEnabled INTEGER NOT NULL CHECK (IsEnabled IN (0, 1))
    );
    """;

            // 执行建User表命令。建表不返回数据，所以用 ExecuteNonQuery。
            command.ExecuteNonQuery();

            // 准备设备配置Devices表。这里只保存配置，不保存运行时的在线/离线状态。
            command.CommandText = """
    CREATE TABLE IF NOT EXISTS Devices (
        Id INTEGER PRIMARY KEY,
        Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
        Host TEXT NOT NULL,
        Port INTEGER NOT NULL CHECK (Port BETWEEN 1 AND 65535),
        UnitId INTEGER NOT NULL CHECK (UnitId BETWEEN 0 AND 255),
        SamplingIntervalMilliseconds INTEGER NOT NULL
            CHECK (SamplingIntervalMilliseconds > 0),
        ConnectTimeoutMilliseconds INTEGER NOT NULL
            CHECK (ConnectTimeoutMilliseconds > 0),
        ReadTimeoutMilliseconds INTEGER NOT NULL
            CHECK (ReadTimeoutMilliseconds > 0),
        WriteTimeoutMilliseconds INTEGER NOT NULL
            CHECK (WriteTimeoutMilliseconds > 0)
    );
    """;

            command.ExecuteNonQuery();

        }

    }
}

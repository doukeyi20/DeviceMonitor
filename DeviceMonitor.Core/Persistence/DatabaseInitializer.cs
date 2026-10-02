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
        // 当前程序支持的最新数据库结构版本。
        // 版本 1：Users 表和设备基本配置。
        // 版本 2：Devices 表增加 IsEnabled，保存是否允许采集。
        private const int CurrentDatabaseVersion = 2;

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

        /// <summary>
        /// 准备数据库，并根据结构版本执行初始化。
        /// 保留已有数据，并将 Users、Devices 两张表升级到当前结构版本。
        /// </summary>
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

            // 建表与版本登记作为一组修改；全部成功后才正式保存。
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;

            // 读取数据库文件中保存的结构版本；未登记过版本时为 0。
            command.CommandText = "PRAGMA user_version;";
            int databaseVersion = Convert.ToInt32(command.ExecuteScalar());

            // 旧程序不能处理比自己更新的数据库结构。
            if (databaseVersion > CurrentDatabaseVersion)
            {
                throw new InvalidOperationException(
                    $"数据库结构版本为 {databaseVersion}，" +
                    $"当前程序只支持到版本 {CurrentDatabaseVersion}。");
            }

            // 已经准备过当前版本，结束方法；using 仍会释放各项资源。
            if (databaseVersion == CurrentDatabaseVersion)
            {
                return;
            }

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

            // 先准备版本 1 的设备表，再按版本补齐新列。
            // 新数据库和旧数据库使用同一条升级路线，避免重复添加 IsEnabled。
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

            if (databaseVersion < 2)
            {
                // 在原来的表上添加一列，不会新建另一张设备表，也不会删除旧记录。
                // 旧设备一律先停用，避免升级后未经确认就自动采集。
                // IsEnabled 是保存的启停配置，不是运行时的在线/离线状态。
                command.CommandText = """
                    ALTER TABLE Devices
                    ADD COLUMN IsEnabled INTEGER NOT NULL DEFAULT 0
                        CHECK (IsEnabled IN (0, 1));
                    """;
                command.ExecuteNonQuery();

                // 只有添加新列成功，才登记版本 2。
                command.CommandText = "PRAGMA user_version = 2;";
                command.ExecuteNonQuery();
            }

            // 表结构与版本号一起保存；中途出错时事务会回滚。
            transaction.Commit();

        }

    }
}

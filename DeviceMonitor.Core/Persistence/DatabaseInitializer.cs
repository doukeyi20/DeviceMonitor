using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DeviceMonitor.Core.Persistence
{

    /// <summary>
    /// 准备数据库：新库建表，旧库升级，保留已有配置和历史。
    /// </summary>
    public class DatabaseInitializer
    {
        // 当前程序支持的最新数据库结构版本。
        // 版本 1：Users 表和设备基本配置。
        // 版本 2：Devices 表增加 IsEnabled，保存是否允许采集。
        // 版本 3：增加 DevicePoints 表，保存每台设备要读取的采集点。
        // 版本 4：增加 HistoryRecords 表，保存读数和读取时间。
        // 版本 5：为全部设备的历史时间排序增加索引，不改表中的记录。
        // 版本 6：采集点可选报警上下限，另建报警记录表，旧点位默认不设置上下限。
        // 版本 7：采集点编号只增不复用，避免删点时还没返回的旧读数混入新点历史。
        private const int CurrentDatabaseVersion = 7;

        // 保存数据库所在的文件夹路径。C:\Users\用户名\AppData\Local\DeviceMonitor
        private readonly string _databaseDirectory;

        // 完整的数据库文件路径，供后续初始化数据库时使用。C:\Users\用户名\AppData\Local\DeviceMonitor\device-monitor.db
        public string DatabasePath { get; }

        /// <summary>
        /// 算出数据库文件夹和文件路径，此时还不创建文件。
        /// </summary>
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
        /// 使用指定的数据库路径，供独立测试使用；仍要调用 Initialize 才会建库。
        /// </summary>
        public DatabaseInitializer(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            if (!Path.IsPathFullyQualified(databasePath) ||
                string.IsNullOrEmpty(Path.GetFileName(databasePath)))
            {
                throw new ArgumentException("请提供完整的数据库文件路径。", nameof(databasePath));
            }

            DatabasePath = Path.GetFullPath(databasePath);
            _databaseDirectory = Path.GetDirectoryName(DatabasePath)
                ?? throw new ArgumentException("数据库路径缺少文件夹。", nameof(databasePath));
        }

        /// <summary>
        /// 由 App.OnStartup 在程序启动时调用。
        /// 打开数据库，检查版本并按需建表或升级，全部成功才保存。
        /// </summary>
        public void Initialize()
        {
            // 文件路径在这个对象创建时已经生成，文件夹不存在就创建；已存在也可以安全调用。
            Directory.CreateDirectory(_databaseDirectory);

            // 准备连接设置
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate, // 允许读写；文件不存在时可以创建 
                ForeignKeys = true // 开启所属关系检查，采集点不能指向一台不存在的设备。
            }.ToString();

            // 创建连接对象
            // 方法结束时 using 负责关闭连接。
            using SqliteConnection connection = new SqliteConnection(connectionString);
            
            // 打开数据库文件
            connection.Open();

            // 准备执行 SQL 命令的对象
            using SqliteCommand command = connection.CreateCommand();

            // 后面的修改要么全部成功，要么失败了撤回，不能只改一半。
            using SqliteTransaction transaction = connection.BeginTransaction();
            // 让这条 SQL 命令也按上面的规则执行。
            command.Transaction = transaction;

            // 看看数据库是第几版；新数据库还没登记时是 0。
            command.CommandText = "PRAGMA user_version;";
            // 执行查询，把查到的版本号存起来。
            int databaseVersion = Convert.ToInt32(command.ExecuteScalar());

            // databaseVersion 是数据库版本，CurrentDatabaseVersion 是程序支持的最高版本。
            // 数据库版本更高，程序处理不了，报错停止。
            if (databaseVersion > CurrentDatabaseVersion)
            {
                throw new InvalidOperationException(
                    $"数据库结构版本为 {databaseVersion}，" +
                    $"当前程序只支持到版本 {CurrentDatabaseVersion}。");
            }

            // 版本一样，不用升级；结束这个方法，回到 App 继续启动。
            if (databaseVersion == CurrentDatabaseVersion)
            {
                return;
            }

            // 能走到这里，说明数据库版本旧，接着往下升级。
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

            if (databaseVersion < 3)
            {
                // 不改动原来的设备表，另外建表保存设备下面的采集点。
                // DeviceId 与 Devices.Id 对应；同一设备内，点位名称不能重复。
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS DevicePoints (
                        Id INTEGER PRIMARY KEY,
                        DeviceId INTEGER NOT NULL REFERENCES Devices(Id) ON DELETE RESTRICT,
                        Name TEXT NOT NULL COLLATE NOCASE,
                        RegisterType INTEGER NOT NULL CHECK (RegisterType IN (3, 4)),
                        Address INTEGER NOT NULL CHECK (Address BETWEEN 0 AND 65535),
                        DataType INTEGER NOT NULL CHECK (DataType IN (0, 1)),
                        UNIQUE (DeviceId, Name)
                    );
                    """;
                command.ExecuteNonQuery();

                command.CommandText = "PRAGMA user_version = 3;";
                command.ExecuteNonQuery();
            }

            if (databaseVersion < 4)
            {
                // 另外建一张历史表，不改动已经保存的用户、设备和采集点。
                // 名称也保存一份：以后设备或点位改名，过去的记录仍显示读取时的名称。
                // 这里故意不关联配置表的删除操作，删除配置不会连带删除历史记录。
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS HistoryRecords (
                        Id INTEGER PRIMARY KEY,
                        DeviceId INTEGER NOT NULL CHECK (DeviceId > 0),
                        DeviceName TEXT NOT NULL,
                        PointId INTEGER NOT NULL CHECK (PointId > 0),
                        PointName TEXT NOT NULL,
                        Value INTEGER NOT NULL,
                        ReadAtUtc TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_HistoryRecords_DeviceId_ReadAtUtc
                    ON HistoryRecords (DeviceId, ReadAtUtc);
                    """;
                command.ExecuteNonQuery();

                // 表和索引都建好后，再把结构版本记为 4。
                command.CommandText = "PRAGMA user_version = 4;";
                command.ExecuteNonQuery();
            }

            if (databaseVersion < 5)
            {
                // 相当于给历史时间准备查找目录，不必每次把全部历史重新排序。
                command.CommandText = """
                    CREATE INDEX IF NOT EXISTS IX_HistoryRecords_ReadAtUtc_Id
                    ON HistoryRecords (ReadAtUtc DESC, Id DESC);
                    PRAGMA user_version = 5;
                    """;
                command.ExecuteNonQuery();
            }

            if (databaseVersion < 6)
            {
                // 只给原来的采集点表补两列；旧配置仍是原来的记录，不会被删除。
                // 没填上下限时是 NULL，意味着不判断这一侧的数值报警。
                command.CommandText = """
                    ALTER TABLE DevicePoints ADD COLUMN LowerLimit INTEGER NULL;
                    ALTER TABLE DevicePoints ADD COLUMN UpperLimit INTEGER NULL;

                    CREATE TABLE IF NOT EXISTS AlarmRecords (
                        Id INTEGER PRIMARY KEY,
                        EventId TEXT NOT NULL UNIQUE,
                        DeviceId INTEGER NOT NULL CHECK (DeviceId > 0),
                        DeviceName TEXT NOT NULL,
                        PointId INTEGER NULL CHECK (PointId IS NULL OR PointId > 0),
                        PointName TEXT NOT NULL,
                        Kind INTEGER NOT NULL CHECK (Kind IN (1, 2, 3)),
                        State INTEGER NOT NULL CHECK (State IN (1, 2, 3)),
                        Message TEXT NOT NULL,
                        Value INTEGER NULL,
                        LimitValue INTEGER NULL,
                        StartedAtUtc TEXT NOT NULL,
                        LastSeenAtUtc TEXT NOT NULL,
                        EndedAtUtc TEXT NULL,
                        Occurrences INTEGER NOT NULL CHECK (Occurrences > 0)
                    );
                    CREATE INDEX IF NOT EXISTS IX_AlarmRecords_StartedAtUtc_Id
                    ON AlarmRecords (StartedAtUtc DESC, Id DESC);
                    CREATE INDEX IF NOT EXISTS IX_AlarmRecords_DeviceId_StartedAtUtc_Id
                    ON AlarmRecords (DeviceId, StartedAtUtc DESC, Id DESC);
                    PRAGMA user_version = 6;
                    """;
                command.ExecuteNonQuery();
            }

            if (databaseVersion < 7)
            {
                // 重建点位表，让编号只增不复用，避免旧读数混到新点位名下。
                // 保留原数据；任一步失败都回到升级前的状态。
                command.CommandText = """
                    CREATE TABLE DevicePoints_v7 (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        DeviceId INTEGER NOT NULL REFERENCES Devices(Id) ON DELETE RESTRICT,
                        Name TEXT NOT NULL COLLATE NOCASE,
                        RegisterType INTEGER NOT NULL CHECK (RegisterType IN (3, 4)),
                        Address INTEGER NOT NULL CHECK (Address BETWEEN 0 AND 65535),
                        DataType INTEGER NOT NULL CHECK (DataType IN (0, 1)),
                        LowerLimit INTEGER NULL,
                        UpperLimit INTEGER NULL,
                        UNIQUE (DeviceId, Name)
                    );
                    INSERT INTO DevicePoints_v7
                        (Id, DeviceId, Name, RegisterType, Address, DataType, LowerLimit, UpperLimit)
                    SELECT Id, DeviceId, Name, RegisterType, Address, DataType, LowerLimit, UpperLimit
                    FROM DevicePoints;
                    DROP TABLE DevicePoints;
                    ALTER TABLE DevicePoints_v7 RENAME TO DevicePoints;

                    -- sqlite_sequence 记住分配过的最大编号；空表也先准备这一条登记。
                    INSERT INTO sqlite_sequence (name, seq)
                    SELECT 'DevicePoints', 0
                    WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'DevicePoints');
                    -- 旧点可能只剩历史或报警；新编号也必须越过这些记录里的最大点位编号。
                    UPDATE sqlite_sequence SET seq = MAX(
                        seq,
                        COALESCE((SELECT MAX(Id) FROM DevicePoints), 0),
                        COALESCE((SELECT MAX(PointId) FROM HistoryRecords), 0),
                        COALESCE((SELECT MAX(PointId) FROM AlarmRecords), 0)
                    ) WHERE name = 'DevicePoints';
                    PRAGMA user_version = 7;
                    """;
                command.ExecuteNonQuery();
            }

            // 前面的修改都成功了，到这里才一起正式保存。
            transaction.Commit();

        }

    }
}

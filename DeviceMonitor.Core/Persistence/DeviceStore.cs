using System;
using System.Collections.Generic;
using System.Text;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;


namespace DeviceMonitor.Core.Persistence
{

    /// <summary>
    /// 设备和点位配置的数据库操作：Devices 存设备，DevicePoints 用 DeviceId 关联设备。
    /// Get 返回配置对象，TryAdd/Update/Delete 改记录；这里不连接 Modbus，也不直接刷新界面表格。
    /// </summary>
    public class DeviceStore
    {
        // 两种设备查询使用相同的列顺序，ReadDevice 按这个顺序还原对象。
        private const string DeviceColumns = """
            Id, Name, Host, Port, UnitId, SamplingIntervalMilliseconds,
            ConnectTimeoutMilliseconds, ReadTimeoutMilliseconds, WriteTimeoutMilliseconds, IsEnabled
            """;
        private readonly string _connectionString;

        /// <summary>
        /// 由 App.OnStartup 创建，传入 DatabaseInitializer.DatabasePath，之后同一个对象一路传到各页面。
        /// 这里只保存 SQLite 连接配置；每个操作方法执行时才创建并打开自己的连接。
        /// </summary>
        public DeviceStore(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            // 保存“怎样打开数据库”的配置；此时还没有打开连接。
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true
            }.ToString();
        }


        /// <summary>
        /// 设备管理、采集点配置和实时监控的“刷新设备”会调用这里，准备各自的设备列表。
        /// 查询 Devices 表，把每一行组装成 DeviceInfo 返回；默认查全部，enabledOnly 为 true 时只查启用设备，不连接设备。
        /// </summary>
        public IReadOnlyList<DeviceInfo> GetDevices(
            UserAccount currentUser, bool enabledOnly = false)
        {
            CheckDeviceReadPermission(currentUser);

            using SqliteConnection connection =
                new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
        SELECT {DeviceColumns}
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
                devices.Add(ReadDevice(reader));
            }

            return devices;
        }

        /// <summary>
        /// 采集任务和单次测试已经知道设备编号时调用，只查询这一台，不把全表读回来再找。
        /// 找不到返回 null；enabledOnly 为 true 时，停用设备也返回 null。
        /// </summary>
        public DeviceInfo? GetDevice(UserAccount currentUser, long deviceId, bool enabledOnly = false)
        {
            CheckDeviceReadPermission(currentUser);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {DeviceColumns} FROM Devices
                WHERE Id = $deviceId AND ($enabledOnly = 0 OR IsEnabled = 1);
                """;
            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.AddWithValue("$enabledOnly", enabledOnly ? 1 : 0);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadDevice(reader) : null;
        }


        /// <summary>
        /// 由 DeviceManagementViewModel.TryAddDevice 调用，输入来自设备表单的 Save。
        /// 检查权限和配置后向 Devices 表插入一行；成功返回 true，同名不插入并返回 false。表格刷新由 ViewModel 负责。
        /// </summary>
        public bool TryAddDevice(UserAccount currentUser, DeviceInput input)
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
                    (Id, Name, Host, Port, UnitId,
                     SamplingIntervalMilliseconds,
                     ConnectTimeoutMilliseconds,
                     ReadTimeoutMilliseconds,
                     WriteTimeoutMilliseconds, IsEnabled)
                VALUES
                    ((SELECT MAX(
                        COALESCE((SELECT MAX(Id) FROM Devices), 0),
                        COALESCE((SELECT MAX(DeviceId) FROM HistoryRecords), 0),
                        COALESCE((SELECT MAX(DeviceId) FROM AlarmRecords), 0)
                    ) + 1), $name, $host, $port, $unitId,
                     $samplingInterval, $connectTimeout,
                     $readTimeout, $writeTimeout, 0)
                ON CONFLICT(Name) DO NOTHING;
                """;

            // 新设备先停用；之后由管理员单独启用，不在新增时自动开始采集。
            // 编号也要避开历史中的旧设备；否则删掉设备后再新增，可能把两台设备的历史混在一起。
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
        /// 由 DeviceManagementViewModel.TryUpdateDevice 调用，传入原设备 Id 和表单的新配置。
        /// 修改 Devices 表中原来的那条记录，Id 不变；不新增记录，也不直接更新界面集合。
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
        /// 由 DeviceManagementViewModel.TrySetDeviceEnabled 调用。
        /// 把指定设备的 IsEnabled 保存成 1 或 0；这是允许采集的配置，不是在线状态。这里不会直接调用采集任务的 Start 或 StopAsync。
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

        /// <summary>
        /// 设备管理删除命令通过 DeviceRuntimeManager 调用。先删所属点位，再删设备，这两步一起成功才保存。
        /// 历史读数和报警表不参与删除，过去的数据仍可以查询。
        /// </summary>
        public bool TryDeleteDevice(UserAccount currentUser, long deviceId)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
            if (currentUser.Role != UserRole.Administrator || !currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以删除设备。");

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$deviceId", deviceId);

            // 点位表禁止留下“设备已不存在”的配置，所以必须先处理设备下面的点。
            command.CommandText = "DELETE FROM DevicePoints WHERE DeviceId = $deviceId;";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM Devices WHERE Id = $deviceId;";
            bool deleted = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return deleted;
        }

        /// <summary>
        /// PointViewModel.LoadPoints、AcquisitionViewModel 的点位加载 / 单次读取配置查询，以及 DeviceSamplingViewModel.LoadConfiguration 会调用这里。
        /// 按设备 Id 查询 DevicePoints，把每一行组装成 PointInfo 列表返回；它们是配置，不是真实读数。
        /// </summary>
        public IReadOnlyList<PointInfo> GetPoints(UserAccount currentUser, long deviceId)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
            if (!currentUser.IsEnabled)
                throw new UnauthorizedAccessException("当前账户不能查看采集点。");

            using SqliteConnection connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, DeviceId, Name, RegisterType, Address, DataType, LowerLimit, UpperLimit
                FROM DevicePoints
                WHERE DeviceId = $deviceId
                ORDER BY Name COLLATE NOCASE;
                """;
            // 窗口传入当前设备的 Id，SQL 用这个编号筛选记录。
            command.Parameters.AddWithValue("$deviceId", deviceId);

            using SqliteDataReader reader = command.ExecuteReader();
            var points = new List<PointInfo>();
            while (reader.Read())
            {
                // 读到一行，就组装一个完整的 PointInfo，供表格显示和后续读取使用。
                points.Add(new PointInfo(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    (RegisterType)reader.GetInt32(3),
                    checked((ushort)reader.GetInt32(4)),
                    (PointDataType)reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7)));
            }
            return points;
        }

        /// <summary>
        /// 由 PointViewModel.SavePoint 在新增模式下调用。
        /// 向 DevicePoints 表插入一条属于指定设备的配置；同名或设备停用 / 不存在时返回 false，不改设备寄存器。
        /// </summary>
        public bool TryAddPoint(UserAccount currentUser, long deviceId, PointInput input)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            ArgumentNullException.ThrowIfNull(input);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);

            // 界面能显示按钮不等于有保存权限，真正写入时也要检查。
            if (currentUser.Role != UserRole.Administrator || !currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以新增采集点。");

            ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
            if (!Enum.IsDefined(input.RegisterType))
                throw new ArgumentOutOfRangeException(nameof(input.RegisterType));
            if (!Enum.IsDefined(input.DataType))
                throw new ArgumentOutOfRangeException(nameof(input.DataType));
            PointInfo.ValidateLimits(input.DataType, input.LowerLimit, input.UpperLimit);

            using SqliteConnection connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO DevicePoints (Id, DeviceId, Name, RegisterType, Address, DataType, LowerLimit, UpperLimit)
                SELECT (
                    SELECT MAX(
                        COALESCE((SELECT MAX(Id) FROM DevicePoints), 0),
                        COALESCE((SELECT MAX(PointId) FROM HistoryRecords), 0),
                        COALESCE((SELECT MAX(PointId) FROM AlarmRecords), 0),
                        COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'DevicePoints'), 0)
                    ) + 1
                ), $deviceId, $name, $registerType, $address, $dataType, $lowerLimit, $upperLimit
                WHERE EXISTS (SELECT 1 FROM Devices WHERE Id = $deviceId AND IsEnabled = 1)
                ON CONFLICT(DeviceId, Name) DO NOTHING;
                """;
            // 当前设备的编号与输入一起保存，重开窗口后仍能找回这台设备的点位。
            // 新编号要越过历史里的旧编号，也要越过“已经删掉但还没保存读数”的旧点编号。
            // sqlite_sequence 由数据库记住曾分配的最大值；插入成功后也会自动更新，不随删点归零。
            // SQL 还会检查数据库中的最新启用状态，不只相信窗口打开时的设备对象。
            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.AddWithValue("$name", input.Name.Trim());
            command.Parameters.AddWithValue("$registerType", (int)input.RegisterType);
            command.Parameters.AddWithValue("$address", (int)input.Address);
            command.Parameters.AddWithValue("$dataType", (int)input.DataType);
            // 参数中的 DBNull.Value 才是数据库的 NULL，不是 0，也不是空字符串。
            command.Parameters.AddWithValue("$lowerLimit", (object?)input.LowerLimit ?? DBNull.Value);
            command.Parameters.AddWithValue("$upperLimit", (object?)input.UpperLimit ?? DBNull.Value);

            return command.ExecuteNonQuery() == 1;
        }

        /// <summary>
        /// 由 PointViewModel.SavePoint 在编辑模式下调用。
        /// 同时按设备 Id 和点位 Id 修改原配置，点位 Id 不变；名称冲突、记录不存在或设备停用时返回 false。
        /// </summary>
        public bool TryUpdatePoint(
            UserAccount currentUser, long deviceId, long pointId, PointInput input)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            ArgumentNullException.ThrowIfNull(input);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointId);

            // 真正写入的位置再检查一次，不只靠按钮能不能点。
            if (currentUser.Role != UserRole.Administrator || !currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以修改采集点。");

            ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
            if (!Enum.IsDefined(input.RegisterType))
                throw new ArgumentOutOfRangeException(nameof(input.RegisterType));
            if (!Enum.IsDefined(input.DataType))
                throw new ArgumentOutOfRangeException(nameof(input.DataType));
            PointInfo.ValidateLimits(input.DataType, input.LowerLimit, input.UpperLimit);

            using SqliteConnection connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();

            // UPDATE 修改原记录，不是 INSERT 新增记录，所以点位 Id 不会改变。
            // 同时检查 DeviceId，防止把别台设备的点位当成当前设备的点位修改。
            // 保存时还要确认设备仍然启用；名称重复则不更新，返回 false。
            command.CommandText = """
                UPDATE OR IGNORE DevicePoints
                SET Name = $name,
                    RegisterType = $registerType,
                    Address = $address,
                    DataType = $dataType,
                    LowerLimit = $lowerLimit,
                    UpperLimit = $upperLimit
                WHERE Id = $pointId AND DeviceId = $deviceId
                    AND EXISTS (
                        SELECT 1 FROM Devices WHERE Id = $deviceId AND IsEnabled = 1
                    );
                """;
            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.AddWithValue("$pointId", pointId);
            command.Parameters.AddWithValue("$name", input.Name.Trim());
            command.Parameters.AddWithValue("$registerType", (int)input.RegisterType);
            command.Parameters.AddWithValue("$address", (int)input.Address);
            command.Parameters.AddWithValue("$dataType", (int)input.DataType);
            command.Parameters.AddWithValue("$lowerLimit", (object?)input.LowerLimit ?? DBNull.Value);
            command.Parameters.AddWithValue("$upperLimit", (object?)input.UpperLimit ?? DBNull.Value);

            return command.ExecuteNonQuery() == 1;
        }

        /// <summary>
        /// 由 PointViewModel.DeletePoint 在用户确认后调用。
        /// 同时按设备 Id 和点位 Id 删除一条配置；目标不属于该设备、不存在或设备停用时返回 false，不删除设备寄存器中的数据。
        /// </summary>
        public bool TryDeletePoint(UserAccount currentUser, long deviceId, long pointId)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointId);
            if (currentUser.Role != UserRole.Administrator || !currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以删除采集点。");

            using SqliteConnection connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            // 两个编号必须一起对上，只删这台设备下面的这一条记录。
            // 再检查数据库里的启用状态，不只相信窗口打开时的旧设备对象。
            command.CommandText = """
                DELETE FROM DevicePoints
                WHERE Id = $pointId AND DeviceId = $deviceId
                    AND EXISTS (
                        SELECT 1 FROM Devices WHERE Id = $deviceId AND IsEnabled = 1
                    );
                """;
            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.AddWithValue("$pointId", pointId);
            return command.ExecuteNonQuery() == 1;
        }

        // GetDevices 和 GetDevice 共用：读同样的十列，避免两个查询组装出不同属性。
        private static DeviceInfo ReadDevice(SqliteDataReader reader) => new(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            checked((byte)reader.GetInt32(4)), reader.GetInt32(5), reader.GetInt32(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetBoolean(9));

        /// <summary>
        /// GetDevices/GetDevice 共用的检查，未启用用户不能查配置；两个已启用角色都可以查。
        /// </summary>
        private static void CheckDeviceReadPermission(UserAccount currentUser)
        {
            ArgumentNullException.ThrowIfNull(currentUser);
            // 两种角色都要查看设备，未启用账户都不能查看。
            if (!currentUser.IsEnabled)
                throw new UnauthorizedAccessException("当前账户不能查看设备。");
        }

    }
}

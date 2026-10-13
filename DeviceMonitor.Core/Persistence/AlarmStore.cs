using System.Globalization;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// 保存和查询报警历史。相同 EventId 更新原记录，不重复新增。
/// 是否报警由 AlarmTracker 判断，这里只操作数据库。
/// </summary>
public sealed class AlarmStore
{
    private const string RecordColumns = """
        Id, EventId, DeviceId, DeviceName, PointId, PointName, Kind, State,
        Message, Value, LimitValue, StartedAtUtc, LastSeenAtUtc, EndedAtUtc, Occurrences
        """;
    private readonly string _connectionString;
    // 数据库文件路径；导出时用来防止误覆盖数据库。
    public string DatabasePath { get; }

    /// <summary>记住数据库路径；每次操作再打开自己的连接。</summary>
    public AlarmStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString();
    }

    /// <summary>
    /// 由 AlarmTracker.FlushAsync 保存待处理报警时调用。
    /// 保存一条报警：新事件插入，已有事件更新。
    /// 已启用的管理员和操作员都可以保存采集产生的报警。
    /// </summary>
    public void SaveRecord(UserAccount currentUser, AlarmRecord record)
    {
        CheckUser(currentUser);
        ValidateRecord(record);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        // 新 EventId 就插入，已有 EventId 就更新；编号对应的设备和点位不能换。
        command.CommandText = """
            INSERT INTO AlarmRecords (
                EventId, DeviceId, DeviceName, PointId, PointName, Kind, State,
                Message, Value, LimitValue, StartedAtUtc, LastSeenAtUtc, EndedAtUtc, Occurrences)
            VALUES (
                $eventId, $deviceId, $deviceName, $pointId, $pointName, $kind, $state,
                $message, $value, $limit, $startedAt, $lastSeenAt, $endedAt, $occurrences)
            ON CONFLICT(EventId) DO UPDATE SET
                State = excluded.State,
                Message = excluded.Message,
                Value = excluded.Value,
                LimitValue = excluded.LimitValue,
                LastSeenAtUtc = excluded.LastSeenAtUtc,
                EndedAtUtc = excluded.EndedAtUtc,
                Occurrences = excluded.Occurrences
            WHERE AlarmRecords.DeviceId = excluded.DeviceId
                AND AlarmRecords.PointId IS excluded.PointId
                AND AlarmRecords.Kind = excluded.Kind
                AND AlarmRecords.StartedAtUtc = excluded.StartedAtUtc;
            """;
        // 记住报警发生时的名称，后来改名也不改旧报警。
        command.Parameters.AddWithValue("$eventId", record.EventId);
        command.Parameters.AddWithValue("$deviceId", record.DeviceId);
        command.Parameters.AddWithValue("$deviceName", record.DeviceName);
        command.Parameters.AddWithValue("$pointId", (object?)record.PointId ?? DBNull.Value);
        command.Parameters.AddWithValue("$pointName", record.PointName);
        command.Parameters.AddWithValue("$kind", (int)record.Kind);
        command.Parameters.AddWithValue("$state", (int)record.State);
        command.Parameters.AddWithValue("$message", record.Message);
        command.Parameters.AddWithValue("$value", (object?)record.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", (object?)record.Limit ?? DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", UtcText(record.StartedAt));
        command.Parameters.AddWithValue("$lastSeenAt", UtcText(record.LastSeenAt));
        command.Parameters.AddWithValue("$endedAt", record.EndedAt.HasValue
            ? UtcText(record.EndedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$occurrences", record.Occurrences);

        // 真正执行保存，必须只影响一条对应的报警。
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("这个报警编号已经用于另一条报警，不能覆盖。");
    }

    /// <summary>
    /// 主要由 AlarmHistoryViewModel.LoadPageAsync 调用。
    /// 按条件查询一页报警。翻页沿用最大编号，排除后来新增的报警。
    /// 已有报警的恢复状态仍可能变化。
    /// </summary>
    public AlarmPage GetPage(UserAccount currentUser, AlarmFilter filter, int pageNumber = 1,
        int pageSize = 100, long? snapshotId = null)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        CheckPageSize(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        if (snapshotId is < 0) throw new ArgumentOutOfRangeException(nameof(snapshotId));
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // 总数和本页一起查，避免两次查询的数据范围对不上。
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        // 首次查询取最大编号，翻页沿用旧编号，排除后来新增的报警。
        command.CommandText = "SELECT COALESCE(MAX(Id), 0) FROM AlarmRecords;";
        long maxId = snapshotId ?? Convert.ToInt64(command.ExecuteScalar());
        string where = AddFilterParameters(command, filter, maxId);
        command.CommandText = $"SELECT COUNT(*) FROM AlarmRecords {where};";
        long count = Convert.ToInt64(command.ExecuteScalar());

        // 算实际页码，limit 限制本页条数，offset 跳过前面的记录。
        long pages = count == 0 ? 1 : (count - 1) / pageSize + 1;
        int actualPage = (int)Math.Min(pageNumber, pages);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)(actualPage - 1) * pageSize);
        command.CommandText = $"""
            SELECT {RecordColumns} FROM AlarmRecords {where}
            ORDER BY StartedAtUtc DESC, Id DESC LIMIT $limit OFFSET $offset;
            """;
        // 每一行装成 AlarmRecord，连同总数和页码交回页面。
        var records = new List<AlarmRecord>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read()) records.Add(ReadRecord(reader));
        }
        transaction.Commit();
        return new AlarmPage(records, count, actualPage, maxId);
    }

    /// <summary>只查最近记录；需要总数和页码时用 GetPage。</summary>
    public IReadOnlyList<AlarmRecord> GetRecentRecords(UserAccount currentUser, int limit = 200) =>
        GetPage(currentUser, new AlarmFilter(), pageSize: limit).Records;

    /// <summary>
    /// 由 AlarmHistoryViewModel.LoadPageAsync 需要刷新设备选项时调用。
    /// 从报警记录里取得设备选项；设备配置被删除，也还能查到过去的报警。
    /// </summary>
    public IReadOnlyList<HistoryDeviceOption> GetDeviceOptions(UserAccount currentUser)
    {
        CheckUser(currentUser);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        // 每台设备取最后一条报警中的名称，组成下拉框选项，不依赖设备配置还在不在。
        command.CommandText = """
            SELECT a.DeviceId, a.DeviceName FROM AlarmRecords a
            JOIN (SELECT DeviceId, MAX(Id) AS LatestId FROM AlarmRecords GROUP BY DeviceId) latest
                ON a.Id = latest.LatestId
            ORDER BY a.DeviceName COLLATE NOCASE, a.DeviceId;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        // 把查询的每一行装成一个设备选项。
        var devices = new List<HistoryDeviceOption>();
        while (reader.Read())
            devices.Add(new HistoryDeviceOption(reader.GetInt64(0), reader.GetString(1)));
        return devices;
    }

    /// <summary>
    /// 由 AlarmCsvExporter.Export 分批导出时调用。
    /// 为导出取一批记录，不一次加载全部历史。
    /// after 是上一批最后一条的位置，这一批接着往后取。
    /// </summary>
    internal IReadOnlyList<AlarmRecord> GetExportBatch(UserAccount currentUser, AlarmFilter filter,
        long snapshotId, AlarmRecord? after, int batchSize, CancellationToken token)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        CheckPageSize(batchSize);
        token.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        string where = AddFilterParameters(command, filter, snapshotId);
        if (after is not null)
        {
            where += " AND (StartedAtUtc < $afterTime OR (StartedAtUtc = $afterTime AND Id < $afterId))";
            command.Parameters.AddWithValue("$afterTime", UtcText(after.StartedAt));
            command.Parameters.AddWithValue("$afterId", after.Id);
        }
        command.Parameters.AddWithValue("$limit", batchSize);
        command.CommandText = $"""
            SELECT {RecordColumns} FROM AlarmRecords {where}
            ORDER BY StartedAtUtc DESC, Id DESC LIMIT $limit;
            """;
        token.ThrowIfCancellationRequested();
        using SqliteDataReader reader = command.ExecuteReader();
        var records = new List<AlarmRecord>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            records.Add(ReadRecord(reader));
        }
        // 返回前关闭查询连接，写文件时不继续占着它。
        return records;
    }

    // 下面是数据库操作共用的小方法。
    private static string AddFilterParameters(SqliteCommand command, AlarmFilter filter, long snapshotId)
    {
        string where = "WHERE Id <= $snapshotId";
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        if (filter.DeviceId.HasValue)
        {
            where += " AND DeviceId = $deviceId";
            command.Parameters.AddWithValue("$deviceId", filter.DeviceId.Value);
        }
        if (filter.Category.HasValue)
        {
            // 类别由 Kind 决定，不另外保存一列，避免两列记成互相矛盾的结果。
            where += filter.Category == AlarmCategory.Value ? " AND Kind IN (1, 2)" : " AND Kind = 3";
        }
        if (filter.StartUtc.HasValue)
        {
            where += " AND StartedAtUtc >= $start";
            command.Parameters.AddWithValue("$start", UtcText(filter.StartUtc.Value));
        }
        if (filter.EndUtcExclusive.HasValue)
        {
            where += " AND StartedAtUtc < $end";
            command.Parameters.AddWithValue("$end", UtcText(filter.EndUtcExclusive.Value));
        }
        return where;
    }

    /// <summary>
    /// 由 GetPage 和 GetExportBatch 读取数据库结果时调用。
    /// 把数据库的一行装成报警对象，空值不能当作 0。
    /// </summary>
    private static AlarmRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        EventId = reader.GetString(1),
        DeviceId = reader.GetInt64(2),
        DeviceName = reader.GetString(3),
        PointId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
        PointName = reader.GetString(5),
        Kind = (AlarmKind)reader.GetInt32(6),
        State = (AlarmState)reader.GetInt32(7),
        Message = reader.GetString(8),
        Value = reader.IsDBNull(9) ? null : reader.GetInt32(9),
        Limit = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        StartedAt = ParseUtc(reader.GetString(11)),
        LastSeenAt = ParseUtc(reader.GetString(12)),
        EndedAt = reader.IsDBNull(13) ? null : ParseUtc(reader.GetString(13)),
        Occurrences = reader.GetInt32(14)
    };

    /// <summary>
    /// 检查报警记录是否完整、取值是否有效，不负责判断越限。
    /// </summary>
    private static void ValidateRecord(AlarmRecord record)
    {
        // 先检查编号、名称、类型和次数等基本信息。
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(record.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EventId);
        if (!Guid.TryParse(record.EventId, out Guid eventId) || eventId == Guid.Empty)
            throw new ArgumentException("报警编号必须是非空的 GUID。", nameof(record));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(record.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.DeviceName);
        if (record.PointId is <= 0) throw new ArgumentOutOfRangeException(nameof(record.PointId));
        ArgumentNullException.ThrowIfNull(record.PointName);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Message);
        if (!Enum.IsDefined(record.Kind)) throw new ArgumentOutOfRangeException(nameof(record.Kind));
        if (!Enum.IsDefined(record.State)) throw new ArgumentOutOfRangeException(nameof(record.State));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(record.Occurrences);

        // 时间和状态必须对应：活动报警不能有结束时间，已结束报警必须有。
        if (record.LastSeenAt < record.StartedAt)
            throw new ArgumentException("最后发生时间不能早于首次发生时间。", nameof(record));
        if (record.State == AlarmState.Active && record.EndedAt.HasValue)
            throw new ArgumentException("未恢复的报警不能填写结束时间。", nameof(record));
        if (record.State != AlarmState.Active &&
            (!record.EndedAt.HasValue || record.EndedAt.Value < record.LastSeenAt))
            throw new ArgumentException("结束时间不能早于最后发生时间。", nameof(record));

        // 数值报警必须说明哪个点、读到多少、超过哪条限值；读取异常可以没有数值。
        if (record.Kind != AlarmKind.AcquisitionFailure &&
            (!record.PointId.HasValue || string.IsNullOrWhiteSpace(record.PointName)
                || !record.Value.HasValue || !record.Limit.HasValue))
            throw new ArgumentException("数值报警需要有采集点、读数和上下限。", nameof(record));
    }

    /// <summary>
    /// 检查账户是否启用；管理员和操作员都可以保存采集报警。
    /// </summary>
    private static void CheckUser(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsEnabled) throw new UnauthorizedAccessException("当前账户不能读写报警记录。");
    }

    /// <summary>
    /// 检查每批条数，避免一次查太多。
    /// </summary>
    private static void CheckPageSize(int size)
    {
        if (size < 1 || size > 1000)
            throw new ArgumentOutOfRangeException(nameof(size), "每次查询数量必须在 1 到 1000 条之间。");
    }

    /// <summary>
    /// 把时间转成数据库使用的 UTC 文字。
    /// </summary>
    private static string UtcText(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// 把数据库时间文字还原成 UTC 时间对象。
    /// </summary>
    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture);
}

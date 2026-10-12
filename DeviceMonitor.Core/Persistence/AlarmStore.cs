using System.Globalization;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// 报警历史的数据库操作：按 EventId 插入或更新同一事件，再为历史页/导出器查询。
/// 不判断是否越限；判断在 AlarmTracker。配置删掉后，过去的报警仍能保留和查询。
/// </summary>
public sealed class AlarmStore
{
    private const string RecordColumns = """
        Id, EventId, DeviceId, DeviceName, PointId, PointName, Kind, State,
        Message, Value, LimitValue, StartedAtUtc, LastSeenAtUtc, EndedAtUtc, Occurrences
        """;
    private readonly string _connectionString;
    // 导出器拿这个路径作比较，防止把数据库误当 CSV 覆盖；它不是一个打开着的连接。
    public string DatabasePath { get; }

    /// <summary>MainViewModel 沿用已初始化的数据库路径创建一次；每次查询或保存仍使用自己的连接。</summary>
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
    /// 第一次发生时插入；同一 EventId 再保存时修改原行，持续超限不会每轮新增一条。
    /// 操作员也能启停采集，所以已启用的两种账户都可以保存采集产生的报警。
    /// </summary>
    public void SaveRecord(UserAccount currentUser, AlarmRecord record)
    {
        CheckUser(currentUser);
        ValidateRecord(record);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
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
        // 设备和点位名称保存发生时的快照；后来改配置，不重写过去报警的名字。
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
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("这个报警编号已经用于另一条报警，不能覆盖。");
    }

    /// <summary>
    /// 报警历史页面查询或翻页时调用；第一次记住最大编号，后面翻页传回同一编号。
    /// 只固定新报警的范围；同一报警的恢复状态仍允许更新，不把历史当作在线证明。
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
        // 总数和当前页在同一个读取事务里查询，避免一半查旧数据、一半查新数据。
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(Id), 0) FROM AlarmRecords;";
        long maxId = snapshotId ?? Convert.ToInt64(command.ExecuteScalar());
        string where = AddFilterParameters(command, filter, maxId);
        command.CommandText = $"SELECT COUNT(*) FROM AlarmRecords {where};";
        long count = Convert.ToInt64(command.ExecuteScalar());
        long pages = count == 0 ? 1 : (count - 1) / pageSize + 1;
        int actualPage = (int)Math.Min(pageNumber, pages);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)(actualPage - 1) * pageSize);
        command.CommandText = $"""
            SELECT {RecordColumns} FROM AlarmRecords {where}
            ORDER BY StartedAtUtc DESC, Id DESC LIMIT $limit OFFSET $offset;
            """;
        var records = new List<AlarmRecord>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read()) records.Add(ReadRecord(reader));
        }
        transaction.Commit();
        return new AlarmPage(records, count, actualPage, maxId);
    }

    /// <summary>需要简单查最近记录时调用；完整报警历史页面用 GetPage 获取总数和页码。</summary>
    public IReadOnlyList<AlarmRecord> GetRecentRecords(UserAccount currentUser, int limit = 200) =>
        GetPage(currentUser, new AlarmFilter(), pageSize: limit).Records;

    /// <summary>从报警记录里取得设备选项；设备配置被删除，也还能查到过去的报警。</summary>
    public IReadOnlyList<HistoryDeviceOption> GetDeviceOptions(UserAccount currentUser)
    {
        CheckUser(currentUser);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.DeviceId, a.DeviceName FROM AlarmRecords a
            JOIN (SELECT DeviceId, MAX(Id) AS LatestId FROM AlarmRecords GROUP BY DeviceId) latest
                ON a.Id = latest.LatestId
            ORDER BY a.DeviceName COLLATE NOCASE, a.DeviceId;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var devices = new List<HistoryDeviceOption>();
        while (reader.Read())
            devices.Add(new HistoryDeviceOption(reader.GetInt64(0), reader.GetString(1)));
        return devices;
    }

    /// <summary>
    /// AlarmCsvExporter 每次取一批报警，写完后再回来取下一批，不一次把全部记录放进内存。
    /// after 是上一批最后一条；按“首次发生时间、编号”接着往后找，同一时间也不会重复。
    /// 最大编号挡住后来新插入的报警；原报警可能继续恢复，所以整份文件不是同一瞬间的状态快照。
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
        // 方法返回时 using 会关掉 reader 和连接；写文件时不会继续占着这批查询的连接。
        return records;
    }

    // 下面是上面几个公开方法共用的细节，不直接给界面调用。
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
    /// 本类查询共用：按 RecordColumns 顺序组装 AlarmRecord；可空列先检查 NULL，不把未知值当 0。
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
    /// 保存前检查记录完整性和取值范围；这不是数值越限判断。
    /// </summary>
    private static void ValidateRecord(AlarmRecord record)
    {
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
        if (record.LastSeenAt < record.StartedAt)
            throw new ArgumentException("最后发生时间不能早于首次发生时间。", nameof(record));
        if (record.State == AlarmState.Active && record.EndedAt.HasValue)
            throw new ArgumentException("未恢复的报警不能填写结束时间。", nameof(record));
        if (record.State != AlarmState.Active &&
            (!record.EndedAt.HasValue || record.EndedAt.Value < record.LastSeenAt))
            throw new ArgumentException("结束时间不能早于最后发生时间。", nameof(record));
        if (record.Kind != AlarmKind.AcquisitionFailure &&
            (!record.PointId.HasValue || string.IsNullOrWhiteSpace(record.PointName)
                || !record.Value.HasValue || !record.Limit.HasValue))
            throw new ArgumentException("数值报警需要有采集点、读数和上下限。", nameof(record));
    }

    /// <summary>
    /// 本类操作前检查账户；启用操作员也能保存采集产生的报警，历史页面和导出器另有角色限制。
    /// </summary>
    private static void CheckUser(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsEnabled) throw new UnauthorizedAccessException("当前账户不能读写报警记录。");
    }

    /// <summary>
    /// 分页/导出分批查询前检查容量，不允许无效或过大的单次请求。
    /// </summary>
    private static void CheckPageSize(int size)
    {
        if (size < 1 || size > 1000)
            throw new ArgumentOutOfRangeException(nameof(size), "每次查询数量必须在 1 到 1000 条之间。");
    }

    /// <summary>
    /// 保存时间或添加查询参数时调用，统一成 UTC 文本再交给 SQLite。
    /// </summary>
    private static string UtcText(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// 读回报警时间文本时调用，还原成 UTC 时间对象；界面显示本机时间由模型属性处理。
    /// </summary>
    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture);
}

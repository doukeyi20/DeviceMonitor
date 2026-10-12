using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// 成功读数的数据库操作：SaveRecord 追加历史；其他方法按条件查询、分页、取趋势或导出批次。
/// 历史里保留当时的设备/点位名称；不依赖配置记录仍存在，不在这里连接设备。
/// </summary>
public sealed class HistoryStore
{
    private const string RecordColumns = "Id, DeviceId, DeviceName, PointId, PointName, Value, ReadAtUtc";
    private readonly string _connectionString;
    // 导出器用它防止覆盖数据库；MainViewModel 也用同一路径准备报警 Store，不是另建一个库。
    // 只有 getter，页面不能改路径；这不是一个已经打开的连接。
    public string DatabasePath { get; }

    /// <summary>
    /// App.OnStartup 在初始化后创建，再将同一个对象传给采集和历史页面。
    /// 保存连接配置；每次操作使用自己的连接，多台设备不共用连接对象。
    /// </summary>
    public HistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true }.ToString();
    }

    /// <summary>
    /// DeviceSamplingViewModel.SaveReadingAsync 读取成功后调用，参数化 INSERT 保存一条。
    /// 出错交给采集任务处理，不把保存失败冒充设备读取失败。
    /// </summary>
    public void SaveRecord(UserAccount currentUser, HistoryRecord record)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(record);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO HistoryRecords (DeviceId, DeviceName, PointId, PointName, Value, ReadAtUtc)
            VALUES ($deviceId, $deviceName, $pointId, $pointName, $value, $readAtUtc);
            """;
        // 名称中的引号或 SQL 文字也只会被当作普通数据。
        command.Parameters.AddWithValue("$deviceId", record.DeviceId);
        command.Parameters.AddWithValue("$deviceName", record.DeviceName);
        command.Parameters.AddWithValue("$pointId", record.PointId);
        command.Parameters.AddWithValue("$pointName", record.PointName);
        command.Parameters.AddWithValue("$value", record.Value);
        command.Parameters.AddWithValue("$readAtUtc", UtcText(record.ReadAt));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 历史页面刷新时调用，生成设备下拉列表；取每个设备最近保存的名称。
    /// 不查 Devices 配置表，所以已删除的设备仍能查旧记录。
    /// </summary>
    public IReadOnlyList<HistoryDeviceOption> GetDeviceOptions(UserAccount currentUser)
    {
        CheckUser(currentUser);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.DeviceId, h.DeviceName FROM HistoryRecords h
            JOIN (SELECT DeviceId, MAX(Id) AS LatestId FROM HistoryRecords GROUP BY DeviceId) latest
                ON h.Id = latest.LatestId
            ORDER BY h.DeviceName COLLATE NOCASE, h.DeviceId;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var devices = new List<HistoryDeviceOption>();
        while (reader.Read()) devices.Add(new HistoryDeviceOption(reader.GetInt64(0), reader.GetString(1)));
        return devices;
    }

    /// <summary>
    /// HistoryViewModel 查询或翻页时调用。第一次不传 snapshotId，记住当时的最大记录编号。
    /// 后面翻页传回这个编号，只查本次范围内的记录，避免新数据挤乱页面。
    /// </summary>
    public HistoryPage GetPage(UserAccount currentUser, HistoryFilter filter, int pageNumber = 1,
        int pageSize = 200, long? snapshotId = null)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        CheckPageSize(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        if (snapshotId is < 0) throw new ArgumentOutOfRangeException(nameof(snapshotId));
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // 总数和当前页在同一次读取中取得，期间的新写入不会让它们彼此矛盾。
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(Id), 0) FROM HistoryRecords;";
        long maxId = snapshotId ?? Convert.ToInt64(command.ExecuteScalar());
        string where = AddFilterParameters(command, filter, maxId);
        command.CommandText = $"SELECT COUNT(*) FROM HistoryRecords {where};";
        long count = Convert.ToInt64(command.ExecuteScalar());
        long pages = count == 0 ? 1 : (count - 1) / pageSize + 1;
        int actualPage = (int)Math.Min(pageNumber, pages);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)(actualPage - 1) * pageSize);
        command.CommandText = $"""
            SELECT {RecordColumns} FROM HistoryRecords {where}
            ORDER BY ReadAtUtc DESC, Id DESC LIMIT $limit OFFSET $offset;
            """;
        var records = new List<HistoryRecord>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read()) records.Add(ReadRecord(reader));
        }
        transaction.Commit();
        return new HistoryPage(records, count, actualPage, maxId);
    }

    /// <summary>简单调用仍可只拿最近记录；完整页面使用 GetPage，另外拿到总数和页码。</summary>
    public IReadOnlyList<HistoryRecord> GetRecentRecords(UserAccount currentUser, int limit = 200) =>
        GetPage(currentUser, new HistoryFilter(), pageSize: limit).Records;

    /// <summary>
    /// 趋势窗口的 HistoryTrendViewModel.LoadTrendAsync 调用，按设备编号和点位编号找同一个点，不靠名称猜。
    /// 沿用表格上次查询的条件和最大记录编号；后来新保存的读数，要重新查询后才进入曲线。
    /// 条件里若指定了另一台设备，两组条件同时生效，所以返回空结果，不偷偷换掉原条件。
    /// </summary>
    public HistoryTrendData GetPointTrend(UserAccount currentUser, long deviceId, long pointId,
        HistoryFilter filter, long snapshotId, int limit = 1000)
    {
        CheckUser(currentUser); // 和原历史查询一致；是否能进入页面，由 ViewModel 检查管理员身份。
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointId);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        CheckPageSize(limit);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // 总数和曲线数据一起查，避免查询过程中正在采集的新数据让两者对不上。
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        string where = AddFilterParameters(command, filter, snapshotId)
            + " AND DeviceId = $trendDeviceId AND PointId = $trendPointId";
        command.Parameters.AddWithValue("$trendDeviceId", deviceId);
        command.Parameters.AddWithValue("$trendPointId", pointId);
        command.CommandText = $"SELECT COUNT(*) FROM HistoryRecords {where};";
        long count = Convert.ToInt64(command.ExecuteScalar());

        // 先按时间倒着取最近 limit 条，再倒过来交给画图：横轴从旧时间走向新时间。
        // 时间完全相同的记录，再按历史编号排列，让结果每次都稳定。
        command.Parameters.AddWithValue("$limit", limit);
        command.CommandText = $"""
            SELECT {RecordColumns} FROM HistoryRecords {where}
            ORDER BY ReadAtUtc DESC, Id DESC LIMIT $limit;
            """;
        var records = new List<HistoryRecord>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read()) records.Add(ReadRecord(reader));
        }
        transaction.Commit();
        records.Reverse();
        return new HistoryTrendData(records, count);
    }

    /// <summary>
    /// CSV 导出器分批调用，读完一批就关闭连接，再写文件，不长期占着数据库读连接。
    /// after 是上一批最后一条记录，下一批从它后面接着取，包含相同时间的记录也不会重复或遗漏。
    /// </summary>
    internal IReadOnlyList<HistoryRecord> GetExportBatch(UserAccount currentUser, HistoryFilter filter,
        long snapshotId, HistoryRecord? after, int batchSize, System.Threading.CancellationToken token)
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
            where += " AND (ReadAtUtc < $afterTime OR (ReadAtUtc = $afterTime AND Id < $afterId))";
            command.Parameters.AddWithValue("$afterTime", UtcText(after.ReadAt));
            command.Parameters.AddWithValue("$afterId", after.Id);
        }
        command.Parameters.AddWithValue("$limit", batchSize);
        command.CommandText = $"""
            SELECT {RecordColumns} FROM HistoryRecords {where}
            ORDER BY ReadAtUtc DESC, Id DESC LIMIT $limit;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var records = new List<HistoryRecord>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            records.Add(ReadRecord(reader));
        }
        return records;
    }

    /// <summary>
    /// 分页、导出和趋势共用同一套条件，避免“表格查的是一种、其他功能却是另一种”。
    /// 只拼固定 SQL 片段，输入值全部交给参数。
    /// </summary>
    private static string AddFilterParameters(SqliteCommand command, HistoryFilter filter, long snapshotId)
    {
        string where = "WHERE Id <= $snapshotId";
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        if (filter.DeviceId.HasValue)
        {
            where += " AND DeviceId = $deviceId";
            command.Parameters.AddWithValue("$deviceId", filter.DeviceId.Value);
        }
        if (!string.IsNullOrEmpty(filter.PointName))
        {
            where += " AND instr(PointName, $pointName) > 0";
            command.Parameters.AddWithValue("$pointName", filter.PointName);
        }
        if (filter.StartUtc.HasValue)
        {
            where += " AND ReadAtUtc >= $start";
            command.Parameters.AddWithValue("$start", UtcText(filter.StartUtc.Value));
        }
        if (filter.EndUtcExclusive.HasValue)
        {
            where += " AND ReadAtUtc < $end";
            command.Parameters.AddWithValue("$end", UtcText(filter.EndUtcExclusive.Value));
        }
        return where;
    }

    /// <summary>
    /// 本类查询共用。按 RecordColumns 的七列顺序取值，组装一条 HistoryRecord；不是读设备配置的列。
    /// </summary>
    private static HistoryRecord ReadRecord(SqliteDataReader reader) => new(
        reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
        reader.GetInt32(5), DateTimeOffset.ParseExact(reader.GetString(6), "O", CultureInfo.InvariantCulture), reader.GetInt64(0));

    /// <summary>
    /// 写入读数时间或查询时间参数时共用，把同一个时间点写成统一 UTC 格式，便于排序比较。
    /// </summary>
    private static string UtcText(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// 历史数据库操作共用的账户检查。采集保存也允许操作员；页面和导出器另有管理员限制。
    /// </summary>
    private static void CheckUser(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsEnabled) throw new UnauthorizedAccessException("当前账户不能读写历史记录。");
    }

    /// <summary>
    /// 分页和分批查询先检查容量，拒绝非法或过大的请求，避免一次读回过多记录。
    /// </summary>
    private static void CheckPageSize(int size)
    {
        if (size < 1 || size > 1000) throw new ArgumentOutOfRangeException(nameof(size), "每次查询数量必须在 1 到 1000 条之间。");
    }
}

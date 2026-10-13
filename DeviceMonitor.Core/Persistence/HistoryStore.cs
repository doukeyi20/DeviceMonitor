using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// 保存和查询读数历史，包括分页、趋势和导出所需的数据。
/// 保留读取时的设备名和点位名，删除配置也不删除历史。
/// </summary>
public sealed class HistoryStore
{
    private const string RecordColumns = "Id, DeviceId, DeviceName, PointId, PointName, Value, ReadAtUtc";
    private readonly string _connectionString;
    // 数据库文件路径，报警也使用这一份库；导出时不能覆盖这个文件。
    public string DatabasePath { get; }

    /// <summary>
    /// 记住数据库连接设置，每次操作再打开自己的连接。
    /// </summary>
    public HistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true }.ToString();
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.SaveReadingAsync 在点位读取成功后调用。
    /// 把一条成功读数写入历史表。
    /// </summary>
    public void SaveRecord(UserAccount currentUser, HistoryRecord record)
    {
        // 先检查用户和传入的记录。
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(record);
        // 本次保存单独开连接，用完释放。
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // 准备新增一条历史的 SQL，不覆盖原记录。
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO HistoryRecords (DeviceId, DeviceName, PointId, PointName, Value, ReadAtUtc)
            VALUES ($deviceId, $deviceName, $pointId, $pointName, $value, $readAtUtc);
            """;
        // 给 SQL 中的占位符填值，输入内容只作为数据，不作为 SQL 执行。
        command.Parameters.AddWithValue("$deviceId", record.DeviceId);
        command.Parameters.AddWithValue("$deviceName", record.DeviceName);
        command.Parameters.AddWithValue("$pointId", record.PointId);
        command.Parameters.AddWithValue("$pointName", record.PointName);
        command.Parameters.AddWithValue("$value", record.Value);
        // 数据库存统一的 UTC 时间，界面需要显示时再转成本地时间。
        command.Parameters.AddWithValue("$readAtUtc", UtcText(record.ReadAt));
        // 到这里才真正写入；出错时由外面的采集对象记录保存失败。
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 由 HistoryViewModel.LoadPageAsync 需要刷新设备选项时调用。
    /// 从历史表准备设备下拉选项，已删除设备的历史也能查询。
    /// </summary>
    public IReadOnlyList<HistoryDeviceOption> GetDeviceOptions(UserAccount currentUser)
    {
        CheckUser(currentUser);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        // 每台设备取最后一条历史中的名称，已删除的设备也能出现在下拉框中。
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
    /// 主要由 HistoryViewModel.LoadPageAsync 调用。
    /// 按条件查一页历史，翻页沿用 snapshotId，排除后来新增的读数。
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
        // 首次查询记住最大编号，翻页继续用原编号，不让新记录挤乱页码。
        command.CommandText = "SELECT COALESCE(MAX(Id), 0) FROM HistoryRecords;";
        long maxId = snapshotId ?? Convert.ToInt64(command.ExecuteScalar());
        string where = AddFilterParameters(command, filter, maxId);
        command.CommandText = $"SELECT COUNT(*) FROM HistoryRecords {where};";
        long count = Convert.ToInt64(command.ExecuteScalar());

        // 算出总页数和实际页码，offset 表示跳过前面几条。
        long pages = count == 0 ? 1 : (count - 1) / pageSize + 1;
        int actualPage = (int)Math.Min(pageNumber, pages);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)(actualPage - 1) * pageSize);
        command.CommandText = $"""
            SELECT {RecordColumns} FROM HistoryRecords {where}
            ORDER BY ReadAtUtc DESC, Id DESC LIMIT $limit OFFSET $offset;
            """;
        // 把这一页的每行数据装成 HistoryRecord，连同总数、页码一起返回。
        var records = new List<HistoryRecord>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read()) records.Add(ReadRecord(reader));
        }
        transaction.Commit();
        return new HistoryPage(records, count, actualPage, maxId);
    }

    /// <summary>只查最近记录；需要总数和页码时用 GetPage。</summary>
    public IReadOnlyList<HistoryRecord> GetRecentRecords(UserAccount currentUser, int limit = 200) =>
        GetPage(currentUser, new HistoryFilter(), pageSize: limit).Records;

    /// <summary>
    /// 由 HistoryTrendViewModel.LoadTrendAsync 调用。
    /// 查询一个点的趋势数据，设备编号和点位编号必须同时对上。
    /// 继续使用上次查询的条件和最大编号，不混入后来新增的读数。
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

        // 先取最近 limit 条，再按从旧到新排列；时间相同时按编号排。
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
    /// 由 HistoryCsvExporter.Export 分批导出时调用。
    /// 为导出取一批记录，返回前关闭查询连接。
    /// after 是上一批最后一条的位置，这一批接着往后取。
    /// </summary>
    internal IReadOnlyList<HistoryRecord> GetExportBatch(UserAccount currentUser, HistoryFilter filter,
        long snapshotId, HistoryRecord? after, int batchSize, System.Threading.CancellationToken token)
    {
        // 检查权限、查询条件和每批数量，再打开查询连接。
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        CheckPageSize(batchSize);
        token.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        string where = AddFilterParameters(command, filter, snapshotId);
        // 接着上一批最后一条往后取；时间相同时再按编号区分，避免漏行或重复。
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
        // 一行行装成 HistoryRecord，期间收到取消就结束本批查询。
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
    /// 把查询条件填进 SQL 参数，分页、趋势和导出共用。
    /// </summary>
    private static string AddFilterParameters(SqliteCommand command, HistoryFilter filter, long snapshotId)
    {
        // 先排除本次查询后新增的记录，再按用户填写的条件逐项加限制。
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
        // 起点包含在内，终点用“小于”，刚好接上下一段范围。
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
    /// 由 GetPage、GetPointTrend 和 GetExportBatch 读取数据库结果时调用。
    /// 把数据库的一行装成历史读数对象。
    /// </summary>
    private static HistoryRecord ReadRecord(SqliteDataReader reader) => new(
        reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
        reader.GetInt32(5), DateTimeOffset.ParseExact(reader.GetString(6), "O", CultureInfo.InvariantCulture), reader.GetInt64(0));

    /// <summary>
    /// 把时间转成数据库使用的 UTC 文字。
    /// </summary>
    private static string UtcText(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// 检查账户是否启用；管理员和操作员都可以保存采集历史。
    /// </summary>
    private static void CheckUser(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsEnabled) throw new UnauthorizedAccessException("当前账户不能读写历史记录。");
    }

    /// <summary>
    /// 检查每批条数，避免一次查太多。
    /// </summary>
    private static void CheckPageSize(int size)
    {
        if (size < 1 || size > 1000) throw new ArgumentOutOfRangeException(nameof(size), "每次查询数量必须在 1 到 1000 条之间。");
    }
}

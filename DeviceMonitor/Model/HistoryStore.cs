// 使用 EF Core 保存、分页查询和读取历史趋势。
using Microsoft.EntityFrameworkCore;

namespace DeviceMonitor.Model;

public sealed class HistoryStore
{
    private readonly MonitorDbContextFactory _database;
    public string DatabasePath
    {
        get;
    }

    public HistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _database = new(DatabasePath);
    }

    /// <summary>保留单条保存入口，测试和少量调用使用；仍复用下面的批量保存。</summary>
    public void SaveRecord(UserAccount currentUser, HistoryRecord record) => SaveRecords(currentUser, new[] { record });

    /// <summary>单个上下文批量提交本轮记录，失败整批回滚。</summary>
    public void SaveRecords(UserAccount currentUser, IReadOnlyList<HistoryRecord> records)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return;

        // 一轮结果统一转换实体，再由一次 SaveChanges 整批提交。
        var rows = records.Select(record =>
        {
            ArgumentNullException.ThrowIfNull(record);
            return new HistoryRow
            {
                DeviceId = record.DeviceId,
                DeviceName = record.DeviceName,
                PointId = record.PointId,
                PointName = record.PointName,
                Value = record.Value,
                Unit = record.Unit,
                ReadAtUtc = record.ReadAt.UtcDateTime
            };
        }).ToArray();
        using var db = _database.Create();
        db.History.AddRange(rows);
        db.SaveChanges();
    }

    /// <summary>从历史里取每台设备最后一次保存的名称。</summary>
    public IReadOnlyList<DeviceFilterOption> GetDeviceOptions(UserAccount currentUser)
    {
        CheckUser(currentUser);
        using var db = _database.Create();
        var latestIds = db.History.GroupBy(x => x.DeviceId).Select(group => group.Max(x => x.Id));
        return db.History.AsNoTracking().Where(x => latestIds.Contains(x.Id))
            .OrderBy(x => EF.Functions.Collate(x.DeviceName, "NOCASE")).ThenBy(x => x.DeviceId)
            .Select(x => new DeviceFilterOption(x.DeviceId, x.DeviceName)).ToList();
    }

    /// <summary>按快照范围筛选、统计并读取历史分页。</summary>
    public HistoryPage GetPage(UserAccount currentUser, HistoryFilter filter, int pageNumber = 1,
        int pageSize = 200, long? snapshotId = null)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        CheckPageSize(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        if (snapshotId is < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshotId));
        using var db = _database.Create();
        using var transaction = db.BeginReadTransaction();

        // 首次查询确定编号上限，后续分页沿用相同范围。
        long maxId = snapshotId ?? db.History.Max(x => (long?)x.Id) ?? 0;
        var query = ApplyFilter(db.History.AsNoTracking(), filter, maxId);
        long count = query.LongCount();
        long pages = count == 0 ? 1 : (count - 1) / pageSize + 1;
        int actualPage = (int)Math.Min(pageNumber, pages);

        // 同一时间再按编号排序，保证分页与导出顺序稳定。
        var rows = query.OrderByDescending(x => x.ReadAtUtc).ThenByDescending(x => x.Id)
            .Skip(checked((actualPage - 1) * pageSize)).Take(pageSize).ToList();
        var records = rows.Select(ToHistoryRecord).ToList();
        transaction.Commit();
        return new HistoryPage(records, count, actualPage, maxId);
    }

    /// <summary>仍共用分页的筛选和数量检查。</summary>
    public IReadOnlyList<HistoryRecord> GetRecentRecords(UserAccount currentUser, int limit = 200) =>
        GetPage(currentUser, new HistoryFilter(), pageSize: limit).Records;

    /// <summary>只查指定设备、指定点的最近一段历史。</summary>
    public HistoryTrendData GetPointTrend(UserAccount currentUser, long deviceId, long pointId,
        HistoryFilter filter, long snapshotId, int limit = 1000, string? unit = null)
    {
        CheckUser(currentUser);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointId);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        CheckPageSize(limit);
        using var db = _database.Create();
        using var transaction = db.BeginReadTransaction();
        var query = ApplyFilter(db.History.AsNoTracking(), filter, snapshotId)
            .Where(x => x.DeviceId == deviceId && x.PointId == pointId);

        if (unit is not null)
            query = query.Where(x => x.Unit == unit);
        long count = query.LongCount();
        var rows = query.OrderByDescending(x => x.ReadAtUtc).ThenByDescending(x => x.Id).Take(limit).ToList();
        var records = rows.Select(ToHistoryRecord).ToList();
        transaction.Commit();
        // 先截取最近数据，再转为时间正序供曲线绘制。
        records.Reverse();
        return new HistoryTrendData(records, count);
    }

    /// <summary>接着上一批的位置查，不一次加载全部历史。</summary>
    internal IReadOnlyList<HistoryRecord> GetExportBatch(UserAccount currentUser, HistoryFilter filter,
        long snapshotId, HistoryRecord? after, int batchSize, CancellationToken token)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        CheckPageSize(batchSize);
        token.ThrowIfCancellationRequested();
        using var db = _database.Create();
        var query = ApplyFilter(db.History.AsNoTracking(), filter, snapshotId);
        if (after is not null)
        {
            DateTime time = after.ReadAt.UtcDateTime;
            long id = after.Id;
            query = query.Where(x => x.ReadAtUtc < time || (x.ReadAtUtc == time && x.Id < id));
        }

        var records = new List<HistoryRecord>();

        foreach (var row in query.OrderByDescending(x => x.ReadAtUtc).ThenByDescending(x => x.Id)
            .Take(batchSize).AsEnumerable())
        {
            token.ThrowIfCancellationRequested();
            records.Add(ToHistoryRecord(row));
        }
        return records;
    }

    /// <summary>分页、趋势、导出共用；只拼 LINQ 条件，还没有执行数据库查询。</summary>
    private static IQueryable<HistoryRow> ApplyFilter(IQueryable<HistoryRow> query, HistoryFilter filter, long snapshotId)
    {
        query = query.Where(x => x.Id <= snapshotId);
        if (filter.DeviceId.HasValue)
        {
            long id = filter.DeviceId.Value;
            query = query.Where(x => x.DeviceId == id);
        }
        if (!string.IsNullOrEmpty(filter.PointName))
            query = query.Where(x => x.PointName.Contains(filter.PointName));
        if (filter.StartUtc.HasValue)
        {
            DateTime start = filter.StartUtc.Value.UtcDateTime;
            query = query.Where(x => x.ReadAtUtc >= start);
        }
        if (filter.EndUtcExclusive.HasValue)
        {
            DateTime end = filter.EndUtcExclusive.Value.UtcDateTime;
            query = query.Where(x => x.ReadAtUtc < end);
        }
        return query;
    }

    /// <summary>查询完成后转换成原来的历史对象，供表格、趋势和导出使用。</summary>
    private static HistoryRecord ToHistoryRecord(HistoryRow x) => new(x.DeviceId, x.DeviceName,
        x.PointId, x.PointName, x.Value, new DateTimeOffset(x.ReadAtUtc, TimeSpan.Zero), x.Id, x.Unit);

    private static void CheckUser(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsEnabled)
            throw new UnauthorizedAccessException("当前账户不能读写历史记录。");
    }

    private static void CheckPageSize(int size)
    {
        if (size < 1 || size > 1000)
            throw new ArgumentOutOfRangeException(nameof(size), "每次查询数量必须在 1 到 1000 条之间。");
    }
}

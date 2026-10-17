using DeviceMonitor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DeviceMonitor.Core.Persistence;

/// <summary>读数历史的 EF 操作。保存、分页、趋势和导出的调用方式保持不变。</summary>
public sealed class HistoryStore
{
    private readonly MonitorDbContextFactory _database;
    public string DatabasePath { get; } // 导出仍用它检查目标文件，不能覆盖数据库。

    public HistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _database = new(DatabasePath);
    }

    /// <summary>DeviceSamplingViewModel.SaveReadingAsync 调用；一条成功读数新增一条历史。</summary>
    public void SaveRecord(UserAccount currentUser, HistoryRecord record)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(record);
        using var db = _database.Create();
        // 多台设备可能同时保存；先取得 SQLite 写事务，再执行 EF 的插入和返回编号。
        using var transaction = db.Database.BeginTransaction();
        db.History.Add(new HistoryRow
        {
            DeviceId = record.DeviceId, DeviceName = record.DeviceName,
            PointId = record.PointId, PointName = record.PointName,
            Value = record.Value, ReadAtUtc = record.ReadAt.UtcDateTime
        });
        db.SaveChanges(); // Add 只是登记，SaveChanges 才真正保存。
        transaction.Commit();
    }

    /// <summary>历史页面刷新设备选项时调用；从历史里取每台设备最后一次保存的名称。</summary>
    public IReadOnlyList<HistoryDeviceOption> GetDeviceOptions(UserAccount currentUser)
    {
        CheckUser(currentUser);
        using var db = _database.Create();
        var latestIds = db.History.GroupBy(x => x.DeviceId).Select(group => group.Max(x => x.Id));
        return db.History.AsNoTracking().Where(x => latestIds.Contains(x.Id))
            .OrderBy(x => EF.Functions.Collate(x.DeviceName, "NOCASE")).ThenBy(x => x.DeviceId)
            .Select(x => new HistoryDeviceOption(x.DeviceId, x.DeviceName)).ToList();
    }

    /// <summary>HistoryViewModel.LoadPageAsync 调用；翻页沿用 snapshotId，排除后来新增的读数。</summary>
    public HistoryPage GetPage(UserAccount currentUser, HistoryFilter filter, int pageNumber = 1,
        int pageSize = 200, long? snapshotId = null)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        CheckPageSize(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        if (snapshotId is < 0) throw new ArgumentOutOfRangeException(nameof(snapshotId));
        using var db = _database.Create();
        using var transaction = db.BeginReadTransaction();

        long maxId = snapshotId ?? db.History.Max(x => (long?)x.Id) ?? 0;
        var query = ApplyFilter(db.History.AsNoTracking(), filter, maxId);
        long count = query.LongCount(); // 在数据库统计，不把全部历史取回来。
        long pages = count == 0 ? 1 : (count - 1) / pageSize + 1;
        int actualPage = (int)Math.Min(pageNumber, pages);

        // 排序、跳过、限制条数都在 SQLite 执行，ToList 才取回这一页。
        var rows = query.OrderByDescending(x => x.ReadAtUtc).ThenByDescending(x => x.Id)
            .SkipRows((long)(actualPage - 1) * pageSize).Take(pageSize).ToList();
        var records = rows.Select(ToHistoryRecord).ToList();
        transaction.Commit();
        return new HistoryPage(records, count, actualPage, maxId);
    }

    /// <summary>只需要最近记录时调用；仍共用分页的筛选和数量检查。</summary>
    public IReadOnlyList<HistoryRecord> GetRecentRecords(UserAccount currentUser, int limit = 200) =>
        GetPage(currentUser, new HistoryFilter(), pageSize: limit).Records;

    /// <summary>HistoryTrendViewModel.LoadTrendAsync 调用；只查指定设备、指定点的最近一段历史。</summary>
    public HistoryTrendData GetPointTrend(UserAccount currentUser, long deviceId, long pointId,
        HistoryFilter filter, long snapshotId, int limit = 1000)
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
        long count = query.LongCount();
        var rows = query.OrderByDescending(x => x.ReadAtUtc).ThenByDescending(x => x.Id).Take(limit).ToList();
        var records = rows.Select(ToHistoryRecord).ToList();
        transaction.Commit();
        records.Reverse(); // 图表需要从旧到新；只反转已经取回的这一小段。
        return new HistoryTrendData(records, count);
    }

    /// <summary>HistoryCsvExporter 分批导出时调用；接着上一批的位置查，不一次加载全部历史。</summary>
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
        // AsEnumerable 从这里开始按行取结果；前面的筛选、排序和 Take 仍由数据库执行。
        foreach (var row in query.OrderByDescending(x => x.ReadAtUtc).ThenByDescending(x => x.Id)
            .Take(batchSize).AsEnumerable())
        {
            token.ThrowIfCancellationRequested();
            records.Add(ToHistoryRecord(row));
        }
        return records; // 返回前释放本批上下文，写文件时不继续占用查询连接。
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
            query = query.Where(x => x.ReadAtUtc < end); // 终点仍然不包含。
        }
        return query;
    }

    /// <summary>查询完成后转换成原来的历史对象，供表格、趋势和导出使用。</summary>
    private static HistoryRecord ToHistoryRecord(HistoryRow x) => new(x.DeviceId, x.DeviceName,
        x.PointId, x.PointName, x.Value, new DateTimeOffset(x.ReadAtUtc, TimeSpan.Zero), x.Id);

    private static void CheckUser(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsEnabled) throw new UnauthorizedAccessException("当前账户不能读写历史记录。");
    }

    private static void CheckPageSize(int size)
    {
        if (size < 1 || size > 1000) throw new ArgumentOutOfRangeException(nameof(size), "每次查询数量必须在 1 到 1000 条之间。");
    }
}

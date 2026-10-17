using DeviceMonitor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DeviceMonitor.Core.Persistence;

/// <summary>报警历史的 EF 操作。判断报警仍由 AlarmTracker 负责，这里只查询和保存。</summary>
public sealed class AlarmStore
{
    private readonly MonitorDbContextFactory _database;
    public string DatabasePath { get; } // 导出仍用这个路径检查，避免覆盖数据库。

    public AlarmStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _database = new(DatabasePath);
    }

    /// <summary>AlarmTracker.FlushAsync 调用；新事件新增，相同事件只更新原记录的状态。</summary>
    public void SaveRecord(UserAccount currentUser, AlarmRecord record)
    {
        CheckUser(currentUser);
        ValidateRecord(record);
        using var db = _database.Create();
        // 查询和新增/更新放在同一写事务里，不把一次保存拆成可能交错的两次操作。
        using var transaction = db.Database.BeginTransaction();
        var row = db.Alarms.FirstOrDefault(x => x.EventId == record.EventId);
        if (row is null)
        {
            row = new AlarmRow
            {
                EventId = record.EventId, DeviceId = record.DeviceId, DeviceName = record.DeviceName,
                PointId = record.PointId, PointName = record.PointName, Kind = record.Kind,
                StartedAtUtc = record.StartedAt.UtcDateTime
            };
            db.Alarms.Add(row);
        }
        else if (row.DeviceId != record.DeviceId || row.PointId != record.PointId ||
            row.Kind != record.Kind || row.StartedAtUtc != record.StartedAt.UtcDateTime)
        {
            // 一个事件编号不能挪给另一台设备、另一个点或另一种报警。
            throw new InvalidOperationException("这个报警编号已经用于另一条报警，不能覆盖。");
        }

        // 相同事件只改这些后续状态，不改首次发生时的名称和身份信息。
        row.State = record.State;
        row.Message = record.Message;
        row.Value = record.Value;
        row.LimitValue = record.Limit;
        row.LastSeenAtUtc = record.LastSeenAt.UtcDateTime;
        row.EndedAtUtc = record.EndedAt?.UtcDateTime;
        row.Occurrences = record.Occurrences;
        db.SaveChanges();
        transaction.Commit();
    }

    /// <summary>AlarmHistoryViewModel.LoadPageAsync 调用；筛选、统计和分页都在数据库完成。</summary>
    public AlarmPage GetPage(UserAccount currentUser, AlarmFilter filter, int pageNumber = 1,
        int pageSize = 100, long? snapshotId = null)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        CheckPageSize(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        if (snapshotId is < 0) throw new ArgumentOutOfRangeException(nameof(snapshotId));
        using var db = _database.Create();
        using var transaction = db.BeginReadTransaction();
        long maxId = snapshotId ?? db.Alarms.Max(x => (long?)x.Id) ?? 0;
        var query = ApplyFilter(db.Alarms.AsNoTracking(), filter, maxId);
        long count = query.LongCount();
        long pages = count == 0 ? 1 : (count - 1) / pageSize + 1;
        int actualPage = (int)Math.Min(pageNumber, pages);

        // 先按发生时间，再按编号排序；新报警被 snapshotId 排除，不挤乱原来的页。
        var rows = query.OrderByDescending(x => x.StartedAtUtc).ThenByDescending(x => x.Id)
            .SkipRows((long)(actualPage - 1) * pageSize).Take(pageSize).ToList();
        var records = rows.Select(ToAlarmRecord).ToList();
        transaction.Commit();
        return new AlarmPage(records, count, actualPage, maxId);
    }

    /// <summary>只需要最近报警时调用，仍共用分页的数量检查。</summary>
    public IReadOnlyList<AlarmRecord> GetRecentRecords(UserAccount currentUser, int limit = 200) =>
        GetPage(currentUser, new AlarmFilter(), pageSize: limit).Records;

    /// <summary>报警页面刷新设备选项时调用；已删除设备仍可按历史记录查询。</summary>
    public IReadOnlyList<HistoryDeviceOption> GetDeviceOptions(UserAccount currentUser)
    {
        CheckUser(currentUser);
        using var db = _database.Create();
        var latestIds = db.Alarms.GroupBy(x => x.DeviceId).Select(group => group.Max(x => x.Id));
        return db.Alarms.AsNoTracking().Where(x => latestIds.Contains(x.Id))
            .OrderBy(x => EF.Functions.Collate(x.DeviceName, "NOCASE")).ThenBy(x => x.DeviceId)
            .Select(x => new HistoryDeviceOption(x.DeviceId, x.DeviceName)).ToList();
    }

    /// <summary>AlarmCsvExporter 分批导出时调用；按上一批末尾的时间和编号继续查询。</summary>
    internal IReadOnlyList<AlarmRecord> GetExportBatch(UserAccount currentUser, AlarmFilter filter,
        long snapshotId, AlarmRecord? after, int batchSize, CancellationToken token)
    {
        CheckUser(currentUser);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        CheckPageSize(batchSize);
        token.ThrowIfCancellationRequested();
        using var db = _database.Create();
        var query = ApplyFilter(db.Alarms.AsNoTracking(), filter, snapshotId);
        if (after is not null)
        {
            DateTime time = after.StartedAt.UtcDateTime;
            long id = after.Id;
            query = query.Where(x => x.StartedAtUtc < time || (x.StartedAtUtc == time && x.Id < id));
        }
        var records = new List<AlarmRecord>();
        foreach (var row in query.OrderByDescending(x => x.StartedAtUtc).ThenByDescending(x => x.Id)
            .Take(batchSize).AsEnumerable())
        {
            token.ThrowIfCancellationRequested();
            records.Add(ToAlarmRecord(row));
        }
        return records; // 返回前释放本批查询，写 CSV 时不继续占用它。
    }

    /// <summary>分页和导出共用；这里只准备查询条件，还没查询数据库。</summary>
    private static IQueryable<AlarmRow> ApplyFilter(IQueryable<AlarmRow> query, AlarmFilter filter, long snapshotId)
    {
        query = query.Where(x => x.Id <= snapshotId);
        if (filter.DeviceId.HasValue)
        {
            long id = filter.DeviceId.Value;
            query = query.Where(x => x.DeviceId == id);
        }
        if (filter.Category.HasValue)
            query = filter.Category == AlarmCategory.Value
                ? query.Where(x => x.Kind == AlarmKind.HighLimit || x.Kind == AlarmKind.LowLimit)
                : query.Where(x => x.Kind == AlarmKind.AcquisitionFailure);
        if (filter.StartUtc.HasValue)
        {
            DateTime start = filter.StartUtc.Value.UtcDateTime;
            query = query.Where(x => x.StartedAtUtc >= start);
        }
        if (filter.EndUtcExclusive.HasValue)
        {
            DateTime end = filter.EndUtcExclusive.Value.UtcDateTime;
            query = query.Where(x => x.StartedAtUtc < end);
        }
        return query;
    }

    /// <summary>查询结束后转换成原来的报警对象，表格和导出不需要接触 EF。</summary>
    private static AlarmRecord ToAlarmRecord(AlarmRow x) => new()
    {
        Id = x.Id, EventId = x.EventId, DeviceId = x.DeviceId, DeviceName = x.DeviceName,
        PointId = x.PointId, PointName = x.PointName, Kind = x.Kind, State = x.State,
        Message = x.Message, Value = x.Value, Limit = x.LimitValue,
        StartedAt = new DateTimeOffset(x.StartedAtUtc, TimeSpan.Zero),
        LastSeenAt = new DateTimeOffset(x.LastSeenAtUtc, TimeSpan.Zero),
        EndedAt = x.EndedAtUtc.HasValue ? new DateTimeOffset(x.EndedAtUtc.Value, TimeSpan.Zero) : null,
        Occurrences = x.Occurrences
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


    private static void CheckUser(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsEnabled) throw new UnauthorizedAccessException("当前账户不能读写报警记录。");
    }

    private static void CheckPageSize(int size)
    {
        if (size < 1 || size > 1000)
            throw new ArgumentOutOfRangeException(nameof(size), "每次查询数量必须在 1 到 1000 条之间。");
    }
}


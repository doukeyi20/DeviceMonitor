namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一页报警查询结果：本页记录、总数、页码、最大记录编号。
/// 排除查询后新增的报警，但已有报警的状态仍可能更新。
/// </summary>
public sealed class AlarmPage
{
    public IReadOnlyList<AlarmRecord> Records { get; } // 本页报警，交给界面表格显示。
    public long TotalRecords { get; } // 所有匹配页的总条数。
    public int PageNumber { get; } // 本页页码。
    public long SnapshotId { get; } // 本次查询的最大编号，翻页和导出继续用。

    // 把这一页记录及分页信息装在一起，交回页面。
    public AlarmPage(IReadOnlyList<AlarmRecord> records, long totalRecords, int pageNumber, long snapshotId)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        Records = records;
        TotalRecords = totalRecords;
        PageNumber = pageNumber;
        SnapshotId = snapshotId;
    }
}

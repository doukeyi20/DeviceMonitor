namespace DeviceMonitor.Core.Models;

/// <summary>
/// AlarmStore 一页查询的返回包：记录、总数、页码和最大记录编号。
/// 最大编号排除查询后新报警，但原事件仍能恢复或更新，所以不是完全不变的数据库快照。
/// </summary>
public sealed class AlarmPage
{
    public IReadOnlyList<AlarmRecord> Records { get; }
    public long TotalRecords { get; }
    public int PageNumber { get; }
    public long SnapshotId { get; }

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

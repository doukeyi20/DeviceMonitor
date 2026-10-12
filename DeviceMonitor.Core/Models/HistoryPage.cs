using System.Collections.Generic;

namespace DeviceMonitor.Core.Models;

/// <summary>
/// HistoryStore 查询一页后的返回包：本页 Records、总数、页码、最大记录编号。
/// SnapshotId 只固定新记录范围，不是复制整个数据库；新读数需重新查询才纳入。
/// </summary>
public sealed class HistoryPage
{
    public IReadOnlyList<HistoryRecord> Records { get; }
    public long TotalRecords { get; }
    public int PageNumber { get; }
    public long SnapshotId { get; }

    public HistoryPage(IReadOnlyList<HistoryRecord> records, long totalRecords, int pageNumber, long snapshotId)
    {
        Records = records;
        TotalRecords = totalRecords;
        PageNumber = pageNumber;
        SnapshotId = snapshotId;
    }
}

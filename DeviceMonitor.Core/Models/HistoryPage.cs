using System.Collections.Generic;

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一页历史查询结果：本页记录、总数、页码、最大记录编号。
/// SnapshotId 排除查询后新写入的读数，重新查询才会包含它们。
/// </summary>
public sealed class HistoryPage
{
    public IReadOnlyList<HistoryRecord> Records { get; } // 本页读数，交给界面表格显示。
    public long TotalRecords { get; } // 所有匹配页的总条数。
    public int PageNumber { get; } // 本页页码。
    public long SnapshotId { get; } // 本次查询的最大编号，翻页和导出继续用。

    // 把这一页记录及分页信息装在一起，交回页面。
    public HistoryPage(IReadOnlyList<HistoryRecord> records, long totalRecords, int pageNumber, long snapshotId)
    {
        Records = records;
        TotalRecords = totalRecords;
        PageNumber = pageNumber;
        SnapshotId = snapshotId;
    }
}

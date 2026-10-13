using System;
using System.Collections.Generic;
using System.Linq;

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 趋势查询结果：Records 用来画图，TotalRecords 是符合条件的总条数。
/// IsTruncated 为 true 表示只画了部分记录。
/// </summary>
public sealed class HistoryTrendData
{
    public IReadOnlyList<HistoryRecord> Records { get; }
    public long TotalRecords { get; }
    public bool IsTruncated => TotalRecords > Records.Count;

    // 检查数量后，保存本次用于画图的记录和匹配总数。
    public HistoryTrendData(IReadOnlyList<HistoryRecord> records, long totalRecords)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRecords);
        if (totalRecords < records.Count)
            throw new ArgumentException("总记录数不能少于实际返回的记录数。", nameof(totalRecords));

        // 复制成只读列表，外面后来修改原列表也不影响本次曲线。
        Records = Array.AsReadOnly(records.ToArray());
        TotalRecords = totalRecords;
    }
}

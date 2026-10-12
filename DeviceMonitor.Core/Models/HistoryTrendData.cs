using System;
using System.Collections.Generic;
using System.Linq;

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 趋势查询的返回包：Records 是最多最近一部分的画图数据，TotalRecords 是实际匹配数。
/// 数量不一致时 IsTruncated=true，界面应说明没有绘制全部历史。
/// </summary>
public sealed class HistoryTrendData
{
    public IReadOnlyList<HistoryRecord> Records { get; }
    public long TotalRecords { get; }
    public bool IsTruncated => TotalRecords > Records.Count;

    public HistoryTrendData(IReadOnlyList<HistoryRecord> records, long totalRecords)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRecords);
        if (totalRecords < records.Count)
            throw new ArgumentException("总记录数不能少于实际返回的记录数。", nameof(totalRecords));

        // 复制列表并包成只读集合；调用方后来修改原列表，不会改变这次查出的曲线。
        Records = Array.AsReadOnly(records.ToArray());
        TotalRecords = totalRecords;
    }
}

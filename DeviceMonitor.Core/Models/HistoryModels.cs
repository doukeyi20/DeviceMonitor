// 定义历史记录、筛选条件、分页和趋势结果。
using System.Collections.Generic;
using System.Linq;
using System;

namespace DeviceMonitor.Core.Models;

/// <summary>一条历史读数：哪台设备、哪个点、什么值、什么时间。</summary>
public sealed class HistoryRecord
{
    public long Id { get; }

    public long DeviceId { get; }
    public string DeviceName { get; }
    public long PointId { get; }
    public string PointName { get; }

    public double Value { get; }
    public string Unit { get; }
    public string ValueText => NumericValue.Format(Value, Unit);
    public DateTimeOffset ReadAt { get; }

    public DateTime ReadAtLocal => ReadAt.LocalDateTime;

    public HistoryRecord(long deviceId, string deviceName, long pointId,
    string pointName, double value, DateTimeOffset readAt, long id = 0, string unit = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pointName);
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        ArgumentNullException.ThrowIfNull(unit);

        Id = id;
        DeviceId = deviceId;
        DeviceName = deviceName;
        PointId = pointId;
        PointName = pointName;
        Value = value;
        Unit = unit;
        ReadAt = readAt;
    }
}

/// <summary>记住本次历史查询的条件，翻页、趋势和导出继续用它。</summary>
public sealed class HistoryFilter
{
    public long? DeviceId { get; }
    public string PointName
    {
        get;
    }
    public DateTimeOffset? StartUtc { get; }
    public DateTimeOffset? EndUtcExclusive { get; }

    public HistoryFilter(long? deviceId = null, string? pointName = null,
        DateTimeOffset? startUtc = null, DateTimeOffset? endUtcExclusive = null)
    {
        if (deviceId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(deviceId));
        if (startUtc.HasValue && endUtcExclusive.HasValue && startUtc >= endUtcExclusive)
            throw new ArgumentException("开始时间不能晚于结束时间。");
        DeviceId = deviceId;
        PointName = pointName?.Trim() ?? string.Empty;
        StartUtc = startUtc?.ToUniversalTime();
        EndUtcExclusive = endUtcExclusive?.ToUniversalTime();
    }
}

/// <summary>一页历史查询结果：本页记录、总数、页码、最大记录编号。</summary>
public sealed class HistoryPage
{
    public IReadOnlyList<HistoryRecord> Records
    {
        get;
    }
    public long TotalRecords
    {
        get;
    }
    public int PageNumber
    {
        get;
    }
    public long SnapshotId
    {
        get;
    }

    public HistoryPage(IReadOnlyList<HistoryRecord> records, long totalRecords, int pageNumber, long snapshotId)
    {
        Records = records;
        TotalRecords = totalRecords;
        PageNumber = pageNumber;
        SnapshotId = snapshotId;
    }
}

/// <summary>趋势查询结果：Records 用来画图，TotalRecords 是符合条件的总条数。</summary>
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

        Records = Array.AsReadOnly(records.ToArray());
        TotalRecords = totalRecords;
    }
}

/// <summary>设备筛选下拉框的一项，来自历史表，不要求这台设备的配置仍然存在。</summary>
public sealed class DeviceFilterOption
{
    public long? DeviceId
    {
        get;
    }
    public string DisplayName
    {
        get;
    }

    public DeviceFilterOption(long? deviceId, string name)
    {
        DeviceId = deviceId;
        DisplayName = deviceId.HasValue ? $"{name}（#{deviceId}）" : name;
    }
}

using System;

namespace DeviceMonitor.Core.Models
{
    /// <summary>
    /// 历史记录表格中的一行：记住哪台设备、哪个点、读到了什么，以及读取时间。
    /// 这里只定义数据的样子，不查询数据库，也不启动采集。
    /// </summary>
    public sealed class HistoryRecord
    {
        // 数据库中的历史编号。读取结果还没保存时为 0，查出后才有真正的编号。
        public long Id { get; }
        // 编号用于以后查询和筛选；名称用于界面显示。
        public long DeviceId { get; }
        public string DeviceName { get; }
        public long PointId { get; }
        public string PointName { get; }

        // 当前读取器返回的是 16 位整数，用 int 同时容纳正数和负数。
        public int Value { get; }
        public DateTimeOffset ReadAt { get; }

        // 保存原来的时间点，表格显示时再转成本机时间，避免把 UTC 直接当北京时间看。
        public DateTime ReadAtLocal => ReadAt.LocalDateTime;

        /// <summary>
        /// 把设备、采集点、读数和时间放进同一个对象，交给 HistoryStore 保存。
        /// HistoryStore 查询时也会创建这种对象，供表格显示和 CSV 导出。
        /// </summary>
        public HistoryRecord(long deviceId, string deviceName, long pointId,
            string pointName, int value, DateTimeOffset readAt, long id = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointId);
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
            ArgumentException.ThrowIfNullOrWhiteSpace(pointName);
            ArgumentOutOfRangeException.ThrowIfNegative(id);

            Id = id;
            DeviceId = deviceId;
            DeviceName = deviceName;
            PointId = pointId;
            PointName = pointName;
            Value = value;
            ReadAt = readAt;
        }
    }
}

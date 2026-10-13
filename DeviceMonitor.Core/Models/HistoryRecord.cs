using System;

namespace DeviceMonitor.Core.Models
{
    /// <summary>
    /// 一条历史读数：哪台设备、哪个点、什么值、什么时间。
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

        // 保留真实时间，表格显示时再转成本机时间。
        public DateTime ReadAtLocal => ReadAt.LocalDateTime;

        /// <summary>
        /// 把一条读数的数据装进对象，保存和查询历史时都用这种对象。
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

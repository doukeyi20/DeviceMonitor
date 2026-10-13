namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 一个点的一次成功读取结果：点位配置、原始值、解释后的值和读取时间。
    /// </summary>
    public sealed class PointReadResult
    {
        public PointInfo Point { get; } // 本次读的是哪个点，它的配置也一起带回来。
        public ushort RawValue { get; } // 设备返回的两个字节组成的原始整数。
        // 用 int 容纳两种 16 位整数的范围，包括 Int16 的负数。
        public int Value { get; }
        public DateTimeOffset ReadAt { get; } // 本次读取成功的时间，按 UTC 保存。

        // 把读取器得到的配置、数值和时间装在一起返回。
        public PointReadResult(PointInfo point, ushort rawValue, int value, DateTimeOffset readAt)
        {
            Point = point ?? throw new ArgumentNullException(nameof(point));
            RawValue = rawValue;
            Value = value;
            ReadAt = readAt;
        }
    }
}

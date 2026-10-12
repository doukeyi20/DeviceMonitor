namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// ModbusPointReader 一次成功读取后返回的结果：原点位配置、原始 16 位值、解释后的值和时间。
    /// 任务把它显示、判断报警并转为 HistoryRecord；单次测试只显示，不保存历史。
    /// </summary>
    public sealed class PointReadResult
    {
        public PointInfo Point { get; }
        public ushort RawValue { get; }
        // 用 int 容纳两种 16 位整数的范围，包括 Int16 的负数。
        public int Value { get; }
        public DateTimeOffset ReadAt { get; }

        public PointReadResult(PointInfo point, ushort rawValue, int value, DateTimeOffset readAt)
        {
            Point = point ?? throw new ArgumentNullException(nameof(point));
            RawValue = rawValue;
            Value = value;
            ReadAt = readAt;
        }
    }
}

namespace DeviceMonitor.Core.Models
{
    /// <summary>
    /// 指定怎样理解读回的数字；目前两种类型都只占一个寄存器。
    /// </summary>
    public enum PointDataType
    {
        UInt16 = 0, // 16 位无符号整数：0 到 65535。
        Int16 = 1   // 16 位有符号整数：-32768 到 32767。
    }
}

namespace DeviceMonitor.Core.Models
{
    /// <summary>
    /// 指定从设备的哪一类寄存器读取；数字与 Modbus 读取功能码一致。
    /// </summary>
    public enum RegisterType
    {
        Holding = 3, // 保持寄存器，读取使用功能码 03。
        Input = 4    // 输入寄存器，读取使用功能码 04。
    }
}

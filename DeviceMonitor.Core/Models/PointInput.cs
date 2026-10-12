namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 采集点表单检查后的输入包，PointViewModel 组装后交给 DeviceStore。
    /// 设备 Id 和编辑点 Id 单独传，防止输入框改到别的目标；空报警限值用 null，不是 0。
    /// </summary>
    public sealed class PointInput
    {
        public string Name { get; set; } = string.Empty;
        public RegisterType RegisterType { get; set; }
        public ushort Address { get; set; }
        public PointDataType DataType { get; set; }
        // 窗口留空时保存为 null，表示不检查这一侧的报警。
        public int? LowerLimit { get; set; }
        public int? UpperLimit { get; set; }
    }
}

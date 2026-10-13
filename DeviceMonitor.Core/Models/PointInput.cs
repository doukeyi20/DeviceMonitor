namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 采集点表单检查后的数据。设备编号和编辑点编号另外传。
    /// 报警限值留空用 null 表示，不是 0。
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

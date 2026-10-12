namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 一条已经保存的点位配置：DeviceId 说明属于哪台设备，Address/类型说明怎么读取。
    /// 设备可有多个点；本对象没有最近读数或采集 Task，读数显示在 PointReadingViewModel。
    /// </summary>
    public sealed class PointInfo
    {
        public long Id { get; }
        // 这个编号决定该点位属于哪台设备，不是窗口序号。
        public long DeviceId { get; }
        public string Name { get; }
        public RegisterType RegisterType { get; }
        // 保存真正发给设备的协议地址，从 0 开始，不包含 4xxxx 等编号前缀。
        public ushort Address { get; }
        public PointDataType DataType { get; }
        // 不填就不检查这一侧；上下限只用于报警，不会修改设备里的数值。
        public int? LowerLimit { get; }
        public int? UpperLimit { get; }

        // 表格显示中文描述，数据库仍保存上面对应的枚举值。
        public string RegisterTypeDescription => RegisterType switch
        {
            RegisterType.Holding => "保持寄存器（03）",
            RegisterType.Input => "输入寄存器（04）",
            _ => "未知寄存器类型"
        };

        public string DataTypeDescription => DataType switch
        {
            PointDataType.UInt16 => "UInt16 · 无符号整数",
            PointDataType.Int16 => "Int16 · 有符号整数",
            _ => "未知数据类型"
        };

        public PointInfo(long id, long deviceId, string name,
            RegisterType registerType, ushort address, PointDataType dataType,
            int? lowerLimit = null, int? upperLimit = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            if (!Enum.IsDefined(registerType))
                throw new ArgumentOutOfRangeException(nameof(registerType));
            if (!Enum.IsDefined(dataType))
                throw new ArgumentOutOfRangeException(nameof(dataType));
            ValidateLimits(dataType, lowerLimit, upperLimit);

            Id = id;
            DeviceId = deviceId;
            Name = name;
            RegisterType = registerType;
            Address = address;
            DataType = dataType;
            LowerLimit = lowerLimit;
            UpperLimit = upperLimit;
        }

        // 构造查询结果、保存表单都用同一套检查，避免界面漏检查后写入错误配置。
        internal static void ValidateLimits(PointDataType dataType, int? lowerLimit, int? upperLimit)
        {
            if (!Enum.IsDefined(dataType))
                throw new ArgumentOutOfRangeException(nameof(dataType));
            int minimum = dataType == PointDataType.Int16 ? short.MinValue : ushort.MinValue;
            int maximum = dataType == PointDataType.Int16 ? short.MaxValue : ushort.MaxValue;
            if (lowerLimit.HasValue && (lowerLimit < minimum || lowerLimit > maximum))
                throw new ArgumentOutOfRangeException(nameof(lowerLimit), $"报警下限必须在 {minimum} 到 {maximum} 之间。");
            if (upperLimit.HasValue && (upperLimit < minimum || upperLimit > maximum))
                throw new ArgumentOutOfRangeException(nameof(upperLimit), $"报警上限必须在 {minimum} 到 {maximum} 之间。");
            if (lowerLimit.HasValue && upperLimit.HasValue && lowerLimit >= upperLimit)
                throw new ArgumentException("报警下限必须小于上限。");
        }
    }
}

namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 点位配置对象，每个对象对应一个点位。
    /// DeviceId 说明属于哪台设备，Address 和类型说明怎么读。
    /// 这里只有配置，最新读数放在 PointReadingViewModel 中。
    /// </summary>
    public sealed class PointInfo
    {
        public long Id { get; } // 数据库中这个采集点的编号。
        // 这个编号决定该点位属于哪台设备，不是窗口序号。
        public long DeviceId { get; }
        public string Name { get; }
        public RegisterType RegisterType { get; } // 决定用 03 还是 04 功能码读取。
        // 保存真正发给设备的协议地址，从 0 开始，不包含 4xxxx 等编号前缀。
        public ushort Address { get; }
        public PointDataType DataType { get; } // 决定把两个字节解释成 UInt16 还是 Int16。
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

        // 检查配置后保存到只读属性，数据库查询结果和配置表的一行都用这个对象。
        public PointInfo(long id, long deviceId, string name,
            RegisterType registerType, ushort address, PointDataType dataType,
            int? lowerLimit = null, int? upperLimit = null)
        {
            // 编号、名称、类型和限值都必须有效。
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            if (!Enum.IsDefined(registerType))
                throw new ArgumentOutOfRangeException(nameof(registerType));
            if (!Enum.IsDefined(dataType))
                throw new ArgumentOutOfRangeException(nameof(dataType));
            ValidateLimits(dataType, lowerLimit, upperLimit);


            // 参数填进属性后，界面就能从这个 PointInfo 取出配置。
            Id = id;
            DeviceId = deviceId;
            Name = name;
            RegisterType = registerType;
            Address = address;
            DataType = dataType;
            LowerLimit = lowerLimit;
            UpperLimit = upperLimit;
        }

        // 查询和保存都检查配置，不能只依赖界面的输入检查。
        internal static void ValidateLimits(PointDataType dataType, int? lowerLimit, int? upperLimit)
        {
            if (!Enum.IsDefined(dataType))
                throw new ArgumentOutOfRangeException(nameof(dataType));
            // 先按类型确定整数范围，再检查限值是否在范围内、下限是否小于上限。
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

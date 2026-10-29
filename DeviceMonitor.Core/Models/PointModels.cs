// 定义点位配置、读取结果及工程量换算。
using System.Globalization;

namespace DeviceMonitor.Core.Models;

/// <summary>点位配置对象，每个对象对应一个点位。</summary>
public sealed class PointInfo
{
    public long Id
    {
        get;
    }

    public long DeviceId { get; }
    public string Name { get; }
    public RegisterType RegisterType
    {
        get;
    }

    public ushort Address { get; }
    public PointDataType DataType
    {
        get;
    }

    public double? LowerLimit { get; }
    public double? UpperLimit { get; }

    public double Scale { get; }
    public double Offset { get; }
    public string Unit { get; }
    public string ConversionDescription => $"× {NumericValue.Format(Scale)} + {NumericValue.Format(Offset)}";

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
        double? lowerLimit = null, double? upperLimit = null,
        double scale = 1, double offset = 0, string unit = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!Enum.IsDefined(registerType))
            throw new ArgumentOutOfRangeException(nameof(registerType));
        if (!Enum.IsDefined(dataType))
            throw new ArgumentOutOfRangeException(nameof(dataType));
        ValidateLimits(lowerLimit, upperLimit);
        ValidateConversion(dataType, scale, offset, unit);

        Id = id;
        DeviceId = deviceId;
        Name = name;
        RegisterType = registerType;
        Address = address;
        DataType = dataType;
        LowerLimit = lowerLimit;
        UpperLimit = upperLimit;
        Scale = scale;
        Offset = offset;
        Unit = unit.Trim();
    }

    internal static void ValidateLimits(double? lowerLimit, double? upperLimit)
    {
        if (lowerLimit.HasValue && !double.IsFinite(lowerLimit.Value))
            throw new ArgumentOutOfRangeException(nameof(lowerLimit), "报警下限必须是有限数值。");
        if (upperLimit.HasValue && !double.IsFinite(upperLimit.Value))
            throw new ArgumentOutOfRangeException(nameof(upperLimit), "报警上限必须是有限数值。");
        if (lowerLimit.HasValue && upperLimit.HasValue && lowerLimit >= upperLimit)
            throw new ArgumentException("报警下限必须小于上限。");
    }

    internal static void ValidateConversion(PointDataType dataType, double scale, double offset, string unit)
    {
        if (!Enum.IsDefined(dataType))
            throw new ArgumentOutOfRangeException(nameof(dataType));
        if (!double.IsFinite(scale) || scale == 0)
            throw new ArgumentOutOfRangeException(nameof(scale), "倍率必须是非零的有限数值。");
        if (!double.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset), "偏移量必须是有限数值。");
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Trim().Length > 20 || unit.Any(char.IsControl))
            throw new ArgumentException("单位最多 20 个字符，不能包含换行或控制字符。", nameof(unit));
        int minimum = dataType == PointDataType.Int16 ? short.MinValue : ushort.MinValue;
        int maximum = dataType == PointDataType.Int16 ? short.MaxValue : ushort.MaxValue;
        if (!double.IsFinite(minimum * scale + offset) || !double.IsFinite(maximum * scale + offset))
            throw new ArgumentException("倍率或偏移量过大，换算结果超出数值范围。");
    }
}

/// <summary>采集点表单检查后的数据。设备编号和编辑点编号另外传。</summary>
public sealed class PointInput
{
    public string Name { get; set; } = string.Empty;
    public RegisterType RegisterType { get; set; }
    public ushort Address { get; set; }
    public PointDataType DataType { get; set; }

    public double? LowerLimit { get; set; }
    public double? UpperLimit { get; set; }
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string Unit { get; set; } = "";
}

/// <summary>一个点的一次成功读取结果：点位配置、原始值、解释后的值和读取时间。</summary>
public sealed class PointReadResult
{
    public PointInfo Point
    {
        get;
    }
    public ushort RawValue
    {
        get;
    }

    public int DecodedValue { get; }

    public double Value { get; }
    public string ValueText => NumericValue.Format(Value, Point.Unit);
    public DateTimeOffset ReadAt
    {
        get;
    }

    public PointReadResult(PointInfo point, ushort rawValue, int value, DateTimeOffset readAt)
    {
        Point = point ?? throw new ArgumentNullException(nameof(point));
        RawValue = rawValue;
        DecodedValue = value;
        // 只在这里完成工程量换算，显示、报警和保存统一使用 Value。
        Value = value * point.Scale + point.Offset;
        if (!double.IsFinite(Value))
            throw new ArgumentOutOfRangeException(nameof(value), "换算结果不是有限数值。");
        ReadAt = readAt;
    }
}

/// <summary>指定从设备的哪一类寄存器读取；数字与 Modbus 读取功能码一致。</summary>
public enum RegisterType
{
    Holding = 3,
    Input = 4
}

/// <summary>指定怎样理解读回的数字；目前两种类型都只占一个寄存器。</summary>
public enum PointDataType
{
    UInt16 = 0,
    Int16 = 1
}

/// <summary>读数、限值和统计共用的显示格式；只格式化文字，不修改保存或比较的数值。</summary>
public static class NumericValue
{
    public static string Format(double value, string unit = "") =>
        value.ToString("G10", CultureInfo.InvariantCulture) +
        (string.IsNullOrWhiteSpace(unit) ? "" : " " + unit);
}

using System.Buffers.Binary;

namespace DeviceMonitor.Core.Communication;

/// <summary>RTU 请求和响应的字节格式；只负责组装、检查，不打开串口。</summary>
public static class ModbusRtuFrame
{
    /// <summary>串口读取前调用；发给一个站号，读取一个 16 位寄存器。</summary>
    public static byte[] CreateReadRequest(byte station, byte function, ushort address)
    {
        if (station is < 1 or > 247) throw new ArgumentOutOfRangeException(nameof(station));
        if (function is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(function));
        byte[] frame = new byte[8];
        frame[0] = station;
        frame[1] = function;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), 1);
        // 数值高字节在前，但末尾 CRC 的低字节在前。
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), CalculateCrc(frame.AsSpan(0, 6)));
        return frame;
    }

    /// <summary>串口收齐后调用；核对站号、功能码、长度和 CRC，再取出原始值。</summary>
    public static ushort ReadValue(ReadOnlySpan<byte> frame, byte station, byte function)
    {
        if (frame.Length < 5) throw new InvalidDataException("RTU 响应不完整。");
        ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        if (CalculateCrc(frame[..^2]) != crc)
            throw new InvalidDataException("RTU 响应 CRC 校验不通过，请检查接线和串口参数。");
        if (frame[0] != station) throw new InvalidDataException("响应站号不一致，不能当作本次读数。");
        if (frame[1] == (function | 0x80))
        {
            if (frame.Length != 5) throw new InvalidDataException("RTU 异常响应长度不正确。");
            string reason = frame[2] switch
            {
                1 => "设备不支持这个读取功能", 2 => "寄存器地址不可用，请核对地址和类型",
                3 => "设备不接受请求参数", 4 => "设备处理请求出错", 6 => "设备正忙",
                _ => "设备拒绝了本次读取"
            };
            throw new InvalidOperationException($"设备返回 Modbus 异常 0x{frame[2]:X2}：{reason}。");
        }
        if (frame[1] != function) throw new InvalidDataException("RTU 响应功能码不一致。");
        if (frame.Length != 7 || frame[2] != 2)
            throw new InvalidDataException("本次应返回一个 16 位寄存器，RTU 响应数据长度不正确。");
        return BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3, 2));
    }

    /// <summary>组装请求、检查响应时调用；CRC 用来检查传输中是否出现字节错误。</summary>
    public static ushort CalculateCrc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }
}

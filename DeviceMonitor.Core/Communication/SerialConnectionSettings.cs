using System.IO.Ports;
using System.Text.RegularExpressions;

namespace DeviceMonitor.Core.Communication;

/// <summary>保存设备和实际读取前，检查同一套 RTU 串口参数。</summary>
public static class SerialConnectionSettings
{
    public static string NormalizePortName(string name) => name.Trim().ToUpperInvariant();

    // 当前是 Windows 桌面程序，允许手填 COM 口，没插设备时也能先保存配置。
    public static bool IsPortNameValid(string name) =>
        Regex.IsMatch(NormalizePortName(name), @"^COM[1-9][0-9]*$");

    /// <summary>由 DeviceStore 和 ModbusPointReader 调用；有问题就说明哪个设置不对。</summary>
    public static void Validate(string portName, int baudRate, Parity parity, StopBits stopBits, byte station)
    {
        if (!IsPortNameValid(portName))
            throw new ArgumentException("串口名称应为 COM 加正整数，例如 COM3。");
        if (baudRate is < 1200 or > 115200)
            throw new ArgumentException("波特率应在 1200～115200 之间，请按设备说明书填写。");
        if (parity is not (Parity.None or Parity.Odd or Parity.Even))
            throw new ArgumentException("校验位只支持无、奇校验和偶校验。");
        if (stopBits is not (StopBits.One or StopBits.Two))
            throw new ArgumentException("停止位只支持 1 位或 2 位。");
        if (station is < 1 or > 247)
            throw new ArgumentException("RTU 站号必须为 1～247；0 是广播地址，不能读取。");
    }
}

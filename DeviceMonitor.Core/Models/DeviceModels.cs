// 定义设备连接配置并校验串口参数。
using System.Collections.Generic;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System;

namespace DeviceMonitor.Core.Models;

/// <summary>一台设备的一条连接配置，设备表格每行对应一个对象。</summary>
public class DeviceInfo
{
    public long Id
    {
        get;
    }
    public string Name { get; }
    public string Host
    {
        get;
    }
    public int Port
    {
        get;
    }
    public DeviceCommunicationMode CommunicationMode { get; }
    public string SerialPortName
    {
        get;
    }
    public int BaudRate
    {
        get;
    }
    public Parity Parity
    {
        get;
    }
    public StopBits StopBits
    {
        get;
    }
    public string CommunicationText => CommunicationMode == DeviceCommunicationMode.Rtu ? "Modbus RTU" : "Modbus TCP";

    public string Endpoint => CommunicationMode == DeviceCommunicationMode.Rtu
        ? $"{SerialPortName} · {BaudRate},8,{(Parity == Parity.None ? "N" : Parity == Parity.Even ? "E" : "O")},{(StopBits == StopBits.Two ? 2 : 1)}"
        : $"{Host}:{Port}";
    public byte UnitId
    {
        get;
    }
    public int SamplingIntervalMilliseconds
    {
        get;
    }
    public int ConnectTimeoutMilliseconds
    {
        get;
    }
    public int ReadTimeoutMilliseconds
    {
        get;
    }
    public int WriteTimeoutMilliseconds
    {
        get;
    }

    public bool IsEnabled { get; }

    public DeviceInfo(
        long id,
        string name,
        string host,
        int port,
        byte unitId,
        int samplingIntervalMilliseconds,
        int connectTimeoutMilliseconds,
        int readTimeoutMilliseconds,
        int writeTimeoutMilliseconds,
        bool isEnabled,
        DeviceCommunicationMode communicationMode = DeviceCommunicationMode.Tcp,
        string serialPortName = "", int baudRate = 4800,
        Parity parity = Parity.None, StopBits stopBits = StopBits.One)
    {
        Id = id;
        Name = name;
        Host = host;
        Port = port;
        CommunicationMode = communicationMode;
        SerialPortName = serialPortName;
        BaudRate = baudRate;
        Parity = parity;
        StopBits = stopBits;
        UnitId = unitId;
        SamplingIntervalMilliseconds = samplingIntervalMilliseconds;
        ConnectTimeoutMilliseconds = connectTimeoutMilliseconds;
        ReadTimeoutMilliseconds = readTimeoutMilliseconds;
        WriteTimeoutMilliseconds = writeTimeoutMilliseconds;
        IsEnabled = isEnabled;
    }
}

/// <summary>设备表单检查后的数据，新增和编辑都用它。</summary>
public class DeviceInput
{
    public string Name { get; set; } = string.Empty;

    public DeviceCommunicationMode CommunicationMode { get; set; } = DeviceCommunicationMode.Tcp;
    public string SerialPortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 4800;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public byte UnitId { get; set; }

    public int SamplingIntervalMilliseconds { get; set; }

    public int ConnectTimeoutMilliseconds { get; set; }

    public int ReadTimeoutMilliseconds { get; set; }

    public int WriteTimeoutMilliseconds { get; set; }
}

public enum DeviceCommunicationMode
{
    Tcp = 0, Rtu = 1
}

/// <summary>DeviceStore 保存和 ModbusPointReader 创建时共用的 RTU 参数检查。</summary>
public static class SerialConnectionSettings
{
    public static string NormalizePortName(string name) => name.Trim().ToUpperInvariant();

    public static bool IsPortNameValid(string name) =>
        Regex.IsMatch(NormalizePortName(name), @"^COM[1-9][0-9]*$");

    /// <summary>有问题就说明哪个设置不对。</summary>
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

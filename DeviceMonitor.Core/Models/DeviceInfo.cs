using System;
using System.Collections.Generic;
using System.Text;
using System.IO.Ports;

namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 一台设备的一条连接配置，设备表格每行对应一个对象。
    /// 这里只放配置，不放采集任务或最新读数。
    /// </summary>
    public class DeviceInfo
    {
        public long Id { get; } // 数据库配置编号；管理器也按它寻找这台设备的任务。
        public string Name { get; }
        public string Host { get; } // 连接的 IP 地址或主机名。
        public int Port { get; } // TCP 端口。
        public DeviceCommunicationMode CommunicationMode { get; }
        public string SerialPortName { get; } // RTU 使用的电脑串口，例如 COM3。
        public int BaudRate { get; } // 串口传输速度，同一条总线上的设备应一致。
        public Parity Parity { get; }
        public StopBits StopBits { get; }
        public string CommunicationText => CommunicationMode == DeviceCommunicationMode.Rtu ? "Modbus RTU" : "Modbus TCP";
        // 表格统一显示连接地址，RTU 不再显示无关的 IP 和 TCP 端口。
        public string Endpoint => CommunicationMode == DeviceCommunicationMode.Rtu
            ? $"{SerialPortName} · {BaudRate},8,{(Parity == Parity.None ? "N" : Parity == Parity.Even ? "E" : "O")},{(StopBits == StopBits.Two ? 2 : 1)}"
            : $"{Host}:{Port}";
        public byte UnitId { get; } // 发给 Modbus 设备的目标编号，不是数据库 Id。
        public int SamplingIntervalMilliseconds { get; } // 一轮全部读完后，再等待多久；单位毫秒。
        public int ConnectTimeoutMilliseconds { get; } // TCP 最多等多久连接成功；RTU 不使用这一项。
        public string ConnectTimeoutText => CommunicationMode == DeviceCommunicationMode.Rtu ? "—" : ConnectTimeoutMilliseconds.ToString();
        public int ReadTimeoutMilliseconds { get; } // 最多等多久收齐响应。
        public int WriteTimeoutMilliseconds { get; } // 最多等多久发完读取请求，不是修改设备数值。

        // 是否允许采集；true 不代表设备已经连接成功或当前在线。
        public bool IsEnabled { get; }

        // 把数据库查到的配置填入属性，供设备表显示和通信读取使用。
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
}

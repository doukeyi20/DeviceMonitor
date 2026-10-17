using System;
using System.Collections.Generic;
using System.Text;
using System.IO.Ports;

namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 设备表单检查后的数据，新增和编辑都用它。
    /// 编辑哪台设备的编号另外传，不放在这份输入里。
    /// </summary>
    public class DeviceInput
    {
        // 设备名称，用于在列表中识别设备。
        public string Name { get; set; } = string.Empty;

        public DeviceCommunicationMode CommunicationMode { get; set; } = DeviceCommunicationMode.Tcp;
        public string SerialPortName { get; set; } = string.Empty; // RTU 才填写，例如 COM3。
        public int BaudRate { get; set; } = 4800;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One; // 这次数据位固定为 8。

        // 设备的 IP 地址或主机名。
        public string Host { get; set; } = string.Empty;

        // TCP 端口，按照设备的实际配置填写。
        public int Port { get; set; }

        // TCP 是 Unit ID；RTU 是站号 1～247。两者都不是数据库设备 Id。
        public byte UnitId { get; set; }

        // 两轮采集之间的等待时间，单位：毫秒。
        public int SamplingIntervalMilliseconds { get; set; }

        // 建立连接时允许等待的最长时间，单位：毫秒。
        public int ConnectTimeoutMilliseconds { get; set; }

        // 读取操作的超时时间，单位：毫秒。
        public int ReadTimeoutMilliseconds { get; set; }

        // 发送读取请求的超时时间，单位：毫秒；不是在这里向设备寄存器写值。
        public int WriteTimeoutMilliseconds { get; set; }

    }
}

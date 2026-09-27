using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceMonitor.Core.Models
{
    /// <summary>
    /// 新增设备时填写的配置类。
    /// 只负责携带数据，不负责连接设备或操作数据库。
    /// </summary>
    public class DeviceInput
    {
        // 设备名称，用于在列表中识别设备。
        public string Name { get; set; } = string.Empty;

        // 设备的 IP 地址或主机名。
        public string Host { get; set; } = string.Empty;

        // TCP 端口，按照设备的实际配置填写。
        public int Port { get; set; }

        // Modbus TCP 的设备标识，按照设备要求填写。
        public byte UnitId { get; set; }

        // 两轮采集之间的等待时间，单位：毫秒。
        public int SamplingIntervalMilliseconds { get; set; }

        // 建立连接时允许等待的最长时间，单位：毫秒。
        public int ConnectTimeoutMilliseconds { get; set; }

        // 读取操作的超时时间，单位：毫秒。
        public int ReadTimeoutMilliseconds { get; set; }

        // 写入操作的超时时间，单位：毫秒。
        public int WriteTimeoutMilliseconds { get; set; }

    }
}

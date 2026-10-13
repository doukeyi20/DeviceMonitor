using System;
using System.Collections.Generic;
using System.Text;

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

        // 发送读取请求的超时时间，单位：毫秒；不是在这里向设备寄存器写值。
        public int WriteTimeoutMilliseconds { get; set; }

    }
}

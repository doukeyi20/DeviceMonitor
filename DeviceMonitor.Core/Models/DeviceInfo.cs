using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceMonitor.Core.Models
{

    /// <summary>
    /// 一条已经保存的设备配置，由 DeviceStore 查询创建，设备列表的每行显示一个它。
    /// Id 是数据库编号，Host/Port 是连接目标，UnitId 是 Modbus 报文目标；不保存采集循环或最新值。
    /// </summary>
    public class DeviceInfo
    {
        public long Id { get; } // 数据库配置编号；管理器也按它寻找这台设备的任务。
        public string Name { get; }
        public string Host { get; }
        public int Port { get; }
        public byte UnitId { get; } // 发给 Modbus 设备的目标编号，不是数据库 Id。
        public int SamplingIntervalMilliseconds { get; }
        public int ConnectTimeoutMilliseconds { get; }
        public int ReadTimeoutMilliseconds { get; }
        public int WriteTimeoutMilliseconds { get; }

        // 是否允许采集；true 不代表设备已经连接成功或当前在线。
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
            bool isEnabled)
        {
            Id = id;
            Name = name;
            Host = host;
            Port = port;
            UnitId = unitId;
            SamplingIntervalMilliseconds = samplingIntervalMilliseconds;
            ConnectTimeoutMilliseconds = connectTimeoutMilliseconds;
            ReadTimeoutMilliseconds = readTimeoutMilliseconds;
            WriteTimeoutMilliseconds = writeTimeoutMilliseconds;
            IsEnabled = isEnabled;
        }

    }
}

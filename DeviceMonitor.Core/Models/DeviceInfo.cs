using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceMonitor.Core.Models
{
    /// <summary>
    /// 从数据库读取出来的一条设备配置。
    /// </summary>
    public class DeviceInfo
    {
        public long Id { get; }
        public string Name { get; }
        public string Host { get; }
        public int Port { get; }
        public byte UnitId { get; }
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

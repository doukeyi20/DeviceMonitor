// 定义 EF Core 数据表实体。
using System.IO.Ports;

namespace DeviceMonitor.Model;

/// <summary>Users 表的一行，包含验证密码所需的数据；不能直接返回给页面。</summary>
internal sealed class UserRow
{
    public long Id { get; set; }
    public string UserName { get; set; } = "";

    public byte[] PasswordSalt { get; set; } = [];
    public byte[] PasswordHash { get; set; } = [];
    public int PasswordIterations { get; set; }

    public UserRole Role { get; set; }
    public bool IsEnabled { get; set; }
}

/// <summary>Devices 表的一行，只保存连接和采集配置，不保存运行任务。</summary>
internal sealed class DeviceRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int UnitId
    {
        get; set;
    }

    public int SamplingIntervalMilliseconds { get; set; }
    public int ConnectTimeoutMilliseconds { get; set; }
    public int ReadTimeoutMilliseconds { get; set; }
    public int WriteTimeoutMilliseconds { get; set; }
    public bool IsEnabled { get; set; }

    public DeviceCommunicationMode CommunicationMode { get; set; }
    public string SerialPortName { get; set; } = "";
    public int BaudRate { get; set; }
    public Parity Parity { get; set; }
    public StopBits StopBits { get; set; }
}

/// <summary>DevicePoints 表的一行；DeviceId 说明这个点属于哪台设备。</summary>
internal sealed class PointRow
{
    public long Id { get; set; }
    public long DeviceId { get; set; }
    public string Name { get; set; } = "";

    public RegisterType RegisterType { get; set; }
    public int Address { get; set; }
    public PointDataType DataType { get; set; }

    public double? LowerLimit { get; set; }
    public double? UpperLimit { get; set; }

    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string Unit { get; set; } = "";
}

/// <summary>HistoryRecords 表的一行，保留采集当时的设备名和点位名。</summary>
internal sealed class HistoryRow
{
    public long Id { get; set; }
    public long DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public long PointId { get; set; }
    public string PointName { get; set; } = "";

    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public DateTime ReadAtUtc
    {
        get; set;
    }
}

/// <summary>AlarmRecords 表的一行；相同 EventId 表示同一条报警的后续状态。</summary>
internal sealed class AlarmRow
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";

    public long DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public long? PointId
    {
        get; set;
    }
    public string PointName { get; set; } = "";

    public AlarmKind Kind { get; set; }
    public AlarmState State { get; set; }
    public string Message { get; set; } = "";
    public double? Value { get; set; }
    public double? LimitValue { get; set; }
    public string Unit { get; set; } = "";

    public DateTime StartedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int Occurrences
    {
        get; set;
    }
}

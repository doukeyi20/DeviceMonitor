using DeviceMonitor.Core.Models;
using System.IO.Ports;

namespace DeviceMonitor.Core.Persistence;

// 下面是 EF 读写表时使用的对象。只留在数据库层，不直接交给界面绑定。
// 界面仍使用原来的 DeviceInfo、PointInfo、HistoryRecord 等对象。

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
    public int UnitId { get; set; } // 数据库用整数保存，交给通信对象时再检查并转 byte。
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
    public int? LowerLimit { get; set; }
    public int? UpperLimit { get; set; }
}

/// <summary>HistoryRecords 表的一行，保留采集当时的设备名和点位名。</summary>
internal sealed class HistoryRow
{
    public long Id { get; set; }
    public long DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public long PointId { get; set; }
    public string PointName { get; set; } = "";
    public int Value { get; set; }
    public DateTime ReadAtUtc { get; set; } // EF 中按 UTC 时间查询，表里仍保存原来的时间文字。
}

/// <summary>AlarmRecords 表的一行；相同 EventId 表示同一条报警的后续状态。</summary>
internal sealed class AlarmRow
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";
    public long DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public long? PointId { get; set; }
    public string PointName { get; set; } = "";
    public AlarmKind Kind { get; set; }
    public AlarmState State { get; set; }
    public string Message { get; set; } = "";
    public int? Value { get; set; }
    public int? LimitValue { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int Occurrences { get; set; }
}

/// <summary>SQLite 自己维护的编号登记；这里只查询，不手工添加或更新。</summary>
internal sealed class SqliteSequenceRow
{
    public string Name { get; set; } = "";
    public long Sequence { get; set; }
}

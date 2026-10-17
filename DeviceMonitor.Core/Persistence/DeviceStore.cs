using DeviceMonitor.Core.Communication;
using DeviceMonitor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DeviceMonitor.Core.Persistence;

/// <summary>设备、点位的数据库操作。调用方式不变，内部用 EF 和 LINQ，不负责通信或刷新表格。</summary>
public class DeviceStore
{
    private readonly MonitorDbContextFactory _database; // 保存连接设置，不长期持有上下文。

    public DeviceStore(string databasePath) => _database = new(databasePath);

    /// <summary>设备管理、点位配置、监控页刷新时调用；默认包含停用设备。</summary>
    public IReadOnlyList<DeviceInfo> GetDevices(UserAccount currentUser, bool enabledOnly = false)
    {
        CheckDeviceReadPermission(currentUser);
        using var db = _database.Create();
        // Where、OrderBy 交给数据库执行；ToList 才真正查询，之后转换成原来的界面对象。
        var rows = db.Devices.AsNoTracking()
            .Where(x => !enabledOnly || x.IsEnabled)
            .OrderBy(x => EF.Functions.Collate(x.Name, "NOCASE")).ToList();
        return rows.Select(ToDeviceInfo).ToList();
    }

    /// <summary>采集任务和单次读取加载配置时调用；查不到或被启用条件排除时返回 null。</summary>
    public DeviceInfo? GetDevice(UserAccount currentUser, long deviceId, bool enabledOnly = false)
    {
        CheckDeviceReadPermission(currentUser);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        using var db = _database.Create();
        var row = db.Devices.AsNoTracking().FirstOrDefault(x => x.Id == deviceId && (!enabledOnly || x.IsEnabled));
        return row is null ? null : ToDeviceInfo(row);
    }

    /// <summary>设备管理新增时调用；同名返回 false，新设备仍先停用。</summary>
    public bool TryAddDevice(UserAccount currentUser, DeviceInput input)
    {
        CheckAdministrator(currentUser, "只有管理员可以新增设备。");
        ValidateDeviceInput(input);
        using var db = _database.Create();
        // 先取得写事务，再检查和分配编号，避免其他写入夹在中间。
        using var transaction = db.Database.BeginTransaction();
        CheckRtuStationAvailable(db, input, null);
        string name = input.Name.Trim();
        if (db.Devices.Any(x => x.Name == name)) return false;

        // 不能和已删除设备留下的历史编号重复，原有规则保留。
        long lastId = Math.Max(db.Devices.Max(x => (long?)x.Id) ?? 0,
            Math.Max(db.History.Max(x => (long?)x.DeviceId) ?? 0, db.Alarms.Max(x => (long?)x.DeviceId) ?? 0));
        var row = new DeviceRow { Id = checked(lastId + 1), IsEnabled = false };
        FillDevice(row, input);
        db.Devices.Add(row); // 这里只登记待新增对象。
        db.SaveChanges();    // 到这里才真正 INSERT。
        transaction.Commit();
        return true;
    }

    /// <summary>设备管理编辑时调用；按原编号更新，不改变启停状态。</summary>
    public bool TryUpdateDevice(UserAccount currentUser, long deviceId, DeviceInput input)
    {
        CheckAdministrator(currentUser, "只有管理员可以修改设备。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ValidateDeviceInput(input);
        using var db = _database.Create();
        using var transaction = db.Database.BeginTransaction();
        CheckRtuStationAvailable(db, input, deviceId);
        string name = input.Name.Trim();
        if (db.Devices.Any(x => x.Id != deviceId && x.Name == name)) return false;
        var row = db.Devices.FirstOrDefault(x => x.Id == deviceId);
        if (row is null) return false;

        // EF 记住查出的原对象，修改属性后 SaveChanges 更新对应记录。
        FillDevice(row, input);
        db.SaveChanges();
        transaction.Commit();
        return true;
    }

    /// <summary>设备管理启停配置时调用；只有状态确实变化才返回 true，不启动通信。</summary>
    public bool TrySetDeviceEnabled(UserAccount currentUser, long deviceId, bool enabled)
    {
        CheckAdministrator(currentUser, "只有管理员可以修改设备启停状态。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        using var db = _database.Create();
        // ExecuteUpdate 直接执行一次 UPDATE，不需要再调用 SaveChanges。
        return db.Devices.Where(x => x.Id == deviceId && x.IsEnabled != enabled)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.IsEnabled, enabled)) == 1;
    }

    /// <summary>运行管理器停完设备后调用；一起删除设备和点位，保留读数历史和报警。</summary>
    public bool TryDeleteDevice(UserAccount currentUser, long deviceId)
    {
        CheckAdministrator(currentUser, "只有已启用的管理员可以删除设备。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        using var db = _database.Create();
        using var transaction = db.Database.BeginTransaction();
        db.Points.Where(x => x.DeviceId == deviceId).ExecuteDelete(); // 先删所属点位。
        bool deleted = db.Devices.Where(x => x.Id == deviceId).ExecuteDelete() == 1;
        transaction.Commit();
        return deleted;
    }

    /// <summary>点位页面和采集加载配置时调用；只查配置，不读取设备数值。</summary>
    public IReadOnlyList<PointInfo> GetPoints(UserAccount currentUser, long deviceId)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        if (!currentUser.IsEnabled) throw new UnauthorizedAccessException("当前账户不能查看采集点。");
        using var db = _database.Create();
        var rows = db.Points.AsNoTracking().Where(x => x.DeviceId == deviceId)
            .OrderBy(x => EF.Functions.Collate(x.Name, "NOCASE")).ToList();
        return rows.Select(ToPointInfo).ToList();
    }

    /// <summary>PointViewModel 新增时调用；同名、设备停用或不存在时返回 false。</summary>
    public bool TryAddPoint(UserAccount currentUser, long deviceId, PointInput input)
    {
        CheckAdministrator(currentUser, "只有已启用的管理员可以新增采集点。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ValidatePointInput(input);
        using var db = _database.Create();
        using var transaction = db.Database.BeginTransaction();
        string name = input.Name.Trim();
        if (!db.Devices.Any(x => x.Id == deviceId && x.IsEnabled) ||
            db.Points.Any(x => x.DeviceId == deviceId && x.Name == name)) return false;

        long lastId = Math.Max(db.Points.Max(x => (long?)x.Id) ?? 0,
            Math.Max(db.History.Max(x => (long?)x.PointId) ?? 0, db.Alarms.Max(x => x.PointId) ?? 0));
        // SQLite 还记着曾用过但已删除的编号，也必须越过它。
        long sequence = db.Sequences.AsNoTracking().Where(x => x.Name == "DevicePoints")
            .Select(x => (long?)x.Sequence).FirstOrDefault() ?? 0;
        var row = new PointRow { Id = checked(Math.Max(lastId, sequence) + 1), DeviceId = deviceId };
        FillPoint(row, input);
        db.Points.Add(row);
        db.SaveChanges();
        transaction.Commit();
        return true;
    }

    /// <summary>PointViewModel 编辑时调用；两个编号一起核对，不把点位移到另一台设备。</summary>
    public bool TryUpdatePoint(UserAccount currentUser, long deviceId, long pointId, PointInput input)
    {
        CheckAdministrator(currentUser, "只有已启用的管理员可以修改采集点。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointId);
        ValidatePointInput(input);
        using var db = _database.Create();
        using var transaction = db.Database.BeginTransaction();
        string name = input.Name.Trim();
        if (!db.Devices.Any(x => x.Id == deviceId && x.IsEnabled) ||
            db.Points.Any(x => x.DeviceId == deviceId && x.Id != pointId && x.Name == name)) return false;
        var row = db.Points.FirstOrDefault(x => x.DeviceId == deviceId && x.Id == pointId);
        if (row is null) return false;
        FillPoint(row, input);
        db.SaveChanges();
        transaction.Commit();
        return true;
    }

    /// <summary>PointViewModel 删除时调用；只删指定设备下面的点位配置。</summary>
    public bool TryDeletePoint(UserAccount currentUser, long deviceId, long pointId)
    {
        CheckAdministrator(currentUser, "只有已启用的管理员可以删除采集点。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointId);
        using var db = _database.Create();
        return db.Points.Where(x => x.Id == pointId && x.DeviceId == deviceId &&
            db.Devices.Any(d => d.Id == deviceId && d.IsEnabled)).ExecuteDelete() == 1;
    }

    /// <summary>新增和编辑设备共用：检查输入；TCP、RTU 仍按各自规则检查。</summary>
    private static void ValidateDeviceInput(DeviceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
        if (!Enum.IsDefined(input.CommunicationMode)) throw new ArgumentException("请选择 TCP 或 RTU 连接方式。");
        if (input.CommunicationMode == DeviceCommunicationMode.Rtu)
            SerialConnectionSettings.Validate(input.SerialPortName, input.BaudRate, input.Parity, input.StopBits, input.UnitId);
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Host);
            if (input.Port is < 1 or > 65535) throw new ArgumentException("TCP 端口必须在 1～65535 之间。");
        }
        if (input.SamplingIntervalMilliseconds <= 0 || input.ConnectTimeoutMilliseconds <= 0 ||
            input.ReadTimeoutMilliseconds <= 0 || input.WriteTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "采集间隔和各项超时时间必须大于 0。");
    }

    private static void ValidatePointInput(PointInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
        if (!Enum.IsDefined(input.RegisterType)) throw new ArgumentOutOfRangeException(nameof(input.RegisterType));
        if (!Enum.IsDefined(input.DataType)) throw new ArgumentOutOfRangeException(nameof(input.DataType));
        PointInfo.ValidateLimits(input.DataType, input.LowerLimit, input.UpperLimit);
    }

    /// <summary>把表单值填到 EF 对象；不改 Id、IsEnabled。</summary>
    private static void FillDevice(DeviceRow row, DeviceInput input)
    {
        bool rtu = input.CommunicationMode == DeviceCommunicationMode.Rtu;
        row.Name = input.Name.Trim();
        row.Host = rtu ? "" : input.Host.Trim();
        row.Port = rtu ? 502 : input.Port; // 兼容旧表约束，RTU 通信不会使用这个占位端口。
        row.UnitId = input.UnitId;
        row.SamplingIntervalMilliseconds = input.SamplingIntervalMilliseconds;
        row.ConnectTimeoutMilliseconds = input.ConnectTimeoutMilliseconds;
        row.ReadTimeoutMilliseconds = input.ReadTimeoutMilliseconds;
        row.WriteTimeoutMilliseconds = input.WriteTimeoutMilliseconds;
        row.CommunicationMode = input.CommunicationMode;
        row.SerialPortName = rtu ? SerialConnectionSettings.NormalizePortName(input.SerialPortName) : "";
        row.BaudRate = input.BaudRate;
        row.Parity = input.Parity;
        row.StopBits = input.StopBits;
    }

    private static void FillPoint(PointRow row, PointInput input)
    {
        row.Name = input.Name.Trim();
        row.RegisterType = input.RegisterType;
        row.Address = input.Address;
        row.DataType = input.DataType;
        row.LowerLimit = input.LowerLimit;
        row.UpperLimit = input.UpperLimit;
    }

    /// <summary>保存前检查同 COM、同站号，排除当前正在编辑的那条记录。</summary>
    private static void CheckRtuStationAvailable(MonitorDbContext db, DeviceInput input, long? editingId)
    {
        if (input.CommunicationMode != DeviceCommunicationMode.Rtu) return;
        string port = SerialConnectionSettings.NormalizePortName(input.SerialPortName);
        if (db.Devices.Any(x => x.CommunicationMode == DeviceCommunicationMode.Rtu &&
            EF.Functions.Collate(x.SerialPortName, "NOCASE") == port && x.UnitId == input.UnitId &&
            (!editingId.HasValue || x.Id != editingId.Value)))
            throw new InvalidOperationException("这个 COM 口下已经配置了同一站号，请检查设备列表。");
    }

    // 查询完成后，转成原来的配置对象；调用方不用认识 EF。
    private static DeviceInfo ToDeviceInfo(DeviceRow x) => new(x.Id, x.Name, x.Host, x.Port,
        checked((byte)x.UnitId), x.SamplingIntervalMilliseconds, x.ConnectTimeoutMilliseconds,
        x.ReadTimeoutMilliseconds, x.WriteTimeoutMilliseconds, x.IsEnabled, x.CommunicationMode,
        x.SerialPortName, x.BaudRate, x.Parity, x.StopBits);

    private static PointInfo ToPointInfo(PointRow x) => new(x.Id, x.DeviceId, x.Name, x.RegisterType,
        checked((ushort)x.Address), x.DataType, x.LowerLimit, x.UpperLimit);

    private static void CheckAdministrator(UserAccount user, string message)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Role != UserRole.Administrator || !user.IsEnabled) throw new UnauthorizedAccessException(message);
    }

    private static void CheckDeviceReadPermission(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsEnabled) throw new UnauthorizedAccessException("当前账户不能查看设备。");
    }
}

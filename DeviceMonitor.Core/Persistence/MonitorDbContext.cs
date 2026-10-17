using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using DeviceMonitor.Core.Models;
using System.Globalization;
using System.IO.Ports;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// EF 的数据库操作入口：提供各表的 DbSet 和对象到表的映射。
/// Store 每次操作创建一份，用完释放；生成迁移时，EF 工具也要读取这里的模型。
/// </summary>
internal sealed class MonitorDbContext : DbContext
{
    // options 中包含 SQLite 连接配置；创建上下文本身不执行建表或查询。
    public MonitorDbContext(DbContextOptions<MonitorDbContext> options) : base(options)
    {
    }

    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<DeviceRow> Devices => Set<DeviceRow>();
    public DbSet<PointRow> Points => Set<PointRow>();
    public DbSet<HistoryRow> History => Set<HistoryRow>();
    public DbSet<AlarmRow> Alarms => Set<AlarmRow>();
    public DbSet<SqliteSequenceRow> Sequences => Set<SqliteSequenceRow>();

    /// <summary>历史和报警分页调用；总数和当前页读同一份数据，不提前占用写入锁。</summary>
    public SqliteTransaction BeginReadTransaction()
    {
        Database.OpenConnection();
        var transaction = ((SqliteConnection)Database.GetDbConnection()).BeginTransaction(deferred: true);
        Database.UseTransaction(transaction);
        return transaction; // 调用处 using 负责结束并释放这次事务。
    }

    /// <summary>EF 自动调用；描述完整表结构，不在这里执行建表。结构变化后要生成新的迁移。</summary>
    protected override void OnModelCreating(ModelBuilder model)
    {
        // 这次从空库生成 InitialCreate，不再经过旧的第 1～8 版手写 SQL 升级。
        // OnModelCreating 是结构描述；Add-Migration 生成步骤；Migrate/Update-Database 执行步骤。
        var users = model.Entity<UserRow>();
        users.ToTable("Users", table =>
        {
            table.HasCheckConstraint("CK_Users_PasswordIterations", "PasswordIterations > 0");
            table.HasCheckConstraint("CK_Users_Role", "Role IN (0, 1)");
            table.HasCheckConstraint("CK_Users_IsEnabled", "IsEnabled IN (0, 1)");
        });
        users.HasKey(x => x.Id);
        users.Property(x => x.Id).ValueGeneratedOnAdd();
        users.Property(x => x.UserName).UseCollation("NOCASE");
        users.HasIndex(x => x.UserName).IsUnique();
        // 不放初始账户或密码：新库仍由登录页创建首次管理员。

        var devices = model.Entity<DeviceRow>();
        devices.ToTable("Devices", table =>
        {
            table.HasCheckConstraint("CK_Devices_Port", "Port BETWEEN 1 AND 65535");
            table.HasCheckConstraint("CK_Devices_UnitId", "UnitId BETWEEN 0 AND 255");
            table.HasCheckConstraint("CK_Devices_SamplingInterval", "SamplingIntervalMilliseconds > 0");
            table.HasCheckConstraint("CK_Devices_ConnectTimeout", "ConnectTimeoutMilliseconds > 0");
            table.HasCheckConstraint("CK_Devices_ReadTimeout", "ReadTimeoutMilliseconds > 0");
            table.HasCheckConstraint("CK_Devices_WriteTimeout", "WriteTimeoutMilliseconds > 0");
            table.HasCheckConstraint("CK_Devices_IsEnabled", "IsEnabled IN (0, 1)");
            table.HasCheckConstraint("CK_Devices_CommunicationMode", "CommunicationMode IN (0, 1)");
            table.HasCheckConstraint("CK_Devices_BaudRate", "BaudRate > 0");
            table.HasCheckConstraint("CK_Devices_Parity", "Parity IN (0, 1, 2)");
            table.HasCheckConstraint("CK_Devices_StopBits", "StopBits IN (1, 2)");
        });
        devices.HasKey(x => x.Id);
        devices.Property(x => x.Id).ValueGeneratedNever(); // DeviceStore 分配编号，避开历史中的旧设备编号。
        devices.Property(x => x.Name).UseCollation("NOCASE");
        devices.Property(x => x.IsEnabled).HasDefaultValue(false);
        devices.Property(x => x.CommunicationMode).HasDefaultValue(DeviceCommunicationMode.Tcp);
        devices.Property(x => x.SerialPortName).UseCollation("NOCASE").HasDefaultValue("");
        devices.Property(x => x.BaudRate).HasDefaultValue(4800);
        devices.Property(x => x.Parity).HasDefaultValue(Parity.None);
        devices.Property(x => x.StopBits).HasDefaultValue(StopBits.One);
        devices.HasIndex(x => x.Name).IsUnique();
        // 只限制 RTU：同一 COM 口下不能配置两个相同站号。TCP 不受这个索引限制。
        devices.HasIndex(x => new { x.SerialPortName, x.UnitId }).IsUnique()
            .HasDatabaseName("IX_Devices_RtuStation").HasFilter("CommunicationMode = 1");

        var points = model.Entity<PointRow>();
        points.ToTable("DevicePoints", table =>
        {
            table.HasCheckConstraint("CK_DevicePoints_RegisterType", "RegisterType IN (3, 4)");
            table.HasCheckConstraint("CK_DevicePoints_Address", "Address BETWEEN 0 AND 65535");
            table.HasCheckConstraint("CK_DevicePoints_DataType", "DataType IN (0, 1)");
        });
        points.HasKey(x => x.Id);
        points.Property(x => x.Id).ValueGeneratedOnAdd(); // SQLite 自增登记，删除后不复用曾用点号。
        points.Property(x => x.Name).UseCollation("NOCASE");
        points.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
        points.HasOne<DeviceRow>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);

        // 表里统一保存 O 格式的 UTC 文字；EF 查询用 UTC DateTime，返回业务对象时仍用 DateTimeOffset。
        var utcText = new ValueConverter<DateTime, string>(
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
            value => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture).UtcDateTime);

        var history = model.Entity<HistoryRow>();
        history.ToTable("HistoryRecords", table =>
        {
            table.HasCheckConstraint("CK_HistoryRecords_DeviceId", "DeviceId > 0");
            table.HasCheckConstraint("CK_HistoryRecords_PointId", "PointId > 0");
        });
        history.HasKey(x => x.Id);
        history.Property(x => x.ReadAtUtc).HasConversion(utcText);
        // 历史按设备/时间筛选，以及按时间/编号倒序分页时使用这些索引。
        history.HasIndex(x => new { x.DeviceId, x.ReadAtUtc }).HasDatabaseName("IX_HistoryRecords_DeviceId_ReadAtUtc");
        history.HasIndex(x => new { x.ReadAtUtc, x.Id }).IsDescending(true, true)
            .HasDatabaseName("IX_HistoryRecords_ReadAtUtc_Id");

        var alarms = model.Entity<AlarmRow>();
        alarms.ToTable("AlarmRecords", table =>
        {
            table.HasCheckConstraint("CK_AlarmRecords_DeviceId", "DeviceId > 0");
            table.HasCheckConstraint("CK_AlarmRecords_PointId", "PointId IS NULL OR PointId > 0");
            table.HasCheckConstraint("CK_AlarmRecords_Kind", "Kind IN (1, 2, 3)");
            table.HasCheckConstraint("CK_AlarmRecords_State", "State IN (1, 2, 3)");
            table.HasCheckConstraint("CK_AlarmRecords_Occurrences", "Occurrences > 0");
        });
        alarms.HasKey(x => x.Id);
        alarms.HasIndex(x => x.EventId).IsUnique();
        alarms.Property(x => x.StartedAtUtc).HasConversion(utcText);
        alarms.Property(x => x.LastSeenAtUtc).HasConversion(utcText);
        alarms.Property(x => x.EndedAtUtc).HasConversion(utcText);
        alarms.HasIndex(x => new { x.StartedAtUtc, x.Id }).IsDescending(true, true)
            .HasDatabaseName("IX_AlarmRecords_StartedAtUtc_Id");
        alarms.HasIndex(x => new { x.DeviceId, x.StartedAtUtc, x.Id }).IsDescending(false, true, true)
            .HasDatabaseName("IX_AlarmRecords_DeviceId_StartedAtUtc_Id");
        // 历史、报警不加指向配置表的外键：删除配置后，过去记录仍然保留。

        var sequence = model.Entity<SqliteSequenceRow>();
        // 这是 SQLite 为自增表自动维护的系统表。只映射供查询，迁移绝不能手工创建它。
        sequence.ToTable("sqlite_sequence", table => table.ExcludeFromMigrations());
        sequence.HasKey(x => x.Name);
        sequence.Property(x => x.Name).HasColumnName("name");
        sequence.Property(x => x.Sequence).HasColumnName("seq");
    }
}

/// <summary>运行时工厂：Store 保存连接设置，每次操作再 Create；不是 EF 工具调用的设计时工厂。</summary>
internal sealed class MonitorDbContextFactory
{
    private readonly DbContextOptions<MonitorDbContext> _options;

    public MonitorDbContextFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true }.ToString();
        _options = new DbContextOptionsBuilder<MonitorDbContext>().UseSqlite(connectionString).Options;
    }

    public MonitorDbContext Create() => new(_options);
}

internal static class DatabaseQueries
{
    /// <summary>分页共用；仍在数据库跳过记录，也保留原来 long 偏移量的范围。</summary>
    public static IQueryable<T> SkipRows<T>(this IQueryable<T> query, long count)
    {
        while (count > 0)
        {
            int step = (int)Math.Min(count, int.MaxValue);
            query = query.Skip(step);
            count -= step;
        }
        return query;
    }
}

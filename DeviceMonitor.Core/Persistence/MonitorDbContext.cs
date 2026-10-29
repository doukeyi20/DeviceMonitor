// 定义 EF 模型映射及运行时、设计时上下文创建方式。
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using DeviceMonitor.Core.Models;
using System.Globalization;
using System.IO.Ports;

namespace DeviceMonitor.Core.Persistence;

/// <summary>EF 的数据库操作入口：提供各表的 DbSet 和对象到表的映射。</summary>
internal sealed class MonitorDbContext : DbContext
{
    public MonitorDbContext(DbContextOptions<MonitorDbContext> options) : base(options)
    {
    }

    // 各表的查询、写入入口由 Store 封装，不直接暴露给页面。
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<DeviceRow> Devices => Set<DeviceRow>();
    public DbSet<PointRow> Points => Set<PointRow>();
    public DbSet<HistoryRow> History => Set<HistoryRow>();
    public DbSet<AlarmRow> Alarms => Set<AlarmRow>();
    public DbSet<SqliteSequenceRow> Sequences => Set<SqliteSequenceRow>();

    /// <summary>总数和当前页读同一份数据，不提前占用写入锁。</summary>
    public SqliteTransaction BeginReadTransaction()
    {
        Database.OpenConnection();
        // 总数与当前页使用同一读事务，不提前占用写锁。
        var transaction = ((SqliteConnection)Database.GetDbConnection()).BeginTransaction(deferred: true);
        Database.UseTransaction(transaction);
        return transaction;
    }

    /// <summary>描述完整表结构，不在这里执行建表。结构变化后要生成新的迁移。</summary>
    protected override void OnModelCreating(ModelBuilder model)
    {
        // 集中描述表结构、约束及索引，变更通过 EF 迁移应用。
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
        devices.Property(x => x.Id).ValueGeneratedNever();
        devices.Property(x => x.Name).UseCollation("NOCASE");
        devices.Property(x => x.IsEnabled).HasDefaultValue(false);
        devices.Property(x => x.CommunicationMode).HasDefaultValue(DeviceCommunicationMode.Tcp);
        devices.Property(x => x.SerialPortName).UseCollation("NOCASE").HasDefaultValue("");
        devices.Property(x => x.BaudRate).HasDefaultValue(4800);
        devices.Property(x => x.Parity).HasDefaultValue(Parity.None);
        devices.Property(x => x.StopBits).HasDefaultValue(StopBits.One);
        devices.HasIndex(x => x.Name).IsUnique();

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
        points.Property(x => x.Id).ValueGeneratedOnAdd();
        points.Property(x => x.Name).UseCollation("NOCASE");

        points.Property(x => x.Scale).HasDefaultValue(1.0);
        points.Property(x => x.Offset).HasDefaultValue(0.0);
        points.Property(x => x.Unit).HasDefaultValue("");
        points.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
        points.HasOne<DeviceRow>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);

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
        history.Property(x => x.Unit).HasDefaultValue("");
        history.Property(x => x.ReadAtUtc).HasConversion(utcText);

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
        alarms.Property(x => x.Unit).HasDefaultValue("");
        alarms.HasIndex(x => x.EventId).IsUnique();
        alarms.Property(x => x.StartedAtUtc).HasConversion(utcText);
        alarms.Property(x => x.LastSeenAtUtc).HasConversion(utcText);
        alarms.Property(x => x.EndedAtUtc).HasConversion(utcText);
        alarms.HasIndex(x => new { x.StartedAtUtc, x.Id }).IsDescending(true, true)
            .HasDatabaseName("IX_AlarmRecords_StartedAtUtc_Id");
        alarms.HasIndex(x => new { x.DeviceId, x.StartedAtUtc, x.Id }).IsDescending(false, true, true)
            .HasDatabaseName("IX_AlarmRecords_DeviceId_StartedAtUtc_Id");

        var sequence = model.Entity<SqliteSequenceRow>();

        sequence.ToTable("sqlite_sequence", table => table.ExcludeFromMigrations());
        sequence.HasKey(x => x.Name);
        sequence.Property(x => x.Name).HasColumnName("name");
        sequence.Property(x => x.Sequence).HasColumnName("seq");
    }
}

/// <summary>供 Store 创建上下文：只长期保存连接配置，每次数据库操作单独 new 上下文，用完释放。</summary>
internal sealed class MonitorDbContextFactory
{
    private readonly DbContextOptions<MonitorDbContext> _options;

    public MonitorDbContextFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString();
        _options = new DbContextOptionsBuilder<MonitorDbContext>().UseSqlite(connectionString).Options;
    }

    public MonitorDbContext Create() => new(_options);
}

/// <summary>EF 迁移工具使用的设计时工厂：Add-Migration / Update-Database 自动调用。</summary>
internal sealed class MonitorDbContextDesignFactory : IDesignTimeDbContextFactory<MonitorDbContext>
{
    public MonitorDbContextDesignFactory()
    {
    }

    public MonitorDbContext CreateDbContext(string[] args)
    {
        string path = DatabaseInitializer.DefaultDatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new DbContextOptionsBuilder<MonitorDbContext>()
            .UseSqlite(DatabaseInitializer.CreateConnectionString(path)).Options;
        return new MonitorDbContext(options);
    }
}

// 初始化数据库目录和连接，应用待执行的 EF 迁移。
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeviceMonitor.Model;

public class DatabaseInitializer
{
    public string DatabasePath { get; }

    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeviceMonitor", "device-monitor.db");

    public DatabaseInitializer() => DatabasePath = DefaultDatabasePath;

    // 允许测试传入临时路径，避免检查程序访问正式数据库。
    public DatabaseInitializer(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (!Path.IsPathFullyQualified(databasePath) || string.IsNullOrEmpty(Path.GetFileName(databasePath)))
            throw new ArgumentException("请提供完整的数据库文件路径。", nameof(databasePath));

        DatabasePath = Path.GetFullPath(databasePath);
    }

    /// <summary>应用待执行迁移，并且首次运行时创建数据库和表。</summary>
    public void Initialize()
    {
        // 创建数据库文件夹
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        // 配置EF
        var options = new DbContextOptionsBuilder<MonitorDbContext>()
            .UseSqlite(CreateConnectionString(DatabasePath)).Options;

        // 创建上下文对象
        using var db = new MonitorDbContext(options);

        // 首次按迁移建库，以后只补执行尚未应用的迁移。
        db.Database.Migrate();
    }

    /// <summary>初始化和迁移工具共用；只生成连接配置字符串，不打开数据库。</summary>
    internal static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
}

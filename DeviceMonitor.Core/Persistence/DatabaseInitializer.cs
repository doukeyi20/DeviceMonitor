using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// App.OnStartup 调用的数据库准备入口。
/// 首次按 EF 迁移建库建表；以后只应用尚未执行的迁移，不清空业务数据。
/// </summary>
public class DatabaseInitializer
{
    public string DatabasePath { get; }

    // 程序启动和 EF 迁移工具共用同一路径，不把数据库建到两个不同的位置。
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeviceMonitor", "device-monitor.db");

    /// <summary>正常启动使用固定的应用数据库；构造时只保存路径，还没有建库。</summary>
    public DatabaseInitializer() => DatabasePath = DefaultDatabasePath;

    /// <summary>独立检查程序可指定临时库；正常启动不会使用这个入口。</summary>
    public DatabaseInitializer(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath) || string.IsNullOrEmpty(Path.GetFileName(databasePath)))
            throw new ArgumentException("请提供完整的数据库文件路径。", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
    }

    /// <summary>App.OnStartup 调用一次，成功后才创建 Store 并显示登录窗口。</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!); // 只准备目录，不是建表。
        var options = new DbContextOptionsBuilder<MonitorDbContext>()
            .UseSqlite(CreateConnectionString(DatabasePath)).Options;
        using var db = new MonitorDbContext(options); // 到这里还没有建库建表。

        // 真正应用项目 Migrations 目录中的迁移。
        // 没有库：创建库和全部业务表；有未执行的迁移：执行升级；没有新迁移：跳过。
        // 这里不会生成迁移，也不会删除数据库或重复创建已经存在的表。
        db.Database.Migrate();
    }

    /// <summary>初始化和迁移工具共用；只生成连接配置字符串，不打开数据库。</summary>
    internal static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate, // 应用首次迁移时，允许创建数据库文件。
            ForeignKeys = true
        }.ToString();
}

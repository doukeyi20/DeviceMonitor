using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// EF 迁移工具使用的设计时工厂：Add-Migration / Update-Database 自动调用。
/// 只创建上下文，不启动 WPF、不登录、不开始采集，也不在这里调用 Migrate。
/// </summary>
internal sealed class MonitorDbContextDesignFactory : IDesignTimeDbContextFactory<MonitorDbContext>
{
    public MonitorDbContextDesignFactory()
    {
    }

    public MonitorDbContext CreateDbContext(string[] args)
    {
        // 与 App.OnStartup 使用同一数据库路径和连接配置，避免迁移操作了另一份库。
        string path = DatabaseInitializer.DefaultDatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new DbContextOptionsBuilder<MonitorDbContext>()
            .UseSqlite(DatabaseInitializer.CreateConnectionString(path)).Options;
        return new MonitorDbContext(options); // 后续由迁移工具读取模型或应用迁移。
    }
}

// 验证项目功能及异常处理的回归检查。
using System.IO;
using DeviceMonitor.Core;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.ViewModels;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static async Task CheckAuditFixesAsync(UserAccount user)
    {
        string directory = Path.Combine(Output, "audit-fixes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "checks.db");
        new DatabaseInitializer(path).Initialize();
        var store = new DeviceStore(path);
        Seed(store, user);
        var page = new DeviceManagementViewModel(user, store);
        await page.RefreshDevicesCommand.ExecuteAsync(null);
        DeviceInfo device = page.Devices.First();

        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holder = HoldAuditWriteLockAsync(path, acquired);
        await acquired.Task;
        Task<bool> saving = page.TrySetDeviceEnabledAsync(device.Id, false);
        Check(!saving.IsCompleted && !page.RefreshDevicesCommand.CanExecute(null), "设备写入等待数据库时异步返回，并禁止重复配置操作");
        await Task.Delay(40);
        Check(!saving.IsCompleted, "数据库仍被占用时，界面已经能处理其他回调");
        await Task.WhenAll(saving, holder);
        Check(saving.Result && !store.GetDevice(user, device.Id)!.IsEnabled
            && page.RefreshDevicesCommand.CanExecute(null), "后台写入完成后显示真实状态并恢复按钮");

        device = store.GetDevices(user).First(item => item.IsEnabled);
        using var points = new PointManagementViewModel(user, store, device);
        await points.RefreshPointsCommand.ExecuteAsync(null);
        points.SelectedPoint = points.Points.First();
        points.UnitText = "audit-unit";
        acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holder = HoldAuditWriteLockAsync(path, acquired);
        await acquired.Task;
        Task pointSave = points.SavePointCommand.ExecuteAsync(null);
        Check(points.IsBusy && !points.CanChoosePoint && !points.CanEditConfiguration
            && !points.ConfirmDiscardChanges(), "点位保存期间锁定输入、选行和关闭，不假装取消已开始写入");
        await Task.WhenAll(pointSave, holder);
        Check(!points.IsBusy && points.SelectedPoint?.Unit == "audit-unit" && !points.HasUnsavedChanges,
            "点位异步保存后恢复选择、快照和可操作状态");

        var login = new LoginViewModel(new UserStore(path));
        await login.InitializeAsync();
        login.AdminUserName = "audit-admin";
        login.AdminPasswordsRequested += request => { request.Password = "Audit123!"; request.Confirmation = "Audit123!"; };
        await login.CreateFirstAdminCommand.ExecuteAsync(null);
        Check(!login.IsWorking && !login.CanCreateFirstAdministrator && login.IsLoginVisible,
            "首个管理员后台创建完成后恢复登录表单");
        login.LoginPasswordRequested += request => request.Password = "Audit123!";
        bool loginSucceeded = false;
        login.LoginSucceeded += _ => loginSucceeded = !login.IsWorking;
        await login.LoginCommand.ExecuteAsync(null);
        Check(loginSucceeded, "异步验证成功先解除忙状态，再通知登录窗口关闭");

        string logs = Path.Combine(directory, "logs");
        AppLog.Initialize(logs);
        AppLog.Warning("采集失败", "设备=1", new TimeoutException("不能记录的密码/SQL"), "read-1");
        AppLog.Warning("采集失败", "设备=1", new TimeoutException(), "read-1");
        AppLog.Warning("采集失败", "设备=1", new IOException(), "read-1");
        string[] lines = Directory.GetFiles(logs).SelectMany(File.ReadAllLines).ToArray();
        Check(lines.Length == 2 && lines[0].Contains("等待超时") && lines[1].Contains("读写失败"),
            "同错误限频，新原因立即记录，日志保留简短原因");
        Check(lines.All(line => !line.Contains("不能记录") && !line.Contains(" at ") && line.Length < 200),
            "日志不写异常原文、密码、SQL或整段堆栈");

        string cleanup = Path.Combine(directory, "cleanup-logs");
        Directory.CreateDirectory(cleanup);
        for (int i = 1; i <= 14; i++)
        {
            string old = Path.Combine(cleanup, $"device-monitor-20261020-120000000-1-{i:D4}.log");
            using (File.Create(old)) { }
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2).AddMinutes(i));
        }
        string locked = Path.Combine(cleanup, "device-monitor-20261020-120000000-1-0001.log");
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AppLog.Initialize(cleanup);
            AppLog.Info("写入成功");
            Check(AppLog.LastWriteError is null && Directory.GetFiles(cleanup).Any(file =>
                file != locked && File.ReadAllText(file).Contains("写入成功")),
                "旧日志无法删除不再误报成新日志写入失败");
        }

        await page.StopOperationsAsync();
    }

    private static Task HoldAuditWriteLockAsync(string path, TaskCompletionSource acquired) => Task.Run(() =>
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        acquired.TrySetResult();
        Thread.Sleep(900);
        transaction.Rollback();
    });
}

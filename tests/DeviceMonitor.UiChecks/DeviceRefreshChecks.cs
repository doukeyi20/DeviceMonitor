// 验证设备列表刷新与右侧表单加载分开；只使用临时数据库，不打开真实设备。
using System.IO;
using DeviceMonitor.Model;
using DeviceMonitor.Services;
using DeviceMonitor.ViewModel;

internal static partial class Program
{
    private static async Task CheckDeviceRefreshInteractionsAsync(UserAccount user)
    {
        string directory = Path.Combine(Output, "device-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "checks.db");
        new DatabaseInitializer(path).Initialize();
        var store = new DeviceStore(path);
        Seed(store, user);
        var tasks = new SamplingTasks(user, store, new HistoryStore(path));
        var page = new DeviceManagementViewModel(user, store, tasks);

        await page.RefreshDevicesCommand.ExecuteAsync(null);
        Check(page.Devices.Count == 2 && page.SelectedDevice is null && page.Editor is null && page.PointEditor is null,
            "第一次刷新只填充设备名单，不按默认设备加载表单或点位");
        page.SelectedDevice = page.Devices.First();
        await WaitForAsync(() => page.PointEditor is { IsBusy: false });
        DeviceInfo first = page.SelectedDevice;
        var oldEditor = page.Editor!;
        var oldPoints = page.PointEditor!;
        oldPoints.SelectedPoint = oldPoints.Points.First();
        oldEditor.DeviceName = "未保存输入";
        await page.RefreshDevicesCommand.ExecuteAsync(null);
        Check(page.SelectedDevice is null && page.Editor is null && page.PointEditor is null && !page.CanOpenPoints,
            "选过设备再刷新，也不恢复原设备、原点位或未保存表单");
        Check(!oldPoints.RefreshPointsCommand.CanExecute(null), "刷新时旧点位表单已解除使用，不再响应加载操作");
        page.SelectedDevice = page.Devices.Single(device => device.Id == first.Id);
        await WaitForAsync(() => page.PointEditor is { IsBusy: false });
        Check(!ReferenceEquals(page.Editor, oldEditor) && !ReferenceEquals(page.PointEditor, oldPoints) && page.PageMessage == ""
            && page.Editor!.DeviceName == first.Name && page.PointEditor!.SelectedPoint is null,
            "重新点击设备才加载最新表单，不恢复上一次点位选择");

        page.AddDeviceCommand.Execute(null);
        Check(page.Editor is not null && page.PointEditor is null && page.SelectedDevice is null,
            "新增仍显示空连接表单，不提前创建点位表单");
        page.Editor!.DeviceName = "尚未保存的新设备";
        await page.RefreshDevicesCommand.ExecuteAsync(null);
        Check(page.Editor is null && page.PointEditor is null && page.Devices.Count == 2,
            "新增状态本来没有选中设备，刷新也能清掉新增表单，不误存输入");

        page.SelectedDevice = page.Devices.Single(device => device.Id == first.Id);
        await WaitForAsync(() => page.PointEditor is { IsBusy: false });
        var connection = page.Editor!;
        connection.DeviceName = page.Devices.Single(device => device.Id != first.Id).Name;
        await connection.SaveCommand.ExecuteAsync(null);
        Check(page.SelectedDevice?.Id == first.Id && ReferenceEquals(page.Editor, connection)
            && connection.PageMessage.Contains("未保存"), "设备保存失败保留当前选择和输入，不能伪装成刷新成功");

        connection.DeviceName = "修改后的设备";
        page.SelectedTabIndex = 1;
        await connection.SaveCommand.ExecuteAsync(null);
        Check(page.SelectedDevice is null && page.Editor is null && page.PointEditor is null
            && page.SelectedTabIndex == 1 && store.GetDevice(user, first.Id)!.Name == "修改后的设备"
            && page.PageMessage.Contains("重新选择"),
            "设备修改保存后清空表单，不恢复选择，也不主动改变标签索引");

        page.SelectedDevice = page.Devices.Single(device => device.Id == first.Id);
        await WaitForAsync(() => page.PointEditor is { IsBusy: false });
        var keptEditor = page.Editor!;
        var keptPoints = page.PointEditor!;
        keptPoints.SelectedPoint = keptPoints.Points.First();
        keptPoints.UnitText = "只改点位";
        await keptPoints.SavePointCommand.ExecuteAsync(null);
        Check(page.SelectedDevice?.Id == first.Id && ReferenceEquals(page.Editor, keptEditor)
            && ReferenceEquals(page.PointEditor, keptPoints) && keptPoints.SelectedPoint?.Unit == "只改点位",
            "保存点位只更新当前点位列表，不刷新设备、不重建父页表单");
        await page.StopOperationsAsync();

        // 没有业务表的临时数据库会查询失败，确认退出时仍能解除通知。
        var brokenStore = new DeviceStore(Path.Combine(directory, "uninitialized.db"));
        var broken = new DeviceManagementViewModel(user, brokenStore, tasks);
        await broken.RefreshDevicesCommand.ExecuteAsync(null);
        Check(broken.PageMessage.Contains("加载设备失败") && !broken.IsBusy, "查询失败显示原因并解除忙状态");
        try { await broken.StopOperationsAsync(); }
        catch (Exception) { /* 原始查询任务报错仍交给退出流程，清理在 finally 完成。 */ }
        int notices = 0;
        broken.PropertyChanged += (_, _) => notices++;
        Set(tasks, nameof(SamplingTasks.SingleReadDeviceId), 123L);
        Set(tasks, nameof(SamplingTasks.SingleReadDeviceId), null);
        Check(notices == 0, "查询任务失败也会在退出时解除状态通知，不留下已关闭的配置页");
    }
}

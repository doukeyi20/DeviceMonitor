// 验证项目功能及异常处理的回归检查。
using DeviceMonitor.Services;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeviceMonitor;
using DeviceMonitor.Model;
using DeviceMonitor.ViewModel;
using DeviceMonitor.View;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static int _checks;
    private static readonly string Output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../..", "artifacts"));

    [STAThread]
    private static int Main()
    {
        int result = 1;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(); Console.WriteLine($"ALL {_checks} UI CHECKS PASSED"); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { SqliteConnection.ClearAllPools(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return result;
    }

    // 整套检查只使用临时库与构造的显示样本，不调用真实串口。
    private static async Task RunAsync()
    {
        Directory.CreateDirectory(Output);
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string path = Path.Combine(Output, $"workbench-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var users = new UserStore(path);
        users.TryCreateFirstAdministrator("admin", "UiAdmin123!");
        var user = users.CheckLogin("admin", "UiAdmin123!")!;
        var store = new DeviceStore(path);
        Seed(store, user);
        var history = new HistoryStore(path);

        await CheckAuditFixesAsync(user);
        await CheckDeviceRefreshInteractionsAsync(user);
        await CheckDirectPasswordFlowsAsync();
        await CheckPointManagementInteractionsAsync(user);
        await CheckLastQueryOperationsAsync(user);
        await CheckCreateUserAsync(user, users);
        await CheckFailedStopCleanupAsync(user, store, history);

        var window = new MainWindow(user, users, store, history);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var main = (MainViewModel)window.DataContext;
        var monitor = main.Pages.OfType<MonitoringViewModel>().Single();
        var config = main.Pages.OfType<DeviceManagementViewModel>().Single();
        Check(main.Pages.Count == 4 && main.Pages.All(page => page.Title != "采集点配置"),
            "管理员四个入口，点位与设备合并配置");

        await WaitForAsync(() => !monitor.IsRefreshing);
        await config.RefreshDevicesCommand.ExecuteAsync(null);
        Check(config.SelectedDevice is null && config.Editor is null && config.PointEditor is null,
            "设备列表加载后不自动选中设备，也不创建右侧表单");
        main.CurrentPage = config;
        Render(window, new Size(1424, 861), "configuration-unselected");
        var configView = Descendants<DeviceManagementView>((DependencyObject)window.Content).Single();
        Check(((StackPanel)configView.FindName("DeviceSelectionHint")).IsVisible
            && ((Grid)configView.FindName("DeviceEditorArea")).Visibility == Visibility.Collapsed
            && ((ListBox)configView.FindName("DevicesList")).SelectedItem is null,
            "未选设备时显示明确提示并隐藏表单，列表不会自动选第一项");
        // 后续删除使用检查中的确认回调；暂时离开配置页，避免真的弹出确认框。
        main.CurrentPage = monitor;
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        config.SelectedDevice = config.Devices.First();
        await WaitForAsync(() => config.PointEditor is { IsBusy: false });
        Check(config.SelectedDevice is not null && config.Editor is not null && config.PointEditor is not null,
            "选中设备同时载入连接表单和点位，不额外打开窗口");
        var first = config.SelectedDevice!;
        config.Editor!.DeviceName = "未保存";
        config.ResetDeviceCommand.Execute(null);
        Check(config.Editor.DeviceName == first.Name && store.GetDevice(user, first.Id)!.Name == first.Name,
            "撤销连接输入只恢复表单，不写数据库");
        config.SelectedTabIndex = 1;
        config.AddDeviceCommand.Execute(null);
        Check(config.SelectedDevice is null && config.SelectedTabIndex == 0 && !config.CanOpenPoints,
            "新增自动回到连接参数，保存前不能误改旧设备点位");
        var editor = config.Editor!;
        editor.DeviceName = "新 TCP 设备";
        editor.Host = "127.0.0.1";
        editor.PortText = "0";
        await editor.SaveCommand.ExecuteAsync(null);
        Check(editor.PageMessage.Contains("端口") && store.GetDevices(user).Count == 2,
            "端口不合法保留输入，尚未保存设备");
        editor.PortText = "503";
        await editor.SaveCommand.ExecuteAsync(null);
        Check(config.SelectedDevice is null && config.SelectedTabIndex == 0 && !config.CanOpenPoints
            && config.Editor is null && config.PointEditor is null && config.Devices.Any(device => device.Name == "新 TCP 设备"),
            "新增保存后只刷新列表并清空表单，不自动选择或跳到点位标签");
        config.SelectedDevice = config.Devices.Single(device => device.Name == "新 TCP 设备");
        await WaitForAsync(() => config.PointEditor is { IsBusy: false });
        Check(config.CanOpenPoints && config.PointEditor?.Points.Count == 0,
            "用户重新点击新设备后，可以自行进入点位配置");
        long addedId = config.SelectedDevice.Id;
        config.ConfirmationRequested += (_, _) => true;
        await config.DeleteDeviceCommand.ExecuteAsync(null);
        Check(store.GetDevice(user, addedId) is null && config.SelectedDevice is null && config.Editor is null,
            "空设备正常删除后清空选择，不自动跳到其他设备");

        config.SelectedDevice = config.Devices.First();
        await WaitForAsync(() => config.PointEditor is { IsBusy: false });
        config.SelectedTabIndex = 1;
        var pointEditor = config.PointEditor!;
        pointEditor.SelectedPoint = pointEditor.Points.First();
        Check(pointEditor.PointName == pointEditor.SelectedPoint.Name && pointEditor.ReadOnceCommand.CanExecute(null),
            "点位选中直接编辑，已保存点可单点测试");
        main.CurrentPage = config;
        Render(window, new Size(1424, 861), "configuration-points");
        CheckNoPageTitle((DependencyObject)window.Content, "统一配置");
        Check(Descendants<PointEditorView>((DependencyObject)window.Content).Any(), "点位表单内嵌在配置页");
        CheckTables((DependencyObject)window.Content, "统一配置正常窗口");
        config.SelectedTabIndex = 0;
        Render(window, new Size(1424, 861), "configuration-connection");
        Render(window, new Size(1240, 681), "configuration-small");
        config.SelectedTabIndex = 1;
        Render(window, new Size(1240, 681), "configuration-points-small");
        CheckTables((DependencyObject)window.Content, "小窗口点位配置");

        await monitor.RefreshDevicesCommand.ExecuteAsync(null);
        Check(monitor.SelectedDevice is not null && !monitor.HasReadings && monitor.ActiveSamplingCount == 0,
            "监控默认选择首设备，但不伪造数据也不自动开始");
        main.CurrentPage = monitor;
        Render(window, new Size(1424, 861), "monitor-empty");
        Check(monitor.StartSamplingCommand.CanExecute(null) && !monitor.StopSamplingCommand.CanExecute(null),
            "未采集设备只允许开始，停止按钮不误导");
        var devices = monitor.Devices.ToArray();
        var firstSession = main.SamplingTasks.GetOrCreateSession(devices[0].Device.Id);
        Set(main.SamplingTasks, nameof(SamplingTasks.SingleReadDeviceId), devices[0].Device.Id);
        bool testConflict = false;
        try { main.SamplingTasks.Start(devices[0].Device.Id); }
        catch (InvalidOperationException) { testConflict = true; }
        Set(main.SamplingTasks, nameof(SamplingTasks.SingleReadDeviceId), null);
        Check(testConflict, "测试尚未收尾时拒绝从监控页同时开始");

        // 构造多点位卡片样本，真实通信另由 RTU/TCP 检查覆盖。
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var row in devices)
        {
            var session = main.SamplingTasks.GetOrCreateSession(row.Device.Id);
            Set(session, nameof(session.IsRunning), true);
            Set(session, nameof(session.LastSuccessfulReadAt), now);
            for (int n = 0; n < 8; n++)
            {
                var point = new PointInfo(row.Device.Id * 100 + n, row.Device.Id,
                    n == 0 ? "温度" : n == 1 ? "湿度" : $"测点 {n + 1}",
                    RegisterType.Holding, (ushort)n, PointDataType.Int16,
                    upperLimit: n == 0 ? 26 : null, scale: 0.1, unit: n == 0 ? "℃" : n == 1 ? "%RH" : "");
                var reading = new PointReadingViewModel(row.Device, point, main.SamplingTasks.Alarms, session);
                session.Readings.Add(reading);
                for (int i = 0; i < 100; i++)
                {
                    int value = 230 + n * 20 + (int)(Math.Sin(i / 10.0) * 20);
                    reading.ShowResult(new PointReadResult(point, (ushort)value, value, now.AddSeconds(i - 99)),
                        TimeSpan.FromSeconds(i));
                }
            }
        }
        monitor.SelectedDevice = devices[0];
        var keptPoint = firstSession.Readings.First();
        monitor.SelectedReading = keptPoint;
        int sampleCount = keptPoint.TrendSamples.Count;
        await WaitForAsync(() => monitor.Trend.Samples.Count == sampleCount);
        Check(monitor.Trend.Samples.SequenceEqual(keptPoint.TrendSamples),
            "任务快速填入读数后，曲线自动显示选中点的全部缓存");
        monitor.SelectedReading = firstSession.Readings[1];
        Check(monitor.Trend.AxisTitle.Contains("湿度")
            && monitor.Trend.Samples.SequenceEqual(firstSession.Readings[1].TrendSamples),
            "选择另一张读数卡只靠属性变化通知切换曲线");
        monitor.SelectedReading = null;
        Check(monitor.Trend.Samples.Count == 0 && monitor.Trend.AxisTitle == "读数",
            "清空读数选择时，属性变化通知也会清空当前曲线显示，不删除点位缓存");
        monitor.SelectedReading = keptPoint;
        Check(monitor.ReadingColumns == 4 && monitor.Readings.Count() == 8 && ReferenceEquals(monitor.Readings, firstSession.Readings),
            "只绑定当前设备原点位集合，不建立全部设备拼接副本");
        monitor.SelectedDevice = devices[1];
        Check(monitor.Readings.All(point => point.DeviceId == devices[1].Device.Id) && firstSession.IsRunning,
            "切换设备只改变显示，另一台仍采集");
        monitor.SelectedDevice = devices[0];
        monitor.SelectedReading = keptPoint;
        Check(monitor.Trend.Samples.Count == sampleCount, "切回设备时恢复原缓存，不清空曲线");

        await CheckAlarmFlowAsync(new AlarmMonitor(user, new AlarmStore(path)), store, user, path, devices[0].Device, keptPoint.Point);
        var currentHigh = new PointReadResult(keptPoint.Point, 300, 300, now.AddSeconds(1));
        keptPoint.ShowResult(currentHigh, TimeSpan.FromSeconds(100));
        await main.SamplingTasks.Alarms.ReportReadingsAsync(devices[0].Device, [currentHigh]);
        Render(window, new Size(1424, 861), "monitor-cards");
        Check(Descendants<TrendChart>((DependencyObject)window.Content).Single().ActualHeight >= 180,
            "八个点位也保留足够曲线高度");
        var cards = Descendants<ListBox>((DependencyObject)window.Content).Single(box => box.Name == "ReadingCards");
        Check(cards.ActualHeight <= 260 && cards.Items.Count == 8, "多点位卡片内部滚动，最多占两行高度");
        Check(!Descendants<CheckBox>((DependencyObject)window.Content).Any(), "监控不再维护第二套勾选选择");
        CheckNoPageTitle((DependencyObject)window.Content, "监控");
        Render(window, new Size(1240, 681), "monitor-small");
        var smallChart = Descendants<TrendChart>((DependencyObject)window.Content).Single();
        var smallRoot = (FrameworkElement)window.Content;
        var bottom = smallChart.TranslatePoint(new Point(0, smallChart.ActualHeight), smallRoot).Y;
        Check(bottom <= smallRoot.ActualHeight && cards.MaxHeight == 128, "小窗口卡片缩为一行，曲线底部仍在窗口内");
        Render(window, new Size(1904, 1001), "monitor-large");

        var current = monitor.SelectedDevice;
        await main.SamplingTasks.Alarms.ReportDeviceFailureAsync(devices[1].Device.Id, devices[1].Device.Name, "等待超时");
        Check(ReferenceEquals(current, monitor.SelectedDevice), "报警不会自动抢走当前设备选择");
        monitor.LocateAlarmCommand.Execute(main.SamplingTasks.Alarms.ActiveRecords.First(a => a.DeviceId == devices[1].Device.Id));
        Check(monitor.SelectedDevice == devices[1], "点击报警才定位到对应设备");
        foreach (var task in main.SamplingTasks.Sessions) Set(task, nameof(task.IsRunning), false);

        DeviceInfo saved = store.GetDevices(user).First();
        PointInfo savedPoint = store.GetPoints(user, saved.Id).First();
        await CheckTrendNotificationsAsync(saved, savedPoint);
        history.SaveRecord(user, new(saved.Id, saved.Name, savedPoint.Id, savedPoint.Name, 23.5, now));
        var historyPage = main.Pages.OfType<HistoryViewModel>().Single();
        historyPage.UseTimeFilter = true;
        historyPage.StartDate = historyPage.EndDate = DateTime.Today;
        historyPage.StartTimeText = "00:00:00";
        historyPage.EndTimeText = "23:59:59";
        await historyPage.RefreshRecordsCommand.ExecuteAsync(null);
        Check(historyPage.UseTimeFilter && historyPage.StartDate == DateTime.Today && historyPage.TotalRecords >= 1,
            "手动填写日期和时间后查询，保留基本时间筛选");
        main.CurrentPage = historyPage;
        Render(window, new Size(1424, 861), "history");
        var historyTable = Descendants<DataGrid>((DependencyObject)window.Content)
            .Single(grid => ReferenceEquals(grid.ItemsSource, historyPage.Records));
        Check(historyTable.SelectedItem is null && historyPage.HasQueried,
            "历史查询成功后表格不自动选择行，页面记录已查询状态");
        var historyView = Descendants<HistoryView>((DependencyObject)window.Content).Single();
        string[] removedActions = { "选中点位趋势", "点位趋势", "最近一小时", "今天", "最近 24 小时", "全部时间" };
        Check(!Descendants<Button>(historyView).Any(button => button.Content is string label && removedActions.Contains(label)),
            "采集历史只提供基本查询分页导出，不保留趋势和快捷时间入口");
        historyTable.SelectedItem = historyPage.Records.First();
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Check(ReferenceEquals(historyTable.SelectedItem, historyPage.Records.First()),
            "表格仍可以选中一行查看，高亮不需要额外业务选择对象");
        await historyPage.RefreshRecordsCommand.ExecuteAsync(null);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Check(historyTable.SelectedItem is null,
            "真实表格刷新后移除旧选择，不自动选第一行");
        await historyPage.Alarms.RefreshRecordsCommand.ExecuteAsync(null);
        var alarmTab = Descendants<TabItem>((DependencyObject)window.Content).Single(tab => Equals(tab.Header, "报警历史"));
        alarmTab.IsSelected = true;
        Render(window, new Size(1424, 861), "alarm-history");
        alarmTab.IsSelected = false;

        var usersPage = main.Pages.OfType<UserManagementViewModel>().Single();
        await usersPage.RefreshUsersCommand.ExecuteAsync(null);
        usersPage.ConfirmationRequested += (_, _) => true;
        usersPage.SelectedUser = usersPage.Users.Single(account => account.Role == UserRole.Operator);
        Check(usersPage.OperatorActionText == "停用账户" && usersPage.ToggleOperatorCommand.CanExecute(null),
            "选中已启用操作员，按钮只显示可执行的停用动作");
        await usersPage.ToggleOperatorCommand.ExecuteAsync(null);
        Check(usersPage.SelectedUser?.IsEnabled == false && usersPage.OperatorActionText == "启用账户",
            "停用保存后按钮自动切换为启用，不显示两枚互斥按钮");
        await usersPage.ToggleOperatorCommand.ExecuteAsync(null);
        Check(usersPage.SelectedUser?.IsEnabled == true, "再次点击可恢复操作员状态");
        usersPage.SelectedUser = usersPage.Users.Single(account => account.Role == UserRole.Administrator);
        Check(!usersPage.ToggleOperatorCommand.CanExecute(null), "同一入口不能停用管理员");
        main.CurrentPage = usersPage;
        Render(window, new Size(1424, 861), "users");
        CheckTables((DependencyObject)window.Content, "用户页");

        var worker = new UserAccount(20, "worker", UserRole.Operator, true);
        var workerMain = new MainViewModel(worker, users, store, history);
        Check(workerMain.Pages.Count == 2 && workerMain.Pages.Any(page => page is HistoryViewModel)
            && !workerMain.Pages.Any(page => page is DeviceManagementViewModel or UserManagementViewModel),
            "操作员能监控和查询历史，但不能修改配置或账户");
        string workerCsv = Path.Combine(Output, "worker-history.csv");
        string workerAlarms = Path.Combine(Output, "worker-alarms.csv");
        new HistoryCsvExporter(history).Export(worker, new HistoryFilter(), long.MaxValue, workerCsv);
        new AlarmCsvExporter(new AlarmStore(path)).Export(worker, new AlarmFilter(), long.MaxValue, workerAlarms);
        Check(File.Exists(workerCsv) && File.Exists(workerAlarms), "操作员的只读权限包含历史和报警导出");
        await workerMain.StopBackgroundWorkAsync();

        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        int nativeStyle = GetWindowLong(handle, -16);
        Check((nativeStyle & 0x00010000) != 0 && (nativeStyle & 0x00040000) == 0,
            "仍允许最大化，但不允许拖边框任意缩放");
        Check(window.Icon is not null && window.Width == 1440 && window.Height == 900,
            "主窗口保留应用图标和初始大小");
        Check(Descendants<Button>((DependencyObject)window.Content).Count(button => Equals(button.Content, "退出登录")) == 1,
            "退出登录只在左下角保留一个入口");
        await main.StopBackgroundWorkAsync();
        Check(!monitor.RefreshDevicesCommand.CanExecute(null) && !config.RefreshDevicesCommand.CanExecute(null),
            "退出后不再接受页面操作");
        typeof(MainWindow).GetField("_canClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
        AppLog.Close();
    }

    private static async Task CheckAlarmFlowAsync(AlarmMonitor alarms, DeviceStore store, UserAccount user,
        string path, DeviceInfo device, PointInfo point)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var high = new PointReadResult(point, 300, 300, now);
        await alarms.ReportReadingsAsync(device, [high]);
        await alarms.ReportReadingsAsync(device, [high]);
        Check(alarms.ActiveCount == 1 && alarms.Records.Count == 1 && alarms.ActiveRecords.Single().Occurrences == 2,
            "持续越限更新原报警次数，不每轮新增");
        await alarms.ReportDeviceFailureAsync(device.Id, device.Name, "等待超时");
        await alarms.ReportDeviceFailureAsync(device.Id, device.Name, "等待超时");
        Check(alarms.ActiveRecords.Count(a => a.Kind == AlarmKind.AcquisitionFailure) == 1
            && alarms.ActiveRecords.Single(a => a.Kind == AlarmKind.AcquisitionFailure).PointId is null,
            "整台设备通信故障只记一条，不按点位复制相同记录");
        await alarms.ReportReadingsAsync(device, [new PointReadResult(point, 200, 200, now.AddSeconds(1))]);
        Check(alarms.ActiveCount == 0 && !alarms.HasActiveAlarms && alarms.Records.All(a => a.State == AlarmState.Recovered),
            "读取成功恢复设备故障，回到范围恢复数值报警，当前列表同步清空");

        RenameTable(path, "AlarmRecords", "HiddenAlarmRecords");
        try { await alarms.ReportDeviceFailureAsync(device.Id, device.Name, "模拟保存失败"); }
        finally { RenameTable(path, "HiddenAlarmRecords", "AlarmRecords"); }
        Check(alarms.PendingSaveCount == 1 && alarms.PersistenceStatusText.Contains("保存失败"),
            "报警保存失败保留待存更新，界面能看到保存故障");
        await alarms.FlushAsync();
        Check(alarms.PendingSaveCount == 0 && alarms.PersistenceStatusText == "",
            "数据库恢复后保存原事件并清除错误提示");
        await alarms.EndDeviceMonitoringAsync(device.Id);
        Check(alarms.ActiveCount == 0 && alarms.Records.Any(a => a.State == AlarmState.NotMonitoring),
            "停止标记不再监测，不冒充设备故障已恢复");
        await alarms.ReportReadingsAsync(device, [high]);
        Check(new AlarmStore(path).GetRecentRecords(user).Any(), "报警变化已写入临时数据库");
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        for (int i = 0; i < 500 && !ready(); i++) await Task.Delay(10);
        Check(ready(), "异步界面加载在限定时间内完成");
    }

    private static async Task CheckLastQueryOperationsAsync(UserAccount user)
    {
        string path = Path.Combine(Output, $"last-query-only-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var store = new DeviceStore(path);
        Seed(store, user);
        DeviceInfo device = store.GetDevices(user).First();
        PointInfo point = store.GetPoints(user, device.Id).First();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var readings = new HistoryStore(path);
        readings.SaveRecords(user, Enumerable.Range(0, 205).Select(index => new HistoryRecord(
            device.Id, device.Name, point.Id, point.Name, index, now.AddSeconds(-index))).ToArray());
        var alarms = new AlarmStore(path);
        alarms.SaveRecords(user, Enumerable.Range(0, 205).Select(index => new AlarmRecord
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            PointId = point.Id,
            PointName = point.Name,
            Kind = AlarmKind.AcquisitionFailure,
            State = AlarmState.Active,
            Message = "测试异常",
            StartedAt = now.AddSeconds(-index),
            LastSeenAt = now.AddSeconds(-index)
        }).ToArray());
        var history = new HistoryViewModel(user, readings, alarms);
        try
        {
            Task loading = history.RefreshRecordsCommand.ExecuteAsync(null);
            Check(history.IsLoading && !history.CanEditFilters && !history.ExportRecordsCommand.CanExecute(null),
                "历史查询期间禁用输入和重复操作，不维护输入版本号");
            await loading;
            Check(history.HasQueried && history.Records.Count == 200 && history.TotalPages == 2,
                "历史查询保存成功状态，第一页最多显示200条");
            readings.SaveRecord(user, new HistoryRecord(device.Id, device.Name, point.Id, point.Name,
                9999, now.AddSeconds(10)));
            history.PointNameFilter = "尚未查询的新条件";
            Check(history.NextPageCommand.CanExecute(null)
                && history.ExportRecordsCommand.CanExecute(null),
                "历史输入改变不锁住当前结果的翻页和导出");
            await history.NextPageCommand.ExecuteAsync(null);
            Check(history.CurrentPage == 2 && history.Records.Count == 5 && history.PointNameFilter == "尚未查询的新条件",
                "翻页沿用查询条件和编号上限，排除新记录并保留未查询输入");

            string csv = Path.Combine(Output, $"last-query-{Guid.NewGuid():N}.csv");
            int historyPathRequests = 0;
            history.ExportPathRequested += () => { historyPathRequests++; return null; };
            history.ExportPathRequested += () => { historyPathRequests++; return csv; };
            await history.ExportRecordsCommand.ExecuteAsync(null);
            Check(historyPathRequests == 1,
                "采集历史有多个路径处理器时也只选择一次路径");
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check(File.ReadLines(csv).Count() == 206 && history.PageMessage.StartsWith("已导出"),
                "历史导出包含当前查询全部205条，不限当前页且排除新增记录");

            string protectedCsv = Path.Combine(Output, $"cancel-history-{Guid.NewGuid():N}.csv");
            File.WriteAllText(protectedCsv, "已有导出文件");
            Func<string?> cancelExport = () =>
            {
                history.ExportRecordsCommand.Cancel();
                return protectedCsv;
            };
            history.ExportPathRequested += cancelExport;
            await history.ExportRecordsCommand.ExecuteAsync(null);
            history.ExportPathRequested -= cancelExport;
            Check(File.ReadAllText(protectedCsv) == "已有导出文件" && history.PageMessage.Contains("取消")
                && !history.IsBusy && history.CanEditFilters,
                "取消历史导出保留原文件，并恢复输入与操作条件");

            long[] previousRows = history.Records.Select(record => record.Id).ToArray();
            RenameTable(path, "HistoryRecords", "HiddenHistoryForCheck");
            try { await history.RefreshRecordsCommand.ExecuteAsync(null); }
            finally { RenameTable(path, "HiddenHistoryForCheck", "HistoryRecords"); }
            Check(history.PageMessage.Contains("查询失败") && previousRows.SequenceEqual(history.Records.Select(record => record.Id))
                && history.PreviousPageCommand.CanExecute(null) && history.ExportRecordsCommand.CanExecute(null),
                "新历史查询失败保留上次结果，翻页和导出继续可用");
            history.PointNameFilter = point.Name;
            await history.RefreshRecordsCommand.ExecuteAsync(null);
            Check(history.CurrentPage == 1 && history.TotalRecords == 206,
                "明确点击查询后应用输入、回到第一页，并纳入后来新增的记录");
            long[] rowsBeforeInvalidTime = history.Records.Select(record => record.Id).ToArray();
            history.UseTimeFilter = true;
            history.StartDate = history.EndDate = DateTime.Today;
            history.StartTimeText = "23:00:00";
            history.EndTimeText = "22:00:00";
            await history.RefreshRecordsCommand.ExecuteAsync(null);
            Check(history.PageMessage.Contains("开始时间不能晚于结束时间")
                && rowsBeforeInvalidTime.SequenceEqual(history.Records.Select(record => record.Id)),
                "手动输入的时间范围无效时保留旧结果");
            await history.ResetFiltersCommand.ExecuteAsync(null);
            Check(!history.UseTimeFilter && history.SelectedDevice?.DeviceId is null
                && history.PointNameFilter == "" && history.TotalRecords == 206 && history.CurrentPage == 1,
                "重置恢复全部设备、空点位条件和全部时间，并查询第一页");
            history.PointNameFilter = "__no_matching_history__";
            await history.RefreshRecordsCommand.ExecuteAsync(null);
            Check(history.HasQueried && history.Records.Count == 0 && history.ShowEmptyMessage
                && !history.NextPageCommand.CanExecute(null) && !history.ExportRecordsCommand.CanExecute(null),
                "成功查询到零条仍算查过，显示空提示并禁用翻页和导出");
            await history.ResetFiltersCommand.ExecuteAsync(null);

            var alarmPage = history.Alarms;
            await alarmPage.RefreshRecordsCommand.ExecuteAsync(null);
            alarmPage.SelectedCategory = alarmPage.CategoryOptions.Single(item => item.Category == AlarmCategory.Value);
            Check(alarmPage.NextPageCommand.CanExecute(null)
                && alarmPage.ExportRecordsCommand.CanExecute(null), "报警历史同样不因未查询输入锁住旧结果");
            await alarmPage.NextPageCommand.ExecuteAsync(null);
            Check(alarmPage.CurrentPage == 2 && alarmPage.Records.Count == 100
                && alarmPage.Records.All(record => record.Kind == AlarmKind.AcquisitionFailure)
                && alarmPage.SelectedCategory.Category == AlarmCategory.Value,
                "报警翻页继续查看原来的采集异常，不切换成尚未查询的数值报警");
            string alarmCsv = Path.Combine(Output, $"last-alarms-{Guid.NewGuid():N}.csv");
            int alarmPathRequests = 0;
            alarmPage.ExportPathRequested += () => { alarmPathRequests++; return null; };
            alarmPage.ExportPathRequested += () => { alarmPathRequests++; return alarmCsv; };
            await alarmPage.ExportRecordsCommand.ExecuteAsync(null);
            Check(alarmPathRequests == 1,
                "多个路径处理器仍只调用当前一个，报警导出不会连续打开保存窗口");
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check(File.ReadLines(alarmCsv).Count() == 206 && alarmPage.PageMessage.StartsWith("已导出"),
                "报警导出沿用上次205条结果，完成后不再接收旧进度");
            int requestsBeforeCancel = alarmPathRequests;
            string previousAlarmCsv = File.ReadAllText(alarmCsv);
            int canceledPathRequests = 0;
            Func<string?> cancelAlarmPath = () => { canceledPathRequests++; return null; };
            alarmPage.ExportPathRequested += cancelAlarmPath;
            await alarmPage.ExportRecordsCommand.ExecuteAsync(null);
            alarmPage.ExportPathRequested -= cancelAlarmPath;
            Check(canceledPathRequests == 1 && alarmPathRequests == requestsBeforeCancel
                && File.ReadAllText(alarmCsv) == previousAlarmCsv && !alarmPage.IsExporting
                && alarmPage.PageMessage.Contains("取消"),
                "取消当前报警保存窗口不再调用旧窗口，也不覆盖已导出的文件");
            long[] previousAlarms = alarmPage.Records.Select(record => record.Id).ToArray();
            RenameTable(path, "AlarmRecords", "HiddenAlarmsForCheck");
            try { await alarmPage.RefreshRecordsCommand.ExecuteAsync(null); }
            finally { RenameTable(path, "HiddenAlarmsForCheck", "AlarmRecords"); }
            Check(alarmPage.PageMessage.Contains("查询失败")
                && previousAlarms.SequenceEqual(alarmPage.Records.Select(record => record.Id))
                && alarmPage.PreviousPageCommand.CanExecute(null) && alarmPage.ExportRecordsCommand.CanExecute(null),
                "报警查询失败不撤销上次已成功查询的结果");
        }
        finally { await history.StopOperationsAsync(); }
    }

    private static void RenameTable(string path, string from, string to)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {from} RENAME TO {to}";
        command.ExecuteNonQuery();
    }


    private static async Task CheckTrendNotificationsAsync(DeviceInfo device, PointInfo point)
    {
        using var first = new PointReadingViewModel(device, point);
        using var second = new PointReadingViewModel(device, point);
        using var trend = new LiveTrendViewModel();
        int displayNotifications = 0;
        int trendNotifications = 0;
        trend.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveTrendViewModel.Samples)) displayNotifications++;
        };
        first.TrendChanged += () => trendNotifications++;
        trend.Select(first);
        Check(trend.Samples.Count == 0, "空波形没有伪造时间点");

        DateTimeOffset readAt = DateTimeOffset.UtcNow;
        first.ShowResult(new PointReadResult(point, 123, 123, readAt), TimeSpan.FromSeconds(1));
        Check(trend.Samples[^1].Elapsed == TimeSpan.FromSeconds(1) && trendNotifications == 1,
            "首个点立即显示，横坐标使用本次采集经过的时间");
        int displaysBeforeBurst = displayNotifications;
        first.ShowResult(new PointReadResult(point, 124, 124, readAt.AddHours(-1)), TimeSpan.FromSeconds(1.5));
        first.ShowFailure("模拟通信超时", TimeSpan.FromSeconds(2));
        Check(trend.Samples.Count == 1 && first.TrendSamples.Count == 3 && trendNotifications == 3,
            "短时间内的更新先进入缓存，曲线不反复重画");
        await WaitForAsync(() => trend.Samples.Count == 3);
        Check(trend.Samples[1].Elapsed == TimeSpan.FromSeconds(1.5)
            && trend.Samples[^1] is { Value: null } last && last.Elapsed == TimeSpan.FromSeconds(2)
            && trend.StatusText.Contains("中断") && displayNotifications == displaysBeforeBurst + 1,
            "合并刷新保留断点，本机时间回拨不改变横坐标");

        first.ShowResult(new PointReadResult(point, 125, 125, readAt), TimeSpan.FromSeconds(3));
        trend.Select(second);
        Check(trend.Samples.Count == 0, "切到尚无数据的点后立即清空原曲线");
        first.ShowResult(new PointReadResult(point, 126, 126, readAt), TimeSpan.FromSeconds(4));
        Check(trend.Samples.Count == 0, "旧点有新数据不刷新已切走的图，但仍保存旧点缓存");
        trend.Select(first);
        Check(trend.Samples.Count == 5 && trend.Samples[^1].Elapsed == TimeSpan.FromSeconds(4),
            "切回原点恢复全部缓存及末项时间，不重新从空曲线开始");

        trend.Dispose();
        int countAtClose = trend.Samples.Count;
        first.ShowCanceled(TimeSpan.FromSeconds(5));
        trend.Select(second);
        first.Dispose();
        first.Dispose();
        Check(trend.Samples.Count == countAtClose && first.TrendSamples.Count == 6,
            "曲线退出后不再响应选择或通知，重复退订点位也安全");
    }


    private static async Task CheckPointManagementInteractionsAsync(UserAccount user)
    {
        string path = Path.Combine(Output, $"point-actions-test-only-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var store = new DeviceStore(path);
        Seed(store, user);
        DeviceInfo device = store.GetDevices(user).First();
        using var points = new PointEditorViewModel(user, store, device);
        await points.RefreshPointsCommand.ExecuteAsync(null);
        bool allowDelete = false;
        points.DeleteConfirmationRequested += _ => allowDelete;

        PointInfo first = points.Points.First();
        PointInfo second = points.Points.Last();
        points.SelectedPoint = first;
        Check(points.IsEditing && points.PointName == first.Name && points.AddressText == first.Address.ToString()
            && points.SelectedDataType == first.DataType,
            "选中采集点直接载入右侧，不需要额外编辑命令");
        points.SelectedPoint = second;
        Check(points.SelectedPoint == second && points.PointName == second.Name,
            "点位切换不弹确认，表单跟随当前选择");

        points.PointName = "尚未保存的名称";
        points.SelectedPoint = first;
        Check(points.SelectedPoint == first && points.PointName == first.Name,
            "切换点位直接载入目标，放弃未保存输入，不保留另一份表单快照");

        // 编辑时撞到别的点，只清地址，不丢失名称、单位，也不能写进数据库。
        points.PointName = "待保存名称";
        points.UnitText = "kPa";
        points.AddressText = second.Address.ToString();
        points.SelectedRegisterType = second.RegisterType;
        await points.SavePointCommand.ExecuteAsync(null);
        Check(points.AddressText == "" && points.PageMessage.Contains(second.Name)
            && points.PageMessage.Contains(second.RegisterTypeDescription)
            && points.PageMessage.Contains($"地址 {second.Address}") && points.PageMessage.Contains("未保存")
            && points.SelectedPoint == first && points.PointName == "待保存名称" && points.UnitText == "kPa",
            "编辑地址冲突时指出占用点和寄存器类型，只清地址，保留其他输入");
        Check(store.GetPoints(user, device.Id).Single(point => point.Id == first.Id).Address == first.Address,
            "地址冲突直接返回，不改变原点位的数据库地址");
        points.RevertPointCommand.Execute(null);

        points.ScaleText = "NaN";
        await points.SavePointCommand.ExecuteAsync(null);
        Check(points.IsEditing && points.PageMessage.Contains("倍率") && points.ScaleText == "NaN",
            "表单拒绝 NaN 倍率，并保留当前点位和编辑输入");
        points.ScaleText = "0.1"; points.OffsetText = "-2"; points.UnitText = "%RH";
        points.LowerLimitText = "-1.5"; points.UpperLimitText = "60.5";
        long changedId = points.SelectedPoint!.Id;
        int originalCount = points.Points.Count;
        await points.SavePointCommand.ExecuteAsync(null);
        PointInfo converted = store.GetPoints(user, device.Id).Single(point => point.Id == changedId);
        Check(points.IsEditing && points.SelectedPoint?.Id == changedId && points.Points.Contains(points.SelectedPoint)
            && points.Points.Count == originalCount,
            "保存修改后保留实际选中行，表单目标一致，不误插入新增记录");
        Check(converted.Scale == 0.1 && converted.Offset == -2 && converted.Unit == "%RH"
            && converted.LowerLimit == -1.5 && converted.UpperLimit == 60.5
            && points.ScaleText == "0.1" && points.OffsetText == "-2" && points.UnitText == "%RH",
            "保存换算和小数报警限值后，表单继续显示已保存配置");

        points.PointName = "不应保存的名字";
        points.RevertPointCommand.Execute(null);
        Check(points.SelectedPoint?.Id == changedId && points.PointName == converted.Name,
            "撤销输入仅恢复当前点配置，不切到新增也不改数据库");
        points.ScaleText = "7";
        await points.RefreshPointsCommand.ExecuteAsync(null);
        Check(points.SelectedPoint?.Id == changedId && points.Points.Contains(points.SelectedPoint)
            && points.ScaleText == "0.1",
            "刷新直接按编号恢复新行对象，同时载入数据库最新配置");

        points.NewPointCommand.Execute(null);
        Check(!points.IsEditing && points.SelectedPoint is null && points.PointName == "" && points.AddressText == ""
            && points.ScaleText == "1" && points.OffsetText == "0" && points.UnitText == "",
            "新增采集点入口清除选择，恢复空表单和不换算默认值");

        points.PointName = "重复地址测试";
        points.AddressText = first.Address.ToString();
        points.SelectedRegisterType = first.RegisterType;
        await points.SavePointCommand.ExecuteAsync(null);
        Check(points.AddressText == "" && points.PointName == "重复地址测试" && points.PageMessage.Contains(first.Name),
            "新增地址冲突时同样清空地址并提示占用点，不清名称");
        Check(store.GetPoints(user, device.Id).Count == originalCount,
            "新增地址冲突不插入数据库记录");

        points.PointName = "新建测试点"; points.AddressText = "2";
        points.SelectedRegisterType = RegisterType.Holding; points.SelectedDataType = PointDataType.UInt16;
        await points.SavePointCommand.ExecuteAsync(null);
        Check(points.Points.Count == originalCount + 1 && points.SelectedPoint?.Name == "新建测试点"
            && points.PointName == "新建测试点" && points.IsEditing,
            "新增保存后自动选中新点，表格和表单不再显示两个目标");
        long addedId = points.SelectedPoint!.Id;

        points.UnitText = "未保存单位";
        await points.DeletePointCommand.ExecuteAsync(null);
        Check(points.SelectedPoint?.Id == addedId && points.UnitText == "未保存单位"
            && store.GetPoints(user, device.Id).Any(point => point.Id == addedId),
            "取消删除保留原点和未保存输入，不发生数据库删除");
        allowDelete = true;
        await points.DeletePointCommand.ExecuteAsync(null);
        Check(points.SelectedPoint is null && !points.IsEditing && points.PointName == ""
            && points.Points.Count == originalCount && !store.GetPoints(user, device.Id).Any(point => point.Id == addedId),
            "确认删除后清除选中与旧输入，不自动编辑邻行");

        points.PointName = "未完成的新增";
        points.NewPointCommand.Execute(null);
        Check(points.PointName == "" && points.SelectedPoint is null,
            "再次点击新增直接清空临时输入，不维护放弃修改确认状态");
        points.PointName = "尚未保存";
        points.RevertPointCommand.Execute(null);
        Check(points.PointName == "",
            "新增状态撤销输入恢复空表单，不误修改之前选中过的点");

        points.PointName = "不同寄存器区测试";
        points.AddressText = first.Address.ToString();
        points.SelectedRegisterType = first.RegisterType == RegisterType.Holding
            ? RegisterType.Input : RegisterType.Holding;
        await points.SavePointCommand.ExecuteAsync(null);
        Check(points.SelectedPoint?.Name == "不同寄存器区测试" && points.SelectedPoint.Address == first.Address
            && points.AddressText == first.Address.ToString() && points.Points.Count == originalCount + 1,
            "03和04属于不同寄存器区，数值相同的地址仍能保存，不误清输入");
    }


    private static async Task CheckFailedStopCleanupAsync(UserAccount user, DeviceStore store, HistoryStore history)
    {
        var tasks = new SamplingTasks(user, store, history);
        var session = tasks.GetOrCreateSession(9999);
        typeof(DeviceSamplingViewModel).GetField("_samplingTask", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session, Task.FromException(new InvalidOperationException("模拟停止收尾异常")));
        try { await tasks.StopAllAsync(); throw new Exception("应把错误交给主窗口"); }
        catch (InvalidOperationException) { Check(true, "停止收尾异常没有被吞掉，交给主窗口记录并提示"); }
        int notifications = 0;
        tasks.PropertyChanged += (_, _) => notifications++;
        Set(session, nameof(session.IsRunning), true);
        Set(session, nameof(session.IsRunning), false);
        Check(notifications == 0, "一台停止失败仍解除任务通知，不留下旧页面订阅");
    }

    private static async Task CheckCreateUserAsync(UserAccount user, UserStore store)
    {
        var vm = new UserManagementViewModel(user, store) { NewUserName = "async-worker" };
        int messages = 0;
        vm.MessageRequested += (_, _) => messages++;
        Task creating = vm.CreateOperatorAsync("Worker123!", "Worker123!");
        Check(!creating.IsCompleted, "创建操作员的密码计算在后台进行，方法异步返回");
        Check(!vm.RefreshUsersCommand.CanExecute(null) && !vm.CanCreateOperator
            && !vm.CanEditNewUser && !vm.CanChooseUser,
            "账户写入期间不重复创建或修改同一份页面状态");
        PageViewModel page = vm;
        Task closing = page.StopOperationsAsync();
        await Task.WhenAll(creating, closing);
        Check(store.GetUsers(user).Any(account => account.UserName == "async-worker"), "退出等待已开始的账户保存完成");
        Check(messages == 0, "退出期间保存完成不再向旧页面弹出成功消息");
    }

    private static void Seed(DeviceStore store, UserAccount user)
    {
        foreach (bool rtu in new[] { true, false })
        {
            store.TryAddDevice(user, new DeviceInput
            {
                Name = rtu ? "装配区温湿度" : "环境采集站",
                Host = "127.0.0.1",
                Port = 502,
                UnitId = 1,
                CommunicationMode = rtu ? DeviceCommunicationMode.Rtu : DeviceCommunicationMode.Tcp,
                SerialPortName = "COM10",
                BaudRate = 4800,
                SamplingIntervalMilliseconds = 1000,
                ConnectTimeoutMilliseconds = 750,
                ReadTimeoutMilliseconds = 1000,
                WriteTimeoutMilliseconds = 1200
            });
        }
        foreach (DeviceInfo device in store.GetDevices(user))
        {

            foreach (ushort address in new ushort[] { 0, 1 }) store.TryAddPoint(user, device.Id, new PointInput
            {
                Name = address == 0 ? "湿度原始值" : "温度原始值",
                Address = address,
                RegisterType = RegisterType.Holding,
                DataType = address == 0 ? PointDataType.UInt16 : PointDataType.Int16
            });
        }
    }


    private static void CheckNoPageTitle(DependencyObject root, string name)
    {
        var titleStyle = (Style)Application.Current.FindResource("PageTitleStyle");
        Check(!Descendants<TextBlock>(root).Any(text => ReferenceEquals(text.Style, titleStyle)),
            name + "不再重复占一行显示页面大标题");
    }


    private static void CheckTables(DependencyObject root, string name)
    {
        foreach (DataGrid table in Descendants<DataGrid>(root))
        {
            var scroll = Descendants<ScrollViewer>(table).FirstOrDefault();
            Check(scroll is null || scroll.ScrollableWidth < 1, name + "/" + table.Name + " 无横向滚动");
        }
    }

    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);

    private static void Render(Window window, Size size, string name)
    {
        if (window.IsVisible)
        {
            window.Width = size.Width + 16;
            window.Height = size.Height + 39;
        }
        var root = (FrameworkElement)window.Content;
        root.DataContext = window.DataContext;
        if (root is Grid grid) grid.Background = window.Background;
        for (int i = 0; i < 3; i++)
        {
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(size));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, name + ".png")); encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); Console.WriteLine($"PASS {++_checks}: {message}"); }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);
}

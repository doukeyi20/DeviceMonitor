// 验证项目功能及异常处理的回归检查。
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
            history.PointNameFilter = "尚未查询的新条件";
            Check(history.NextPageCommand.CanExecute(null)
                && history.ExportRecordsCommand.CanExecute(null) && history.ShowTrendCommand.CanExecute(null),
                "历史输入改变不锁住上次结果的翻页、导出和趋势");
            await history.NextPageCommand.ExecuteAsync(null);
            Check(history.CurrentPage == 2 && history.Records.Count == 5 && history.PointNameFilter == "尚未查询的新条件",
                "翻页沿用旧条件和批次，不应用也不清除新输入");
            HistoryTrendViewModel? requestedTrend = null;
            history.TrendRequested += model => requestedTrend = model;
            history.ShowTrendCommand.Execute(null);
            var trend = requestedTrend ?? throw new Exception("未打开点位趋势");
            await trend.LoadTrendCommand.ExecuteAsync(null);
            Check(trend.TotalRecords == 205, "点位趋势使用上次查询范围，不偷偷采用新输入");
            trend.StopUpdating();
            Check(!trend.LoadTrendCommand.CanExecute(null), "关闭只读趋势后不再接受新查询");

            string csv = Path.Combine(Output, $"last-query-{Guid.NewGuid():N}.csv");
            history.ExportPathRequested += () => csv;
            await history.ExportRecordsCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check(File.ReadLines(csv).Count() == 206 && history.PageMessage.StartsWith("已导出"),
                "历史导出包含上次结果全部205条，迟到进度不覆盖完成提示");

            long[] previousRows = history.Records.Select(record => record.Id).ToArray();
            RenameTable(path, "HistoryRecords", "HiddenHistoryForCheck");
            try { await history.RefreshRecordsCommand.ExecuteAsync(null); }
            finally { RenameTable(path, "HiddenHistoryForCheck", "HistoryRecords"); }
            Check(history.PageMessage.Contains("查询失败") && previousRows.SequenceEqual(history.Records.Select(record => record.Id))
                && history.PreviousPageCommand.CanExecute(null) && history.ExportRecordsCommand.CanExecute(null),
                "新历史查询失败仍保留且允许操作上次成功结果");
            history.PointNameFilter = point.Name;
            await history.RefreshRecordsCommand.ExecuteAsync(null);
            Check(history.CurrentPage == 1 && history.TotalRecords == 205,
                "明确点击查询后才应用输入，并回到第一页");

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
            alarmPage.ExportPathRequested += () => alarmCsv;
            await alarmPage.ExportRecordsCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check(File.ReadLines(alarmCsv).Count() == 206 && alarmPage.PageMessage.StartsWith("已导出"),
                "报警导出沿用上次205条结果，完成后不再接收旧进度");
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

    private static async Task CheckDeleteIdleDeviceAsync(UserAccount user)
    {
        string path = Path.Combine(Output, $"delete-idle-only-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var store = new DeviceStore(path);
        Seed(store, user);
        var tasks = new DeviceTasks(user, store, new HistoryStore(path));
        var management = new DeviceManagementViewModel(user, store, tasks);
        await management.RefreshDevicesCommand.ExecuteAsync(null);
        DeviceInfo device = management.Devices.First();
        management.SelectedDevice = device;
        var sampling = tasks.GetOrCreateSession(device.Id);
        int confirmations = 0;
        management.ConfirmationRequested += request => { confirmations++; request.Confirmed = true; };
        var setTestDevice = typeof(DeviceManagementViewModel).GetMethod("SetConnectionTestDevice",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        int testStateNotifications = 0;
        management.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(management.IsTestingConnection))
                testStateNotifications++;
        };
        try
        {
            Set(sampling, nameof(sampling.IsRunning), true);
            Check(!management.DeleteDeviceCommand.CanExecute(null), "正在采集的目标不能删除，必须先手动停止");
            await management.DeleteDeviceCommand.ExecuteAsync(null);
            try { await tasks.DeleteDeviceAsync(device.Id); throw new Exception("运行中的设备不应允许删除"); }
            catch (InvalidOperationException) { }
            Check(confirmations == 0 && sampling.IsRunning && store.GetDevice(user, device.Id) is not null,
                "删除拒绝时不弹确认、不代做停止，也不删除配置");
            Set(sampling, nameof(sampling.IsRunning), false);

            Set(tasks, nameof(tasks.SingleReadDeviceId), device.Id);
            Check(!management.DeleteDeviceCommand.CanExecute(null), "单点测试尚未收尾时禁止删除目标");
            try { await tasks.DeleteDeviceAsync(device.Id); throw new Exception("测试中的设备不应允许删除"); }
            catch (InvalidOperationException) { }
            Set(tasks, nameof(tasks.SingleReadDeviceId), null);
            Check(management.DeleteDeviceCommand.CanExecute(null), "单点测试结束后恢复删除按钮");

            setTestDevice.Invoke(management, new object?[] { device.Id });
            Check(management.IsTestingConnection && management.TestConnectionButtonText == "测试中…"
                && testStateNotifications == 1, "测试目标编号统一驱动测试状态、按钮文字和界面通知");
            Check(!management.DeleteDeviceCommand.CanExecute(null), "本设备TCP测试期间禁止删除，不自动取消测试");
            management.SelectedDevice = management.Devices.First(item => item.Id != device.Id);
            Check(management.DeleteDeviceCommand.CanExecute(null), "一台设备测试不妨碍删除另一台闲置设备");
            setTestDevice.Invoke(management, new object?[] { null });
            Check(!management.IsTestingConnection && management.TestConnectionButtonText == "TCP端口测试"
                && testStateNotifications == 2, "清空测试目标后同步恢复状态，不维护第二份布尔值");
            management.SelectedDevice = management.Devices.Single(item => item.Id == device.Id);
            await management.DeleteDeviceCommand.ExecuteAsync(null);
            Check(confirmations == 1 && store.GetDevice(user, device.Id) is null && tasks.Sessions.All(item => item.DeviceId != device.Id),
                "用户先停止后，删除只确认一次并清掉配置及旧任务");
        }
        finally
        {
            Set(sampling, nameof(sampling.IsRunning), false);
            Set(tasks, nameof(tasks.SingleReadDeviceId), null);
            await management.StopOperationsAsync();
            await tasks.StopAllAsync();
        }
    }

    private static async Task CheckDeviceSelectionAfterRefreshAsync(UserAccount user)
    {
        string path = Path.Combine(Output, $"device-selection-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var store = new DeviceStore(path);
        Seed(store, user);
        store.TryAddDevice(user, new DeviceInput
        {
            Name = "Z额外设备", Host = "127.0.0.1", Port = 502, UnitId = 1,
            SamplingIntervalMilliseconds = 1000, ConnectTimeoutMilliseconds = 1000,
            ReadTimeoutMilliseconds = 1000, WriteTimeoutMilliseconds = 1000
        });
        long extraId = store.GetDevices(user).Single(device => device.Name == "Z额外设备").Id;
        store.TrySetDeviceEnabled(user, extraId, true);
        var tasks = new DeviceTasks(user, store, new HistoryStore(path));
        var management = new DeviceManagementViewModel(user, store, tasks);
        var points = new PointConfigurationViewModel(user, store, tasks);
        var pointQuery = typeof(PointConfigurationViewModel).GetField("_pointLoadTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        management.ConfirmationRequested += request => request.Confirmed = true;
        try
        {
            await management.RefreshDevicesCommand.ExecuteAsync(null);
            await points.RefreshDevicesCommand.ExecuteAsync(null);
            management.SelectedDevice = management.Devices.Last();
            points.SelectedDevice = points.Devices.Last();
            await WaitForPointsAsync();
            await management.DeleteDeviceCommand.ExecuteAsync(null);
            await WaitForPointsAsync();
            Check(management.SelectedDevice == management.Devices.First()
                && points.SelectedDevice == points.Devices.First()
                && points.Points.All(point => point.DeviceId == points.SelectedDevice.Id),
                "删除末行后两页统一选择首项，点位不保留被删设备的数据");

            management.SelectedDevice = management.Devices.Last();
            points.SelectedDevice = points.Devices.Last();
            await WaitForPointsAsync();
            long selectedId = management.SelectedDevice.Id;
            await management.RefreshDevicesCommand.ExecuteAsync(null);
            await points.RefreshDevicesCommand.ExecuteAsync(null);
            Check(management.SelectedDevice?.Id == selectedId && points.SelectedDevice?.Id == selectedId,
                "刷新仍按编号保留存在的设备，不因首项回退规则丢掉正常选择");

            while (management.Devices.Count > 0)
            {
                await management.DeleteDeviceCommand.ExecuteAsync(null);
                await WaitForPointsAsync();
            }
            Check(management.SelectedDevice is null && points.SelectedDevice is null
                && points.Devices.Count == 0 && points.Points.Count == 0,
                "删除唯一剩余设备后清空选择和点位，空列表不访问旧行位置");
        }
        finally
        {
            await tasks.StopAllAsync();
            await management.StopOperationsAsync();
            await points.StopOperationsAsync();
        }

        Task WaitForPointsAsync() => ((Task)pointQuery.GetValue(points)!).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void CheckTrendNotifications(DeviceInfo device, PointInfo point)
    {
        using var first = new PointMonitorViewModel(device, point);
        using var second = new PointMonitorViewModel(device, point);
        using var trend = new LiveTrendViewModel();
        int endTimeNotifications = 0;
        int trendNotifications = 0;
        trend.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveTrendViewModel.EndTime)) endTimeNotifications++;
        };
        first.TrendChanged += () => trendNotifications++;
        trend.Select(first);
        Check(trend.EndTime is null && trend.Samples.Count == 0, "空波形没有伪造结束时间");

        DateTimeOffset readAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        first.ShowResult(new PointReadResult(point, 123, 123, readAt));
        Check(trend.EndTime == readAt && endTimeNotifications > 0 && trendNotifications == 1,
            "成功后样本末项决定横轴结束时间，并发出属性和无参数曲线通知");
        first.ShowFailure("模拟通信超时");
        Check(trend.Samples[^1].Value is null && trend.EndTime == trend.Samples[^1].ReadAt
            && trend.StatusText.Contains("中断") && trendNotifications == 2,
            "失败断点也更新横轴，不把断点画成零");

        trend.Select(second);
        Check(trend.EndTime is null && trend.Samples.Count == 0, "切到尚无数据的点后横轴不沿用旧点时间");
        first.ShowResult(new PointReadResult(point, 124, 124, DateTimeOffset.UtcNow));
        Check(trend.Samples.Count == 0 && trend.EndTime is null, "旧点有新数据不刷新已切走的图，但仍保存旧点缓存");
        trend.Select(first);
        Check(trend.Samples.Count == 3 && trend.EndTime == first.TrendSamples[^1].ReadAt,
            "切回原点恢复全部缓存及末项时间，不重新从空曲线开始");

        trend.Dispose();
        int countAtClose = trend.Samples.Count;
        first.ShowCanceled();
        trend.Select(second);
        first.Dispose();
        first.Dispose();
        Check(trend.Samples.Count == countAtClose && first.TrendSamples.Count == 4,
            "曲线退出后不再响应选择或通知，重复退订点位也安全");
    }

    private static async Task RunAsync()
    {
        Directory.CreateDirectory(Output);
        string path = Path.Combine(Output, $"ui-test-only-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var user = new UserAccount(1, "ui-check-admin", UserRole.Administrator, true);
        var store = new DeviceStore(path);
        Seed(store, user);
        await CheckAuditFixesAsync(user);
        await CheckPointManagementInteractionsAsync(user);
        await CheckLastQueryOperationsAsync(user);
        await CheckDeleteIdleDeviceAsync(user);
        await CheckDeviceSelectionAfterRefreshAsync(user);
        var seededHistory = new HistoryStore(path);
        DeviceInfo historyDevice = store.GetDevices(user).First();
        PointInfo historyPoint = store.GetPoints(user, historyDevice.Id).First();
        CheckTrendNotifications(historyDevice, historyPoint);
        seededHistory.SaveRecords(user, Enumerable.Range(0, 3).Select(index => new HistoryRecord(
            historyDevice.Id, historyDevice.Name, historyPoint.Id, historyPoint.Name,
            20 + index, DateTimeOffset.Now.AddSeconds(-index))).ToArray());
        await CheckPointLoadingAsync(user, store, new HistoryStore(path));
        await CheckCreateUserAsync(user, new UserStore(path));
        await CheckFailedStopCleanupAsync(user, store, new HistoryStore(path));
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(user, new UserStore(path), store, new HistoryStore(path));
        var main = (MainViewModel)window.DataContext;
        var management = main.Pages.OfType<DeviceManagementViewModel>().Single();
        await management.RefreshDevicesCommand.ExecuteAsync(null);
        var acquisition = main.Pages.OfType<PointConfigurationViewModel>().Single();
        var monitoring = main.Pages.OfType<MonitoringViewModel>().Single();

        var status = new TextBlock { Style = (Style)app.FindResource("StatusMessageTextStyle") };
        Check(status.Visibility == Visibility.Collapsed, "空提示自动收起");
        status.Text = "读取失败：连接已断开";
        Check(status.Visibility == Visibility.Visible, "错误提示仍正常显示");
        status.Text = "";
        Check(status.Visibility == Visibility.Collapsed, "清空状态后不留下空白提示行");
        try
        {
            Check(management.SelectedDevice?.Id == management.Devices.First().Id,
                "设备管理首次载入默认选中首行，只显示详情，不启动通信");
            var changes = new HashSet<string?>();
            management.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            management.SelectedDevice = management.Devices.Single(d => d.CommunicationMode == DeviceCommunicationMode.Rtu);
            Check(changes.Contains(nameof(management.SelectedDeviceTitle)) && changes.Contains(nameof(management.SelectedDeviceConnectionDetails))
                && changes.Contains(nameof(management.SelectedDeviceTimeoutDetails)), "选择设备会通知三项详情重新显示");
            Check(management.SelectedDeviceConnectionDetails.Contains("COM10") && management.SelectedDeviceConnectionDetails.Contains("站号 1")
                && management.SelectedDeviceTimeoutDetails.Contains("RTU 不使用 TCP"), "RTU 详情不冒充网络地址和连接超时");
            long selectedDeviceId = management.SelectedDevice!.Id;
            await management.RefreshDevicesCommand.ExecuteAsync(null);
            Check(management.SelectedDevice?.Id == selectedDeviceId
                && management.Devices.Contains(management.SelectedDevice), "刷新设备后按编号恢复实际新行对象");
            main.CurrentPage = management;
            Render(window, new Size(1424, 861), "devices-rtu-large");
            Check(window.Icon is not null, "主窗口已加载自定义应用图标");
            Check(window.ResizeMode == ResizeMode.CanMinimize && window.WindowStyle == WindowStyle.SingleBorderWindow,
                "主窗口保留原生标题栏，不开放任意缩放");
            Check(window.Width == 1440 && window.Height == 900, "保留原来的初始窗口大小，不强制启动即最大化");

            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            int nativeStyle = GetWindowLong(handle, -16);
            Check((nativeStyle & 0x00010000) != 0 && (nativeStyle & 0x00020000) != 0,
                "原生窗口保留最大化、还原和最小化入口");
            Check((nativeStyle & 0x00040000) == 0, "原生窗口禁用边框拖动缩放");
            var footer = (Border)window.FindName("WorkspaceFooter");
            var logout = (Button)window.FindName("LogoutButton");
            var workspace = (FrameworkElement)window.Content;
            Check(Grid.GetRow(footer) == 2 && footer.TranslatePoint(new Point(), workspace).Y > workspace.RenderSize.Height * 0.75,
                "退出入口固定在左侧工作空间底部");
            Check(ReferenceEquals(logout.Command, main.LogoutCommand), "退出仍绑定原命令，不绕过确认与采集收尾");
            Check(Descendants<Button>(workspace).Count(b => Equals(b.Content, "退出登录")) == 1,
                "界面只保留左下角一个退出入口");
            CheckDevicePage(window, "正常窗口");
            CheckNoPageTitle((DependencyObject)window.Content, "设备管理");
            Render(window, new Size(1240, 681), "devices-rtu-small");
            CheckDevicePage(window, "小工作区");
            management.SelectedDevice = management.Devices.Single(d => d.CommunicationMode == DeviceCommunicationMode.Tcp);
            Render(window, new Size(1424, 861), "devices-tcp-large");
            Check(management.SelectedDeviceTimeoutDetails.Contains("连接超时 750 ms"), "TCP 详情保留连接超时");
            management.SelectedDevice = null;
            Check(management.SelectedDeviceTitle == "设备详情" && management.SelectedDeviceConnectionDetails.Contains("选择"), "未选择设备时显示提示而不是旧参数");

            await acquisition.RefreshDevicesCommand.ExecuteAsync(null);
            Check(acquisition.SelectedDevice is not null && acquisition.SelectedPoint is not null
                && acquisition.SelectedPoint.DeviceId == acquisition.SelectedDevice.Id,
                "采集点页面首次刷新默认选设备和该设备的首个点位");
            acquisition.SelectedDevice = acquisition.Devices.Single(d => d.CommunicationMode == DeviceCommunicationMode.Rtu);
            await acquisition.RefreshPointsCommand.ExecuteAsync(null);

            for (int i = 0; i < 100 && acquisition.IsLoadingPoints; i++) await Task.Delay(10);
            acquisition.SelectedPoint = acquisition.Points.First();
            long selectedPointId = acquisition.SelectedPoint.Id;
            long acquisitionDeviceId = acquisition.SelectedDevice!.Id;
            await acquisition.RefreshDevicesCommand.ExecuteAsync(null);
            Check(acquisition.SelectedDevice?.Id == acquisitionDeviceId && acquisition.SelectedPoint?.Id == selectedPointId
                && acquisition.Points.Contains(acquisition.SelectedPoint), "刷新采集点页面后保留设备和点位选择");
            main.CurrentPage = acquisition;
            foreach (SingleReadState state in Enum.GetValues<SingleReadState>())
            {
                Set(acquisition, nameof(acquisition.IsReading), state == SingleReadState.Reading);
                Set(acquisition, nameof(acquisition.ReadState), state);
                Set(acquisition, nameof(acquisition.ReadValueText), state == SingleReadState.Succeeded ? "-32768" : "—");
                Set(acquisition, nameof(acquisition.ReadTimeText), state == SingleReadState.Succeeded ? "15:42:08.123" : "—");
                Set(acquisition, nameof(acquisition.ReadTargetText), "一号温湿度传感器 / 低温温度检测点 · 地址 1");
                Set(acquisition, nameof(acquisition.ReadResultText), state switch
                {
                    SingleReadState.Succeeded => "整数 -32768 · Int16 · 原始值 0x8000",
                    SingleReadState.Failed => "COM10 通信失败，请检查转换器、接线和串口是否存在。若串口被其他软件占用，请先关闭调试软件。",
                    SingleReadState.TimedOut => "等待 RTU 设备响应超时（1000 ms）。请检查供电、A/B、站号和串口参数。",
                    SingleReadState.Canceled => "本次读取已取消，没有新的读数。",
                    _ => "选择点位后点击读取一次。"
                });
                Render(window, new Size(1424, 861), $"read-{state}-large");
                CheckReadPage(window, acquisition, state, "正常窗口");
                Render(window, new Size(1240, 681), $"read-{state}-small");
                CheckReadPage(window, acquisition, state, "小工作区");
            }
            CheckNoPageTitle((DependencyObject)window.Content, "采集点配置");

            await monitoring.RefreshDevicesCommand.ExecuteAsync(null);
            MonitoringDeviceViewModel rtuRow = monitoring.Devices.Single(d => d.Device.CommunicationMode == DeviceCommunicationMode.Rtu);
            Check(rtuRow.ConnectionSummary == "RTU · COM10 · 站号 1" && !rtuRow.ConnectionSummary.Contains("4800")
                && rtuRow.ConnectionDetails.Contains("4800,8,N,1"), "监控设备行简写目标，完整串口参数保留在详情");
            foreach (MonitoringDeviceViewModel row in monitoring.Devices) row.IsChecked = true;
            PrepareMonitoringSamples(monitoring, store, user);
            main.CurrentPage = monitoring;
            Render(window, new Size(1424, 861), "monitor-connections-large");
            Check(monitoring.Readings.Count == 4 && monitoring.Trend.Samples.Count > 30,
                "监控离屏截图包含模拟读数和已有波形，不启动真实采集");
            Render(window, new Size(1920, 1040), "monitor-maximized");
            CheckTables((DependencyObject)window.Content, "最大化工作区");
            Render(window, new Size(1240, 681), "monitor-connections-small");
            var monitorView = Descendants<MonitoringView>((DependencyObject)window.Content).Single();
            var deviceList = (ItemsControl)monitorView.FindName("MonitoringDevicesList");
            Check(deviceList is not Selector && Descendants<CheckBox>(deviceList).Count() == monitoring.Devices.Count,
                "监控设备卡片只用勾选，不再叠加表格高亮选择");
            Check(Descendants<CheckBox>(deviceList).All(check => check.HorizontalContentAlignment == HorizontalAlignment.Stretch
                && check.Content is StackPanel), "设备名称和状态都是勾选框内容，整张卡片可勾选");
            Check(Descendants<Button>(monitorView).Any(button => ReferenceEquals(button.Command, monitoring.StopAllSamplingCommand)),
                "监控工具栏提供不受勾选范围影响的停止全部入口");
            monitoring.ClearDeviceChecksCommand.Execute(null);
            Check(monitoring.Devices.All(row => !row.IsChecked), "清空勾选只清除唯一一套设备选择");
            monitoring.CheckAllDevicesCommand.Execute(null);
            long[] checkedIds = monitoring.Devices.Where(row => row.IsChecked).Select(row => row.Device.Id).Order().ToArray();
            await monitoring.RefreshDevicesCommand.ExecuteAsync(null);
            Check(checkedIds.SequenceEqual(monitoring.Devices.Where(row => row.IsChecked).Select(row => row.Device.Id).Order()),
                "监控刷新保留原有设备勾选，不擅自改采集范围");
            Render(window, new Size(1240, 681), "monitor-refreshed-small");
            Check(!Descendants<TextBlock>(monitorView).Any(t => t.Text.Contains("取消勾选不会停采")
                || t.Text.Contains("点击行查看波形") || t.Text.Contains("勾选设备批量启停")),
                "监控页不再显示重复操作说明");
            Check(Descendants<TextBlock>(monitorView).Any(t => t.Text == rtuRow.ConnectionSummary), "监控 XAML 已绑定新的连接简写");
            CheckTables(monitorView, "监控设备表");
            CheckNoPageTitle(monitorView, "实时监控");

            main.CurrentPage = main.Pages.OfType<UserManagementViewModel>().Single();
            Render(window, new Size(1424, 861), "users-clean");
            CheckNoPageTitle((DependencyObject)window.Content, "用户管理");
            Render(window, new Size(1240, 681), "users-small");
            CheckTables((DependencyObject)window.Content, "用户管理小工作区");
            var history = main.Pages.OfType<HistoryViewModel>().Single();
            await history.RefreshRecordsCommand.ExecuteAsync(null);
            await history.Alarms.RefreshRecordsCommand.ExecuteAsync(null);
            Check(history.SelectedRecord is not null && history.Records.Contains(history.SelectedRecord),
                "历史查询后默认选中实际记录，趋势入口可直接使用");
            history.SelectedRecord = history.Records.Last();
            long selectedRecordId = history.SelectedRecord.Id;
            await history.RefreshRecordsCommand.ExecuteAsync(null);
            Check(history.SelectedRecord?.Id == selectedRecordId && history.Records.Contains(history.SelectedRecord),
                "历史刷新后按记录编号保留选择，不持有旧行对象");
            main.CurrentPage = history;
            Render(window, new Size(1424, 861), "history-clean");
            CheckNoPageTitle((DependencyObject)window.Content, "历史记录");
            history.UseTimeFilter = true;
            Render(window, new Size(1240, 681), "history-filters-small");
            CheckHistoryFilters(Descendants<HistoryView>((DependencyObject)window.Content).Single(), "采集历史小工作区");
            Render(window, new Size(1424, 861), "history-filters-large");
            CheckHistoryFilters(Descendants<HistoryView>((DependencyObject)window.Content).Single(), "采集历史正常窗口");
            var tabs = Descendants<TabControl>((DependencyObject)window.Content).Single();
            tabs.SelectedIndex = 1;
            history.Alarms.UseTimeFilter = true;
            Render(window, new Size(1424, 861), "alarms-clean");
            CheckHistoryFilters(Descendants<AlarmHistoryView>((DependencyObject)window.Content).Single(), "报警历史正常窗口");
            Render(window, new Size(1240, 681), "alarms-filters-small");
            CheckHistoryFilters(Descendants<AlarmHistoryView>((DependencyObject)window.Content).Single(), "报警历史小工作区");
            Check(!Descendants<TextBlock>((DependencyObject)window.Content).Any(t => t.Text.Contains("正常采集不会生成报警")),
                "报警页保留空结果状态，不显示冗余说明");

            using var points = acquisition.CreatePointManagementViewModel(acquisition.Devices.First());
            await points.RefreshPointsCommand.ExecuteAsync(null);
            var pointWindow = new PointManagementWindow(points);
            Render(pointWindow, new Size(1040, 720), "points-clean");
            CheckPointWindow(pointWindow, "点位窗口正常大小");
            points.SelectedPoint = points.Points.First();
            Render(pointWindow, new Size(940, 660), "points-conversion");
            CheckPointWindow(pointWindow, "点位窗口小工作区");
            pointWindow.Close();
            var deviceEditor = new DeviceEditorWindow(management,
                management.Devices.Single(device => device.CommunicationMode == DeviceCommunicationMode.Rtu));
            Render(deviceEditor, new Size(704, 721), "device-form-clean");
            CheckEditableSerialPort(deviceEditor);
            deviceEditor.Close();
            Render(new PasswordWindow(new PasswordEditViewModel(user, new UserStore(path))), new Size(520, 430), "password-clean");
            var login = new LoginWindow(new UserStore(path), store, new HistoryStore(path));
            await ((LoginViewModel)login.DataContext).InitializeAsync();
            Render(login, new Size(500, 530), "login-clean");
            Check(login.Icon is not null && login.ResizeMode == ResizeMode.NoResize,
                "登录窗口使用同一图标并保留固定大小");
            var stopBeforeExit = typeof(MainWindow).GetMethod("StopBeforeExitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)stopBeforeExit.Invoke(window, null)!;
            Check(main.DeviceTasks.IsShuttingDown && !history.RefreshRecordsCommand.CanExecute(null),
                "简化退出仍等待后台收尾，并禁止旧主窗口发起新查询或采集");
            Check(typeof(MainWindow).GetMethod("ShowLoginWindowOrExit", BindingFlags.NonPublic | BindingFlags.Instance) is null,
                "退出不再保留循环重建登录窗口的方法");
        }
        finally { await main.StopBackgroundWorkAsync(); }
        Console.WriteLine($"Temporary database only: {path}");
    }

    private static async Task CheckPointManagementInteractionsAsync(UserAccount user)
    {
        string path = Path.Combine(Output, $"point-actions-test-only-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var store = new DeviceStore(path);
        Seed(store, user);
        DeviceInfo device = store.GetDevices(user).First();
        using var points = new PointManagementViewModel(user, store, device);
        await points.RefreshPointsCommand.ExecuteAsync(null);
        bool allowDelete = false;
        points.DeleteConfirmationRequested += request => request.Confirmed = allowDelete;

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
    }

    private static async Task CheckPointLoadingAsync(UserAccount user, DeviceStore store, HistoryStore history)
    {
        var runtime = new DeviceTasks(user, store, history);
        var vm = new PointConfigurationViewModel(user, store, runtime);
        var pendingField = typeof(PointConfigurationViewModel).GetField("_pointLoadTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        DeviceInfo[] devices = store.GetDevices(user).ToArray();
        try
        {
            vm.SelectedDevice = devices[0];
            Task pending = (Task)pendingField.GetValue(vm)!;
            Check(vm.IsLoadingPoints && !vm.CanChooseDevice && vm.Points.Count == 0,
                "查询点位期间禁用设备选择，不再给旧选择排队");
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(vm.Points.Count == 2 && vm.Points.All(p => p.DeviceId == devices[0].Id) && vm.CanChooseDevice,
                "查询结束显示当前设备点位，恢复选择");

            vm.SelectedDevice = devices[1];
            pending = (Task)pendingField.GetValue(vm)!;
            vm.SelectedDevice = null;
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(vm.Points.Count == 0 && vm.SelectedPoint is null && !vm.IsLoadingPoints,
                "程序清空选择后，尚未结束的旧查询不会把点位放回来");

            vm.SelectedDevice = devices[0];
            Task stopping = vm.StopOperationsAsync();
            Check(!stopping.IsCompleted, "退出时等待当前查询收尾，不等待旧选择队列");
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Check(vm.Points.Count == 0 && !vm.CanChooseDevice, "退出后查询不再回填页面或恢复操作");
        }
        finally
        {
            await vm.StopOperationsAsync();
            await runtime.StopAllAsync();
        }
    }

    private static async Task CheckFailedStopCleanupAsync(UserAccount user, DeviceStore store, HistoryStore history)
    {
        var tasks = new DeviceTasks(user, store, history);
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
        vm.PasswordsRequested += input => { input.Password = "Worker123!"; input.Confirmation = "Worker123!"; };
        vm.MessageRequested += (_, _) => messages++;
        Task creating = vm.CreateOperatorCommand.ExecuteAsync(null);
        Check(!creating.IsCompleted, "创建操作员的密码计算在后台进行，命令异步返回");
        Check(!vm.RefreshUsersCommand.CanExecute(null) && !vm.CreateOperatorCommand.CanExecute(null)
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
                Name = rtu ? "一号温湿度传感器" : "TCP 采集试验设备",
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
            store.TrySetDeviceEnabled(user, device.Id, true);
            foreach (ushort address in new ushort[] { 0, 1 }) store.TryAddPoint(user, device.Id, new PointInput
            {
                Name = address == 0 ? "湿度原始值" : "温度原始值",
                Address = address,
                RegisterType = RegisterType.Holding,
                DataType = address == 0 ? PointDataType.UInt16 : PointDataType.Int16
            });
        }
    }

    private static void CheckDevicePage(Window window, string name)
    {
        DeviceManagementView view = Descendants<DeviceManagementView>((DependencyObject)window.Content).Single();
        var table = (DataGrid)view.FindName("DevicesGrid");
        Check(table.Columns.Count == 6 && table.Columns.All(c => !c.Header.ToString()!.Contains("超时")), name + "设备主表只保留六个常用字段");
        var details = (Border)view.FindName("SelectedDeviceDetails");
        Check(details.ActualHeight > 60 && table.ActualHeight >= 100, name + "详情可见且表格仍有高度");
        CheckTables(view, name + "设备表");
    }

    private static void PrepareMonitoringSamples(MonitoringViewModel monitoring, DeviceStore store, UserAccount user)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MonitoringDeviceViewModel deviceRow in monitoring.Devices)
        {
            DeviceSamplingViewModel session = monitoring.DeviceTasks.GetOrCreateSession(deviceRow.Device.Id);
            foreach (PointInfo savedPoint in store.GetPoints(user, deviceRow.Device.Id))
            {
                bool humidity = savedPoint.DataType == PointDataType.UInt16;
                var point = new PointInfo(savedPoint.Id, savedPoint.DeviceId, humidity ? "湿度" : "温度",
                    savedPoint.RegisterType, savedPoint.Address, savedPoint.DataType,
                    scale: 0.1, unit: humidity ? "%RH" : "℃");
                var reading = new PointMonitorViewModel(deviceRow.Device, point, monitoring.DeviceTasks.Alarms, session);
                session.Readings.Add(reading);
                for (int index = 0; index < 120; index++)
                {
                    int raw = (humidity ? 450 : 235) + (int)Math.Round(8 * Math.Sin(index / 8.0));
                    reading.ShowResult(new PointReadResult(point, (ushort)raw, raw, now.AddSeconds((index - 119) * 2.5)));
                }
            }
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        monitoring.SelectedReading = monitoring.Readings.FirstOrDefault(reading => reading.Point.Unit == "℃");
    }

    private static void CheckNoPageTitle(DependencyObject root, string name)
    {
        var titleStyle = (Style)Application.Current.FindResource("PageTitleStyle");
        Check(!Descendants<TextBlock>(root).Any(text => ReferenceEquals(text.Style, titleStyle)),
            name + "不再重复占一行显示页面大标题");
    }

    private static void CheckPointWindow(PointManagementWindow window, string name)
    {
        var root = (DependencyObject)window.Content;
        var table = Descendants<DataGrid>(root).Single();
        Check(table.Columns.Count == 3, name + "左表保留三列识别信息，完整配置放右侧");
        Check(!Descendants<Button>(root).Any(button => Equals(button.Content, "编辑选中点"))
            && Descendants<Button>(root).Any(button => Equals(button.Content, "新增采集点")),
            name + "没有重复编辑按钮，新增有明确入口");
        CheckNoPageTitle(root, name);
        CheckTables(root, name);
    }

    private static void CheckHistoryFilters(FrameworkElement view, string name)
    {
        var expander = (Expander)view.FindName("FilterExpander");
        DatePicker[] dates = Descendants<DatePicker>(expander).ToArray();
        Check(expander.IsExpanded && dates.Length == 2 && dates.All(date => date.ActualWidth >= 130 && date.ActualHeight >= 25),
            name + "时间筛选展开后开始与结束日期控件完整显示");
        var dateText = (TextBox)dates[0].Template.FindName("PART_TextBox", dates[0]);
        Check(!dateText.IsReadOnly, name + "使用标准日期输入，不再查模板并强制锁住文本框");
        DateTime? originalDate = dates[0].SelectedDate;
        dates[0].SetCurrentValue(DatePicker.TextProperty, "2026/10/29");
        Check(dates[0].SelectedDate == new DateTime(2026, 10, 29), name + "手动输入日期可正常转换为筛选日期");
        dates[0].SetCurrentValue(DatePicker.SelectedDateProperty, originalDate);
        var filterScroll = Descendants<ScrollViewer>(expander).First();
        Check(filterScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && filterScroll.ScrollableWidth < 1,
            name + "筛选区按宽度换行，不产生横向滚动");
        Check(Descendants<Button>(view).Any(button => Equals(button.Content, "重置条件") && button.ActualHeight >= 28)
            && Descendants<Button>(view).Any(button => Equals(button.Content, "查询 / 刷新") && button.ActualHeight >= 28),
            name + "查询和重置按钮固定可见");
        Check(Descendants<DataGrid>(view).Single().ActualHeight >= 70, name + "筛选展开后仍给结果表保留高度");
        CheckNoPageTitle(view, name);
        CheckTables(view, name);
    }

    private static void CheckEditableSerialPort(DeviceEditorWindow window)
    {
        var combo = (ComboBox)window.FindName("SerialPortNameInput");
        combo.ApplyTemplate();
        var textBox = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
        Check(combo.IsEditable && textBox is not null, "串口下拉模板保留可编辑输入区，不只是显示选中项");
        textBox!.SetCurrentValue(TextBox.TextProperty, "COM77");
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        var vm = (DeviceEditorViewModel)window.DataContext;
        Check(vm.SerialPortName == "COM77", "手动输入未枚举出的COM号能写回设备表单");
        vm.SerialPortName = "COM88";
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Check(textBox.Text == "COM88", "设备表单重新赋串口值时，可编辑下拉同步显示");
        CheckNoPageTitle((DependencyObject)window.Content, "设备编辑表单");
    }

    private static void CheckReadPage(Window window, PointConfigurationViewModel vm, SingleReadState state, string name)
    {
        PointConfigurationView view = Descendants<PointConfigurationView>((DependencyObject)window.Content).Single();
        var value = (TextBlock)view.FindName("ReadValueDisplay");
        var target = (TextBlock)view.FindName("ReadTargetDisplay");
        var badge = (Border)view.FindName("ReadStatusBadge");
        var status = (TextBlock)badge.Child;
        Check(value.Text == vm.ReadValueText && target.Text == vm.ReadTargetText && status.Text == vm.ReadStatusText,
            $"{name}/{state} 数值、目标和状态绑定正确");
        Border card = (Border)view.FindName("SingleReadResultCard");
        Point position = value.TranslatePoint(new Point(), card);
        Check(position.X >= 0 && position.Y >= 0 && position.X + value.ActualWidth <= card.ActualWidth
            && position.Y + value.ActualHeight <= card.ActualHeight, $"{name}/{state} 大号读数不越出结果卡片");
        if (state is SingleReadState.Succeeded or SingleReadState.Failed or SingleReadState.TimedOut)
        {
            Color expected = state == SingleReadState.Succeeded ? Color.FromRgb(39, 102, 78)
                : state == SingleReadState.Failed ? Color.FromRgb(180, 35, 24) : Color.FromRgb(149, 85, 0);
            Check(status.Foreground is SolidColorBrush brush && brush.Color == expected, $"{name}/{state} 状态文字使用对应颜色");
        }
        CheckTables(view, name + "/" + state);
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
        var root = (FrameworkElement)window.Content;
        root.DataContext = window.DataContext;
        if (root is Grid grid) grid.Background = window.Background;
        for (int i = 0; i < 3; i++)
        {
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
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

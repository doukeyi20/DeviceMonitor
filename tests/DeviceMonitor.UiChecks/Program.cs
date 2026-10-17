using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeviceMonitor;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Models;
using DeviceMonitor.ViewModels;
using DeviceMonitor.Views;
using Microsoft.Data.Sqlite;

// 只绘制离屏界面、使用临时数据库，不打开真实 COM 或连接设备。
internal static class Program
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

    private static async Task RunAsync()
    {
        Directory.CreateDirectory(Output);
        string path = Path.Combine(Output, $"ui-test-only-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var user = new UserAccount(1, "ui-check-admin", UserRole.Administrator, true);
        var store = new DeviceStore(path);
        Seed(store, user);
        var app = new App(); app.InitializeComponent(); // 不 Run、不调用正式程序的启动方法。
        var window = new MainWindow(user, new UserStore(path), store, new HistoryStore(path));
        var main = (MainViewModel)window.DataContext;
        var management = main.Pages.OfType<DeviceManagementViewModel>().Single();
        var acquisition = main.Pages.OfType<AcquisitionViewModel>().Single();
        var monitoring = main.Pages.OfType<MonitoringViewModel>().Single();
        // 空说明不占位置；非空错误仍可见，避免清理小字时一起隐藏故障提示。
        var status = new TextBlock { Style = (Style)app.FindResource("StatusMessageTextStyle") };
        Check(status.Visibility == Visibility.Collapsed, "空提示自动收起");
        status.Text = "读取失败：连接已断开";
        Check(status.Visibility == Visibility.Visible, "错误提示仍正常显示");
        status.Text = "";
        Check(status.Visibility == Visibility.Collapsed, "清空状态后不留下空白提示行");
        try
        {
            var changes = new HashSet<string?>();
            management.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            management.SelectedDevice = management.Devices.Single(d => d.CommunicationMode == DeviceCommunicationMode.Rtu);
            Check(changes.Contains(nameof(management.SelectedDeviceTitle)) && changes.Contains(nameof(management.SelectedDeviceConnectionDetails))
                && changes.Contains(nameof(management.SelectedDeviceTimeoutDetails)), "选择设备会通知三项详情重新显示");
            Check(management.SelectedDeviceConnectionDetails.Contains("COM10") && management.SelectedDeviceConnectionDetails.Contains("站号 1")
                && management.SelectedDeviceTimeoutDetails.Contains("RTU 不使用 TCP"), "RTU 详情不冒充网络地址和连接超时");
            main.CurrentPage = management;
            Render(window, new Size(1424, 861), "devices-rtu-large");
            Check(window.Icon is not null, "主窗口已加载自定义应用图标");
            Check(window.ResizeMode == ResizeMode.CanMinimize && window.WindowStyle == WindowStyle.SingleBorderWindow,
                "主窗口保留原生标题栏，不开放任意缩放");
            Check(window.Width == 1440 && window.Height == 900, "保留原来的初始窗口大小，不强制启动即最大化");
            // 只创建隐藏窗口句柄，不 Show；直接检查标题栏按钮和边框的实际设置。
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            int nativeStyle = GetWindowLong(handle, -16);
            Check((nativeStyle & 0x00010000) != 0 && (nativeStyle & 0x00020000) != 0,
                "原生窗口保留最大化、还原和最小化入口");
            Check((nativeStyle & 0x00040000) == 0, "原生窗口禁用边框拖动缩放");
            var footer = (Border)window.FindName("WorkspaceFooter");
            var logout = (Button)window.FindName("LogoutButton");
            var workspace = (FrameworkElement)window.Content; // 离屏渲染的是内容，不是原生窗口外框。
            Check(Grid.GetRow(footer) == 2 && footer.TranslatePoint(new Point(), workspace).Y > workspace.RenderSize.Height * 0.75,
                "退出入口固定在左侧工作空间底部");
            Check(ReferenceEquals(logout.Command, main.LogoutCommand), "退出仍绑定原命令，不绕过确认与采集收尾");
            Check(Descendants<Button>(workspace).Count(b => Equals(b.Content, "退出登录")) == 1,
                "界面只保留左下角一个退出入口");
            CheckDevicePage(window, "正常窗口");
            Render(window, new Size(1240, 681), "devices-rtu-small");
            CheckDevicePage(window, "小工作区");
            management.SelectedDevice = management.Devices.Single(d => d.CommunicationMode == DeviceCommunicationMode.Tcp);
            Render(window, new Size(1424, 861), "devices-tcp-large");
            Check(management.SelectedDeviceTimeoutDetails.Contains("连接超时 750 ms"), "TCP 详情保留连接超时");
            management.SelectedDevice = null;
            Check(management.SelectedDeviceTitle == "设备详情" && management.SelectedDeviceConnectionDetails.Contains("选择"), "未选择设备时显示提示而不是旧参数");

            await acquisition.RefreshDevicesCommand.ExecuteAsync(null);
            acquisition.SelectedDevice = acquisition.Devices.Single(d => d.CommunicationMode == DeviceCommunicationMode.Rtu);
            await acquisition.RefreshPointsCommand.ExecuteAsync(null);
            // 等设备变化发起的点位查询结束，避免给未加载的点设置选择。
            for (int i = 0; i < 100 && acquisition.IsLoadingPoints; i++) await Task.Delay(10);
            acquisition.SelectedPoint = acquisition.Points.First();
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

            await monitoring.RefreshDevicesCommand.ExecuteAsync(null);
            MonitoringDeviceViewModel rtuRow = monitoring.Devices.Single(d => d.Device.CommunicationMode == DeviceCommunicationMode.Rtu);
            Check(rtuRow.ConnectionSummary == "RTU · COM10 · 站号 1" && !rtuRow.ConnectionSummary.Contains("4800")
                && rtuRow.ConnectionDetails.Contains("4800,8,N,1"), "监控设备行简写目标，完整串口参数保留在详情");
            foreach (MonitoringDeviceViewModel row in monitoring.Devices) row.IsChecked = true;
            main.CurrentPage = monitoring;
            Render(window, new Size(1424, 861), "monitor-connections-large");
            Render(window, new Size(1920, 1040), "monitor-maximized");
            CheckTables((DependencyObject)window.Content, "最大化工作区");
            Render(window, new Size(1240, 681), "monitor-connections-small");
            var monitorView = Descendants<MonitoringView>((DependencyObject)window.Content).Single();
            var deviceGrid = (DataGrid)monitorView.FindName("MonitoringDevicesGrid");
            Check(Equals(deviceGrid.Columns[0].Header, ""), "勾选列表去掉选字，复选框列保留");
            Check(!Descendants<TextBlock>(monitorView).Any(t => t.Text.Contains("取消勾选不会停采")
                || t.Text.Contains("点击行查看波形") || t.Text.Contains("勾选设备批量启停")),
                "监控页不再显示重复操作说明");
            Check(Descendants<TextBlock>(monitorView).Any(t => t.Text == rtuRow.ConnectionSummary), "监控 XAML 已绑定新的连接简写");
            CheckTables(monitorView, "监控设备表");

            // 其余页面与表单也只做离屏布局检查，仍使用同一个临时数据库。
            main.CurrentPage = main.Pages.OfType<UserManagementViewModel>().Single();
            Render(window, new Size(1424, 861), "users-clean");
            var history = main.Pages.OfType<HistoryViewModel>().Single();
            await history.RefreshRecordsCommand.ExecuteAsync(null);
            await history.Alarms.RefreshRecordsCommand.ExecuteAsync(null);
            main.CurrentPage = history;
            Render(window, new Size(1424, 861), "history-clean");
            var tabs = Descendants<TabControl>((DependencyObject)window.Content).Single();
            tabs.SelectedIndex = 1;
            Render(window, new Size(1424, 861), "alarms-clean");
            Check(!Descendants<TextBlock>((DependencyObject)window.Content).Any(t => t.Text.Contains("正常采集不会生成报警")),
                "报警页保留空结果状态，不显示重复教学段落");
            using var points = acquisition.CreatePointViewModel(acquisition.Devices.First());
            Render(new PointWindow(points), new Size(1040, 720), "points-clean");
            Render(new AddDeviceWindow(management), new Size(704, 721), "device-form-clean");
            Render(new PasswordWindow(new PasswordEditViewModel(user, new UserStore(path))), new Size(520, 430), "password-clean");
            var login = new LoginWindow(new UserStore(path), store, new HistoryStore(path));
            Render(login, new Size(500, 530), "login-clean");
            Check(login.Icon is not null && login.ResizeMode == ResizeMode.NoResize,
                "登录窗口使用同一图标并保留固定大小");
        }
        finally { await main.StopBackgroundWorkAsync(); }
        Console.WriteLine($"Temporary database only: {path}");
    }

    private static void Seed(DeviceStore store, UserAccount user)
    {
        foreach (bool rtu in new[] { true, false })
        {
            store.TryAddDevice(user, new DeviceInput
            {
                Name = rtu ? "一号温湿度传感器" : "TCP 采集试验设备", Host = "127.0.0.1", Port = 502, UnitId = 1,
                CommunicationMode = rtu ? DeviceCommunicationMode.Rtu : DeviceCommunicationMode.Tcp,
                SerialPortName = "COM10", BaudRate = 4800, SamplingIntervalMilliseconds = 1000,
                ConnectTimeoutMilliseconds = 750, ReadTimeoutMilliseconds = 1000, WriteTimeoutMilliseconds = 1200
            });
        }
        foreach (DeviceInfo device in store.GetDevices(user))
        {
            store.TrySetDeviceEnabled(user, device.Id, true);
            foreach (ushort address in new ushort[] { 0, 1 }) store.TryAddPoint(user, device.Id, new PointInput
            { Name = address == 0 ? "湿度原始值" : "温度原始值", Address = address, RegisterType = RegisterType.Holding,
                DataType = address == 0 ? PointDataType.UInt16 : PointDataType.Int16 });
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

    private static void CheckReadPage(Window window, AcquisitionViewModel vm, SingleReadState state, string name)
    {
        AcquisitionView view = Descendants<AcquisitionView>((DependencyObject)window.Content).Single();
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

    // 测试专用：准备显示状态，不启动真实通信；状态转换另由 RTU 流程测试验证。
    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);

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

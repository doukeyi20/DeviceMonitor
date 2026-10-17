using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeviceMonitor;
using DeviceMonitor.Core.Communication;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.ViewModels;
using DeviceMonitor.Views;
using DeviceMonitor.Sampling;
using Microsoft.Data.Sqlite;

// 只用临时数据库和假串口，不打开真实串口、不启动正式程序。
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
            try
            {
                Directory.CreateDirectory(Output);
                CheckFrames();
                await CheckBusAsync();
                await CheckRtuFlowAsync();
                CheckStorageAndForm();
                Console.WriteLine($"ALL {_checks} RTU CHECKS PASSED");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { SqliteConnection.ClearAllPools(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return result;
    }

    private static void CheckFrames()
    {
        byte[] documentedRequest = Convert.FromHexString("010300000002C40B");
        Check(ModbusRtuFrame.CalculateCrc(documentedRequest.AsSpan(0, 6)) == 0x0BC4, "说明书请求 CRC 低字节在前");
        byte[] documentedResponse = Convert.FromHexString("01030401E6FF9F1BA0");
        Check(ModbusRtuFrame.CalculateCrc(documentedResponse.AsSpan(0, 7)) == 0xA01B, "说明书响应 CRC");
        byte[] request = ModbusRtuFrame.CreateReadRequest(1, 3, 1);
        Check(request.Length == 8 && request[3] == 1 && request[5] == 1, "单点请求地址 1、数量 1");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) == ModbusRtuFrame.CalculateCrc(request.AsSpan(0, 6)), "生成的单点请求 CRC");
        Check(ModbusRtuFrame.ReadValue(Response(1, 3, 486), 1, 3) == 486, "湿度原始值 486，不擅自除以 10");
        Check(unchecked((short)ModbusRtuFrame.ReadValue(Response(1, 3, 0xFF9F), 1, 3)) == -97, "负温度按 Int16 得到 -97");
        Check(ModbusRtuFrame.ReadValue(Response(7, 4, 65535), 7, 4) == 65535, "04 和 UInt16 上限");
        Throws<ArgumentOutOfRangeException>(() => ModbusRtuFrame.CreateReadRequest(0, 3, 0), "拒绝广播站号读取");
        Throws<ArgumentOutOfRangeException>(() => ModbusRtuFrame.CreateReadRequest(248, 3, 0), "拒绝超出范围的站号");
        Throws<ArgumentOutOfRangeException>(() => ModbusRtuFrame.CreateReadRequest(1, 6, 0), "不允许写寄存器功能码");
        byte[] bad = Response(1, 3, 42); bad[4] ^= 1;
        Throws<InvalidDataException>(() => ModbusRtuFrame.ReadValue(bad, 1, 3), "拒绝 CRC 损坏的数据");
        Throws<InvalidDataException>(() => ModbusRtuFrame.ReadValue(Response(2, 3, 42), 1, 3), "拒绝其他站号响应");
        Throws<InvalidDataException>(() => ModbusRtuFrame.ReadValue(Response(1, 4, 42), 1, 3), "拒绝不同功能码响应");
        byte[] error = WithCrc([1, 0x83, 2]);
        Throws<InvalidOperationException>(() => ModbusRtuFrame.ReadValue(error, 1, 3), "设备异常响应显示地址错误");
        Throws<InvalidDataException>(() => ModbusRtuFrame.ReadValue(WithCrc([1, 3, 4, 0, 1, 0, 2]), 1, 3), "拒绝错误的寄存器数量");
        SerialConnectionSettings.Validate(" com3 ", 4800, Parity.None, StopBits.One, 1);
        Check(SerialConnectionSettings.NormalizePortName(" com3 ") == "COM3", "串口名称统一大小写和空格");
        Check(!SerialConnectionSettings.IsPortNameValid("COM0") && !SerialConnectionSettings.IsPortNameValid("abc"), "COM 口基本检查");
        Throws<ArgumentException>(() => SerialConnectionSettings.Validate("COM3", 4800, Parity.Mark, StopBits.One, 1), "拒绝不支持的校验位");
        Throws<ArgumentException>(() => SerialConnectionSettings.Validate("COM3", 4800, Parity.None, StopBits.OnePointFive, 1), "拒绝不支持的停止位");
    }

    private static async Task CheckBusAsync()
    {
        var ports = new List<FakePort>();
        var bus = new RtuSerialBus(_ => { var port = new FakePort(); ports.Add(port); return port; });
        object first = new(), second = new();
        DeviceInfo device = Device(1), other = Device(2);
        Check(await bus.ReadAsync(first, device, Point(device), default) == 486, "分批收到一字节也能收齐");
        await bus.ReadAsync(second, other, Point(other), default);
        Check(ports.Count == 1 && ports[0].Writes == 2, "同 COM 多站共用一个串口句柄");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => bus.ReadAsync(i % 2 == 0 ? first : second,
            i % 2 == 0 ? device : other, Point(i % 2 == 0 ? device : other), default)));
        Check(ports.Count == 1 && !ports[0].Overlapped, "并发请求按整次收发排队，不混包");
        await bus.ReleaseAsync(first);
        Check(!ports[0].Disposed, "停一台设备不关闭其他设备的串口");
        await ThrowsAsync<InvalidOperationException>(() => bus.ReadAsync(new object(), Device(3, 9600), Point(Device(3)), default), "同 COM 活动设备参数冲突明确拒绝");
        await bus.ReleaseAsync(second);
        Check(ports[0].Disposed, "最后一个使用者退出关闭串口");
        object third = new();
        await bus.ReadAsync(third, Device(3, 9600), Point(Device(3)), default);
        Check(ports.Count == 2, "全停后可以换波特率重新打开");
        await bus.ReleaseAsync(third);

        var silent = new FakePort { Silent = true };
        var timeoutBus = new RtuSerialBus(_ => silent);
        await ThrowsAsync<TimeoutException>(() => timeoutBus.ReadAsync(first, Device(1, timeout: 90), Point(device), default), "无响应按整次读取超时");
        Check(silent.Disposed, "超时后关闭句柄");
        await timeoutBus.ReleaseAsync(first);

        var cancelPort = new FakePort { Silent = true };
        var cancelBus = new RtuSerialBus(_ => cancelPort);
        using var cancellation = new CancellationTokenSource(60);
        var elapsed = Stopwatch.StartNew();
        await ThrowsAsync<OperationCanceledException>(() => cancelBus.ReadAsync(first, Device(1, timeout: 3000), Point(device), cancellation.Token), "收包等待中能停止");
        Check(elapsed.ElapsedMilliseconds < 1000 && cancelPort.Disposed, "停止不等待完整 3 秒超时，并关闭句柄");
        await cancelBus.ReleaseAsync(first);

        var blockedPort = new FakePort { Silent = true };
        var blockedBus = new RtuSerialBus(_ => blockedPort);
        using var activeCancel = new CancellationTokenSource();
        Task active = blockedBus.ReadAsync(first, Device(1, timeout: 3000), Point(device), activeCancel.Token);
        await Task.Delay(30);
        using var queuedCancel = new CancellationTokenSource(30);
        await ThrowsAsync<OperationCanceledException>(() => blockedBus.ReadAsync(second, other, Point(other), queuedCancel.Token), "排队等待也能停止");
        activeCancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => active, "取消前一条请求");
        await blockedBus.ReleaseAsync(first);
        Check(blockedPort.Writes == 1, "已取消的排队请求没有发给设备");

        int opened = 0;
        var retryBus = new RtuSerialBus(_ => new FakePort { FailOpen = ++opened == 1 });
        await ThrowsAsync<IOException>(() => retryBus.ReadAsync(first, Device(1, timeout: 30), Point(device), default), "串口断开显示失败");
        Check(await retryBus.ReadAsync(first, Device(1, timeout: 30), Point(device), default) == 486 && opened == 2, "下一轮能重新打开恢复读取");
        await retryBus.ReleaseAsync(first);
    }

    private static void CheckStorageAndForm()
    {
        string path = Path.Combine(Output, $"rtu-test-only-{Guid.NewGuid():N}.db");
        var initializer = new DatabaseInitializer(path);
        initializer.Initialize();
        var users = new UserStore(path);
        users.TryCreateFirstAdministrator("rtu_admin", "RtuChecks42!");
        UserAccount user = users.CheckLogin("rtu_admin", "RtuChecks42!")!;
        var store = new DeviceStore(path);
        var tcp = Input("旧TCP");
        tcp.CommunicationMode = DeviceCommunicationMode.Tcp; tcp.Host = "127.0.0.1"; tcp.Port = 502; tcp.UnitId = 255;
        Check(store.TryAddDevice(user, tcp), "旧 TCP 参数仍可保存，包括 Unit ID 255");

        // 当前已从空库采用 EF 首次迁移，不再测试旧的第 7→8 版 SQL 升级。
        // 重复初始化必须保留已存的 TCP 配置，不能当作“每次启动都重建”。
        initializer.Initialize(); initializer.Initialize();
        DeviceInfo old = store.GetDevices(user).Single();
        Check(old.CommunicationMode == DeviceCommunicationMode.Tcp && old.Host == "127.0.0.1" && old.UnitId == 255, "EF 重复初始化保留已保存的 TCP 设备");
        Check(store.TryAddDevice(user, Input("温湿度")), "RTU 不用填写 IP 也能保存");
        DeviceInfo rtu = store.GetDevices(user).Single(d => d.Name == "温湿度");
        Check(rtu.SerialPortName == "COM3" && rtu.BaudRate == 4800 && rtu.Parity == Parity.None && rtu.StopBits == StopBits.One, "串口参数保存并还原");
        Check(rtu.Endpoint == "COM3 · 4800,8,N,1" && rtu.ConnectTimeoutText == "—", "RTU 列表不显示无关 TCP 地址和超时");
        Throws<InvalidOperationException>(() => store.TryAddDevice(user, Input("重复站号")), "阻止同 COM 同站号重复配置");
        var another = Input("另一站"); another.UnitId = 2;
        Check(store.TryAddDevice(user, another), "同 COM 不同站号可以新增");
        var invalid = Input("非法站号"); invalid.UnitId = 0;
        Throws<ArgumentException>(() => store.TryAddDevice(user, invalid), "保存层拒绝站号 0");
        var updated = Input("温湿度已改"); updated.UnitId = 3; updated.BaudRate = 9600; updated.Parity = Parity.Even; updated.StopBits = StopBits.Two;
        Check(store.TryUpdateDevice(user, rtu.Id, updated), "RTU 编辑保存");
        rtu = store.GetDevice(user, rtu.Id)!;
        Check(rtu.BaudRate == 9600 && rtu.Parity == Parity.Even && rtu.StopBits == StopBits.Two && rtu.Id != old.Id, "编辑保留设备编号、还原全部串口参数");

        var management = new DeviceManagementViewModel(user, store);
        management.SelectedDevice = rtu;
        Check(!management.TestConnectionCommand.CanExecute(null), "RTU 不误用 TCP 连接测试");
        management.SelectedDevice = old;
        Check(management.TestConnectionCommand.CanExecute(null), "TCP 连接测试仍可使用");
        var editor = new DeviceEditorViewModel(management, rtu);
        Check(editor.IsRtu && editor.SerialPortName == "COM3" && editor.SelectedParity == Parity.Even, "编辑表单还原 RTU 配置");
        var create = new DeviceEditorViewModel(management) { SelectedCommunicationMode = DeviceCommunicationMode.Rtu, DeviceName = "表单新增", SerialPortName = "COM8" };
        bool saved = false; create.SaveSucceeded += () => saved = true;
        create.SaveCommand.Execute(null);
        Check(saved && store.GetDevices(user).Any(d => d.Name == "表单新增"), "RTU 表单无需隐藏的 TCP 字段即可保存");
        create.UnitIdText = "248";
        string? inputError = null; create.InputErrorRequested += (_, property) => inputError = property;
        create.SaveCommand.Execute(null);
        Check(inputError == nameof(DeviceEditorViewModel.UnitIdText), "RTU 输入错误定位站号");

        var app = new App(); app.InitializeComponent(); // 不 Run、不触发正式数据库初始化。
        var window = new AddDeviceWindow(management, rtu);
        var vm = (DeviceEditorViewModel)window.DataContext;
        vm.SelectedCommunicationMode = DeviceCommunicationMode.Rtu;
        Render(window, new Size(704, 721), "rtu-device-form");
        Check(IsContentVisible((FrameworkElement)window.FindName("SerialPortNameInput")) && !IsContentVisible((FrameworkElement)window.FindName("HostInput")), "RTU 界面显示串口、隐藏 IP");
        vm.SelectedCommunicationMode = DeviceCommunicationMode.Tcp;
        Render(window, new Size(704, 721), "tcp-device-form");
        Check(!IsContentVisible((FrameworkElement)window.FindName("SerialPortNameInput")) && IsContentVisible((FrameworkElement)window.FindName("HostInput")), "TCP 界面显示 IP、隐藏串口");
        vm.SelectedCommunicationMode = DeviceCommunicationMode.Rtu;
        Render(window, new Size(604, 521), "rtu-device-form-small");
        var save = (Button)window.FindName("SaveDeviceButton");
        Check(IsContentVisible(save) && save.ActualHeight > 0, "小工作区底部保存按钮仍可见");
        window.Close();
        Console.WriteLine($"Temporary database: {path}");
    }

    private static async Task CheckRtuFlowAsync()
    {
        string path = Path.Combine(Output, $"rtu-flow-test-only-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(path).Initialize();
        var user = new UserAccount(1, "rtu-flow-admin", UserRole.Administrator, true);
        var store = new DeviceStore(path);
        foreach (byte station in new byte[] { 1, 2 })
        {
            DeviceInput input = Input($"RTU试验站{station}"); input.SerialPortName = "COM77";
            input.UnitId = station; input.SamplingIntervalMilliseconds = 40;
            store.TryAddDevice(user, input);
            DeviceInfo device = store.GetDevices(user).Single(d => d.UnitId == station);
            store.TrySetDeviceEnabled(user, device.Id, true);
            store.TryAddPoint(user, device.Id, new PointInput
            { Name = "湿度原始值", RegisterType = RegisterType.Holding, Address = 0, DataType = PointDataType.UInt16, UpperLimit = 400 });
            store.TryAddPoint(user, device.Id, new PointInput
            { Name = "温度原始值", RegisterType = RegisterType.Holding, Address = 1, DataType = PointDataType.Int16 });
        }
        var ports = new List<FakePort>();
        bool silent = false, corrupt = false;
        var bus = new RtuSerialBus(_ => { var port = new FakePort { Silent = silent, CorruptResponse = corrupt }; ports.Add(port); return port; });
        // 测试时把 COM77 换成假串口；仍执行真正的读取器、页面命令、采集循环和数据库保存。
        var buses = (ConcurrentDictionary<string, RtuSerialBus>)typeof(ModbusRtuTransport)
            .GetField("Buses", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        if (!buses.TryAdd("COM77", bus)) throw new Exception("Test COM77 already in use");
        var runtime = new DeviceRuntimeManager(user, store, new HistoryStore(path));
        var acquisition = new AcquisitionViewModel(user, store, runtime);
        var monitor = new MonitoringViewModel(user, store, runtime);
        try
        {
            await acquisition.RefreshDevicesCommand.ExecuteAsync(null);
            DeviceInfo first = store.GetDevices(user).First(d => d.UnitId == 1);
            acquisition.SelectedDevice = acquisition.Devices.Single(d => d.Id == first.Id);
            await UntilAsync(() => acquisition.Points.Count == 2 && !acquisition.IsLoadingPoints);
            acquisition.SelectedPoint = acquisition.Points.Single(p => p.Address == 0);
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadResultText.Contains("486"), "RTU 读取一次命令走完整入口并显示整数");
            Check(acquisition.ReadValueText == "486" && acquisition.ReadState == DeviceMonitor.Models.SingleReadState.Succeeded
                && acquisition.ReadTimeText != "—", "成功结果分开保存数值、状态和时间");
            Check(CountRows(path, "HistoryRecords") == 0 && CountRows(path, "AlarmRecords") == 0, "单次读取不保存历史或报警");
            Check(ports.Single().Disposed, "单次读取 finally 释放串口");
            acquisition.SelectedPoint = acquisition.Points.Single(p => p.Address == 1);
            Check(acquisition.ReadValueText == "—" && acquisition.ReadState == DeviceMonitor.Models.SingleReadState.NotRead,
                "换点清掉旧值，不把湿度结果挂到温度上");
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadResultText.Contains("-97"), "RTU 读取器与页面正确显示有符号温度");
            Check(acquisition.ReadValueText == "-97" && acquisition.ReadTargetText.Contains("温度原始值"), "负数和本次读取目标对应");

            silent = true;
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadState == DeviceMonitor.Models.SingleReadState.TimedOut && acquisition.ReadValueText == "—"
                && acquisition.ReadTimeText == "—" && acquisition.ReadResultText.Contains("超时"), "超时显示真实原因，不留旧成功值和时间");
            Task cancelRead = acquisition.ReadOnceCommand.ExecuteAsync(null);
            await Task.Delay(30);
            acquisition.ReadOnceCommand.Cancel();
            await cancelRead;
            Check(acquisition.ReadState == DeviceMonitor.Models.SingleReadState.Canceled && acquisition.ReadValueText == "—"
                && !acquisition.IsReading, "取消显示独立状态，收尾完成后恢复按钮");
            silent = false; corrupt = true;
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadState == DeviceMonitor.Models.SingleReadState.Failed && acquisition.ReadValueText == "—"
                && acquisition.ReadResultText.Contains("CRC"), "响应损坏显示读取失败和 CRC 原因");
            corrupt = false;
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadState == DeviceMonitor.Models.SingleReadState.Succeeded && acquisition.ReadValueText == "-97",
                "失败后重新读取可恢复成功显示");
            Check(CountRows(path, "HistoryRecords") == 0 && CountRows(path, "AlarmRecords") == 0,
                "新增显示状态没有让单次测试保存历史或报警");
            int portsBeforeSampling = ports.Count;

            await monitor.RefreshDevicesCommand.ExecuteAsync(null);
            foreach (MonitoringDeviceViewModel row in monitor.Devices) row.IsChecked = true;
            monitor.StartSamplingCommand.Execute(null);
            await UntilAsync(() => runtime.Sessions.Count == 2 && runtime.Sessions.All(s => s.CompletedRounds >= 2));
            Check(runtime.Sessions.All(s => s.Readings.Count == 2 && s.Readings.Any(r => r.ValueText == "486") && s.Readings.Any(r => r.ValueText == "-97")), "同 COM 两站连续采集，各点读数不混淆");
            Check(ports.Count == portsBeforeSampling + 1 && !ports.Last().Overlapped, "单次测试后两台连续任务只再开一条串口");
            Check(CountRows(path, "HistoryRecords") >= 8 && CountRows(path, "AlarmRecords") >= 2, "RTU 连续读数进入历史和报警数据库");
            Check(monitor.Readings.Count == 4 && monitor.Readings.Count(r => r.IsAlert) == 2, "RTU 读数与当前报警进入合并表");
            Check(runtime.Sessions.All(s => s.Readings.All(r => r.TrendSamples.Count >= 2)), "RTU 各点从开始采集独立缓存波形");
            DeviceSamplingViewModel other = runtime.Sessions.Single(s => s.DeviceId != first.Id);
            await runtime.StopAsync(first.Id);
            int rounds = other.CompletedRounds;
            await UntilAsync(() => other.CompletedRounds > rounds);
            Check(!ports.Last().Disposed && other.IsRunning, "停止一站，另一站在同 COM 继续读取");
            await runtime.StopAsync(other.DeviceId);
            Check(ports.Last().Disposed && runtime.Sessions.All(s => !s.IsRunning), "最后一站停止后串口释放、采集收尾完成");
            int records = CountRows(path, "HistoryRecords");
            await Task.Delay(100);
            Check(CountRows(path, "HistoryRecords") == records, "停止完成后不再写 RTU 读数历史");
        }
        finally
        {
            await acquisition.StopOperationsAsync();
            await runtime.StopAllAsync();
            await monitor.StopOperationsAsync();
            buses.TryRemove("COM77", out _);
        }
    }

    private static int CountRows(string path, string table)
    {
        using var connection = new SqliteConnection($"Data Source={path}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("RTU flow condition timed out");
            await Task.Delay(10);
        }
    }

    private static DeviceInput Input(string name) => new()
    {
        Name = name, CommunicationMode = DeviceCommunicationMode.Rtu, SerialPortName = " com3 ", BaudRate = 4800,
        UnitId = 1, SamplingIntervalMilliseconds = 1000, ConnectTimeoutMilliseconds = 1000,
        ReadTimeoutMilliseconds = 1000, WriteTimeoutMilliseconds = 1000
    };
    private static DeviceInfo Device(byte station, int baud = 4800, int timeout = 300) => new(
        station, $"站{station}", "", 502, station, 1000, 1000, timeout, 300, true,
        DeviceCommunicationMode.Rtu, "COM3", baud);
    private static PointInfo Point(DeviceInfo device) => new(1, device.Id, "湿度", RegisterType.Holding, 0, PointDataType.UInt16);
    private static byte[] Response(byte station, byte function, ushort value) => WithCrc([station, function, 2, (byte)(value >> 8), (byte)value]);
    private static byte[] WithCrc(byte[] body)
    {
        byte[] result = new byte[body.Length + 2]; body.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(body.Length), ModbusRtuFrame.CalculateCrc(body));
        return result;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine($"PASS {++_checks}: {message}"); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { Check(true, message); return; } throw new Exception("Expected " + typeof(T).Name + ": " + message); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { Check(true, message); return; } throw new Exception("Expected " + typeof(T).Name + ": " + message); }
    private static void Render(Window window, Size size, string name)
    {
        var content = (FrameworkElement)window.Content; content.DataContext = window.DataContext;
        if (content is Grid grid) grid.Background = window.Background;
        for (int i = 0; i < 3; i++)
        {
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, name + ".png")); png.Save(stream);
    }

    // 离屏窗口没 Show，IsVisible 永远是 false；这里检查内容及其父级是否被折叠。
    private static bool IsContentVisible(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null && current is not Window)
        {
            if (current is UIElement item && item.Visibility != Visibility.Visible) return false;
            current = VisualTreeHelper.GetParent(current);
        }
        return true;
    }

    private sealed class FakePort : IRtuSerialPort
    {
        private byte[] _response = [];
        private int _offset;
        public int Writes { get; private set; }
        public bool Overlapped { get; private set; }
        public bool Disposed { get; private set; }
        public bool Silent { get; init; }
        public bool FailOpen { get; init; }
        public bool CorruptResponse { get; init; }
        public void Open() { if (FailOpen) throw new IOException("fake unplug"); }
        public void DiscardInput() { if (_offset < _response.Length) Overlapped = true; }
        public void Write(byte[] bytes, int timeout)
        {
            if (_offset < _response.Length) Overlapped = true;
            Writes++; _response = Response(bytes[0], bytes[1], bytes[3] == 1 ? (ushort)0xFF9F : (ushort)486); _offset = 0;
            if (CorruptResponse) _response[^1] ^= 1;
        }
        public int Read(byte[] bytes, int offset, int count, int timeout)
        {
            if (Silent) { Thread.Sleep(timeout); throw new TimeoutException(); }
            Thread.Sleep(1); bytes[offset] = _response[_offset++]; return 1;
        }
        public void Dispose() => Disposed = true;
    }
}

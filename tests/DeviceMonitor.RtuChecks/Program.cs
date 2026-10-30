// 验证项目功能及异常处理的回归检查。
using DeviceMonitor.ViewModel;
using System.Buffers.Binary;
using NModbus;
using NModbus.IO;
using NModbus.Message;
using NModbus.Utility;
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
using DeviceMonitor.Model;
using DeviceMonitor.View;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private static int _checks;
    private static readonly string Output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../..", "artifacts"));

    [STAThread]
    private static int Main(string[] args)
    {
        int result = 1;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (args.Contains("--tcp"))
                {
                    await TcpLibraryChecks.RunAsync(Check);
                    Console.WriteLine($"ALL {_checks} TCP LIBRARY CHECKS PASSED");
                    result = 0;
                    return;
                }
                Directory.CreateDirectory(Output);
                await CheckFramesAsync();
                await CheckBatchAsync();
                await CheckBusAsync();
                await CheckPersistentReaderAsync();
                await CheckRtuFlowAsync();
                await CheckStorageAndFormAsync();
                Console.WriteLine($"ALL {_checks} RTU CHECKS PASSED");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { SqliteConnection.ClearAllPools(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return result;
    }

    private static async Task CheckFramesAsync()
    {
        using var resource = new FakeResource();
        using var transport = new ModbusFactory().CreateRtuTransport(resource);
        Check(transport.BuildMessageFrame(new ReadHoldingInputRegistersRequest(3, 1, 0, 2))
            .SequenceEqual(Convert.FromHexString("010300000002C40B")), "库生成与说明书一致的批量请求");
        byte[] response = Convert.FromHexString("01030401E6FF9F1BA0");
        Check(ModbusUtility.CalculateCrc(response[..^2]).SequenceEqual(response[^2..]), "库 CRC 与说明书一致");
        Check(await ReadFixtureAsync(Response(1, 3, 486)) == 486, "库解析湿度原始值");
        Check(await ReadFixtureAsync(Response(7, 4, ushort.MaxValue), 7, RegisterType.Input) == ushort.MaxValue, "04及UInt16上限");
        Check(unchecked((short)await ReadFixtureAsync(Response(1, 3, 0xFF9F))) == -97, "负温度保持正确位模式");
        byte[] bad = Response(1, 3, 42); bad[^1] ^= 1;
        await ThrowsAsync<IOException>(() => ReadFixtureAsync(bad), "原生库拒绝CRC损坏");
        await ThrowsAsync<IOException>(() => ReadFixtureAsync(Response(2, 3, 42)), "原生库拒绝错误站号");
        await ThrowsAsync<IOException>(() => ReadFixtureAsync(Response(1, 4, 42)), "原生库拒绝错误功能码");
        await ThrowsAsync<InvalidOperationException>(() => ReadFixtureAsync(WithCrc([1, 0x83, 2])), "设备异常翻译为地址错误");
        await ThrowsAsync<InvalidOperationException>(() => ReadFixtureAsync(WithCrc([1, 0x83, 6])), "设备忙不无限重试");
        await ThrowsAsync<InvalidDataException>(() => ReadFixtureAsync(WithCrc([2, 0x83, 2])), "其他站号的异常响应不误报成本设备配置错误");
        await ThrowsAsync<InvalidDataException>(() => ReadFixtureAsync(WithCrc([1, 0x84, 2])), "其他功能的异常响应不误报成本设备配置错误");
        await ThrowsAsync<InvalidDataException>(() => ReadFixtureAsync([1, 0x7F, 0, 0]), "库收到未知功能码时按响应异常处理");
        Check(ModbusPointReader.CanRetry(new InvalidDataException("响应不符")), "坏响应允许清理连接后重试");
        Check(!ModbusPointReader.CanRetry(new NotImplementedException("程序尚未实现")), "程序自己的未实现错误不能被无限重试掩盖");
        await ThrowsAsync<IOException>(() => ReadFixtureAsync(WithCrc([1, 3, 4, 0, 1, 0, 2])), "库拒绝寄存器数量不符");
        Throws<ArgumentException>(() => SerialConnectionSettings.Validate("COM3", 4800, Parity.None, StopBits.One, 0), "拒绝广播读取");
        Throws<ArgumentException>(() => SerialConnectionSettings.Validate("COM3", 4800, Parity.None, StopBits.One, 248), "拒绝越界站号");
        Check(SerialConnectionSettings.NormalizePortName(" com3 ") == "COM3", "COM名称规范化");
    }

    private static async Task<ushort> ReadFixtureAsync(byte[] response, byte station = 1, RegisterType type = RegisterType.Holding)
    {
        var reader = new FixtureReader(_ => new ModbusFactory().CreateRtuMaster(new FakeResource { FixedResponse = response }));
        DeviceInfo device = Device(station);
        var point = new PointInfo(1, device.Id, "测试点", type, 0, PointDataType.UInt16);
        return (await reader.ReadAsync(device, point)).RawValue;
    }

    private static async Task CheckBusAsync()
    {
        int active = 0, opened = 0;
        bool overlap = false;
        var resources = new ConcurrentQueue<FakeResource>();
        var reader = new FixtureReader(_ =>
        {
            if (Interlocked.Increment(ref active) > 1) overlap = true;
            Interlocked.Increment(ref opened);
            var resource = new FakeResource { ChunkDelayMilliseconds = 5, OnDispose = () => Interlocked.Decrement(ref active) };
            resources.Enqueue(resource);
            return new ModbusFactory().CreateRtuMaster(resource);
        });
        DeviceInfo first = Device(1), second = Device(2);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            reader.ReadAsync(i % 2 == 0 ? first : second, Point(i % 2 == 0 ? first : second))));
        Check(!overlap && opened == 1 && resources.All(item => item.Disposed), "同COM并行单次请求共用串口、整次收发排队，最后使用者释放");

        var silent = new FakeResource { Silent = true };
        var blockedReader = new FixtureReader(_ => new ModbusFactory().CreateRtuMaster(silent));
        using var stop = new CancellationTokenSource();
        Task activeRead = blockedReader.ReadAsync(Device(1, timeout: 200), Point(first), stop.Token);
        await UntilAsync(() => silent.ReadCalls > 0);
        using var queuedStop = new CancellationTokenSource(30);
        await ThrowsAsync<OperationCanceledException>(() => blockedReader.ReadAsync(second, Point(second), queuedStop.Token),
            "尚在等待COM名额时可以直接取消");
        stop.Cancel();
        Check(!activeRead.IsCompleted, "已开始的同步库读取不被Cancel强制终止");
        await ThrowsAsync<OperationCanceledException>(() => activeRead, "底层超时后把用户停止识别为取消");
        Check(silent.Disposed && silent.OutstandingIo == 0, "取消返回时本次资源已经释放，没有遗留IO");
        Check((await reader.ReadAsync(first, Point(first))).RawValue == 486, "失败或停止后可以重新打开并读取");
    }

    private static async Task CheckBatchAsync()
    {
        DeviceInfo device = Device(1, timeout: 1000);
        PointInfo[] points =
        [
            new(2, device.Id, "温度", RegisterType.Holding, 1, PointDataType.Int16, scale: 0.1, unit: "℃"),
            new(1, device.Id, "湿度", RegisterType.Holding, 0, PointDataType.UInt16, scale: 0.1, offset: 1, unit: "%RH")
        ];
        var resources = new ConcurrentQueue<FakeResource>();
        var reader = new FixtureReader(_ =>
        {
            var resource = new FakeResource(); resources.Enqueue(resource);
            return new ModbusFactory().CreateRtuMaster(resource);
        });
        var results = await reader.ReadPointsAsync(device, points);
        Check(resources.Count == 1 && resources.Single().LastRequest.SequenceEqual(Convert.FromHexString("010300000002C40B")),
            "两个连续点只调用一次批量请求");
        Check(results[0].Point.Id == 2 && results[0].DecodedValue == -97 && Math.Abs(results[0].Value + 9.7) < 1e-9,
            "列表顺序不等于地址顺序时，仍对应正确温度");
        Check(Math.Abs(results[1].Value - 49.6) < 1e-9, "湿度独立应用倍率和偏移");
        Check(results[0].ReadAt == results[1].ReadAt && resources.Single().Disposed, "同批时间相同，返回时资源已释放");
        var single = await reader.ReadAsync(device, points[0]);
        Check(single.DecodedValue == -97 && resources.Last().LastRequest[5] == 1, "单点测试仍只读一个地址");
        var maximum = Enumerable.Range(1000, 125).Select((address, i) => new PointInfo(i + 10, device.Id,
            $"点{i}", RegisterType.Input, (ushort)address, PointDataType.UInt16)).ToArray();
        var many = await reader.ReadPointsAsync(device, maximum);
        Check(many.Count == 125 && many[124].Value == 1124 && resources.Last().LastRequest[1] == 4, "04批量125个寄存器");

        int count = resources.Count;
        await ThrowsAsync<ArgumentException>(() => reader.ReadPointsAsync(device, []), "拒绝空列表");
        await ThrowsAsync<ArgumentException>(() => reader.ReadPointsAsync(device,
            [points[1], new PointInfo(3, device.Id, "跳号", RegisterType.Holding, 2, PointDataType.UInt16)]), "拒绝不连续地址");
        await ThrowsAsync<ArgumentException>(() => reader.ReadPointsAsync(device, [points[1], points[1]]), "拒绝重复地址");
        await ThrowsAsync<ArgumentException>(() => reader.ReadPointsAsync(device,
            [points[1], new PointInfo(3, device.Id, "04", RegisterType.Input, 1, PointDataType.UInt16)]), "拒绝混合寄存器类型");
        await ThrowsAsync<ArgumentException>(() => reader.ReadPointsAsync(device,
            [new PointInfo(3, 999, "别台", RegisterType.Holding, 0, PointDataType.UInt16)]), "拒绝别台设备的点");
        await ThrowsAsync<ArgumentException>(() => reader.ReadPointsAsync(device,
            Enumerable.Range(0, 126).Select(i => new PointInfo(i + 1, device.Id, $"点{i}", RegisterType.Holding,
                (ushort)i, PointDataType.UInt16)).ToArray()), "拒绝超过125个点");
        Check(resources.Count == count, "配置错误在打开连接前拒绝");
    }

    private static async Task CheckPersistentReaderAsync()
    {
        var resources = new List<FakeResource>();
        IModbusMaster Create(DeviceInfo _) { var r = new FakeResource(); resources.Add(r); return new ModbusFactory().CreateRtuMaster(r); }
        DeviceInfo a = Device(1), b = Device(2);
        var first = new ModbusPointReader(a, new[] { Point(a) }, Create);
        var second = new ModbusPointReader(b, new[] { Point(b) }, Create);
        try
        {
            await first.ReadAsync(); await first.ReadAsync(); await second.ReadAsync();
            Check(resources.Count == 1 && !resources[0].Disposed && resources[0].Requests.Count == 3,
                "RTU 多轮和同口两站只打开一个主站，正常读取不关闭");
            Throws<ArgumentException>(() => new ModbusPointReader(Device(3, baud: 9600), new[] { Point(Device(3)) }, Create),
                "同口同时运行的串口格式不一致时拒绝，不偷偷改另一个设备的参数");
            resources[0].FixedResponse = WithCrc([1, 0x83, 6]);
            try { await first.ReadAsync(); throw new Exception("应当设备忙"); }
            catch (ModbusDeviceException ex) { Check(ex.Code == 6 && ModbusPointReader.CanRetry(ex), "设备忙保留异常码，可稍后再试"); }
            Check(!resources[0].Disposed, "合法的设备忙响应不被当作断线");
            resources[0].FixedResponse = WithCrc([1, 0x83, 2]);
            try { await first.ReadAsync(); throw new Exception("应当地址错误"); }
            catch (ModbusDeviceException ex) { Check(ex.IsConfigurationError && !ModbusPointReader.CanRetry(ex), "地址错误不可盲目重连重试"); }
            resources[0].FixedResponse = null;
            resources[0].Silent = true;
            await ThrowsAsync<TimeoutException>(() => first.ReadAsync(), "持久串口超时正常抛出");
            Check(resources[0].Disposed, "通信超时关闭失效的共用主站");
            await second.ReadAsync();
            Check(resources.Count == 2 && !resources[1].Disposed, "下一站重新打开故障串口后恢复读取");
            await first.DisposeAsync(); await first.DisposeAsync();
            await second.ReadAsync();
            Check(resources.Count == 2 && !resources[1].Disposed, "停止一站及重复释放都不关闭另一站的连接");
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); }
        Check(resources.All(r => r.Disposed), "最后一个读取对象释放时端口关闭");
        await ThrowsAsync<ObjectDisposedException>(() => second.ReadAsync(), "已经释放的读取对象不能继续通信");
    }

    private static async Task CheckStorageAndFormAsync()
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

        initializer.Initialize(); initializer.Initialize();
        DeviceInfo old = store.GetDevices(user).Single();
        Check(old.CommunicationMode == DeviceCommunicationMode.Tcp && old.Host == "127.0.0.1" && old.UnitId == 255, "EF 重复初始化保留已保存的 TCP 设备");
        Check(store.TryAddDevice(user, Input("温湿度")), "RTU 不用填写 IP 也能保存");
        DeviceInfo rtu = store.GetDevices(user).Single(d => d.Name == "温湿度");
        Check(rtu.SerialPortName == "COM3" && rtu.BaudRate == 4800 && rtu.Parity == Parity.None && rtu.StopBits == StopBits.One, "串口参数保存并还原");
        Check(rtu.Endpoint == "COM3 · 4800,8,N,1", "RTU 连接地址只显示串口和串口参数");
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
        Check(management.SelectedDeviceTimeoutDetails.Contains("RTU 不使用 TCP 连接超时")
            && !management.IsTcpDeviceSelected, "RTU 设备详情明确不使用 TCP 连接超时");
        Check(!management.TestConnectionCommand.CanExecute(null), "RTU 不误用 TCP 连接测试");
        management.SelectedDevice = old;
        Check(management.TestConnectionCommand.CanExecute(null), "TCP 连接测试仍可使用");
        var editor = new DeviceEditorViewModel(management, rtu);
        Check(editor.IsRtu && editor.SerialPortName == "COM3" && editor.SelectedParity == Parity.Even, "编辑表单还原 RTU 配置");
        var create = new DeviceEditorViewModel(management) { SelectedCommunicationMode = DeviceCommunicationMode.Rtu, DeviceName = "表单新增", SerialPortName = "COM8" };
        bool saved = false; create.SaveSucceeded += () => saved = true;
        await create.SaveCommand.ExecuteAsync(null);
        Check(saved && store.GetDevices(user).Any(d => d.Name == "表单新增"), "RTU 表单无需隐藏的 TCP 字段即可保存");
        create.UnitIdText = "248";
        string? inputError = null; create.InputErrorRequested += (_, property) => inputError = property;
        await create.SaveCommand.ExecuteAsync(null);
        Check(inputError == nameof(DeviceEditorViewModel.UnitIdText), "RTU 输入错误定位站号");

        var app = new App(); app.InitializeComponent();
        var window = new DeviceEditorWindow(management, rtu);
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
        var ports = new ConcurrentQueue<FakeResource>();
        bool silent = false, corrupt = false, overlapped = false;
        byte? exceptionCode = null;
        int active = 0;
        Func<DeviceInfo, IReadOnlyList<PointInfo>, ModbusPointReader> MakeReader() => (device, points) =>
            new ModbusPointReader(device, points, _ =>
        {
            if (Interlocked.Increment(ref active) > 1) overlapped = true;
            var port = new FakeResource
            {
                Silent = silent,
                CorruptResponse = corrupt,
                FixedResponse = exceptionCode.HasValue ? WithCrc([device.UnitId, 0x83, exceptionCode.Value]) : null,
                OnDispose = () => Interlocked.Decrement(ref active)
            };
            ports.Enqueue(port);
            return new ModbusFactory().CreateRtuMaster(port);
        });
        var runtime = new DeviceTasks(user, store, new HistoryStore(path));
        var acquisition = new PointConfigurationViewModel(user, store, runtime);
        var monitor = new MonitoringViewModel(user, store, runtime);

        typeof(PointConfigurationViewModel).GetField("_createReader", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(acquisition, MakeReader());
        foreach (var configured in store.GetDevices(user))
            typeof(DeviceSamplingViewModel).GetField("_createReader", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(runtime.GetOrCreateSession(configured.Id), MakeReader());
        try
        {
            await acquisition.RefreshDevicesCommand.ExecuteAsync(null);
            DeviceInfo first = store.GetDevices(user).First(d => d.UnitId == 1);
            acquisition.SelectedDevice = acquisition.Devices.Single(d => d.Id == first.Id);
            await UntilAsync(() => acquisition.Points.Count == 2 && !acquisition.IsLoadingPoints);
            acquisition.SelectedPoint = acquisition.Points.Single(p => p.Address == 0);
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadResultText.Contains("486"), "RTU 读取一次命令走完整入口并显示整数");
            Check(acquisition.ReadValueText == "486" && acquisition.ReadState == DeviceMonitor.ViewModel.SingleReadState.Succeeded
                && acquisition.ReadTimeText != "—", "成功结果分开保存数值、状态和时间");
            Check(CountRows(path, "HistoryRecords") == 0 && CountRows(path, "AlarmRecords") == 0, "单次读取不保存历史或报警");
            Check(ports.Single().Disposed, "单次读取 finally 释放串口");
            acquisition.SelectedPoint = acquisition.Points.Single(p => p.Address == 1);
            Check(acquisition.ReadValueText == "—" && acquisition.ReadState == DeviceMonitor.ViewModel.SingleReadState.NotRead,
                "换点清掉旧值，不把湿度结果挂到温度上");
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadResultText.Contains("-97"), "RTU 读取器与页面正确显示有符号温度");
            Check(acquisition.ReadValueText == "-97" && acquisition.ReadTargetText.Contains("温度原始值"), "负数和本次读取目标对应");

            silent = true;
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadState == DeviceMonitor.ViewModel.SingleReadState.TimedOut && acquisition.ReadValueText == "—"
                && acquisition.ReadTimeText == "—" && acquisition.ReadResultText.Contains("超时"), "超时显示真实原因，不留旧成功值和时间");
            Task cancelRead = acquisition.ReadOnceCommand.ExecuteAsync(null);
            await Task.Delay(30);
            Check(runtime.SingleReadDeviceId == first.Id, "单次读取登记目标，另一页不能删除仍在测试的设备");
            bool deleteRejected = false;
            try { await runtime.DeleteDeviceAsync(first.Id); }
            catch (InvalidOperationException) { deleteRejected = true; }
            Check(deleteRejected && acquisition.IsReading && store.GetDevice(user, first.Id) is not null,
                "测试中的设备删除被拒绝，不会由删除流程自动取消读取");
            acquisition.ReadOnceCommand.Cancel();
            Check(runtime.SingleReadDeviceId == first.Id, "发出取消不等于收尾结束，测试目标仍暂时禁止删除");
            await cancelRead;
            Check(runtime.SingleReadDeviceId is null, "单次测试实际收尾后才解除删除限制");
            Check(acquisition.ReadState == DeviceMonitor.ViewModel.SingleReadState.Canceled && acquisition.ReadValueText == "—"
                && !acquisition.IsReading, "取消显示独立状态，收尾完成后恢复按钮");
            silent = false; corrupt = true;
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadState == DeviceMonitor.ViewModel.SingleReadState.Failed && acquisition.ReadValueText == "—"
                && !string.IsNullOrWhiteSpace(acquisition.ReadResultText), "库发现响应损坏后显示失败原因");
            corrupt = false;
            await acquisition.ReadOnceCommand.ExecuteAsync(null);
            Check(acquisition.ReadState == DeviceMonitor.ViewModel.SingleReadState.Succeeded && acquisition.ReadValueText == "-97",
                "失败后重新读取可恢复成功显示");
            Check(CountRows(path, "HistoryRecords") == 0 && CountRows(path, "AlarmRecords") == 0,
                "新增显示状态没有让单次测试保存历史或报警");
            int portsBeforeSampling = ports.Count;

            await monitor.RefreshDevicesCommand.ExecuteAsync(null);
            foreach (MonitoringDeviceViewModel row in monitor.Devices) row.IsChecked = true;
            monitor.StartSamplingCommand.Execute(null);
            await UntilAsync(() => runtime.Sessions.Count == 2 && runtime.Sessions.All(s => s.CompletedRounds >= 2));
            Check(runtime.Sessions.All(s => s.Readings.Count == 2 && s.Readings.Any(r => r.ValueText == "486") && s.Readings.Any(r => r.ValueText == "-97")), "同 COM 两站连续采集，各点读数不混淆");
            runtime.Start(first.Id);
            Check(runtime.Sessions.Count == 2, "只用一个任务集合，重复开始仍复用原任务");
            Check(ports.Count == portsBeforeSampling + 1 && !overlapped, "同COM两站共用打开的串口，收发排队不抢端口");
            Check(ports.Skip(portsBeforeSampling).SelectMany(port => port.Requests).All(request => request[4] == 0 && request[5] == 2),
                "两台连续采集每轮都批量读取两点，没有退回单点请求");
            Check(CountRows(path, "HistoryRecords") >= 8 && CountRows(path, "AlarmRecords") >= 2, "RTU 连续读数进入历史和报警数据库");
            Check(monitor.Readings.Count == 4 && monitor.Readings.Count(r => r.IsAlert) == 2, "RTU 读数与当前报警进入合并表");
            Check(runtime.Sessions.All(s => s.Readings.All(r => r.TrendSamples.Count >= 2)), "RTU 各点从开始采集独立缓存波形");
            Check(monitor.Readings.All(row => runtime.Sessions.Single(task => task.DeviceId == row.DeviceId).Readings.Contains(row)),
                "监控表和采集任务使用同一个点位对象，不再复制或转发一行");
            var checkedDevice = monitor.Devices.Single(row => row.Device.Id == first.Id);
            var keptPoint = runtime.Sessions.Single(task => task.DeviceId == first.Id).Readings[0];
            int samplesBeforeUncheck = keptPoint.TrendSamples.Count;
            checkedDevice.IsChecked = false;
            Check(monitor.Readings.All(row => row.DeviceId != first.Id) && runtime.IsRunning(first.Id),
                "取消勾选只移除显示，不释放正在采集的点位对象");
            await UntilAsync(() => keptPoint.TrendSamples.Count > samplesBeforeUncheck);
            checkedDevice.IsChecked = true;
            Check(ReferenceEquals(monitor.Readings.Single(row => row.DeviceId == first.Id && row.Point.Id == keptPoint.Point.Id), keptPoint),
                "重新勾选继续使用原对象和已积累的曲线");
            DeviceSamplingViewModel other = runtime.Sessions.Single(s => s.DeviceId != first.Id);
            await runtime.StopAsync(first.Id);
            int rounds = other.CompletedRounds;
            await UntilAsync(() => other.CompletedRounds > rounds);
            Check(other.IsRunning && !overlapped, "停止一站不关闭共用串口，另一站继续复用读取");
            await runtime.StopAsync(other.DeviceId);
            Check(ports.All(port => port.Disposed) && runtime.Sessions.All(s => !s.IsRunning), "最后一站停止后串口释放、采集收尾完成");
            int records = CountRows(path, "HistoryRecords");
            await Task.Delay(100);
            Check(CountRows(path, "HistoryRecords") == records, "停止完成后不再写 RTU 读数历史");

            var oldPoints = runtime.Sessions.Single(task => task.DeviceId == first.Id).Readings.ToArray();
            int oldPointNotifications = 0;
            System.ComponentModel.PropertyChangedEventHandler countOld = (_, _) => oldPointNotifications++;
            foreach (var point in oldPoints) point.PropertyChanged += countOld;
            silent = true;
            runtime.Start(first.Id);
            DeviceSamplingViewModel firstSession = runtime.Sessions.Single(s => s.DeviceId == first.Id);
            await UntilAsync(() => firstSession.CompletedRounds >= 1);
            Check(firstSession.Readings.All(row => row.ValueText == "—" && row.QualityText.Contains("失败")),
                "批量超时时全部点显示失败，不保留伪成功");
            Check(CountRows(path, "HistoryRecords") == records, "整批失败不保存任何点的假历史");
            Check(oldPointNotifications == 0, "重新开始后旧点位已断开报警和任务通知，不跟着新任务重复刷新");
            foreach (var point in oldPoints) point.PropertyChanged -= countOld;
            silent = false;
            foreach (var port in ports) port.Silent = false;
            await UntilAsync(() => firstSession.Readings.Count == 2
                && firstSession.Readings.All(row => row.ValueText != "—" && row.ReadTimeText != "—"));
            Check(firstSession.Readings.Any(row => row.ValueText == "486") && firstSession.Readings.Any(row => row.ValueText == "-97"),
                "批量超时后的下一轮重新打开，全部点恢复成功");
            await runtime.StopAsync(first.Id);

            silent = true;
            runtime.Start(first.Id);
            await UntilAsync(() => ports.Last().Silent && ports.Last().ReadCalls > 0);
            var stopTime = Stopwatch.StartNew();
            Task stopping = runtime.StopAsync(first.Id);
            Check(firstSession.IsStopping && monitor.Readings.Where(row => row.DeviceId == first.Id)
                .All(row => row.StatusText == "正在停止"), "监控直接绑定停止状态，不再判断提示文字前缀");
            await stopping;
            Check(!firstSession.IsRunning && ports.Last().Disposed && ports.Last().OutstandingIo == 0
                && !firstSession.IsStopping && stopTime.ElapsedMilliseconds < 2000, "停止批量读取等待库超时后释放，不遗留串口任务");
            silent = false;

            PointInfo savedTemperature = store.GetPoints(user, first.Id).Single(point => point.Address == 1);
            store.TryUpdatePoint(user, first.Id, savedTemperature.Id, new PointInput
            { Name = savedTemperature.Name, RegisterType = savedTemperature.RegisterType, Address = 2, DataType = savedTemperature.DataType });
            int opensBeforeInvalid = ports.Count;
            runtime.Start(first.Id);
            await UntilAsync(() => !firstSession.IsRunning);
            Check(firstSession.StatusText.Contains("地址连续") && ports.Count == opensBeforeInvalid,
                "开始时发现不连续配置就明确结束，不打开串口反复重试配置错误");
            store.TryUpdatePoint(user, first.Id, savedTemperature.Id, new PointInput
            { Name = savedTemperature.Name, RegisterType = savedTemperature.RegisterType, Address = 1, DataType = savedTemperature.DataType });

            int beforeFatalOpens = ports.Count;
            int beforeFatalRecords = CountRows(path, "HistoryRecords");
            exceptionCode = 2;
            runtime.Start(first.Id);
            await UntilAsync(() => !firstSession.IsRunning);
            Check(firstSession.StatusText.Contains("地址") && ports.Count == beforeFatalOpens + 1,
                "连续采集收到 02 地址异常后提示原因并停止");
            await Task.Delay(1100);
            Check(ports.Count == beforeFatalOpens + 1 && CountRows(path, "HistoryRecords") == beforeFatalRecords,
                "配置错误不会后台无限重连，也不保存假历史");
            exceptionCode = 6;
            int beforeBusyOpens = ports.Count;
            runtime.Start(first.Id);
            await UntilAsync(() => firstSession.CompletedRounds >= 1);
            Check(firstSession.IsRunning && !ports.Last().Disposed, "设备忙时任务保留，连接仍保持打开");
            exceptionCode = null;
            ports.Last().FixedResponse = null;
            await UntilAsync(() => firstSession.Readings.Count == 2
                && firstSession.Readings.All(row => row.ValueText != "—" && row.ReadTimeText != "—"));
            Check(ports.Count == beforeBusyOpens + 1, "设备忙恢复后复用原连接，不多建连接");
            await runtime.StopAsync(first.Id);

            foreach (DeviceInfo configured in store.GetDevices(user))
                foreach (PointInfo point in store.GetPoints(user, configured.Id))
                    store.TryUpdatePoint(user, configured.Id, point.Id, new PointInput
                    {
                        Name = point.Name,
                        RegisterType = point.RegisterType,
                        Address = point.Address,
                        DataType = point.DataType,
                        Scale = 0.1,
                        Unit = point.Address == 0 ? "%RH" : "℃",
                        UpperLimit = point.Address == 0 ? 40.5 : null
                    });
            monitor.StartSamplingCommand.Execute(null);
            await UntilAsync(() => runtime.Sessions.All(s => s.CompletedRounds >= 2));
            Check(runtime.Sessions.All(s => s.Readings.Any(r => r.ValueText == "48.6 %RH") &&
                s.Readings.Any(r => r.ValueText == "-9.7 ℃")), "连续采集显示配置换算后的温湿度");
            Check(runtime.Sessions.All(s => s.Readings.Single(r => r.Point.Address == 0).TrendSamples
                .Any(sample => sample.Value.HasValue && Math.Abs(sample.Value.Value - 48.6) < 1e-10)), "实时曲线缓存实际值，不是原始 486");
            var convertedAlarms = new AlarmStore(path).GetRecentRecords(user).Where(a => a.Unit == "%RH").ToArray();
            Check(convertedAlarms.Length == 2 && convertedAlarms.All(a => Math.Abs(a.Value!.Value - 48.6) < 1e-10 &&
                a.Limit == 40.5 && a.Message.Contains("48.6 %RH")), "报警按实际值 48.6 与实际限值 40.5 比较并保存单位");
            Check(new HistoryStore(path).GetRecentRecords(user).Any(r => r.ValueText == "-9.7 ℃"), "连续采集历史保存小数和单位");
            monitor.SelectedReading = monitor.Readings.First(r => r.Point.Address == 0);
            Check(monitor.Trend.AxisTitle.Contains("%RH"), "实时纵轴显示所选点位单位");

            monitor.ClearDeviceChecksCommand.Execute(null);
            Check(monitor.Devices.All(row => !row.IsChecked) && runtime.Sessions.All(session => session.IsRunning),
                "清空勾选后两台设备仍在正常采集");
            Check(!monitor.StopSamplingCommand.CanExecute(null) && monitor.StopAllSamplingCommand.CanExecute(null),
                "没有勾选设备时不能停止勾选，但仍可以停止全部任务");
            await monitor.StopAllSamplingCommand.ExecuteAsync(null);
            Check(runtime.Sessions.All(session => !session.IsRunning) && !runtime.IsShuttingDown
                && ports.All(port => port.Disposed), "停止全部等待串口释放，但不会把项目标记成退出状态");
            Check(!monitor.StopAllSamplingCommand.CanExecute(null), "全部已停止时停止全部按钮不可重复执行");

            monitor.CheckAllDevicesCommand.Execute(null);
            Check(monitor.StartSamplingCommand.CanExecute(null), "停止全部后重新勾选设备仍可以开始采集");
            monitor.StartSamplingCommand.Execute(null);
            await UntilAsync(() => runtime.Sessions.All(session => session.IsRunning && session.CompletedRounds >= 1));
            Check(!runtime.IsShuttingDown && monitor.Readings.Count == 4
                && runtime.Sessions.All(session => session.Readings.Any(row => row.ValueText == "48.6 %RH")),
                "停止全部后重新开始，两台 RTU 恢复显示和实际数值读取");
            await runtime.StopAllAsync();
        }
        finally
        {
            await acquisition.StopOperationsAsync();
            await runtime.StopAllAsync();
            await monitor.StopOperationsAsync();
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
        Name = name,
        CommunicationMode = DeviceCommunicationMode.Rtu,
        SerialPortName = " com3 ",
        BaudRate = 4800,
        UnitId = 1,
        SamplingIntervalMilliseconds = 1000,
        ConnectTimeoutMilliseconds = 1000,
        ReadTimeoutMilliseconds = 1000,
        WriteTimeoutMilliseconds = 1000
    };
    private static DeviceInfo Device(byte station, int baud = 4800, int timeout = 300) => new(
        station, $"站{station}", "", 502, station, 1000, 1000, timeout, 300, true,
        DeviceCommunicationMode.Rtu, "COM3", baud);
    private static PointInfo Point(DeviceInfo device) => new(1, device.Id, "湿度", RegisterType.Holding, 0, PointDataType.UInt16);
    private static byte[] Response(byte station, byte function, ushort value) => WithCrc([station, function, 2, (byte)(value >> 8), (byte)value]);
    private static byte[] WithCrc(byte[] body)
    {
        byte[] result = new byte[body.Length + 2]; body.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(body.Length), BinaryPrimitives.ReadUInt16LittleEndian(ModbusUtility.CalculateCrc(body)));
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

    private sealed class FixtureReader(Func<DeviceInfo, IModbusMaster> createMaster)
    {
        public async Task<IReadOnlyList<PointReadResult>> ReadPointsAsync(DeviceInfo device,
            IReadOnlyList<PointInfo> points, CancellationToken token = default)
        {
            await using var reader = new ModbusPointReader(device, points, createMaster);
            return await reader.ReadAsync(token);
        }
        public async Task<PointReadResult> ReadAsync(DeviceInfo device, PointInfo point, CancellationToken token = default) =>
            (await ReadPointsAsync(device, new[] { point }, token))[0];
    }

    private sealed class FakeResource : IStreamResource
    {
        private byte[] _response = [];
        private int _offset, _disposed, _outstandingIo;
        public int InfiniteTimeout => Timeout.Infinite;
        public int ReadTimeout { get; set; } = 1000;
        public int WriteTimeout { get; set; } = 1000;
        public byte[] LastRequest { get; private set; } = [];
        public ConcurrentQueue<byte[]> Requests { get; } = new();
        public byte[]? FixedResponse { get; set; }
        public bool Silent { get; set; }
        public bool CorruptResponse { get; set; }
        public int ChunkSize { get; init; } = 2;
        public int ChunkDelayMilliseconds { get; init; }
        public int ReadCalls { get; private set; }
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public int OutstandingIo => Volatile.Read(ref _outstandingIo);
        public Action? OnDispose { get; init; }
        public void DiscardInBuffer() => _offset = _response.Length;
        public void Write(byte[] bytes, int offset, int count)
        {
            LastRequest = bytes.AsSpan(offset, count).ToArray();
            Requests.Enqueue(LastRequest);
            ushort start = BinaryPrimitives.ReadUInt16BigEndian(LastRequest.AsSpan(2, 2));
            ushort quantity = BinaryPrimitives.ReadUInt16BigEndian(LastRequest.AsSpan(4, 2));
            byte[] body = new byte[3 + quantity * 2];
            body[0] = LastRequest[0]; body[1] = LastRequest[1]; body[2] = (byte)(quantity * 2);
            for (int i = 0; i < quantity; i++)
            {
                int address = start + i;
                ushort value = address == 0 ? (ushort)486 : address == 1 ? (ushort)0xFF9F : (ushort)address;
                BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3 + i * 2), value);
            }
            _response = FixedResponse?.ToArray() ?? WithCrc(body);
            if (CorruptResponse) _response[^1] ^= 1;
            _offset = 0;
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _outstandingIo);
            try
            {
                ReadCalls++;
                if (Silent) { Thread.Sleep(ReadTimeout); throw new TimeoutException("模拟底层读取超时"); }
                if (ChunkDelayMilliseconds > 0) Thread.Sleep(ChunkDelayMilliseconds);
                int received = Math.Min(Math.Min(count, ChunkSize), _response.Length - _offset);
                _response.AsSpan(_offset, received).CopyTo(buffer.AsSpan(offset));
                _offset += received;
                return received;
            }
            finally { Interlocked.Decrement(ref _outstandingIo); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) OnDispose?.Invoke();
        }
    }
}

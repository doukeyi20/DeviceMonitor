// 使用 NModbus 批量读取寄存器，复用连接并转换点位数值。
using System.IO.Ports;
using System.Net.Sockets;
using NModbus;
using NModbus.Serial;

namespace DeviceMonitor.Model;

/// <summary>连续采集在循环外创建一次，顺序等待 ReadAsync，结束后 DisposeAsync；同一对象不并行读取或释放。</summary>
public sealed class ModbusPointReader : IAsyncDisposable
{
    // 固定本次运行的设备和点位配置，不随页面选择改变。
    private readonly DeviceInfo _device;
    private readonly PointInfo[] _points;

    private readonly ushort _startAddress;
    private readonly ushort _registerCount;
    private readonly SerialConnection? _sharedSerial;
    private TcpClient? _tcpClient;
    private IModbusMaster? _tcpMaster;
    private bool _disposed;

    // 同 COM 共用主站；登记表保护引用计数，实际收发另用串口许可排队。
    private static readonly object SerialConnectionsLock = new();
    private static readonly Dictionary<string, SerialConnection> SerialConnectionsByPort = new(StringComparer.OrdinalIgnoreCase);

    public ModbusPointReader(DeviceInfo device, IReadOnlyList<PointInfo> points)
    : this(device, points, CreateRtuMaster)
    {
    }

    internal ModbusPointReader(DeviceInfo device, IReadOnlyList<PointInfo> points,
        Func<DeviceInfo, IModbusMaster> createRtuMaster)
    {
        // 在建立连接前检查批量读取范围，错误配置不进入重连循环。
        (_startAddress, _registerCount) = ValidateReadRange(device, points);
        _device = device;
        _points = points.ToArray();

        if (device.CommunicationMode == DeviceCommunicationMode.Rtu)
        {
            SerialConnectionSettings.Validate(device.SerialPortName, device.BaudRate, device.Parity, device.StopBits, device.UnitId);
            _sharedSerial = AttachSerial(device, createRtuMaster);
        }
        else if (device.CommunicationMode == DeviceCommunicationMode.Tcp)
            TcpConnectionTester.Validate(device);
        else
            throw new ArgumentException("不支持这个连接方式。", nameof(device));
    }

    /// <summary>读取一批点位；复用现有连接，通信失败后清理连接。</summary>
    public async Task<IReadOnlyList<PointReadResult>> ReadAsync(CancellationToken token = default)
    {
        // 调用方先等本次读取完成，再开始下一轮或释放；同 COM 收发另有共用锁。
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();

            // TCP、RTU 先统一返回原始寄存器数组，后面共用换算逻辑。
            ushort[] registers;

            if (_sharedSerial is not null)
                registers = await ReadRtuAsync(token).ConfigureAwait(false);
            else
            {
                try
                {
                    await ConnectAsync(token).ConfigureAwait(false);

                    // 同步收发开始后等待库调用结束，再响应取消。
                    registers = await Task.Run(() => ReadRegisters(_tcpMaster!, token)).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsConnectionFailure(ex))
                {
                    // 丢弃故障连接，是否继续读取由调用方决定。
                    CloseTcp();
                    throw;
                }
            }

            token.ThrowIfCancellationRequested();

            // 同批点使用相同时间；按协议地址定位，不依赖页面中的排列顺序。
            DateTimeOffset readAt = DateTimeOffset.UtcNow;
            var results = new PointReadResult[_points.Length];

            for (int i = 0; i < _points.Length; i++)
            {
                PointInfo point = _points[i];

                int registerIndex = point.Address - _startAddress;
                results[i] = BuildResult(point, registers[registerIndex], readAt);
            }

            return results;
        }

        // 用户停止优先按取消返回，避免同时显示通信故障。
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException("本次读取已取消。", ex, token);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            throw new TimeoutException("等待设备响应超时，请检查供电、接线和通信参数。", ex);
        }
    }

    /// <summary>区分可重试的通信故障、暂时设备错误与配置错误。</summary>
    public static bool CanRetry(Exception exception) => IsConnectionFailure(exception)
        || exception is ModbusDeviceException { IsConfigurationError: false };

    // 恢复分类不等于已经确认物理断线。

    private static bool IsConnectionFailure(Exception exception) =>
        exception is IOException or InvalidDataException or SocketException or TimeoutException or FormatException or UnauthorizedAccessException;

    /// <summary>校验点位类型及地址连续性，返回批量读取范围。</summary>
    internal static (ushort StartAddress, ushort Count) ValidateReadRange(DeviceInfo device, IReadOnlyList<PointInfo> points)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(device.Id);
        if (!device.IsEnabled)
            throw new InvalidOperationException("设备已停用，不能读取。");
        if (device.ConnectTimeoutMilliseconds <= 0 || device.ReadTimeoutMilliseconds <= 0 || device.WriteTimeoutMilliseconds <= 0)
            throw new ArgumentException("连接、读取、写入超时都必须大于 0。", nameof(device));
        if (points.Count is < 1 or > 125)
            throw new ArgumentException("连续采集需要 1～125 个点，每个点占一个 16 位寄存器。", nameof(points));

        // 排序仅用于校验连续地址，不改变调用方的点位顺序。
        PointInfo[] ordered = points.OrderBy(point => point.Address).ToArray();
        RegisterType type = ordered[0].RegisterType;
        ushort startAddress = ordered[0].Address;

        for (int i = 0; i < ordered.Length; i++)
        {
            PointInfo point = ordered[i];
            if (point.DeviceId != device.Id)
                throw new ArgumentException("采集点不属于当前设备。", nameof(points));
            if (!Enum.IsDefined(point.RegisterType) || !Enum.IsDefined(point.DataType))
                throw new ArgumentException("暂不支持这个采集点的寄存器类型或数据类型。", nameof(points));
            if (point.RegisterType != type)
                throw new ArgumentException("连续采集要求全部点使用同一种寄存器类型（03 或 04）；单点测试仍可分别读取。", nameof(points));
            if (point.Address != startAddress + i)
                throw new ArgumentException("连续采集要求点位地址连续且不重复；请调整点位配置，单点测试仍可分别读取。", nameof(points));
        }

        return (startAddress, (ushort)points.Count);
    }

    /// <summary>首次读取或故障清理后建立 TCP 连接。</summary>
    private async Task ConnectAsync(CancellationToken token)
    {
        if (_tcpMaster is not null)
            return;

        // 连接成功后移交给字段；失败仅释放本次临时连接。
        var tcp = new TcpClient { NoDelay = true };

        try
        {
            await TcpConnectionTester.ConnectAsync(tcp, _device, token).ConfigureAwait(false);

            _tcpMaster = new ModbusFactory().CreateMaster(tcp);
            _tcpClient = tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>设置库的超时及重试参数，读取并校验寄存器结果。</summary>
    private ushort[] ReadRegisters(IModbusMaster master, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // 持有同口许可后设置本设备的读写超时。
        master.Transport.ReadTimeout = _device.ReadTimeoutMilliseconds;
        master.Transport.WriteTimeout = _device.WriteTimeoutMilliseconds;

        // 库不重发，避免与外层轮询叠加重试。
        master.Transport.Retries = 0;
        master.Transport.SlaveBusyUsesRetryCount = true;
        master.Transport.WaitToRetryMilliseconds = 0;

        ushort[] registers;
        try
        {
            // 功能码由配置决定，组帧、收齐和基础协议校验交给库。
            registers = _points[0].RegisterType == RegisterType.Holding
                ? master.ReadHoldingRegisters(_device.UnitId, _startAddress, _registerCount)
                : master.ReadInputRegisters(_device.UnitId, _startAddress, _registerCount);
        }
        catch (SlaveException ex)
        {
            // 先核对站号和异常功能码，再解释设备错误码。

            if (ex.SlaveAddress != _device.UnitId || ex.FunctionCode != ((byte)_points[0].RegisterType | 0x80))
                throw new InvalidDataException("响应站号或功能码与请求不符。", ex);

            throw new ModbusDeviceException(ex);
        }
        catch (NotImplementedException ex) when (
            ex.TargetSite?.DeclaringType?.Assembly == typeof(ModbusFactory).Assembly)
        {
            // 仅转换库内部的未知功能码异常，不掩盖项目实现错误。
            throw new InvalidDataException("设备响应包含不支持的功能码。", ex);
        }

        token.ThrowIfCancellationRequested();
        // 整批数量不符时拒绝结果，不能把缺失寄存器当成零。
        if (registers.Length != _registerCount)
            throw new InvalidDataException("响应寄存器数量与本次请求不一致。");

        return registers;
    }

    /// <summary>串行执行同 COM 的完整请求响应，并保留帧间空闲。</summary>
    private async Task<ushort[]> ReadRtuAsync(CancellationToken token)
    {
        SerialConnection serial = _sharedSerial!;

        // 同一总线上，一次完整请求响应必须结束后才允许下一站发送。
        await serial.RequestLock.WaitAsync(token).ConfigureAwait(false);

        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                // 首次读取或故障清理后才打开串口，正常轮次继续复用。
                serial.RtuMaster ??= serial.CreateMaster(_device);

                return ReadRegisters(serial.RtuMaster, token);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            DisposeMaster(ref serial.RtuMaster);
            throw;
        }
        finally
        {
            try
            {
                // 按每字符 11 位保留帧间空闲；取消时也不跳过。
                int gap = _device.BaudRate > 19200 ? 2 : (int)Math.Ceiling(3.5 * 11 * 1000 / _device.BaudRate);
                await Task.Delay(gap).ConfigureAwait(false);
            }
            finally
            {
                serial.RequestLock.Release();
            }
        }
    }

    private static SerialConnection AttachSerial(DeviceInfo device, Func<DeviceInfo, IModbusMaster> createMaster)
    {
        string name = SerialConnectionSettings.NormalizePortName(device.SerialPortName);

        lock (SerialConnectionsLock)
        {
            if (!SerialConnectionsByPort.TryGetValue(name, out SerialConnection? serial))
            {
                serial = new SerialConnection(device, createMaster);
                SerialConnectionsByPort.Add(name, serial);
            }
            else if (serial.BaudRate != device.BaudRate || serial.Parity != device.Parity || serial.StopBits != device.StopBits)
                throw new ArgumentException("同一 COM 口的设备必须使用相同的波特率、校验位和停止位。");

            // 登记读取对象的使用次数，最后一个对象释放时才关闭共用串口。
            serial.ReaderCount++;
            return serial;
        }
    }

    private static IModbusMaster CreateRtuMaster(DeviceInfo device)
    {
        var port = new SerialPort(SerialConnectionSettings.NormalizePortName(device.SerialPortName),
            device.BaudRate, device.Parity, 8, device.StopBits)
        {
            Handshake = Handshake.None
        };
        try
        {
            port.Open();

            return new ModbusFactory().CreateRtuMaster(new SerialPortAdapter(port));
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    /// <summary>解释寄存器符号并按点位配置生成工程量结果。</summary>
    private static PointReadResult BuildResult(PointInfo point, ushort raw, DateTimeOffset readAt)
    {
        // 保留原始位模式，先解释正负号，再由结果对象应用倍率与偏移。
        int decoded = point.DataType == PointDataType.Int16 ? unchecked((short)raw) : raw;
        return new PointReadResult(point, raw, decoded, readAt);
    }

    private void CloseTcp()
    {
        DisposeMaster(ref _tcpMaster);

        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    private static void DisposeMaster(ref IModbusMaster? master)
    {
        IModbusMaster? old = master;
        master = null; // 先清空引用，防止释放失败后复用坏连接。
        try
        {
            old?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warning("关闭 Modbus 连接失败", exception: ex);
        }
    }

    /// <summary>调用方已等读取结束；释放本对象及不再共用的连接。</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        CloseTcp();

        if (_sharedSerial is not null)
        {
            lock (SerialConnectionsLock)
            {
                if (--_sharedSerial.ReaderCount == 0)
                {
                    DisposeMaster(ref _sharedSerial.RtuMaster);
                    SerialConnectionsByPort.Remove(_sharedSerial.PortName);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private sealed class SerialConnection(DeviceInfo device, Func<DeviceInfo, IModbusMaster> createMaster)
    {
        public string PortName { get; } = SerialConnectionSettings.NormalizePortName(device.SerialPortName);
        public int BaudRate { get; } = device.BaudRate;
        public Parity Parity { get; } = device.Parity;
        public StopBits StopBits { get; } = device.StopBits;
        public Func<DeviceInfo, IModbusMaster> CreateMaster { get; } = createMaster;
        public SemaphoreSlim RequestLock { get; } = new(1, 1);
        public IModbusMaster? RtuMaster;
        public int ReaderCount;
    }
}

/// <summary>ReadRegisters 核对站号和功能码后，把库的 SlaveException 转成这个异常，供 RunAsync 判断如何处理。</summary>
public sealed class ModbusDeviceException(SlaveException original) : InvalidOperationException(
    $"Modbus 异常 0x{original.SlaveExceptionCode:X2}：{Describe(original.SlaveExceptionCode)}。", original)
{
    public byte Code { get; } = original.SlaveExceptionCode;
    public bool IsConfigurationError => Code is 1 or 2 or 3;
    private static string Describe(byte code) => code switch
    {
        1 => "不支持这个读取功能，请检查寄存器类型",
        2 => "寄存器地址不可用，请检查点位配置",
        3 => "请求参数不正确，请检查读取配置",
        6 => "设备正忙，稍后重试",
        _ => "设备未能执行本次请求"
    };
}

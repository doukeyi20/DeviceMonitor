// 按组读取 Modbus 寄存器并换算点位。TCP 在这里收发报文，RTU 通过 NModbus 收发。
using System.Buffers.Binary;
using System.IO.Ports;
using System.Net.Sockets;
using NModbus;
using NModbus.Serial;

using DeviceMonitor.Model;

namespace DeviceMonitor.Services;

/// <summary>连续采集在循环外创建一次，顺序等待 ReadAsync，结束后 DisposeAsync；同一对象不并行读取或释放。</summary>
public sealed class ModbusPointReader : IAsyncDisposable
{
    // 固定本次运行的设备和点位配置，不随页面选择改变。
    private readonly DeviceInfo _device;
    private readonly PointInfo[] _points;

    // 相同寄存器类型、连续地址合为一组；每次请求最多 125 个寄存器。
    private readonly PointInfo[][] _readGroups;
    private readonly SerialConnection? _sharedSerial;
    private TcpClient? _tcpClient;
    private ushort _tcpTransactionId;
    // 读取器已释放标记：第一次 DisposeAsync 时打上；以后禁止再读，重复释放直接返回，不再清掉。
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
        // 在构造方法就先根据地址把点位分组了，因为要全部读取，一次请求不能分开地址连续读寄存器，所以得把不连续的地址分组，分组分成不同请求读
        _readGroups = BuildReadGroups(device, points);
        _device = device;
        _points = points.ToArray();

        if (device.CommunicationMode == DeviceCommunicationMode.Rtu)
        {
            // 当设备是Rtu通信时，检查参数，创建COM管理器
            SerialConnectionSettings.Validate(device.SerialPortName, device.BaudRate, device.Parity, device.StopBits, device.UnitId);
            // 创建串口管理器
            _sharedSerial = AttachSerial(device, createRtuMaster);
        }
        else if (device.CommunicationMode == DeviceCommunicationMode.Tcp)
            ValidateTcp(device);
        else
            throw new ArgumentException("不支持这个连接方式。", nameof(device));
    }

    /// <summary>
    /// 循环读完分好组的全部点位
    /// </summary>
    /// <param name="token"></param>
    /// <returns>封装好的结果</returns>
    /// <exception cref="OperationCanceledException"></exception>
    /// <exception cref="TimeoutException"></exception>
    public async Task<IReadOnlyList<PointReadResult>> ReadAsync(CancellationToken token = default)
    {
        // 调用方先等本次读取完成，再开始下一轮或释放；同 COM 收发另有共用锁。
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();

            // 一轮可包含多次请求；全部成功后才返回交给显示、报警和历史。也就是把这个设备的全部点位分好组，读完才返回数值
            // 按点位编号临时记结果；分组按地址排序，但返回仍按原点位顺序。
            var rawValues = new Dictionary<long, ushort>();
            // 分好组后开始读取，读取全部组也就是一个设备全部点位
            foreach (PointInfo[] group in _readGroups)
            {
                ushort[] registers;
                // 分支判断是TCP设备还是Rtu
                if (_sharedSerial is not null)
                    registers = await ReadRtuAsync(group, token).ConfigureAwait(false);
                else
                {
                    try
                    {
                        await ConnectAsync(token).ConfigureAwait(false);
                        //  ReadTcpRegisters是真正从组帧到切分到得到数值的读取方法
                        registers = await Task.Run(() => ReadTcpRegisters(group, token)).ConfigureAwait(false);
                    }
                    // 当是可以重连的异常
                    catch (Exception ex) when (IsConnectionFailure(ex))
                    {
                        // 清理就连接
                        CloseTcp();
                        throw;
                    }
                }
                for (int i = 0; i < group.Length; i++)
                    rawValues[group[i].Id] = registers[i];
            }
            token.ThrowIfCancellationRequested();

            // 本轮统一时间，返回顺序仍与调用方传入的点位顺序一致。
            DateTimeOffset readAt = DateTimeOffset.UtcNow;
            // 把读到的数值和各种结合生成PointReadResult。也就是各方需要使用的
            var results = _points.Select(point => BuildResult(point, rawValues[point.Id], readAt)).ToArray();

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

    /// <summary>
    /// 如果是能重连的异常，则返回True
    /// </summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    public static bool CanRetry(Exception exception) => IsConnectionFailure(exception)
        || exception is ModbusDeviceException { IsConfigurationError: false };

    // 恢复分类不等于已经确认物理断线。

    /// <summary>
    /// 把能重连的异常传入，返回True
    /// </summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    private static bool IsConnectionFailure(Exception exception) =>
        exception is IOException or InvalidDataException or SocketException or TimeoutException or FormatException or UnauthorizedAccessException;

    /// <summary>
    /// 根据地址/地址连续/数量进行对点位的分组
    /// </summary>
    /// <param name="device">设备配置</param>
    /// <param name="points">点位列表</param>
    /// <returns>分好组的嵌套点位数组</returns>
    /// <exception cref="ArgumentException"></exception>
    private static PointInfo[][] BuildReadGroups(DeviceInfo device, IReadOnlyList<PointInfo> points)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("设备没有采集点。", nameof(points));
        if (device.ReadTimeoutMilliseconds <= 0 || device.WriteTimeoutMilliseconds <= 0)
            throw new ArgumentException("读写超时必须大于 0。", nameof(device));

        // 外面是组列表，里面是一组点位
        var groups = new List<List<PointInfo>>();
        // 先按照寄存器功能码排然后按照地址排，进行遍历
        foreach (PointInfo point in points.OrderBy(point => point.RegisterType).ThenBy(point => point.Address))
        {
            if (point.DeviceId != device.Id)
                throw new ArgumentException("采集点不属于当前设备。", nameof(points));

            // last是内层数组中最新的那组
            var last = groups.LastOrDefault();

            // 这个是判断点位地址是否相同，相同报错
            if (last is not null &&   // 如果最新组不为null，也就是内部有数组
                last[^1].RegisterType == point.RegisterType && // 如果遍历出的该点位的寄存器类型与本组的最新点位一样
                last[^1].Address == point.Address) // 前两个条件判断出是一组了，如果俩点地址一样，肯定不对
                throw new ArgumentException("同一寄存器类型不能配置重复地址。", nameof(points));

            // 判断该不该因为数量/寄存器不同/地址不连续而新建组
            if (last is null ||    // 如果内层数组为空
                last.Count == 125 ||  // 或如果组内 数量达到限制
                last[^1].RegisterType != point.RegisterType ||  // 或者寄存器地址开始和最新点位不一样了
                point.Address != last[^1].Address + 1) // 或者地址开始不连续了
            {
                // 创建新的组
                last = new List<PointInfo>();
                // 加入外层
                groups.Add(last);
            }
            // 被遍历出的这个点位经过两轮判断，判断是继续加入原组还是新建加入
            last.Add(point);
        }
        return groups.Select(group => group.ToArray()).ToArray();
    }

    private static void ValidateTcp(DeviceInfo device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Host);
        if (device.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(device.Port), "端口必须在 1 到 65535 之间。");
        if (device.ConnectTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(device.ConnectTimeoutMilliseconds), "连接超时必须大于 0。");
    }

    /// <summary>首次读取或故障清理后建立 TCP 连接。</summary>
    private async Task ConnectAsync(CancellationToken token)
    {
        // 组与组之间复用连接，遇到通信故障时由调用方清理。
        if (_tcpClient is not null)
            return;

        // 连接成功后移交给字段；失败仅释放本次临时连接。
        var tcp = new TcpClient { NoDelay = true };

        try
        {
            // 连接超时
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            // 配置计时（自动发出取消的取消源），设备配置的连接超时用在这
            timeout.CancelAfter(_device.ConnectTimeoutMilliseconds);
            try
            {
                await tcp.ConnectAsync(_device.Host.Trim(), _device.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"连接设备超时（{_device.ConnectTimeoutMilliseconds} ms）。", ex);
            }

            _tcpClient = tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 封装好的从组帧到切分到得到数值的Tcp读取方法
    /// </summary>
    /// <param name="group">分好的组，点位对象数组</param>
    /// <param name="token"></param>
    /// <returns>单纯对应的寄存器数值</returns>
    private ushort[] ReadTcpRegisters(PointInfo[] group, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // 连接在外面连接，这里得到流
        NetworkStream stream = _tcpClient!.GetStream();
        // 设备配置的读取和写入超时
        stream.ReadTimeout = _device.ReadTimeoutMilliseconds;
        stream.WriteTimeout = _device.WriteTimeoutMilliseconds;

        // 组装请求帧
        ushort transactionId = unchecked(++_tcpTransactionId);
        byte functionCode = (byte)group[0].RegisterType;
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request, transactionId);
        // 下标 2、3 的协议编号为 0。长度从站号开始计数。
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6);
        request[6] = _device.UnitId;
        request[7] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), group[0].Address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), (ushort)group.Length);
        stream.Write(request);

        // 调用ReadTcpResponse处理半包，得到数据
        ushort[] registers = ReadTcpResponse(stream, transactionId, functionCode, group.Length);
        token.ThrowIfCancellationRequested();
        return registers;
    }

    /// <summary>
    /// 真正读取响应，处理半包的方法
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="transactionId"></param>
    /// <param name="functionCode"></param>
    /// <param name="quantity">group的长度，也就是有几个寄存器的长度</param>
    /// <returns>得到一组对应寄存器保存的数据</returns>
    /// <exception cref="IOException"></exception>
    /// <exception cref="FormatException"></exception>
    /// <exception cref="ModbusDeviceException"></exception>
    private ushort[] ReadTcpResponse(NetworkStream stream, ushort transactionId, byte functionCode, int quantity)
    {
        // TCP 可能分几次给字节。ReadExactly 循环读满指定长度。
        var header = new byte[7];
        // 分两次读取，第一次读取完整头
        stream.ReadExactly(header);
        // 根据头得到数据区长度
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (length is < 3 or > 254)
            throw new IOException("Modbus TCP 响应长度超出范围。");

        if (BinaryPrimitives.ReadUInt16BigEndian(header) != transactionId)
            throw new IOException("Modbus TCP 响应事务编号与请求不符。");
        if (header[2] != 0 || header[3] != 0 || header[6] != _device.UnitId)
            throw new IOException("Modbus TCP 响应协议编号或站号与请求不符。");

        // 第二次读取，读取完整的数据区长度字节
        var pdu = new byte[length - 1];
        stream.ReadExactly(pdu);
        if (pdu[0] == (functionCode | 0x80))
        {
            if (pdu.Length != 2)
                throw new FormatException("Modbus TCP 异常响应长度不符。");
            throw new ModbusDeviceException(pdu[1]);
        }

        if (pdu[0] != functionCode)
            throw new IOException("Modbus TCP 响应功能码与请求不符。");

        int byteCount = pdu[1];
        if ((byteCount & 1) != 0 || byteCount != pdu.Length - 2)
            throw new FormatException("Modbus TCP 响应数据字节数不符。");
        if (byteCount != quantity * 2)
            throw new IOException("响应寄存器数量与本次请求不一致。");

        // 根据分组有几个寄存器，返回几个数值
        var registers = new ushort[quantity];
        for (int i = 0; i < registers.Length; i++)
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(2 + i * 2));
        return registers;
    }









    /* ---------------Rtu----------------*/





    /// <summary>
    /// Rtu真正的读取响应方法，返回数值
    /// </summary>
    /// <param name="master"></param>
    /// <param name="group"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="InvalidDataException"></exception>
    /// <exception cref="ModbusDeviceException"></exception>
    private ushort[] ReadRegisters(IModbusMaster master, PointInfo[] group, CancellationToken token)
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
            registers = group[0].RegisterType == RegisterType.Holding
                ? master.ReadHoldingRegisters(_device.UnitId, group[0].Address, (ushort)group.Length)
                : master.ReadInputRegisters(_device.UnitId, group[0].Address, (ushort)group.Length);
        }
        catch (SlaveException ex)
        {
            // 先核对站号和异常功能码，再解释设备错误码。

            if (ex.SlaveAddress != _device.UnitId || ex.FunctionCode != ((byte)group[0].RegisterType | 0x80))
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
        if (registers.Length != group.Length)
            throw new InvalidDataException("响应寄存器数量与本次请求不一致。");

        return registers;
    }


    /// <summary>
    /// 封装好的从组帧到切分到得到数值的Rtu读取方法,循环读完一组
    /// </summary>
    /// <param name="group"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task<ushort[]> ReadRtuAsync(PointInfo[] group, CancellationToken token)
    {
        //SerialConnection 是一个 COM 口的共享连接对象，里面既有配置，也有运行时状态。
        SerialConnection serial = _sharedSerial!;

        // Rtu设备读取是在同一总线上，一次完整请求响应必须结束后才允许下一站发送。也就是一个COM口，可能多设备共用，并且不能同时收发，需要排队
        await serial.RequestLock.WaitAsync(token).ConfigureAwait(false);

        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                // 首次读取时先打开串口并创建modbus读取对象，存到管理器
                serial.RtuMaster ??= serial.CreateMaster(_device);

                // 返回读取结果
                return ReadRegisters(serial.RtuMaster, group, token);
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

    /// <summary>按 COM 口找到共用连接信息并登记使用者；真正打开串口在首次 ReadRtuAsync 时。</summary>
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

    /// <summary>
    /// 用保存的串口参数打开端口，再交给 NModbus生成moudbus读取对象；组帧、CRC 和响应读取由库完成
    /// </summary>
    /// <param name="device"></param>
    /// <returns></returns>
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

    /// <summary>故障时丢弃旧连接；下次 ReadAsync 会通过 ConnectAsync 重新建立。</summary>
    private void CloseTcp()
    {
        TcpClient? tcp = _tcpClient;
        _tcpClient = null;
        tcp?.Dispose();
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

    /// <summary>
    /// 管理一个COM口的使用权，让不同设备使用同一个COM时排队，每个COM口对应一个
    /// </summary>
    /// <param name="device"></param>
    /// <param name="createMaster"></param>
    private sealed class SerialConnection(DeviceInfo device, Func<DeviceInfo, IModbusMaster> createMaster)
    {
        // 这些参数是对于COM口的，也就是使用同一个COM口的设备参数这些参数是相同的
        public string PortName { get; } = SerialConnectionSettings.NormalizePortName(device.SerialPortName);
        public int BaudRate { get; } = device.BaudRate;
        public Parity Parity { get; } = device.Parity;
        public StopBits StopBits { get; } = device.StopBits;
        public Func<DeviceInfo, IModbusMaster> CreateMaster { get; } = createMaster;
        // 许可证
        public SemaphoreSlim RequestLock { get; } = new(1, 1);
        public IModbusMaster? RtuMaster;
        public int ReaderCount;
    }
}

/// <summary>保存设备返回的异常码，供 RunAsync 判断是否重试。</summary>
public sealed class ModbusDeviceException(byte code, Exception? original = null) : InvalidOperationException(
    $"Modbus 异常 0x{code:X2}：{Describe(code)}。", original)
{
    public ModbusDeviceException(SlaveException original) : this(original.SlaveExceptionCode, original)
    {
    }

    public byte Code { get; } = code;
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

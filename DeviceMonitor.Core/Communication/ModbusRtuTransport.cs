using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Communication;

/// <summary>ModbusPointReader 的串口入口；同一个 COM 口只创建一条共享通信通道。</summary>
internal static class ModbusRtuTransport
{
    // 按 COM 名共享，不按设备 Id。站号不同的设备也不能同时往一根线上发请求。
    private static readonly ConcurrentDictionary<string, RtuSerialBus> Buses = new(StringComparer.OrdinalIgnoreCase);

    internal static Task<ushort> ReadAsync(object owner, DeviceInfo device, PointInfo point, CancellationToken token) =>
        Buses.GetOrAdd(SerialConnectionSettings.NormalizePortName(device.SerialPortName), _ => new RtuSerialBus())
            .ReadAsync(owner, device, point, token);

    internal static Task ReleaseAsync(string portName, object owner) =>
        Buses.TryGetValue(portName, out RtuSerialBus? bus) ? bus.ReleaseAsync(owner) : Task.CompletedTask;
}

/// <summary>一条 COM 口的排队、收发和关闭；不管界面、报警或历史。</summary>
internal sealed class RtuSerialBus
{
    private readonly SemaphoreSlim _gate = new(1, 1); // 一次锁住“发请求到收完响应”，不是只锁发送。
    private readonly HashSet<object> _owners = new(); // 哪些读取器仍在使用这条串口。
    private readonly Func<DeviceInfo, IRtuSerialPort> _createPort;
    private IRtuSerialPort? _port;
    private (int Baud, Parity Parity, StopBits StopBits)? _settings;
    private long _lastTraffic; // 上次收发结束的时间，用来留出 RTU 报文间隔。
    private long _recoverUntil; // 超时后先等旧响应过去，再发下一条。

    internal RtuSerialBus(Func<DeviceInfo, IRtuSerialPort>? createPort = null) =>
        _createPort = createPort ?? (device => new RtuSerialPort(device));

    /// <summary>读取器调用；先排队，再到后台完成整次收发，不堵住界面。</summary>
    internal async Task<ushort> ReadAsync(object owner, DeviceInfo device, PointInfo point, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // 同步串口读取用短等待反复检查停止信号，避免留下没结束的异步读取。
            return await Task.Run(() => ReadCore(owner, device, point, token), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>停止采集或单次读取结束时调用；最后一个使用者退出才关闭串口。</summary>
    internal async Task ReleaseAsync(object owner)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _owners.Remove(owner);
            if (_owners.Count == 0)
            {
                ClosePort();
                _settings = null;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>ReadAsync 的后台部分：检查参数、发请求、收齐响应，再交给报文类检查。</summary>
    private ushort ReadCore(object owner, DeviceInfo device, PointInfo point, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var settings = (device.BaudRate, device.Parity, device.StopBits);
        // 正在共用同一串口的设备，不能一台用 4800、另一台用 9600。
        if (_settings.HasValue && _settings.Value != settings && _owners.Count > 0)
            throw new InvalidOperationException("同一 COM 口正在使用不同的串口参数。请先停止该串口下的所有设备，再统一参数。");
        _settings = settings;
        _owners.Add(owner);

        try
        {
            WaitUntil(_recoverUntil, token);
            if (_port is null)
            {
                _port = _createPort(device);
                _port.Open();
            }
            // 低波特率按 3.5 个字符留空闲；高速串口至少留 2 毫秒。
            int gap = Math.Max(2, (int)Math.Ceiling(3.5 * 11 * 1000 / device.BaudRate));
            WaitUntil(_lastTraffic + MillisecondTicks(gap), token);
            _port.DiscardInput();
            byte function = (byte)point.RegisterType;
            byte[] request = ModbusRtuFrame.CreateReadRequest(device.UnitId, function, point.Address);
            try { _port.Write(request, device.WriteTimeoutMilliseconds); }
            catch (TimeoutException ex)
            { throw new TimeoutException($"发送 RTU 读取请求超时（{device.WriteTimeoutMilliseconds} ms）。", ex); }
            token.ThrowIfCancellationRequested();

            // 正常响应 7 字节，异常响应 5 字节。分批收到也共用一次读取超时。
            var timer = Stopwatch.StartNew();
            byte[] header = new byte[3];
            ReadExactly(header, 0, 3, timer, device.ReadTimeoutMilliseconds, token);
            if (header[0] != device.UnitId || (header[1] != function && header[1] != (function | 0x80)))
                throw new InvalidDataException("RTU 响应站号或功能码不一致，请核对站号和接线。");
            bool isError = header[1] == (function | 0x80);
            if (!isError && header[2] != 2)
                throw new InvalidDataException("RTU 响应不是本次要求的一个寄存器。");
            byte[] response = new byte[isError ? 5 : 7];
            header.CopyTo(response, 0);
            ReadExactly(response, 3, response.Length - 3, timer, device.ReadTimeoutMilliseconds, token);
            return ModbusRtuFrame.ReadValue(response, device.UnitId, function);
        }
        catch (Exception ex)
        {
            // 断线、超时或停止都关闭当前句柄；下一轮读取可以重新打开。
            ClosePort();
            _recoverUntil = Stopwatch.GetTimestamp() + MillisecondTicks(device.ReadTimeoutMilliseconds);
            if (token.IsCancellationRequested) throw new OperationCanceledException("RTU 读取已停止。", ex, token);
            if (ex is UnauthorizedAccessException)
                throw new IOException($"无法打开 {device.SerialPortName}，可能被其他软件占用。请先关闭串口调试软件。", ex);
            if (ex is IOException && ex is not InvalidDataException)
                throw new IOException($"{device.SerialPortName} 通信失败，请检查转换器、接线和串口是否存在。{ex.Message}", ex);
            throw;
        }
        finally { _lastTraffic = Stopwatch.GetTimestamp(); }
    }

    /// <summary>ReadCore 调用；把分批收到的字节补齐，每隔最多 50 毫秒检查停止和总超时。</summary>
    private void ReadExactly(byte[] buffer, int offset, int count, Stopwatch timer, int timeout, CancellationToken token)
    {
        int end = offset + count;
        while (offset < end)
        {
            token.ThrowIfCancellationRequested();
            int remaining = timeout - (int)timer.ElapsedMilliseconds;
            if (remaining <= 0) throw new TimeoutException($"等待 RTU 设备响应超时（{timeout} ms）。请检查供电、A/B、站号和串口参数。");
            try { offset += _port!.Read(buffer, offset, end - offset, Math.Min(50, remaining)); }
            catch (TimeoutException) { /* 只是这 50 毫秒没收到，继续检查总超时和停止。 */ }
        }
    }

    // 报文间隔和故障后的等待也能被停止打断。
    private static void WaitUntil(long deadline, CancellationToken token)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            int wait = Math.Clamp((int)Math.Ceiling((deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency), 1, 50);
            if (token.WaitHandle.WaitOne(wait)) token.ThrowIfCancellationRequested();
        }
        token.ThrowIfCancellationRequested();
    }

    private static long MillisecondTicks(int milliseconds) => (long)(milliseconds * (double)Stopwatch.Frequency / 1000);

    // 关闭失败不覆盖原来的通信错误，下一次仍会重新创建串口。
    private void ClosePort()
    {
        IRtuSerialPort? port = _port;
        _port = null;
        try { port?.Dispose(); } catch (Exception) { /* 释放失败不盖掉原来的错误。 */ }
    }
}

// 把实际串口收发隔开，自动检查可以用假串口验证分批收包、超时和排队，不需要碰真实设备。
internal interface IRtuSerialPort : IDisposable
{
    void Open();
    void DiscardInput();
    void Write(byte[] bytes, int timeout);
    int Read(byte[] bytes, int offset, int count, int timeout);
}

internal sealed class RtuSerialPort : IRtuSerialPort
{
    private readonly SerialPort _port;
    internal RtuSerialPort(DeviceInfo device) => _port = new SerialPort(
        SerialConnectionSettings.NormalizePortName(device.SerialPortName), device.BaudRate,
        device.Parity, 8, device.StopBits) { Handshake = Handshake.None };
    public void Open() => _port.Open();
    public void DiscardInput() => _port.DiscardInBuffer();
    public void Write(byte[] bytes, int timeout)
    {
        _port.WriteTimeout = timeout;
        _port.Write(bytes, 0, bytes.Length);
    }
    public int Read(byte[] bytes, int offset, int count, int timeout)
    {
        _port.ReadTimeout = timeout;
        return _port.Read(bytes, offset, count);
    }
    public void Dispose() => _port.Dispose();
}

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Collections.Concurrent;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Communication
{

    /// <summary>
    /// 连接设备，用 Modbus 03/04 读取一个 16 位寄存器，返回 PointReadResult。
    /// 单次测试和连续采集共用；这里只读一次，不循环、不保存历史。
    /// </summary>
    public sealed class ModbusPointReader
    {
        // 这个读取器实际用过哪些串口；结束时只退出自己的使用，不关闭别人的采集。
        private readonly ConcurrentDictionary<string, byte> _serialPorts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 由 AcquisitionViewModel.ReadOnceAsync 或 DeviceSamplingViewModel.RunAsync 调用。
        /// 根据设备配置连接，发送点位读取请求，收到并检查响应后返回数值。
        /// TCP 每次独立连接；RTU 同一 COM 口排队共用。显示、报警和历史由调用方处理。
        /// </summary>
        public async Task<PointReadResult> ReadAsync(
            DeviceInfo device, PointInfo point, CancellationToken cancellationToken = default)
        {
            // 先检查配置，以及点位是否属于这台设备，不合法就不发网络请求。
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(point);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(device.Id);
            if (device.ConnectTimeoutMilliseconds <= 0 ||
                device.ReadTimeoutMilliseconds <= 0 || device.WriteTimeoutMilliseconds <= 0)
                throw new ArgumentException("连接、读取、写入超时都必须大于 0。", nameof(device));
            if (!device.IsEnabled)
                throw new InvalidOperationException("设备已停用，不能读取。");
            if (point.DeviceId != device.Id)
                throw new ArgumentException("采集点不属于当前设备。", nameof(point));
            if (!Enum.IsDefined(point.RegisterType) || !Enum.IsDefined(point.DataType))
                throw new ArgumentException("暂不支持这个采集点的寄存器类型或数据类型。", nameof(point));
            cancellationToken.ThrowIfCancellationRequested();

            // RTU 只换掉收发字节的方式，点位、返回对象和后续采集流程仍沿用原来的。
            if (device.CommunicationMode == DeviceCommunicationMode.Rtu)
            {
                SerialConnectionSettings.Validate(device.SerialPortName, device.BaudRate,
                    device.Parity, device.StopBits, device.UnitId);
                _serialPorts.TryAdd(SerialConnectionSettings.NormalizePortName(device.SerialPortName), 0);
                ushort raw = await ModbusRtuTransport.ReadAsync(this, device, point, cancellationToken).ConfigureAwait(false);
                return BuildResult(point, raw, cancellationToken);
            }
            if (device.CommunicationMode != DeviceCommunicationMode.Tcp)
                throw new ArgumentException("不支持这个连接方式。", nameof(device));
            ArgumentException.ThrowIfNullOrWhiteSpace(device.Host);
            if (device.Port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(device.Port), "端口必须在 1 到 65535 之间。");

            // 每次读取独立创建连接，using 保证成功、失败或取消后都关闭。
            using TcpClient client = new() { NoDelay = true };
            try
            {
                // 连上设备，再取出用于收发字节的网络流。
                await ConnectAsync(client, device, cancellationToken).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();

                // 每条连接只发一次请求，所以报文编号固定用 1；它不是数据库设备编号。
                const ushort transactionId = 1;
                // 寄存器类型决定用 03 还是 04，Address 决定读哪个位置。
                byte functionCode = (byte)point.RegisterType;
                byte[] request = CreateRequest(transactionId, device.UnitId, functionCode, point.Address);
                // request 是待发送的字节，调用发送方法才真正发到设备。
                await SendRequestAsync(stream, request, device, cancellationToken).ConfigureAwait(false);
                // 收齐并检查响应，取出一个 16 位原始值。
                ushort rawValue = await ReadResponseAsync(
                    stream, device, transactionId, functionCode, cancellationToken).ConfigureAwait(false);

                return BuildResult(point, rawValue, cancellationToken);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                // 停止时网络也可能抛其他异常，统一按取消处理，不误报故障。
                throw new OperationCanceledException("本次读取已取消。", ex, cancellationToken);
            }
        }

        /// <summary>采集循环的 finally、单次读取的 finally 调用，退出本读取器占用的串口。</summary>
        public async Task CloseAsync()
        {
            foreach (string port in _serialPorts.Keys)
            {
                await ModbusRtuTransport.ReleaseAsync(port, this).ConfigureAwait(false);
                _serialPorts.TryRemove(port, out _);
            }
        }

        /// <summary>两种连接读到原始值后都调用这里，按点位类型解释整数并附上时间。</summary>
        private static PointReadResult BuildResult(PointInfo point, ushort raw, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int value = point.DataType == PointDataType.Int16 ? unchecked((short)raw) : raw;
            return new PointReadResult(point, raw, value, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// 由本类 ReadAsync 调用。
        /// 按设备地址和端口建立连接，同时处理手动取消和连接超时。
        /// </summary>
        private static async Task ConnectAsync(
            TcpClient client, DeviceInfo device, CancellationToken cancellationToken)
        {
            // 手动取消或超时，任一发生都结束等待。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // 超时只取消本次连接，不会取消整台设备的采集循环。
            timeout.CancelAfter(device.ConnectTimeoutMilliseconds);
            try
            {
                await client.ConnectAsync(device.Host.Trim(), device.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // 没收到手动停止信号，说明是连接超时。
                throw new TimeoutException($"连接设备超时（{device.ConnectTimeoutMilliseconds} ms）。", ex);
            }
        }

        /// <summary>
        /// 由本类 ReadAsync 调用。
        /// 按 Modbus TCP 格式，把报文编号、Unit ID、功能码和地址组装成请求字节。
        /// </summary>
        private static byte[] CreateRequest(ushort transactionId, byte unitId, byte functionCode, ushort address)
        {
            // 共 12 字节：前 7 字节是报文头，后面是功能码、地址和读取数量。
            byte[] request = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0); // 协议编号：Modbus 为 0。
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), 6); // 后面还剩 6 字节。
            // Unit ID 是协议中的目标编号，不是数据库设备 Id。
            request[6] = unitId;
            request[7] = functionCode;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), address);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10, 2), 1); // 本次只读取一个寄存器。
            return request;
        }

        /// <summary>
        /// 由本类 ReadAsync 调用。
        /// 把请求字节发给设备；这里的“写入”是发读取请求，不是修改寄存器。
        /// </summary>
        private static async Task SendRequestAsync(
            NetworkStream stream, byte[] request, DeviceInfo device, CancellationToken cancellationToken)
        {
            // 为本次发送单独计时，同时监听手动停止。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(device.WriteTimeoutMilliseconds);
            try
            {
                // 异步发送要传入超时 token，不能只设置 WriteTimeout 属性。
                await stream.WriteAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"发送读取请求超时（{device.WriteTimeoutMilliseconds} ms）。", ex);
            }
        }

        /// <summary>
        /// 由本类 ReadAsync 调用。
        /// 收齐并检查响应，取出一个 16 位原始值；有无符号的转换由 ReadAsync 处理。
        /// </summary>
        private static async Task<ushort> ReadResponseAsync(
            NetworkStream stream, DeviceInfo device, ushort transactionId,
            byte functionCode, CancellationToken cancellationToken)
        {
            // 报文头和正文共用一次超时，分几次收到也不会重新计时。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(device.ReadTimeoutMilliseconds);
            try
            {
                // 先收固定的 7 字节报文头，再根据其中的长度收正文。
                byte[] header = new byte[7];
                // TCP 可能分几次送来，ReadExactlyAsync 会一直读到指定字节数收齐。
                await stream.ReadExactlyAsync(header.AsMemory(), timeout.Token).ConfigureAwait(false);

                // 核对报文编号、协议编号和 Unit ID，确认是本次请求的响应。
                if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2)) != transactionId)
                    throw new InvalidDataException("响应报文编号与请求不一致，不能把它当成本次读数。");
                if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)) != 0)
                    throw new InvalidDataException("响应不是有效的 Modbus TCP 报文。");
                if (header[6] != device.UnitId)
                    throw new InvalidDataException("响应的 Unit ID 与设备配置不一致，请检查 Unit ID。");

                ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));

                // 长度含头里已收到的 Unit ID；先检查范围，再按剩余长度创建正文数组。
                if (length is < 2 or > 254)
                    throw new InvalidDataException("响应报文长度不正确。");
                byte[] body = new byte[length - 1];
                // 扣掉 Unit ID 的 1 字节，其余全部收到 body 中。
                await stream.ReadExactlyAsync(body.AsMemory(), timeout.Token).ConfigureAwait(false);

                // 功能码带 0x80 表示设备返回错误，后一个字节是错误编号。
                if (body[0] == (functionCode | 0x80))
                {
                    if (body.Length != 2)
                        throw new InvalidDataException("设备返回的异常报文长度不正确。");
                    throw new InvalidOperationException(
                        $"设备返回 Modbus 异常 0x{body[1]:X2}：{DescribeException(body[1])}。");
                }
                if (body[0] != functionCode)
                    throw new InvalidDataException("响应功能码与请求不一致。");
                // 正常正文应是：功能码、字节数 2、两个数值字节。
                if (body.Length != 4 || body[1] != 2)
                    throw new InvalidDataException("响应数据长度不正确，本次应返回一个 16 位寄存器。");

                // 数值从下标 2 开始，高位字节在前，按这个顺序还原整数。
                return BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2, 2));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"等待设备响应超时（{device.ReadTimeoutMilliseconds} ms）。", ex);
            }
            catch (EndOfStreamException ex)
            {
                throw new IOException("设备提前关闭了连接，未收到完整的 Modbus 响应。", ex);
            }
        }

        /// <summary>
        /// 由 ReadResponseAsync 收到设备返回的错误编号时调用。
        /// 把设备返回的错误编号换成能看懂的原因。
        /// </summary>
        private static string DescribeException(byte code) => code switch
        {
            0x01 => "设备不支持这个读取功能",
            0x02 => "寄存器地址不可用，请核对地址和寄存器类型",
            0x03 => "设备不接受请求中的参数",
            0x04 => "设备处理请求时出错",
            0x05 => "设备已接受请求，但尚未处理完成",
            0x06 => "设备正忙",
            0x08 => "设备存储校验出错",
            0x0A => "网关通路不可用",
            0x0B => "网关后的设备没有响应",
            _ => "设备拒绝了本次读取"
        };
    }
}

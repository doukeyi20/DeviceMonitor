using System.Buffers.Binary;
using System.Net.Sockets;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Communication
{

    /// <summary>
    /// 按一台设备配置读取一个点：连接 TCP、发 Modbus 03/04 请求、收齐并校验响应，返回 PointReadResult。
    /// 单次测试和连续采集共用它；只支持一个 16 位寄存器，不写数据库，也不负责循环。
    /// </summary>
    public sealed class ModbusPointReader
    {
        /// <summary>
        /// 单次读取由 AcquisitionViewModel.ReadOnceAsync 调用；连续采集由 DeviceSamplingViewModel.RunAsync 对每个点调用。
        /// 连接设备 → CreateRequest 组报文 → SendRequestAsync 发送 → ReadResponseAsync 收响应 → 返回 PointReadResult。
        /// 每次调用有独立的临时连接，结束就释放；不操作数据库，也不保存历史读数。
        /// </summary>
        public async Task<PointReadResult> ReadAsync(
            DeviceInfo device, PointInfo point, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(point);
            ArgumentException.ThrowIfNullOrWhiteSpace(device.Host);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(device.Id);
            if (device.Port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(device.Port), "端口必须在 1 到 65535 之间。");
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

            // 本方法每次只读一个点；采集间隔由 DeviceSamplingViewModel.RunAsync 在一轮读完后使用。
            // 客户端放在方法里，每次读取都有自己的连接，多个设备之间不会共用它。
            // using 保证成功、失败、超时、取消时，连接都会释放。
            using TcpClient client = new() { NoDelay = true };
            try
            {
                await ConnectAsync(client, device, cancellationToken).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();

                // 每条独立连接只发一条请求，因此本次报文编号使用 1。
                // 这是协议中匹配请求和响应的编号，不是数据库里的设备 / 点位编号。
                const ushort transactionId = 1;
                byte functionCode = (byte)point.RegisterType;
                byte[] request = CreateRequest(transactionId, device.UnitId, functionCode, point.Address);
                await SendRequestAsync(stream, request, device, cancellationToken).ConfigureAwait(false);
                ushort rawValue = await ReadResponseAsync(
                    stream, device, transactionId, functionCode, cancellationToken).ConfigureAwait(false);

                // 相同的两个字节可以解释成无符号数，也可以解释成有符号数。
                // 例如原始值 65535（0xFFFF），按 Int16 解释就是 -1，不是读取失败。
                int value = point.DataType == PointDataType.Int16
                    ? unchecked((short)rawValue)
                    : rawValue;
                cancellationToken.ThrowIfCancellationRequested();
                return new PointReadResult(point, rawValue, value, DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                // 窗口关闭或用户取消时，有的网络操作也可能抛 IOException。
                // 统一当作取消处理，不误报为设备故障。
                throw new OperationCanceledException("本次读取已取消。", ex, cancellationToken);
            }
        }

        /// <summary>
        /// 由 ReadAsync 调用，使用设备的 Host、Port 和连接超时建立 TCP 连接。
        /// 用户取消时结束等待；仅连接超时则抛 TimeoutException，让外层显示对应原因。
        /// </summary>
        private static async Task ConnectAsync(
            TcpClient client, DeviceInfo device, CancellationToken cancellationToken)
        {
            // 同时监听“用户取消”和“到达连接超时”，任何一个发生都停止等待。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(device.ConnectTimeoutMilliseconds);
            try
            {
                await client.ConnectAsync(device.Host.Trim(), device.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"连接设备超时（{device.ConnectTimeoutMilliseconds} ms）。", ex);
            }
        }

        /// <summary>
        /// 由 ReadAsync 在连接成功后调用。
        /// 将报文编号、Unit ID、功能码和寄存器地址组装成 Modbus TCP 字节数组，交给 SendRequestAsync；不是 SQL，也不是设备表的 Id。
        /// </summary>
        private static byte[] CreateRequest(ushort transactionId, byte unitId, byte functionCode, ushort address)
        {
            // 这里按 Modbus TCP 的固定格式组装 12 字节请求，不是随意拼字符串。
            // 前 7 字节是报文头，后面依次是功能码、协议地址、读取数量。
            byte[] request = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0); // 协议编号：Modbus 为 0。
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), 6); // 后面还剩 6 字节。
            request[6] = unitId;
            request[7] = functionCode;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), address);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10, 2), 1); // 本次只读取一个寄存器。
            return request;
        }

        /// <summary>
        /// 由 ReadAsync 调用，把 CreateRequest 生成的字节发送到 TCP 连接。
        /// 这里的“写入”是发送读取请求，不是写设备寄存器；等待受写入超时和取消控制。
        /// </summary>
        private static async Task SendRequestAsync(
            NetworkStream stream, byte[] request, DeviceInfo device, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(device.WriteTimeoutMilliseconds);
            try
            {
                // “写入”指把读取请求发到 TCP 连接，并不是修改设备寄存器。
                // 异步写入不能只设置 WriteTimeout 属性，需要把超时 token 传进去。
                await stream.WriteAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"发送读取请求超时（{device.WriteTimeoutMilliseconds} ms）。", ex);
            }
        }

        /// <summary>
        /// 由 ReadAsync 在发送请求后调用。
        /// 收齐响应、核对报文编号和格式，再取出一个 16 位原始值返回；解释成有符号或无符号数由 ReadAsync 处理。
        /// </summary>
        private static async Task<ushort> ReadResponseAsync(
            NetworkStream stream, DeviceInfo device, ushort transactionId,
            byte functionCode, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(device.ReadTimeoutMilliseconds);
            try
            {
                byte[] header = new byte[7];
                // TCP 可能把一条响应分几次送来，ReadExactlyAsync 会等到所需字节收齐。
                // 报文头和正文使用同一个超时，总等待时间不会因为分包而不断延长。
                await stream.ReadExactlyAsync(header.AsMemory(), timeout.Token).ConfigureAwait(false);

                if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2)) != transactionId)
                    throw new InvalidDataException("响应报文编号与请求不一致，不能把它当成本次读数。");
                if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)) != 0)
                    throw new InvalidDataException("响应不是有效的 Modbus TCP 报文。");
                if (header[6] != device.UnitId)
                    throw new InvalidDataException("响应的 Unit ID 与设备配置不一致，请检查 Unit ID。");

                ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                // 长度包含已读到的 Unit ID；后面的正文最多 253 字节。
                // 先检查长度，再创建数组，不能直接相信对方报文中的数字。
                if (length is < 2 or > 254)
                    throw new InvalidDataException("响应报文长度不正确。");
                byte[] body = new byte[length - 1];
                await stream.ReadExactlyAsync(body.AsMemory(), timeout.Token).ConfigureAwait(false);

                if (body[0] == (functionCode | 0x80))
                {
                    if (body.Length != 2)
                        throw new InvalidDataException("设备返回的异常报文长度不正确。");
                    throw new InvalidOperationException(
                        $"设备返回 Modbus 异常 0x{body[1]:X2}：{DescribeException(body[1])}。");
                }
                if (body[0] != functionCode)
                    throw new InvalidDataException("响应功能码与请求不一致。");
                // 本次请求一个寄存器：正文应当是功能码、字节数 2、两个数据字节。
                if (body.Length != 4 || body[1] != 2)
                    throw new InvalidDataException("响应数据长度不正确，本次应返回一个 16 位寄存器。");

                // Modbus 一个寄存器的高位字节在前，用这个方法还原成整数。
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
        /// 由 ReadResponseAsync 收到 Modbus 异常响应时调用。
        /// 把设备返回的错误编号转成直白原因，拼进异常消息，最后由页面或读数行显示。
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

// 验证项目功能及异常处理的回归检查。
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DeviceMonitor.Core.Communication;
using DeviceMonitor.Core.Models;

internal static class TcpLibraryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await CheckPersistentAsync(check);
        PointInfo[] batchPoints =
        [
            new(2, 1, "温度", RegisterType.Holding, 11, PointDataType.Int16, scale: 0.1, unit: "℃"),
            new(1, 1, "湿度", RegisterType.Holding, 10, PointDataType.UInt16, scale: 0.1, unit: "%RH")
        ];
        int batchRequests = 0;
        var batch = await ReadPointsAsync(request =>
        {
            batchRequests++;
            check(request[7] == 3 && request[9] == 10 && request[11] == 2, "TCP 批量请求按配置使用起始地址10和数量2");
            return ResponseMany(request, [486, 0xFF9F]);
        }, batchPoints, splitDelay: 2);
        check(batchRequests == 1 && batch[0].Point.Id == 2 && Math.Abs(batch[0].Value + 9.7) < 1e-9
            && batch[1].Value == 48.6, "一条TCP请求返回多点，输入顺序不同也按地址正确映射和换算");
        check(batch[0].ReadAt == batch[1].ReadAt, "同批 TCP 点位读取时间一致");

        var maxPoints = Enumerable.Range(0, 125).Select(i =>
            new PointInfo(i + 1, 1, $"点{i}", RegisterType.Input, (ushort)i, PointDataType.UInt16)).ToArray();
        var maximum = await ReadPointsAsync(r => ResponseMany(r, Enumerable.Range(0, 125).Select(i => (ushort)i).ToArray()), maxPoints);
        check(maximum.Count == 125 && maximum[124].Value == 124, "TCP 04批量125个寄存器，允许259字节正常响应");
        try
        {
            await ReadPointsAsync(r => ResponseMany(r, [486]), batchPoints);
            throw new Exception("数量不匹配应当失败");
        }
        catch (IOException) { check(true, "批量响应数量不足时整批拒绝，不伪造第二个点"); }
        try
        {
            await ReadPointsAsync(r => [r[0], r[1], 0, 0, 0, 3, r[6], (byte)(r[7] | 0x80), 2], batchPoints);
            throw new Exception("批量异常响应应当失败");
        }
        catch (InvalidOperationException ex) { check(ex.Message.Contains("0x02"), "批量读取仍接受合法异常帧并提示设备错误"); }
        try
        {
            await ReadPointsAsync(_ => null, batchPoints, timeout: 100);
            throw new Exception("批量静默应当超时");
        }
        catch (TimeoutException) { check(true, "批量无响应由库的读取超时退出"); }

        PointReadResult humidity = await ReadAsync(request =>
        {
            check(request.Length == 12 && request[2] == 0 && request[3] == 0 && request[5] == 6,
                "库生成 TCP MBAP 头及长度");
            check(request[6] == 255 && request[7] == 3 && request[8] == 0 && request[9] == 0
                && request[10] == 0 && request[11] == 1, "库生成站号 255、03、地址 0、数量 1");
            return Response(request, 486);
        }, splitDelay: 2, scale: 0.1, unit: "%RH");
        check(humidity.RawValue == 486 && humidity.Value == 48.6 && humidity.ValueText.Contains("%RH"),
            "TCP 分批响应经库解析后应用倍率和单位");

        PointReadResult temperature = await ReadAsync(request =>
        {
            check(request[7] == 4 && request[9] == 1, "输入寄存器调用库的 04 读取方法");
            return Response(request, 0xFF9F);
        }, RegisterType.Input, PointDataType.Int16, address: 1, scale: 0.1, unit: "℃");
        check(temperature.RawValue == 0xFF9F && temperature.DecodedValue == -97 && Math.Abs(temperature.Value + 9.7) < 1e-9,
            "TCP 负温度先按 Int16 解释，再换算为 -9.7℃");
        check((await ReadAsync(r => Response(r, ushort.MaxValue))).Value == ushort.MaxValue, "TCP UInt16 上限不变负数");

        await RejectAsync<IOException>(r => { var b = Response(r, 42); b[1]++; return b; }, "库拒绝事务号不匹配", check);
        await RejectAsync<IOException>(r => { var b = Response(r, 42); b[6]--; return b; }, "库拒绝站号不匹配", check);
        await RejectAsync<IOException>(r => { var b = Response(r, 42); b[7] = 4; return b; }, "库拒绝功能码不匹配", check);
        await RejectAsync<FormatException>(r => { var b = Response(r, 42); b[8] = 4; return b; }, "库拒绝数据字节数不匹配", check);
        await RejectAsync<FormatException>(r =>
        {
            var b = Response(r, 42); b[5] = 6; b[8] = 3; return [.. b, 0];
        }, "奇数字节数不能假装是一个寄存器", check);
        await RejectAsync<IOException>(r =>
        {
            byte[] b = new byte[261]; r.AsSpan(0, 7).CopyTo(b);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), 65535);
            return b;
        }, "库收到声明长度但连接提前结束时拒绝响应", check);
        await RejectAsync<IOException>(r => Response(r, 42)[..9], "断开前只回半帧不会返回假成功", check);

        try
        {
            await ReadAsync(r => [r[0], r[1], 0, 0, 0, 3, r[6], (byte)(r[7] | 0x80), 2]);
            throw new Exception("应当返回设备异常");
        }
        catch (InvalidOperationException ex)
        {
            check(ex.Message.Contains("0x02") && ex.Message.Contains("地址"), "库解析异常帧，项目提示地址错误");
        }

        PointReadResult first = await ReadAsync(r => [.. Response(r, 486), .. Response(r, 123)]);
        check(first.Value == 486, "TCP 合并到达时只取当前响应，随后关闭独立连接");
        await RejectAsync<TimeoutException>(_ => null, "设备静默触发接收超时", check, timeout: 100);
        check((await ReadAsync(r => Response(r, 42), timeout: 100, splitDelay: 30)).Value == 42,
            "原生读取超时限制每次底层等待，不再强制整帧共用总期限");

        using var stop = new CancellationTokenSource(50);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await ReadAsync(_ => null, timeout: 350, token: stop.Token);
            throw new Exception("应该取消");
        }
        catch (OperationCanceledException)
        {
            check(elapsed.ElapsedMilliseconds >= 200 && elapsed.ElapsedMilliseconds < 1500,
                "停止等待当前库读取超时后退出，不强制关闭也不遗留后台任务");
        }
        PointReadResult recovered = await ReadAsync(r => Response(r, 99));
        check(recovered.Value == 99, "取消后下一次读取可重新连接");
        PointReadResult[] parallel = await Task.WhenAll(ReadAsync(r => Response(r, 1)), ReadAsync(r => Response(r, 2)));
        check(parallel[0].Value == 1 && parallel[1].Value == 2, "多 TCP 读取各自独立，不混读数");
    }

    private static async Task CheckPersistentAsync(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(10000);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port, accepts = 0, requestNumber = 0;
        Task server = ServeAsync();
        var device = new DeviceInfo(1, "长连接模拟", "127.0.0.1", port, 1, 100, 1000, 150, 500, true);
        var point = new PointInfo(1, 1, "点", RegisterType.Holding, 0, PointDataType.UInt16);
        var reader = new ModbusPointReader(device, new[] { point });
        try
        {
            await reader.ReadAsync(); await reader.ReadAsync();
            check(accepts == 1 && requestNumber == 2, "TCP 多轮读取只建一次连接，正常时不关闭");
            try { await reader.ReadAsync(); throw new Exception("应当返回忙"); }
            catch (ModbusDeviceException ex) { check(ex.Code == 6 && ModbusPointReader.CanRetry(ex), "TCP 设备忙是可重试响应，不是断线"); }
            await reader.ReadAsync();
            check(accepts == 1, "设备忙后仍复用原 TCP 连接");
            try { await reader.ReadAsync(); throw new Exception("应当地址错误"); }
            catch (ModbusDeviceException ex) { check(!ModbusPointReader.CanRetry(ex) && ex.Code == 2, "TCP 地址错误保留原始码，不进入通信故障重试"); }
            await RejectReadAsync<IOException>();
            await reader.ReadAsync();
            check(accepts == 2, "对端断开后，下一次读取重新建立 TCP 连接");
            await RejectReadAsync<TimeoutException>();
            await reader.ReadAsync();
            check(accepts == 3, "读取超时清掉旧连接，下一次使用新连接恢复");

            await reader.ReadAsync();
            await RejectReadAsync<IOException>();
            var recovered = await reader.ReadAsync();
            check(accepts == 4 && recovered[0].Value == 12, "长连接残留旧事务响应时拒绝本轮，重连后恢复，不伪造读数");
            await reader.DisposeAsync(); await reader.DisposeAsync();
            await RejectReadAsync<ObjectDisposedException>();
            check(!ModbusPointReader.CanRetry(new ArgumentException("配置错")), "普通参数错误不按断线无限重试");
        }
        finally
        {
            await reader.DisposeAsync();
            deadline.Cancel();
            try { await server; } catch (OperationCanceledException) { }
        }

        async Task RejectReadAsync<T>() where T : Exception
        {
            try { await reader.ReadAsync(); }
            catch (T) { check(true, typeof(T).Name + " 被正确识别"); return; }
            throw new Exception("没有拒绝 " + typeof(T).Name);
        }
        async Task ServeAsync()
        {
            while (!deadline.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(deadline.Token);
                Interlocked.Increment(ref accepts);
                using NetworkStream stream = client.GetStream();
                while (!deadline.IsCancellationRequested)
                {
                    byte[] request = new byte[12];
                    try { await stream.ReadExactlyAsync(request, deadline.Token); }
                    catch (EndOfStreamException) { break; }
                    catch (IOException) { break; }
                    int number = Interlocked.Increment(ref requestNumber);
                    if (number == 6) break;
                    if (number == 8)
                    {
                        int closed = await stream.ReadAsync(new byte[1], deadline.Token);
                        if (closed != 0) throw new Exception("静默后不应在原连接上再收到请求。");
                        break;
                    }
                    byte[] response = number is 3 or 5
                        ? [request[0], request[1], 0, 0, 0, 3, request[6], (byte)(request[7] | 0x80), (byte)(number == 3 ? 6 : 2)]
                        : Response(request, (ushort)number);
                    if (number == 10) response = [.. response, .. response];
                    try { await stream.WriteAsync(response, deadline.Token); }
                    catch (IOException) { break; }
                }
            }
        }
    }

    private static async Task RejectAsync<T>(Func<byte[], byte[]?> response, string title, Action<bool, string> check,
        int timeout = 500, int splitDelay = 0) where T : Exception
    {
        try { await ReadAsync(response, timeout: timeout, splitDelay: splitDelay); }
        catch (T) { check(true, title); return; }
        throw new Exception("应当拒绝：" + title);
    }

    private static byte[] Response(byte[] request, ushort value) =>
        [request[0], request[1], 0, 0, 0, 5, request[6], request[7], 2, (byte)(value >> 8), (byte)value];

    private static byte[] ResponseMany(byte[] request, ushort[] values)
    {
        byte[] response = new byte[9 + values.Length * 2];
        request.AsSpan(0, 4).CopyTo(response);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), (ushort)(3 + values.Length * 2));
        response[6] = request[6]; response[7] = request[7]; response[8] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9 + i * 2, 2), values[i]);
        return response;
    }

    private static async Task<PointReadResult> ReadAsync(Func<byte[], byte[]?> makeResponse,
        RegisterType type = RegisterType.Holding, PointDataType dataType = PointDataType.UInt16,
        ushort address = 0, int timeout = 500, int splitDelay = 0, double scale = 1, string unit = "",
        CancellationToken token = default)
    {
        var point = new PointInfo(1, 1, "测试点", type, address, dataType, scale: scale, unit: unit);
        return (await ReadPointsAsync(makeResponse, [point], timeout, splitDelay, token))[0];
    }

    private static async Task<IReadOnlyList<PointReadResult>> ReadPointsAsync(Func<byte[], byte[]?> makeResponse,
        IReadOnlyList<PointInfo> points, int timeout = 500, int splitDelay = 0, CancellationToken token = default)
    {
        using var deadline = new CancellationTokenSource(5000);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = ServeAsync();
        var device = new DeviceInfo(1, "本机模拟", "127.0.0.1", port, 255, 1000, 1000, timeout, 500, true);
        var reader = new ModbusPointReader(device, points);
        try { return await reader.ReadAsync(token); }
        finally
        {
            await reader.DisposeAsync();
            deadline.Cancel();
            try { await server; } catch (OperationCanceledException) { }
        }

        async Task ServeAsync()
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(deadline.Token);
            using NetworkStream stream = client.GetStream();
            byte[] request = new byte[12];
            await stream.ReadExactlyAsync(request, deadline.Token);
            byte[]? response = makeResponse(request);
            if (response is null)
            {
                int count = await stream.ReadAsync(new byte[1], deadline.Token);
                if (count != 0) throw new Exception("静默测试不应收到第二条请求。");
                return;
            }
            try
            {
                if (splitDelay == 0) await stream.WriteAsync(response, deadline.Token);
                else foreach (byte item in response)
                {
                    await stream.WriteAsync(new byte[] { item }, deadline.Token);
                    await Task.Delay(splitDelay, deadline.Token);
                }
            }
            catch (IOException) { /* 客户端拒绝响应或取消后，会主动断开。 */ }
        }
    }
}

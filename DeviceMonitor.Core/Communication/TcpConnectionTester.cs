// 执行单次 TCP 连接及连接超时控制。
using System.Net.Sockets;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Communication;

public static class TcpConnectionTester
{
    /// <summary>测试 TCP 端口连接，不执行 Modbus 读取。</summary>
    public static async Task TestAsync(DeviceInfo device, CancellationToken cancellationToken = default)
    {
        using var tcp = new TcpClient();
        await ConnectAsync(tcp, device, cancellationToken).ConfigureAwait(false);
    }

    internal static void Validate(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.CommunicationMode != DeviceCommunicationMode.Tcp)
            throw new ArgumentException("这里只测试 TCP 端口；RTU 请用读取一次验证。", nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Host);
        if (device.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(device.Port), "端口必须在 1 到 65535 之间。");
        if (device.ConnectTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(device.ConnectTimeoutMilliseconds), "连接超时必须大于 0。");
    }

    /// <summary>TestAsync 和 ModbusPointReader.ConnectAsync 共用：一次 TCP 连接的限时。</summary>
    internal static async Task ConnectAsync(TcpClient tcp, DeviceInfo device, CancellationToken token)
    {
        Validate(device);

        // 关联用户停止信号，并为本次连接单独设置超时。
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);

        connectionTimeout.CancelAfter(device.ConnectTimeoutMilliseconds);
        try
        {
            await tcp.ConnectAsync(device.Host.Trim(), device.Port, connectionTimeout.Token).ConfigureAwait(false);
        }
        // 外部未取消而连接被取消，按连接超时处理；用户取消则保留原取消异常。
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"连接设备超时（{device.ConnectTimeoutMilliseconds} ms）。", ex);
        }
    }
}

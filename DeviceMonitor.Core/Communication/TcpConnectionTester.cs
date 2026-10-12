using System.Net.Sockets;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Communication
{

    /// <summary>
    /// 只测试 Host/Port 能否建立 TCP 连接，结束就释放连接；不发送 Modbus 报文。
    /// 设备管理页的测试命令调用它；TCP 成功不等于 Unit ID/寄存器地址正确。
    /// </summary>
    public sealed class TcpConnectionTester
    {
        /// <summary>
        /// 由 DeviceManagementViewModel.TestConnectionAsync 调用，传入选中设备的配置。
        /// 只尝试连接 Host 和 Port；正常返回就是 TCP 连接成功，失败或超时抛出异常，结束后释放连接。不会发送 Modbus 请求。
        /// </summary>
        public async Task TestAsync(DeviceInfo device, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentException.ThrowIfNullOrWhiteSpace(device.Host);

            if (device.Port < 1 || device.Port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(device.Port),
                    "端口必须在 1 到 65535 之间。");
            }

            if (device.ConnectTimeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(device.ConnectTimeoutMilliseconds),
                    "连接超时必须大于 0。");
            }

            // using 保证正常结束或发生异常时，临时 TCP 连接都会关闭。
            using TcpClient client = new();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // ConnectAsync 不读取设备的超时属性，需要明确安排到期取消。
            timeout.CancelAfter(device.ConnectTimeoutMilliseconds);

            try
            {
                await client.ConnectAsync(device.Host, device.Port, timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 点击“取消测试”或退出登录时，保留“用户取消”，不要误报成连接超时。
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
            {
                // 将“到期取消”转成更易理解的超时异常，供 ViewModel 区分提示。
                throw new TimeoutException(
                    $"TCP 连接超时（{device.ConnectTimeoutMilliseconds} ms）。", ex);
            }
        }
    }
}

using System.Net.Sockets;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Communication
{

    /// <summary>
    /// 测试设备的地址和端口能否连通，不发送 Modbus 请求。
    /// TCP 连通不代表 Unit ID 和寄存器地址正确。
    /// </summary>
    public sealed class TcpConnectionTester
    {
        /// <summary>
        /// 由 DeviceManagementViewModel.TestConnectionAsync 调用。
        /// 尝试连接设备，失败或超时就抛异常；测试结束后关闭连接。
        /// </summary>
        public async Task TestAsync(DeviceInfo device, CancellationToken cancellationToken = default)
        {
            // 先检查地址、端口和超时设置。
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

            // using 保证退出方法时关闭连接；timeout 同时响应手动取消和超时。
            using TcpClient client = new();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 到设定时间自动取消等待，不能只把超时数字放在配置里。
            timeout.CancelAfter(device.ConnectTimeoutMilliseconds);

            try
            {
                await client.ConnectAsync(device.Host, device.Port, timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 手动取消要原样传出去，不误报为超时。
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
            {
                // 不是手动取消，而是时间到了，改为超时异常。
                throw new TimeoutException(
                    $"TCP 连接超时（{device.ConnectTimeoutMilliseconds} ms）。", ex);
            }
        }
    }
}

using System.Net.Sockets;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Communication
{
    /// <summary>
    /// 根据设备配置尝试建立一次 TCP 连接，测试结束后立即释放连接。
    /// 只检查主机与端口能否连接，不读写数据库，也不验证 Modbus 协议。
    /// </summary>
    public sealed class TcpConnectionTester
    {
        /// <summary>
        /// 正常返回表示连接成功；连接失败或超时则抛出异常，由调用者显示结果。
        /// </summary>
        public async Task TestAsync(DeviceInfo device)
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
            using CancellationTokenSource timeout = new();

            // ConnectAsync 不读取设备的超时属性，需要明确安排到期取消。
            timeout.CancelAfter(device.ConnectTimeoutMilliseconds);

            try
            {
                await client.ConnectAsync(device.Host, device.Port, timeout.Token);
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

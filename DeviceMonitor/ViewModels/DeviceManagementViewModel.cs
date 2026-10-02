using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Core.Communication;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;

namespace DeviceMonitor.ViewModels
{
    public class DeviceManagementViewModel: PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;

        // 通信对象只负责连接测试；不需要数据库路径，也不操作设备表。
        private readonly TcpConnectionTester _connectionTester = new();

        private bool _isTestingConnection;
        public bool IsTestingConnection
        {
            get => _isTestingConnection;
            private set => SetProperty(ref _isTestingConnection, value);
        }

        private string _connectionTestMessage = "尚未进行连接测试。";
        public string ConnectionTestMessage
        {
            get => _connectionTestMessage;
            private set => SetProperty(ref _connectionTestMessage, value);
        }

        // 界面绑定这个集合；增加或清空其中的项目时，界面会更新。
        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        public DeviceManagementViewModel(UserAccount currentUser, DeviceStore deviceStore) : base("设备管理")
        {
            _currentUser = currentUser
                            ?? throw new ArgumentNullException(nameof(currentUser));
            _deviceStore = deviceStore
                ?? throw new ArgumentNullException(nameof(deviceStore));

            if (_currentUser.Role != UserRole.Administrator)
            {
                throw new UnauthorizedAccessException("只有管理员可以管理设备。");
            }

            // 页面对象创建时，先读取数据库中已有的设备。
            RefreshDevices();
        }

        /// <summary>
        /// 使用选中设备的配置做一次 TCP 测试，并把结果交给界面显示。
        /// 不更改设备启停状态，也不发送 Modbus 报文。
        /// </summary>
        public async Task TestConnectionAsync(DeviceInfo device)
        {
            ArgumentNullException.ThrowIfNull(device);

            if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有已启用的管理员可以测试设备连接。");
            }

            // 不只依赖按钮禁用：方法本身也避免同时启动多次测试。
            if (IsTestingConnection)
            {
                return;
            }

            IsTestingConnection = true;
            string target = $"{device.Name}（{device.Host}:{device.Port}）";

            try
            {
                ConnectionTestMessage = $"正在测试 {target}，连接超时：" +
                    $"{device.ConnectTimeoutMilliseconds} ms…";

                // await 等待真正的网络连接；完成后继续更新提示，不会卡住界面。
                await _connectionTester.TestAsync(device);

                ConnectionTestMessage = $"TCP 连接成功：{target}。测试连接已关闭。" +
                    "这只说明该端口可连接，尚未验证 Modbus 通信。";
            }
            catch (TimeoutException)
            {
                ConnectionTestMessage = $"TCP 连接超时：{target}。" +
                    $"在 {device.ConnectTimeoutMilliseconds} ms 内未能建立连接，" +
                    "请检查地址、端口、网络和防火墙。";
            }
            catch (SocketException ex)
            {
                string reason = ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "连接被拒绝，请检查对方是否已启动服务、监听端口是否正确。",
                    SocketError.HostNotFound => "无法解析主机地址，请检查主机名。",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                        "网络或主机不可达，请检查网络连接。",
                    _ => ex.Message
                };

                ConnectionTestMessage = $"TCP 连接失败：{target}。{reason}";
            }
            catch (ArgumentException ex)
            {
                ConnectionTestMessage = $"设备连接配置不正确：{ex.Message}";
            }
            catch (Exception ex)
            {
                ConnectionTestMessage = $"测试 {target} 时发生错误：{ex.Message}";
            }
            finally
            {
                // 无论成功还是失败，都结束“测试中”状态，让按钮恢复可用。
                IsTestingConnection = false;
            }
        }

        /// <summary>
        /// 刷新设备列表，更新编辑或启用修改后的实时状态
        /// 之所以在viewmodel提供方法，是因为view后台只写逻辑处理方法，界面的数据提供仍由viewmodel提供。
        /// </summary>
        public void RefreshDevices()
        {
            // 先查询成功，再替换界面列表；查询失败时不清空原列表。
            var latestDevices = _deviceStore.GetDevices(_currentUser);

            Devices.Clear();
            foreach (DeviceInfo device in latestDevices)
            {
                Devices.Add(device);
            }
        }

        /// <summary>
        /// 新增一台设备；成功后重新读取列表。
        /// </summary>
        public bool TryAddDevice(DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            // 当前登录用户由这个 ViewModel 保存，界面不必再传一次。
            bool added = _deviceStore.TryAddDevice(_currentUser, input);

            if (added)
            {
                RefreshDevices(); // 数据库多了一行，让表格也显示出来。
            }

            return added; // false 表示设备名称重复。
        }

        /// <summary>
        /// 修改一台已保存的设备；数据库修改成功后再刷新界面列表
        /// </summary>
        public bool TryUpdateDevice(long deviceId, DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            // 当前用户和数据库操作对象已由构造函数保存，窗口只需传设备编号与新配置。
            bool updated = _deviceStore.TryUpdateDevice(_currentUser, deviceId, input);

            if (updated)
            {
                RefreshDevices();
            }

            return updated;
        }

        /// <summary>
        /// 修改设备的启停配置，并重新读取列表。
        /// enabled 为 true 表示启用，false 表示停用。
        /// </summary>
        public bool TrySetDeviceEnabled(long deviceId, bool enabled)
        {
            bool updated = _deviceStore.TrySetDeviceEnabled(
                _currentUser, deviceId, enabled);

            // 即使设备已经是目标状态，也重新读取，避免显示旧数据。
            RefreshDevices();

            return updated;
        }


    }
}

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels
{
    /// <summary>
    /// 提供采集管理页面的数据和操作命令。
    /// 当前阶段只负责加载、刷新和选择设备。
    /// </summary>
    public class AcquisitionViewModel : PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;

        // 表格绑定这个集合；集合变化时，表格会更新。
        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        private DeviceInfo? _selectedDevice;

        // 保存当前选中的设备，后续点位配置和采集操作会使用它。
        public DeviceInfo? SelectedDevice
        {
            get => _selectedDevice;
            set => SetProperty(ref _selectedDevice, value);
        }

        private string _pageMessage = "正在准备已启用设备列表…";

        public string PageMessage
        {
            get => _pageMessage;
            private set => SetProperty(ref _pageMessage, value);
        }

        // 按钮绑定的异步命令，真正执行的方法在下面。
        public IAsyncRelayCommand RefreshDevicesCommand { get; }

        public AcquisitionViewModel(
            UserAccount currentUser,
            DeviceStore deviceStore) : base("采集管理")
        {
            _currentUser = currentUser
                ?? throw new ArgumentNullException(nameof(currentUser));

            _deviceStore = deviceStore
                ?? throw new ArgumentNullException(nameof(deviceStore));

            if (_currentUser.Role != UserRole.Administrator ||
                !_currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException(
                    "只有已启用的管理员可以进入采集管理。");
            }

            // 把方法包装成命令，供前端 Button.Command 绑定。
            RefreshDevicesCommand =
                new AsyncRelayCommand(RefreshDevicesAsync);
        }

        /// <summary>
        /// 从数据库重新读取设备配置，并更新界面集合。
        /// 这里只查询配置，不连接设备、不启动采集。
        /// </summary>
        private async Task RefreshDevicesAsync()
        {
            try
            {
                PageMessage = "正在读取已启用设备配置…";

                // 在 SQL 查询中只取已启用设备，不在界面层过滤。
                // GetDevices 是同步查询，把它放到后台线程执行。
                // 后台只查询数据库，不操作界面绑定的 Devices 集合。
                var latestDevices = await Task.Run(
                    () => _deviceStore.GetDevices(_currentUser, enabledOnly: true));

                // await 后回到界面线程，再更新界面数据。
                // 记住当前选择的编号，刷新后尽量保持原来的选择。
                long? selectedId = SelectedDevice?.Id;

                // 查询成功才清空旧列表；查询失败时保留原来的显示。
                Devices.Clear();
                foreach (DeviceInfo device in latestDevices)
                {
                    Devices.Add(device);
                }

                // 查询得到的是新对象，因此按编号找到新的对应对象。
                // 原设备不存在或已经停用时，选择恢复为 null。
                SelectedDevice = selectedId.HasValue
                    ? Devices.FirstOrDefault(
                        device => device.Id == selectedId.Value)
                    : null;

                PageMessage = Devices.Count == 0
                    ? "暂无已启用设备，请先在设备管理中启用设备。"
                    : $"已加载 {Devices.Count} 台已启用设备，设备配置已刷新。";
            }
            catch (Exception ex)
            {
                // 捕获查询异常，显示提示，避免命令出错导致程序退出。
                PageMessage = $"刷新设备失败：{ex.Message}";
            }
        }
    }
}

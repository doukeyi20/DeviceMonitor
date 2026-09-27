using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace DeviceMonitor.ViewModels
{
    public class DeviceManagementViewModel: PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;

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
        /// 刷新
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
    }
}

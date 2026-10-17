using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Core.Communication;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Sampling;
using System;
using DeviceMonitor.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 设备管理页的数据源：列表刷新、新增/编辑/删除、启停配置和 TCP 端口测试。
    /// </summary>
    public partial class DeviceManagementViewModel : PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;
        // 主窗口共用的管理器，删除设备前通过它停止原采集任务。
        private readonly DeviceRuntimeManager? _deviceRuntimeManager;
        private Task? _stopOperationsTask; // 退出时的等待任务，重复调用也只收尾一次。
        private bool _isStopping; // 已开始退出，不再允许新操作。
        private long? _testingDeviceId; // 正在测试连接的设备编号；没在测试时为 null。

        // 通信对象只负责连接测试；不需要数据库路径，也不操作设备表。
        private readonly TcpConnectionTester _connectionTester = new();

        // 表格把选择写到这里；选择变化后，各命令重新判断是否允许执行。
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EditDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(EnableDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeleteDeviceCommand))]
        [NotifyPropertyChangedFor(nameof(SelectedDeviceTitle), nameof(SelectedDeviceConnectionDetails), nameof(SelectedDeviceTimeoutDetails))]
        private DeviceInfo? _selectedDevice;

        // 表格选中一台设备后，下方只读详情直接显示已有配置，不再查询或连接设备。
        public string SelectedDeviceTitle => SelectedDevice is null
            ? "设备详情" : $"{SelectedDevice.Name} · 配置编号 {SelectedDevice.Id}";
        public string SelectedDeviceConnectionDetails => SelectedDevice is null
            ? "选择表格中的一台设备，查看完整连接参数和超时设置。"
            : $"{SelectedDevice.CommunicationText} · {SelectedDevice.Endpoint} · " +
                $"{(SelectedDevice.CommunicationMode == DeviceCommunicationMode.Rtu ? "站号" : "Unit ID")} {SelectedDevice.UnitId}";
        public string SelectedDeviceTimeoutDetails => SelectedDevice is null
            ? "这里只显示配置；是否读通，请到采集点配置测试。"
            : $"读取超时 {SelectedDevice.ReadTimeoutMilliseconds} ms · 发送超时 {SelectedDevice.WriteTimeoutMilliseconds} ms · " +
                (SelectedDevice.CommunicationMode == DeviceCommunicationMode.Rtu
                    ? "RTU 不使用 TCP 连接超时"
                    : $"连接超时 {SelectedDevice.ConnectTimeoutMilliseconds} ms");

        public event Action<DeviceInfo?>? DeviceEditorRequested;
        public event Action<string, string>? MessageRequested;
        public event Action<DeviceConfirmationRequest>? ConfirmationRequested;

        private bool _isDeletingDevice; // 删除尚未完成时，限制重复操作并显示“删除中”。
        public bool IsDeletingDevice
        {
            get => _isDeletingDevice;
            private set
            {
                if (SetProperty(ref _isDeletingDevice, value))
                {
                    OnPropertyChanged(nameof(DeleteDeviceButtonText));
                    NotifyDeviceCommands();
                }
            }
        }

        public string DeleteDeviceButtonText => IsDeletingDevice ? "删除中…" : "删除设备";

        private string _pageMessage = string.Empty;
        public string PageMessage
        {
            get => _pageMessage;
            private set => SetProperty(ref _pageMessage, value);
        }

        private bool _isTestingConnection;
        // 状态只允许本类修改，所以保留手写的 private set。
        public bool IsTestingConnection
        {
            get => _isTestingConnection;
            private set
            {
                if (SetProperty(ref _isTestingConnection, value))
                {
                    // 一个通知更新按钮文字，另一个通知更新命令是否可执行。
                    OnPropertyChanged(nameof(TestConnectionButtonText));
                    TestConnectionCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string TestConnectionButtonText =>
            IsTestingConnection ? "测试中…" : "TCP端口测试";

        private string _connectionTestMessage = string.Empty;
        public string ConnectionTestMessage
        {
            get => _connectionTestMessage;
            private set => SetProperty(ref _connectionTestMessage, value);
        }

        // 设备表绑定这里，每项是一个 DeviceInfo；选择的那项写入 SelectedDevice。
        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        /// <summary>
        /// 接住用户、数据库对象和共用管理器，加载设备列表。
        /// </summary>
        public DeviceManagementViewModel(UserAccount currentUser, DeviceStore deviceStore,
            DeviceRuntimeManager? deviceRuntimeManager = null) : base("设备管理")
        {
            _currentUser = currentUser
                            ?? throw new ArgumentNullException(nameof(currentUser));
            _deviceStore = deviceStore
                ?? throw new ArgumentNullException(nameof(deviceStore));
            _deviceRuntimeManager = deviceRuntimeManager;

            if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有已启用的管理员可以管理设备。");
            }

            if (_deviceRuntimeManager is not null)
                _deviceRuntimeManager.PropertyChanged += DeviceRuntimeManager_PropertyChanged;

            // 页面对象创建时，先读取数据库中已有的设备。
            RefreshDevices();
        }

        /// <summary>
        /// 由 RefreshDevicesCommand 调用；本类构造方法也会调用一次。
        /// 刷新设备配置列表，不测试连接，也不启动采集。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanManageDevices))]
        public void RefreshDevices()
        {
            if (!CanManageDevices())
                return;

            try
            {
                LoadDevices();
                PageMessage = $"已刷新 {Devices.Count} 台设备。";
            }
            catch (Exception ex)
            {
                PageMessage = $"刷新设备失败：{ex.Message}。原列表已保留，请点击“刷新设备”重试。";
            }
        }

        // 刷新按钮和写入后的刷新共用这里；先查成功，再动界面集合。
        // 由 RefreshDevices 和 RefreshDevicesAfterOperation 调用。
        private void LoadDevices()
        {
            // 先查询成功，再替换界面列表；查询失败时不清空原列表。
            var latestDevices = _deviceStore.GetDevices(_currentUser);
            long? selectedId = SelectedDevice?.Id;

            Devices.Clear();
            foreach (DeviceInfo device in latestDevices)
            {
                Devices.Add(device);
            }

            // Clear 会让表格取消选择，所以提前保存编号，再选中刷新后的新对象。
            // 新对象带着数据库最新的启用状态，命令条件也会随选择通知重新判断。
            SelectedDevice = selectedId.HasValue
                ? Devices.FirstOrDefault(device => device.Id == selectedId.Value)
                : null;
        }

        /// <summary>
        /// 由 AddDeviceCommand 调用。
        /// 请求打开新增设备表单，传 null 表示新增。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanManageDevices))]
        private void AddDevice()
        {
            // CanExecute 控制按钮；这里的判断保护代码直接执行命令的情况。
            if (CanManageDevices())
                // 让 DeviceManagementView.OpenDeviceEditor 打开空白表单，null 表示新增。
                DeviceEditorRequested?.Invoke(null);
        }

        /// <summary>
        /// 由 DeviceEditorViewModel.Save 在新增设备时调用。
        /// 保存新增设备，成功后刷新列表；刷新失败单独提示。
        /// </summary>
        public bool TryAddDevice(DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!CanManageDevices())
                return false;

            // 当前登录用户由这个 ViewModel 保存，界面不必再传一次。
            bool added = _deviceStore.TryAddDevice(_currentUser, input);

            if (added)
            {
                RefreshDevicesAfterOperation($"设备“{input.Name}”已保存。");
            }

            return added; // false 表示设备名称重复。
        }

        /// <summary>
        /// 由 EditDeviceCommand 调用。
        /// 请求打开选中设备的编辑表单。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEditDevice))]
        private void EditDevice()
        {
            DeviceInfo? device = SelectedDevice;
            if (CanEditDevice() && device is not null)
                // 让 DeviceManagementView.OpenDeviceEditor 打开带原配置的表单，传 device 表示编辑。
                DeviceEditorRequested?.Invoke(device);
        }

        // 先停止这台设备，才能修改它的连接参数。
        // 由 EditDeviceCommand 判断按钮能不能点时调用。
        private bool CanEditDevice() =>
            CanManageDevices() && SelectedDevice is not null
            && CanChangeDeviceConfiguration(SelectedDevice.Id);

        /// <summary>
        /// 由 DeviceEditorViewModel.Save 在编辑设备时调用。
        /// 按原编号修改设备配置，成功后刷新列表，不新增另一台设备。
        /// </summary>
        public bool TryUpdateDevice(long deviceId, DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!CanManageDevices())
                return false;

            // 表单打开后也可能开始了采集，保存前再检查一次。
            EnsureDeviceConfigurationCanChange(deviceId);

            // 当前用户和数据库操作对象已由构造函数保存，窗口只需传设备编号与新配置。
            bool updated = _deviceStore.TryUpdateDevice(_currentUser, deviceId, input);

            if (updated)
            {
                RefreshDevicesAfterOperation($"设备“{input.Name}”的配置已保存。");
            }

            return updated;
        }

        /// <summary>
        /// 由 EnableDeviceCommand 调用。
        /// 将选中设备设为启用，传 true。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEnableDevice))]
        private void EnableDevice()
        {
            // true 表示启用。
            ChangeSelectedDeviceStatus(true);
        }

        // 有权限且设备目前停用，才能启用。
        // 由 EnableDeviceCommand 判断按钮能不能点时调用。
        private bool CanEnableDevice() =>
            CanManageDevices() && SelectedDevice is { IsEnabled: false }
            && CanChangeDeviceConfiguration(SelectedDevice.Id);

        /// <summary>
        /// 由 DisableDeviceCommand 调用。
        /// 将选中设备设为停用，传 false。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDisableDevice))]
        private void DisableDevice()
        {
            // false 表示停用。
            ChangeSelectedDeviceStatus(false);
        }

        // 设备已停止且目前启用，才能停用；正在收尾时也要等完。
        // 由 DisableDeviceCommand 判断按钮能不能点时调用。
        private bool CanDisableDevice() =>
            CanManageDevices() && SelectedDevice is { IsEnabled: true }
            && CanChangeDeviceConfiguration(SelectedDevice.Id);

        /// <summary>
        /// 由 EnableDevice 和 DisableDevice 调用。
        /// 保存选中设备的启停设置，true 是启用，false 是停用；停用前先确认。
        /// </summary>
        private void ChangeSelectedDeviceStatus(bool enabled)
        {
            DeviceInfo? device = SelectedDevice;
            if (!CanManageDevices() || device is null || device.IsEnabled == enabled)
                return;

            string action = enabled ? "启用" : "停用";
            try
            {
                EnsureDeviceConfigurationCanChange(device.Id);
                // 如果是停用，先弹出确认框；启用不需要确认。
                if (!enabled)
                {
                    var confirmation = new DeviceConfirmationRequest(
                        $"确定停用“{device.Name}”吗？\n\n" +
                        "停用后不能再开始采集；以后需要使用时，可以重新启用。", "停用设备");
                    
                    // 让 DeviceManagementView.ConfirmAction 弹框，把选择写回 confirmation.Confirmed。
                    ConfirmationRequested?.Invoke(confirmation);

                    if (!confirmation.Confirmed || !CanManageDevices())
                        return;
                }

                // 此方法会同时设置成功/未改变提示和刷新警告；
                TrySetDeviceEnabled(device.Id, enabled);
            }
            catch (Exception ex)
            {
                // 调用 DeviceManagementView.xaml.cs 的 ShowMessage：显示启用/停用失败的原因。
                MessageRequested?.Invoke($"{action}设备时发生错误：{ex.Message}", "操作失败");
            }
        }

        /// <summary>
        /// 由 ChangeSelectedDeviceStatus 调用。
        /// 保存设备启停设置并刷新列表，不直接启动或停止采集任务。
        /// </summary>
        public bool TrySetDeviceEnabled(long deviceId, bool enabled)
        {
            if (!CanManageDevices())
                return false;
            EnsureDeviceConfigurationCanChange(deviceId);
            string target = Devices.FirstOrDefault(device => device.Id == deviceId)?.Name
                ?? $"编号 {deviceId}";
            bool updated = _deviceStore.TrySetDeviceEnabled(
                _currentUser, deviceId, enabled);

            // 即使设备已经是目标状态，也重新读取，避免显示旧数据。
            string resultMessage = updated
                ? enabled
                    ? $"已启用“{target}”。"
                    : $"已停用“{target}”。"
                : $"设备不存在，或已经处于{(enabled ? "启用" : "停用")}状态。";
            RefreshDevicesAfterOperation(resultMessage);

            return updated;
        }

        /// <summary>
        /// 由 DeleteDeviceCommand 调用。
        /// 确认后，先结束本设备的连接测试和采集，再删除设备及点位配置。
        /// 历史读数和报警保留。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDeleteDevice))]
        private async Task DeleteDeviceAsync()
        {
            // 先记住点击的是哪一台；等待期间即使表格选择变化，也不能删错设备。
            DeviceInfo? device = SelectedDevice;
            if (device is null || !CanDeleteDevice() || _deviceRuntimeManager is null)
                return;

            var confirmation = new DeviceConfirmationRequest(
                $"确定删除设备“{device.Name}”（编号 {device.Id}）吗？\n\n" +
                "会先停止这台设备的采集，再删除设备及其全部采集点配置。\n" +
                "已有历史读数和报警历史会保留。此操作不能撤销。", "删除设备");
            
            // 让 DeviceManagementView.ConfirmAction 弹出删除确认框，结果写回 confirmation.Confirmed。
            ConfirmationRequested?.Invoke(confirmation);

            if (!confirmation.Confirmed || !CanDeleteDevice())
                return;

            IsDeletingDevice = true;
            try
            {
                PageMessage = $"正在停止“{device.Name}”的任务并删除配置…";

                // 判断当前正在测试连接的设备是否就是要删除的设备，如果是，则取消连接测试。
                if (_testingDeviceId == device.Id)
                    await CancelConnectionTestAsync();

                // 删除涉及采集，所以交给管理器先停任务，再删配置；历史保留。
                bool deleted = await _deviceRuntimeManager.DeleteDeviceAsync(device.Id);
                RefreshDevicesAfterOperation(deleted
                    ? $"已删除“{device.Name}”及其采集点配置；历史读数和报警历史已保留。"
                    : $"“{device.Name}”已不存在；历史记录仍保留。");
            }
            catch (Exception ex)
            {
                PageMessage = $"删除“{device.Name}”失败：{ex.Message}。请刷新确认配置；已停止的采集不会自动重启。";
            }
            finally
            {
                IsDeletingDevice = false;
            }
        }

        /// <summary>
        /// 由 DeleteDeviceCommand 判断按钮能不能点时调用。
        /// 有权限、选中设备且有共用管理器，才能删除。
        /// </summary>
        private bool CanDeleteDevice() => CanManageDevices() && SelectedDevice is not null
            && _deviceRuntimeManager is not null
            && !_deviceRuntimeManager.IsDeletingDevice(SelectedDevice.Id);



        /// <summary>
        /// 由 TestConnectionCommand 调用。
        /// 测试选中设备的 TCP 端口，显示结果，不验证 Modbus 协议。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanTestConnection), IncludeCancelCommand = true)]
        private async Task TestConnectionAsync(CancellationToken cancellationToken)
        {
            // 先抓取本次目标；等待连接时即使切换选择，也不改变本次测试对象。
            DeviceInfo? device = SelectedDevice;
            if (device is null || !CanTestConnection())
            {
                return;
            }

            IsTestingConnection = true;
            _testingDeviceId = device.Id;
            string target = $"{device.Name}（{device.Host}:{device.Port}）";

            try
            {
                ConnectionTestMessage = $"正在测试 {target}，连接超时：" +
                    $"{device.ConnectTimeoutMilliseconds} ms…";

                // 异步等待连接，不堵住界面；cancellationToken 由工具包命令提供，不用手动传。
                await _connectionTester.TestAsync(device, cancellationToken);
                
                // 记入日志
                AppLog.Info("TCP 测试连接成功", $"DeviceId={device.Id}");

                ConnectionTestMessage = $"TCP 连接成功：{target}。测试连接已关闭。" +
                    "这只说明该端口可连接，尚未验证 Modbus 通信。";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ConnectionTestMessage = $"已取消 TCP 测试：{target}。临时连接已关闭。";
            }
            catch (TimeoutException ex)
            {
                AppLog.Warning("TCP 测试连接超时", $"DeviceId={device.Id}", ex);
                ConnectionTestMessage = $"TCP 连接超时：{target}。" +
                    $"在 {device.ConnectTimeoutMilliseconds} ms 内未能建立连接，" +
                    "请检查地址、端口、网络和防火墙。";
            }
            catch (SocketException ex)
            {
                AppLog.Warning("TCP 测试连接失败", $"DeviceId={device.Id}; SocketError={ex.SocketErrorCode}", ex);
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
                AppLog.Warning("TCP 测试配置错误", $"DeviceId={device.Id}", ex);
                ConnectionTestMessage = $"设备连接配置不正确：{ex.Message}";
            }
            catch (Exception ex)
            {
                AppLog.Error("TCP 测试异常", $"DeviceId={device.Id}", ex);
                ConnectionTestMessage = $"测试 {target} 时发生错误：{ex.Message}";
            }
            finally
            {
                // 无论成功还是失败，都结束“测试中”状态，让按钮恢复可用。
                _testingDeviceId = null;
                IsTestingConnection = false;
            }
        }

        // 停用设备也可以手动测试端口；停用配置不等于不能测试连接。
        // 有权限、已选设备且没有正在进行的测试，才能测试连接。
        // 由 TestConnectionCommand 判断按钮能不能点时调用。
        private bool CanTestConnection() =>
            CanManageDevices() && SelectedDevice?.CommunicationMode == DeviceCommunicationMode.Tcp && !IsTestingConnection;


        /// <summary>
        /// 由 DeleteDeviceAsync 和 StopOperationsCoreAsync 调用。
        /// 取消连接测试并等它结束；普通取消按钮直接用生成的取消命令。
        /// </summary>
        private async Task CancelConnectionTestAsync()
        {
            TestConnectionCommand.Cancel();
            if (TestConnectionCommand.ExecutionTask is { } testing)
                await testing;
        }

        // 数据库操作完成后，更新列表和提示
        // 由新增、编辑、启停设置和删除成功后的方法调用。
        private void RefreshDevicesAfterOperation(string resultMessage)
        {
            try
            {
                LoadDevices();
                PageMessage = resultMessage;
            }
            catch (Exception ex)
            {
                PageMessage = $"{resultMessage} 但刷新列表失败：{ex.Message}。" +
                    "当前表格可能仍是旧数据，请点击“刷新设备”重新加载。";
            }
        }

        /// <summary>
        /// 由 RefreshDevicesCommand、AddDeviceCommand 判断按钮能不能点时调用。
        /// 共用操作条件：账户是启用的管理员，且没在关窗或删除设备。
        /// </summary>
        private bool CanManageDevices() =>
            _currentUser.Role == UserRole.Administrator && _currentUser.IsEnabled
            && !_isStopping && !IsDeletingDevice
            && _deviceRuntimeManager?.IsShuttingDown != true;

        // 编辑、启停设置共用这个检查；删除另走“先停再删”的流程。
        private bool CanChangeDeviceConfiguration(long deviceId) =>
            _deviceRuntimeManager is null
            || (!_deviceRuntimeManager.IsShuttingDown
                && !_deviceRuntimeManager.IsDeletingDevice(deviceId)
                && !_deviceRuntimeManager.IsRunning(deviceId));

        // 保存入口也要检查，不能只把界面按钮变灰。
        private void EnsureDeviceConfigurationCanChange(long deviceId)
        {
            if (!CanChangeDeviceConfiguration(deviceId))
                throw new InvalidOperationException("请先到实时监控页停止这台设备，等停止完成后再修改配置或停用。");
        }

        // 监控页开始、停止或删除设备后，管理页的按钮也跟着更新。
        private void DeviceRuntimeManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceRuntimeManager.ActiveSamplingCount)
                or nameof(DeviceRuntimeManager.IsShuttingDown) or nameof(DeviceRuntimeManager.IsDeletingDevices))
                NotifyDeviceCommands();
        }

        /// <summary>
        /// 由 IsDeletingDevice 属性变化或 StopOperationsAsync 开始退出时调用。
        /// 让有关按钮重新判断能不能点，不执行按钮操作。
        /// </summary>
        private void NotifyDeviceCommands()
        {
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            AddDeviceCommand.NotifyCanExecuteChanged();
            EditDeviceCommand.NotifyCanExecuteChanged();
            EnableDeviceCommand.NotifyCanExecuteChanged();
            DisableDeviceCommand.NotifyCanExecuteChanged();
            DeleteDeviceCommand.NotifyCanExecuteChanged();
            TestConnectionCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 由 MainViewModel.StopBackgroundWorkAsync 在退出前调用。
        /// 退出时禁止新操作、取消测试并等待删除结束；重复调用共用同一次收尾。
        /// </summary>
        public Task StopOperationsAsync()
        {
            if (_stopOperationsTask is not null)
                return _stopOperationsTask;
            _isStopping = true;
            if (_deviceRuntimeManager is not null)
                _deviceRuntimeManager.PropertyChanged -= DeviceRuntimeManager_PropertyChanged;
            NotifyDeviceCommands();
            _stopOperationsTask = StopOperationsCoreAsync();
            return _stopOperationsTask;
        }

        /// <summary>
        /// 由 StopOperationsAsync 调用。
        /// 等连接测试和设备删除真正结束。
        /// </summary>
        private async Task StopOperationsCoreAsync()
        {
            await CancelConnectionTestAsync();
            if (DeleteDeviceCommand.ExecutionTask is { } deleting)
                await deleting;
        }


    }
}

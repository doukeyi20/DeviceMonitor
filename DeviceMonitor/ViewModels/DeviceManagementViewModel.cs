// 管理设备配置、启用状态和 TCP 连接测试。
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Core.Communication;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using DeviceMonitor.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DeviceMonitor.ViewModels
{
    public partial class DeviceManagementViewModel : PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;

        private readonly DeviceTasks? _deviceTasks;
        private Task? _stopOperationsTask;
        private bool _isStopping;

        private long? _connectionTestDeviceId;
        // 退出时等待已开始的数据库操作，避免关闭后回填旧页面。
        private Task _databaseWork = Task.CompletedTask;
        private bool _isLoadingOrSaving;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EditDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(EnableDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeleteDeviceCommand))]
        [NotifyPropertyChangedFor(nameof(SelectedDeviceTitle), nameof(SelectedDeviceConnectionDetails), nameof(SelectedDeviceTimeoutDetails), nameof(IsTcpDeviceSelected))]
        private DeviceInfo? _selectedDevice;

        public bool IsTcpDeviceSelected => SelectedDevice?.CommunicationMode == DeviceCommunicationMode.Tcp;

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
        public event Action<ConfirmationRequest>? ConfirmationRequested;

        private bool _isDeletingDevice;
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

        public bool IsTestingConnection
        {
            get => _isTestingConnection;
            private set
            {
                if (SetProperty(ref _isTestingConnection, value))
                {
                    OnPropertyChanged(nameof(TestConnectionButtonText));
                    TestConnectionCommand.NotifyCanExecuteChanged();
                    DeleteDeviceCommand.NotifyCanExecuteChanged();
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

        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        public DeviceManagementViewModel(UserAccount currentUser, DeviceStore deviceStore,
    DeviceTasks? deviceTasks = null) : base("设备管理")
        {
            _currentUser = currentUser
                            ?? throw new ArgumentNullException(nameof(currentUser));
            _deviceStore = deviceStore
                ?? throw new ArgumentNullException(nameof(deviceStore));
            _deviceTasks = deviceTasks;

            if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException("只有已启用的管理员可以管理设备。");
            }

            if (_deviceTasks is not null)
                _deviceTasks.PropertyChanged += DeviceTasks_PropertyChanged;
        }

        /// <summary>刷新设备配置列表，不测试连接，也不启动采集。</summary>
        [RelayCommand(CanExecute = nameof(CanManageDevices))]
        public async Task RefreshDevicesAsync()
        {
            if (!CanManageDevices())
                return;

            try
            {
                _isLoadingOrSaving = true;
                NotifyDeviceCommands();

                await LoadDevicesAsync();
                PageMessage = $"已刷新 {Devices.Count} 台设备。";
            }
            catch (Exception ex)
            {
                PageMessage = $"刷新设备失败：{ex.Message}。原列表已保留，请点击“刷新设备”重试。";
            }

            finally
            {
                _isLoadingOrSaving = false;
                NotifyDeviceCommands();
            }
        }

        private async Task LoadDevicesAsync(string? selectName = null)
        {
            var loading = Task.Run(() => _deviceStore.GetDevices(_currentUser));

            _databaseWork = loading;

            // 等查询成功后再替换集合，失败时保留原列表。
            var latestDevices = await loading;

            if (_isStopping)
                return;

            // 刷新按编号保留选择；新增、删除后选择实际存在的记录。
            long? selectedId = SelectedDevice?.Id;
            int selectedIndex = SelectedDevice is null ? 0 : Devices.IndexOf(SelectedDevice);

            Devices.Clear();
            foreach (DeviceInfo device in latestDevices)
            {
                Devices.Add(device);
            }

            SelectedDevice = (selectName is null ? null : Devices.FirstOrDefault(
                device => string.Equals(device.Name, selectName, StringComparison.OrdinalIgnoreCase)))
                ?? Devices.FirstOrDefault(device => device.Id == selectedId)
                ?? Devices.ElementAtOrDefault(Math.Clamp(selectedIndex, 0, Math.Max(0, Devices.Count - 1)));
        }

        /// <summary>请求打开新增设备表单，传 null 表示新增。</summary>
        [RelayCommand(CanExecute = nameof(CanManageDevices))]
        private void AddDevice()
        {
            if (CanManageDevices())

                DeviceEditorRequested?.Invoke(null);
        }

        /// <summary>保存新增设备，成功后刷新列表；刷新失败单独提示。</summary>
        public async Task<bool> TryAddDeviceAsync(DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!CanManageDevices())
                return false;

            _isLoadingOrSaving = true;
            NotifyDeviceCommands();

            try
            {
                var saving = Task.Run(() => _deviceStore.TryAddDevice(_currentUser, input));
                _databaseWork = saving;

                bool added = await saving;

                // 区分写入失败与写入成功后的刷新失败。
                if (added && !_isStopping)
                    await RefreshDevicesAfterOperationAsync($"设备“{input.Name}”已保存。", input.Name.Trim());

                return added;
            }
            finally
            {
                _isLoadingOrSaving = false;
                NotifyDeviceCommands();
            }
        }

        /// <summary>请求打开选中设备的编辑表单。</summary>
        [RelayCommand(CanExecute = nameof(CanEditDevice))]
        private void EditDevice()
        {
            DeviceInfo? device = SelectedDevice;
            if (CanEditDevice() && device is not null)

                DeviceEditorRequested?.Invoke(device);
        }

        private bool CanEditDevice() =>
            CanManageDevices() && SelectedDevice is not null
            && CanChangeDeviceConfiguration(SelectedDevice.Id);

        /// <summary>按原编号修改设备配置，成功后刷新列表，不新增另一台设备。</summary>
        public async Task<bool> TryUpdateDeviceAsync(long deviceId, DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!CanManageDevices())
                return false;

            EnsureDeviceConfigurationCanChange(deviceId);

            _isLoadingOrSaving = true;
            NotifyDeviceCommands();
            try
            {
                var saving = Task.Run(() => _deviceStore.TryUpdateDevice(_currentUser, deviceId, input));
                _databaseWork = saving;

                bool updated = await saving;

                if (updated && !_isStopping)
                    await RefreshDevicesAfterOperationAsync($"设备“{input.Name}”的配置已保存。");
                return updated;
            }
            finally
            {
                _isLoadingOrSaving = false;
                NotifyDeviceCommands();
            }
        }

        /// <summary>将选中设备设为启用，传 true。</summary>
        [RelayCommand(CanExecute = nameof(CanEnableDevice))]
        private async Task EnableDeviceAsync()
        {
            await ChangeSelectedDeviceStatusAsync(true);
        }

        private bool CanEnableDevice() =>
            CanManageDevices() && SelectedDevice is { IsEnabled: false }
            && CanChangeDeviceConfiguration(SelectedDevice.Id);

        /// <summary>将选中设备的配置设为停用，传 false；不会代替“停止采集”。</summary>
        [RelayCommand(CanExecute = nameof(CanDisableDevice))]
        private async Task DisableDeviceAsync()
        {
            await ChangeSelectedDeviceStatusAsync(false);
        }

        private bool CanDisableDevice() =>
            CanManageDevices() && SelectedDevice is { IsEnabled: true }
            && CanChangeDeviceConfiguration(SelectedDevice.Id);

        /// <summary>保存选中设备的启停设置，true 是启用，false 是停用；停用前先确认。</summary>
        private async Task ChangeSelectedDeviceStatusAsync(bool enabled)
        {
            DeviceInfo? device = SelectedDevice;
            if (!CanManageDevices() || device is null || device.IsEnabled == enabled)
                return;

            string action = enabled ? "启用" : "停用";
            try
            {
                EnsureDeviceConfigurationCanChange(device.Id);

                if (!enabled)
                {
                    var confirmation = new ConfirmationRequest(
                        $"确定停用“{device.Name}”吗？\n\n" +
                        "停用后不能再开始采集；以后需要使用时，可以重新启用。", "停用设备");

                    ConfirmationRequested?.Invoke(confirmation);

                    if (!confirmation.Confirmed || !CanManageDevices())
                        return;
                }

                await TrySetDeviceEnabledAsync(device.Id, enabled);
            }
            catch (Exception ex)
            {
                MessageRequested?.Invoke($"{action}设备时发生错误：{ex.Message}", "操作失败");
            }
        }

        /// <summary>保存设备启停设置并刷新列表，不直接启动或停止采集任务。</summary>
        public async Task<bool> TrySetDeviceEnabledAsync(long deviceId, bool enabled)
        {
            if (!CanManageDevices())
                return false;
            EnsureDeviceConfigurationCanChange(deviceId);
            string target = Devices.FirstOrDefault(device => device.Id == deviceId)?.Name
                ?? $"编号 {deviceId}";

            _isLoadingOrSaving = true;
            NotifyDeviceCommands();
            try
            {
                var saving = Task.Run(() => _deviceStore.TrySetDeviceEnabled(_currentUser, deviceId, enabled));
                _databaseWork = saving;
                bool updated = await saving;

                string resultMessage = updated
                    ? enabled
                        ? $"已启用“{target}”。"
                        : $"已停用“{target}”。"
                    : $"设备不存在，或已经处于{(enabled ? "启用" : "停用")}状态。";

                if (!_isStopping)
                    await RefreshDevicesAfterOperationAsync(resultMessage);

                return updated;
            }
            finally
            {
                _isLoadingOrSaving = false;
                NotifyDeviceCommands();
            }
        }

        /// <summary>用户先手动停止采集或测试；确认后只删除设备及点位配置，不代做停止。</summary>
        [RelayCommand(CanExecute = nameof(CanDeleteDevice))]
        private async Task DeleteDeviceAsync()
        {
            DeviceInfo? device = SelectedDevice;
            if (device is null || !CanDeleteDevice() || _deviceTasks is null)
                return;

            var confirmation = new ConfirmationRequest(
                $"确定删除设备“{device.Name}”（编号 {device.Id}）吗？\n\n" +
                "会删除设备及其全部采集点配置。\n" +
                "已有历史读数和报警历史会保留。此操作不能撤销。", "删除设备");

            ConfirmationRequested?.Invoke(confirmation);

            if (!confirmation.Confirmed || !CanDeleteDevice())
                return;

            IsDeletingDevice = true;
            try
            {
                PageMessage = $"正在删除“{device.Name}”的配置…";

                bool deleted = await _deviceTasks.DeleteDeviceAsync(device.Id);
                await RefreshDevicesAfterOperationAsync(deleted
                    ? $"已删除“{device.Name}”及其采集点配置；历史读数和报警历史已保留。"
                    : $"“{device.Name}”已不存在；历史记录仍保留。");
            }
            catch (Exception ex)
            {
                PageMessage = $"删除“{device.Name}”失败：{ex.Message}。请刷新确认配置。";
            }
            finally
            {
                IsDeletingDevice = false;
            }
        }

        /// <summary>目标没有采集、单点测试或 TCP 端口测试，才允许删除；不影响其他设备运行。</summary>
        private bool CanDeleteDevice() => CanManageDevices() && SelectedDevice is not null
            && _deviceTasks is not null
            && !_deviceTasks.IsDeletingDevice(SelectedDevice.Id)
            && !_deviceTasks.IsRunning(SelectedDevice.Id)
            && _deviceTasks.SingleReadDeviceId != SelectedDevice.Id
            && _connectionTestDeviceId != SelectedDevice.Id;

        /// <summary>测试选中设备的 TCP 端口，显示结果，不验证 Modbus 协议。</summary>
        [RelayCommand(CanExecute = nameof(CanTestConnection), IncludeCancelCommand = true)]
        private async Task TestConnectionAsync(CancellationToken cancellationToken)
        {
            DeviceInfo? device = SelectedDevice;
            if (device is null || !CanTestConnection())
            {
                return;
            }

            _connectionTestDeviceId = device.Id;
            IsTestingConnection = true;
            string target = $"{device.Name}（{device.Host}:{device.Port}）";

            try
            {
                ConnectionTestMessage = $"正在测试 {target}，连接超时：" +
                    $"{device.ConnectTimeoutMilliseconds} ms…";

                await TcpConnectionTester.TestAsync(device, cancellationToken);

                AppLog.Info("TCP 测试成功", $"设备={device.Id}");

                ConnectionTestMessage = $"TCP 连接成功：{target}。测试连接已关闭。" +
                    "这只说明该端口可连接，尚未验证 Modbus 通信。";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ConnectionTestMessage = $"已取消 TCP 测试：{target}。临时连接已关闭。";
            }
            catch (TimeoutException ex)
            {
                AppLog.Warning("TCP 测试超时", $"设备={device.Id}", ex);
                ConnectionTestMessage = $"TCP 连接超时：{target}。" +
                    $"在 {device.ConnectTimeoutMilliseconds} ms 内未能建立连接，" +
                    "请检查地址、端口、网络和防火墙。";
            }
            catch (SocketException ex)
            {
                AppLog.Warning("TCP 测试失败", $"设备={device.Id}", ex);
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
                AppLog.Warning("TCP 参数错误", $"设备={device.Id}", ex);
                ConnectionTestMessage = $"设备连接配置不正确：{ex.Message}";
            }
            catch (Exception ex)
            {
                AppLog.Error("TCP 测试异常", $"设备={device.Id}", ex);
                ConnectionTestMessage = $"测试 {target} 时发生错误：{ex.Message}";
            }
            finally
            {
                _connectionTestDeviceId = null;
                IsTestingConnection = false;
            }
        }

        private bool CanTestConnection() =>
            CanManageDevices() && SelectedDevice?.CommunicationMode == DeviceCommunicationMode.Tcp && !IsTestingConnection;

        /// <summary>删除不再自动取消测试。</summary>
        private async Task CancelConnectionTestAsync()
        {
            TestConnectionCommand.Cancel();
            if (TestConnectionCommand.ExecutionTask is { } testing)
                await testing;
        }

        private async Task RefreshDevicesAfterOperationAsync(string resultMessage, string? selectName = null)
        {
            try
            {
                await LoadDevicesAsync(selectName);
                PageMessage = resultMessage;
            }
            catch (Exception ex)
            {
                PageMessage = $"{resultMessage} 但刷新列表失败：{ex.Message}。" +
                    "当前表格可能仍是旧数据，请点击“刷新设备”重新加载。";
            }
        }

        /// <summary>账户权限已在构造时验证；这里只检查之后会变化的关窗、删除状态。</summary>
        private bool CanManageDevices() =>
            !_isStopping && !IsDeletingDevice && !_isLoadingOrSaving
            && _deviceTasks?.IsShuttingDown != true;

        private bool CanChangeDeviceConfiguration(long deviceId) =>
            _deviceTasks is null
            || (!_deviceTasks.IsShuttingDown
                && !_deviceTasks.IsDeletingDevice(deviceId)
                && !_deviceTasks.IsRunning(deviceId));

        private void EnsureDeviceConfigurationCanChange(long deviceId)
        {
            if (!CanChangeDeviceConfiguration(deviceId))
                throw new InvalidOperationException("请先到实时监控页停止这台设备，等停止完成后再修改配置或停用。");
        }

        private void DeviceTasks_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceTasks.ActiveSamplingCount)
                or nameof(DeviceTasks.IsShuttingDown) or nameof(DeviceTasks.IsDeletingDevices)
                or nameof(DeviceTasks.SingleReadDeviceId))
                NotifyDeviceCommands();
        }

        /// <summary>让有关按钮重新判断能不能点，不执行按钮操作。</summary>
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

        /// <summary>退出时禁止新操作、取消测试并等待删除结束；重复调用共用同一次收尾。</summary>
        public Task StopOperationsAsync()
        {
            if (_stopOperationsTask is not null)
                return _stopOperationsTask;
            _isStopping = true;
            if (_deviceTasks is not null)
                _deviceTasks.PropertyChanged -= DeviceTasks_PropertyChanged;
            NotifyDeviceCommands();
            _stopOperationsTask = StopOperationsCoreAsync();
            return _stopOperationsTask;
        }

        /// <summary>等连接测试和设备删除真正结束。</summary>
        private async Task StopOperationsCoreAsync()
        {
            Task cancelTest = CancelConnectionTestAsync();
            try
            {
                await _databaseWork;
            }
            catch (Exception)
            {
                /* 数据库调用方已经显示失败；这里只等它结束，不在退出时重复报错。 */
            }

            await Task.WhenAll(cancelTest,
                RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask,
                EnableDeviceCommand.ExecutionTask ?? Task.CompletedTask,
                DisableDeviceCommand.ExecutionTask ?? Task.CompletedTask,
                DeleteDeviceCommand.ExecutionTask ?? Task.CompletedTask);
        }
    }
}

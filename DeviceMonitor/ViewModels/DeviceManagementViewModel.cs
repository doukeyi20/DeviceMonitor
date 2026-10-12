using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Core.Communication;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Sampling;
using System;
using DeviceMonitor.Core.Diagnostics;
using System.Collections.ObjectModel;
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
        // 主窗口传来的同一份管理器；删除前要停原任务，不能另外 new 一份去“停止”。
        private readonly DeviceRuntimeManager? _deviceRuntimeManager;
        private Task? _stopOperationsTask;
        private bool _isStopping;
        private long? _testingDeviceId;

        // 通信对象只负责连接测试；不需要数据库路径，也不操作设备表。
        private readonly TcpConnectionTester _connectionTester = new();

        // 表格把选择写到这里；选择变化后，各命令重新判断是否允许执行。
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EditDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(EnableDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisableDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeleteDeviceCommand))]
        private DeviceInfo? _selectedDevice;

        public event Action<DeviceInfo?>? DeviceEditorRequested;
        public event Action<string, string>? MessageRequested;
        public event Action<DeviceConfirmationRequest>? ConfirmationRequested;

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

        private string _pageMessage = "启用只是允许采集，不会自动开始；真正的开始、停止在实时监控页。";
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
            IsTestingConnection ? "测试中…" : "测试连接";

        private string _connectionTestMessage = "尚未进行连接测试。";
        public string ConnectionTestMessage
        {
            get => _connectionTestMessage;
            private set => SetProperty(ref _connectionTestMessage, value);
        }

        // 界面绑定这个集合；增加或清空其中的项目时，界面会更新。
        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        /// <summary>
        /// 由 MainViewModel 为管理员创建，再放进 Pages。
        /// 保存用户和 DeviceStore，调用 RefreshDevices 加载已有设备；这里与采集页面不同，创建对象时就查询列表。
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

            // 页面对象创建时，先读取数据库中已有的设备。
            RefreshDevices();
        }

        /// <summary>
        /// 构造函数和“刷新设备”按钮调用这里。
        /// 真正读取和替换列表交给 LoadDevices；这里只刷新配置，不连接设备，也不开始采集。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanManageDevices))]
        public void RefreshDevices()
        {
            if (!CanManageDevices())
                return;

            try
            {
                LoadDevices();
                PageMessage = $"已刷新 {Devices.Count} 台设备。启用只是允许采集，不会自动开始。";
            }
            catch (Exception ex)
            {
                PageMessage = $"刷新设备失败：{ex.Message}。原列表已保留，请点击“刷新设备”重试。";
            }
        }

        // 刷新按钮和写入后的刷新共用这里；先查成功，再动界面集合。
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
        /// 点击“新增设备”后，AddDeviceCommand 调用这里。
        /// 触发 DeviceEditorRequested，参数传 null 表示新增；已订阅的 DeviceManagementView.OpenDeviceEditor 负责开表单。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanManageDevices))]
        private void AddDevice()
        {
            // CanExecute 控制按钮；这里的判断保护代码直接执行命令的情况。
            if (CanManageDevices())
                // 调用 DeviceManagementView.xaml.cs 的 OpenDeviceEditor：
                // 传 null 表示新增，后台方法按照新增表单的默认值显示空白表单；保存后会调用 TryAddDevice。
                DeviceEditorRequested?.Invoke(null);
        }

        /// <summary>
        /// 由 DeviceEditorViewModel.Save 在新增模式下调用，传入整理好的 DeviceInput。
        /// 把当前用户和输入交给 DeviceStore.TryAddDevice；写入成功再刷新列表，刷新失败会单独提示。
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
                RefreshDevicesAfterOperation($"设备“{input.Name}”已保存。启用只是允许采集，不会自动开始。");
            }

            return added; // false 表示设备名称重复。
        }

        /// <summary>
        /// 点击“编辑选中设备”后，EditDeviceCommand 调用这里。
        /// 把 SelectedDevice 通过 DeviceEditorRequested 交给 DeviceManagementView.OpenDeviceEditor；
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEditDevice))]
        private void EditDevice()
        {
            DeviceInfo? device = SelectedDevice;
            if (CanEditDevice() && device is not null)
                // 调用 DeviceManagementView.xaml.cs 的 OpenDeviceEditor：
                // 跟新增一样，但是会把选中的 device 传过去，打开带有原配置的 AddDeviceWindow 编辑表单。
                DeviceEditorRequested?.Invoke(device);
        }

        // EditDeviceCommand、EditDevice 和测试连接的条件判断会用到它：当前有管理权限，并且选中了一台设备。
        private bool CanEditDevice() =>
            CanManageDevices() && SelectedDevice is not null;

        /// <summary>
        /// 由 DeviceEditorViewModel.Save 在编辑按钮下调用，传入原设备 Id 和新 DeviceInput。
        /// 调用 DeviceStore.TryUpdateDevice 修改原记录，成功后刷新列表；不会新增另一台设备。
        /// </summary>
        public bool TryUpdateDevice(long deviceId, DeviceInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!CanManageDevices())
                return false;

            // 当前用户和数据库操作对象已由构造函数保存，窗口只需传设备编号与新配置。
            bool updated = _deviceStore.TryUpdateDevice(_currentUser, deviceId, input);

            if (updated)
            {
                RefreshDevicesAfterOperation($"设备“{input.Name}”的配置已保存。");
            }

            return updated;
        }

        /// <summary>
        /// 点击“启用设备”后，EnableDeviceCommand 调用这里，再把 true 交给 ChangeSelectedDeviceStatus。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEnableDevice))]
        private void EnableDevice()
        {
            // 传入ture
            ChangeSelectedDeviceStatus(true);
        }

        // EnableDeviceCommand 判断按钮能不能点时调用：必须有管理权限，并且选中设备目前是停用的。
        private bool CanEnableDevice() =>
            CanManageDevices() && SelectedDevice is { IsEnabled: false };

        /// <summary>
        /// 点击“停用设备”后，DisableDeviceCommand 调用这里，再把 false 交给 ChangeSelectedDeviceStatus。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDisableDevice))]
        private void DisableDevice()
        {
            // 传入false
            ChangeSelectedDeviceStatus(false);
        }

        // DisableDeviceCommand 判断按钮能不能点时调用：必须有管理权限，并且选中设备目前是启用的。
        private bool CanDisableDevice() =>
            CanManageDevices() && SelectedDevice is { IsEnabled: true };

        /// <summary>
        /// 由 EnableDevice 和 DisableDevice 共用，true 表示启用，false 表示停用。
        /// 检查点击时的设备，再调用 TrySetDeviceEnabled 保存状态；出错时触发 MessageRequested，让页面后台显示提示。
        /// </summary>
        private void ChangeSelectedDeviceStatus(bool enabled)
        {
            DeviceInfo? device = SelectedDevice;
            if (!CanManageDevices() || device is null || device.IsEnabled == enabled)
                return;

            string action = enabled ? "启用" : "停用";
            try
            {
                // 如果是停用，先弹出确认框；启用不需要确认。
                if (!enabled)
                {
                    var confirmation = new DeviceConfirmationRequest(
                        $"确定停用“{device.Name}”吗？\n\n" +
                        "停用后不能再开始采集。正在采集的任务会在后续检查到停用时结束，" +
                        "不是立即中断当前读取。\n若要立即请求停止，请到实时监控页点击停止。", "停用设备");
                    
                    // 调用 DeviceManagementView.xaml.cs 的 ConfirmAction：弹出停用确认框。
                    // 后台把选择填入 confirmation.Confirmed，关闭确认框后再决定是否保存停用状态。
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
        /// 由 ChangeSelectedDeviceStatus 调用，传入设备 Id 和目标启用状态。
        /// 调用 DeviceStore.TrySetDeviceEnabled 更新数据库，再刷新列表显示最新状态；这里不直接启动或取消采集任务。
        /// </summary>
        public bool TrySetDeviceEnabled(long deviceId, bool enabled)
        {
            if (!CanManageDevices())
                return false;
            string target = Devices.FirstOrDefault(device => device.Id == deviceId)?.Name
                ?? $"编号 {deviceId}";
            bool updated = _deviceStore.TrySetDeviceEnabled(
                _currentUser, deviceId, enabled);

            // 即使设备已经是目标状态，也重新读取，避免显示旧数据。
            string resultMessage = updated
                ? enabled
                    ? $"已启用“{target}”；这只是允许采集，请到实时监控页手动开始。"
                    : $"已停用“{target}”；正在运行的任务会在后续检查到停用时结束。"
                : $"设备不存在，或已经处于{(enabled ? "启用" : "停用")}状态。";
            RefreshDevicesAfterOperation(resultMessage);

            return updated;
        }

        /// <summary>
        /// “删除设备”命令调用：先确认，再取消本设备连接测试，等采集停止后删除配置。
        /// 只删除设备和它的采集点；历史读数、报警历史保留，不跟着设备一起清空。
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
            
            // 调用 DeviceManagementView.xaml.cs 的 ConfirmAction：弹出删除确认框。
            // 后台填回 confirmation.Confirmed；只有确认后，下面才会停止采集并删除配置。
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

                // 设备管理器会先停止采集任务，再删除设备和它的采集点配置；历史读数和报警历史保留。
                // 删除设备的方法在设备管理器，因为跟采集有关
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
        /// 删除按钮的条件：选中了设备，有管理权限，并且拿到了能先停采集的共用管理器。
        /// </summary>
        private bool CanDeleteDevice() => CanEditDevice() && _deviceRuntimeManager is not null;



        /// <summary>
        /// 点击“测试连接”后，TestConnectionCommand 调用这里。
        /// 把点击时的设备交给 TcpConnectionTester.TestAsync，等待结果后更新页面提示；只测 TCP 端口，不验证 Modbus，也不改数据库。
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

                // await 等待真正的网络连接；完成后继续更新提示，不会卡住界面。
                // 注意这里本方法参数cancellationToken是工具包自动生成的，不用手都传入
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
        // TestConnectionCommand 和 TestConnectionAsync 用它检查：有权限、已选设备，而且没有另一场连接测试正在进行。
        private bool CanTestConnection() =>
            CanEditDevice() && !IsTestingConnection;


        /// <summary>
        /// 删除同设备、退出登录或关窗时调用：发出取消，再等连接测试真正释放套接字。
        /// 普通“取消测试”按钮直接使用工具包生成的 TestConnectionCancelCommand。
        /// </summary>
        private async Task CancelConnectionTestAsync()
        {
            TestConnectionCommand.Cancel();
            if (TestConnectionCommand.ExecutionTask is { } testing)
                await testing;
        }

        // 数据库操作完成后，更新列表和提示
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
        /// 新增、刷新、编辑、启停等操作共用的检查：已启用管理员，并且没在关窗或删除设备。
        /// 共用的操作条件
        /// </summary>
        private bool CanManageDevices() =>
            _currentUser.Role == UserRole.Administrator && _currentUser.IsEnabled
            && !_isStopping && !IsDeletingDevice;

        /// <summary>
        /// 删除开始/结束或页面退出时调用，让有关按钮重新判断是否能点；不会替用户执行按钮。
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
        /// MainViewModel 退出时调用。先禁止新操作，再取消测试，并等待已经开始的删除结束。
        /// 重复调用共用同一份任务，不会一边关窗一边又启动新的设备操作。
        /// </summary>
        public Task StopOperationsAsync()
        {
            if (_stopOperationsTask is not null)
                return _stopOperationsTask;
            _isStopping = true;
            NotifyDeviceCommands();
            _stopOperationsTask = StopOperationsCoreAsync();
            return _stopOperationsTask;
        }

        /// <summary>
        /// StopOperationsAsync 调用的实际等待：先取消并等 TCP 测试，再等正在进行的设备删除。
        /// </summary>
        private async Task StopOperationsCoreAsync()
        {
            await CancelConnectionTestAsync();
            if (DeleteDeviceCommand.ExecutionTask is { } deleting)
                await deleting;
        }


    }
}

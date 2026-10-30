// 管理点位列表、设备选择和单点读取测试。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;
using DeviceMonitor;

namespace DeviceMonitor.ViewModel
{
    public partial class PointConfigurationViewModel : PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;

        private readonly DeviceTasks _deviceTasks;

        private Func<DeviceInfo, IReadOnlyList<PointInfo>, ModbusPointReader> _createReader =
            (device, points) => new ModbusPointReader(device, points);
        private bool _isShuttingDown;

        private int _deviceRemovalVersion;

        // 只保存当前查询，刷新设备后可等点位载入；不再给旧选择排队。
        private Task _pointLoadTask = Task.CompletedTask;

        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        public ObservableCollection<PointInfo> Points { get; } = new();

        [ObservableProperty]

        private DeviceInfo? _selectedDevice;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ReadOnceCommand))]
        private PointInfo? _selectedPoint;

        private bool _isReading;
        public bool IsReading
        {
            get => _isReading;
            private set
            {
                if (!SetProperty(ref _isReading, value))
                    return;
                OnPropertyChanged(nameof(ReadOnceButtonText));
                NotifyOperationStateChanged();
            }
        }
        public string ReadOnceButtonText => IsReading ? "读取中…" : "读取一次";

        public string ReadGuardText => SelectedDevice is not null && _deviceTasks.IsDeletingDevice(SelectedDevice.Id)
            ? "这台设备正在删除，暂时不能配置或测试，请选择其他设备。"
            : SelectedDevice is not null && _deviceTasks.IsRunning(SelectedDevice.Id)
            ? "此设备正在采集或停止收尾，请等停止完成后再修改点位或测试单个点。"
            : string.Empty;

        private bool _isLoadingPoints;
        public bool IsLoadingPoints
        {
            get => _isLoadingPoints;
            private set
            {
                if (SetProperty(ref _isLoadingPoints, value))
                    NotifyOperationStateChanged();
            }
        }

        private bool _isRefreshingDevices;
        public bool IsRefreshingDevices
        {
            get => _isRefreshingDevices;
            private set
            {
                if (SetProperty(ref _isRefreshingDevices, value))
                    NotifyOperationStateChanged();
            }
        }

        public bool CanChooseDevice => !_isShuttingDown && !_deviceTasks.IsShuttingDown
            && !IsReading && !IsRefreshingDevices && !IsLoadingPoints;
        public bool CanChoosePoint => CanChooseDevice
            && (SelectedDevice is null || !_deviceTasks.IsDeletingDevice(SelectedDevice.Id));

        private string _pointMessage = "请先在上方选择一台设备。";
        public string PointMessage
        {
            get => _pointMessage;
            private set => SetProperty(ref _pointMessage, value);
        }

        private SingleReadState _readState = SingleReadState.NotRead;
        public SingleReadState ReadState
        {
            get => _readState;
            private set
            {
                if (SetProperty(ref _readState, value))
                    OnPropertyChanged(nameof(ReadStatusText));
            }
        }
        public string ReadStatusText => ReadState switch
        {
            SingleReadState.Reading => "读取中",
            SingleReadState.Succeeded => "读取成功",
            SingleReadState.TimedOut => "读取超时",
            SingleReadState.Failed => "读取失败",
            SingleReadState.Canceled => "已取消",
            _ => "尚未读取"
        };
        private string _readValueText = "—";
        public string ReadValueText
        {
            get => _readValueText; private set => SetProperty(ref _readValueText, value);
        }
        private string _readTimeText = "—";
        public string ReadTimeText
        {
            get => _readTimeText; private set => SetProperty(ref _readTimeText, value);
        }
        private string _readTargetText = "请选择设备和点位";
        public string ReadTargetText
        {
            get => _readTargetText; private set => SetProperty(ref _readTargetText, value);
        }

        private string _readResultText = string.Empty;
        public string ReadResultText
        {
            get => _readResultText;
            private set => SetProperty(ref _readResultText, value);
        }

        public event Action<DeviceInfo>? PointManagementRequested;

        private string _pageMessage = "正在准备已启用设备列表…";

        public string PageMessage
        {
            get => _pageMessage;
            private set => SetProperty(ref _pageMessage, value);
        }

        public PointConfigurationViewModel(
    UserAccount currentUser,
    DeviceStore deviceStore,
    DeviceTasks deviceTasks) : base("采集点配置")
        {
            _currentUser = currentUser
                ?? throw new ArgumentNullException(nameof(currentUser));

            _deviceStore = deviceStore
                ?? throw new ArgumentNullException(nameof(deviceStore));

            _deviceTasks = deviceTasks
                ?? throw new ArgumentNullException(nameof(deviceTasks));

            if (_currentUser.Role != UserRole.Administrator ||
                !_currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException(
                    "只有已启用的管理员可以进入采集管理。");
            }

            _deviceTasks.PropertyChanged += DeviceTasks_PropertyChanged;
            _deviceTasks.DeviceDeleted += DeviceTasks_DeviceDeleted;
        }

        /// <summary>查询启用设备并恢复原选择，再等这台设备的点位加载完成。</summary>
        [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
        private async Task RefreshDevicesAsync()
        {
            if (!CanRefreshDevices())
                return;
            IsRefreshingDevices = true;

            int removalVersionAtStart = _deviceRemovalVersion;
            try
            {
                PageMessage = "正在读取已启用设备配置…";

                var latestDevices = await Task.Run(
                    () => _deviceStore.GetDevices(_currentUser, enabledOnly: true));
                if (_isShuttingDown || _deviceTasks.IsShuttingDown || removalVersionAtStart != _deviceRemovalVersion)
                    return;

                long? selectedId = SelectedDevice?.Id;
                long? selectedPointId = SelectedPoint?.Id;

                Devices.Clear();
                foreach (DeviceInfo device in latestDevices)
                {
                    Devices.Add(device);
                }

                SelectedDevice = Devices.FirstOrDefault(device => device.Id == selectedId)
                    ?? Devices.FirstOrDefault();

                await _pointLoadTask;

                if (SelectedDevice?.Id == selectedId && selectedPointId.HasValue)
                    SelectedPoint = Points.FirstOrDefault(point => point.Id == selectedPointId)
                        ?? Points.FirstOrDefault();

                PageMessage = Devices.Count == 0
                    ? "暂无已启用设备，请先在设备管理中启用设备。"
                    : $"已加载 {Devices.Count} 台已启用设备。";
            }
            catch (Exception ex)
            {
                PageMessage = $"刷新设备失败：{ex.Message}";
            }
            finally
            {
                IsRefreshingDevices = false;
            }
        }

        private bool CanRefreshDevices() => CanChooseDevice;

        /// <summary>切换设备后加载对应点位并重置读取显示。</summary>
        partial void OnSelectedDeviceChanged(DeviceInfo? value)
        {
            bool isClosing = _isShuttingDown || _deviceTasks.IsShuttingDown;

            NotifyOperationStateChanged();

            SelectedPoint = null;
            Points.Clear();

            if (!isClosing)
                _pointLoadTask = LoadPointsForDeviceAsync(value);
        }

        /// <summary>切换点位后重置单次读取显示。</summary>
        partial void OnSelectedPointChanged(PointInfo? value)
        {
            if (!IsReading)
                ResetSingleReadResult();
        }

        /// <summary>只重置显示，不读取设备或删除历史。</summary>
        private void ResetSingleReadResult()
        {
            ReadState = SingleReadState.NotRead;
            ReadValueText = "—";
            ReadTimeText = "—";
            ReadTargetText = SelectedDevice is null ? "请选择设备和点位"
                : SelectedPoint is null ? $"{SelectedDevice.Name} · 请选择一个点位"
                : $"{SelectedDevice.Name} / {SelectedPoint.Name} · 地址 {SelectedPoint.Address}";
            ReadResultText = string.Empty;
        }

        /// <summary>查询当前设备的点位；期间禁用选择，完成后恢复。</summary>
        private async Task LoadPointsForDeviceAsync(DeviceInfo? device)
        {
            if (_isShuttingDown || _deviceTasks.IsShuttingDown)
                return;
            IsLoadingPoints = true;

            if (!IsReading)
                ResetSingleReadResult();

            try
            {
                if (device is null)
                {
                    PointMessage = "请先在上方选择一台设备。";
                    return;
                }

                PointMessage = $"正在加载“{device.Name}”的采集点…";

                long? selectedId = SelectedPoint?.Id;

                var latestPoints = await Task.Run(
                    () => _deviceStore.GetPoints(_currentUser, device.Id));

                if (_isShuttingDown || _deviceTasks.IsShuttingDown
                    || !ReferenceEquals(device, SelectedDevice))
                    return;

                SelectedPoint = null;
                Points.Clear();

                foreach (PointInfo point in latestPoints)
                    Points.Add(point);
                SelectedPoint = selectedId.HasValue
                    ? Points.FirstOrDefault(point => point.Id == selectedId.Value)
                        ?? Points.FirstOrDefault()
                    : Points.FirstOrDefault();

                PointMessage = Points.Count == 0
                    ? $"“{device.Name}”尚无采集点，请点击“管理采集点”添加配置。"
                    : $"“{device.Name}”已加载 {Points.Count} 个采集点。";
            }
            catch (Exception ex)
            {
                if (!_isShuttingDown && ReferenceEquals(device, SelectedDevice))
                    PointMessage = $"加载采集点失败：{ex.Message}";
            }
            finally
            {
                // 删除设备等程序操作仍可能换选择，旧查询不能解除新查询的忙状态。
                if (ReferenceEquals(device, SelectedDevice))
                    IsLoadingPoints = false;
            }
        }

        /// <summary>重新查询当前设备的点位配置，不读取设备数值。</summary>
        [RelayCommand(CanExecute = nameof(CanRefreshPoints))]
        private async Task RefreshPointsAsync()
        {
            if (!CanRefreshPoints())
                return;

            _pointLoadTask = LoadPointsForDeviceAsync(SelectedDevice);

            await _pointLoadTask;
        }

        private bool CanRefreshPoints() => CanChoosePoint && SelectedDevice is { IsEnabled: true };

        /// <summary>请求打开选中设备的采集点管理窗口。</summary>
        [RelayCommand(CanExecute = nameof(CanManagePoints))]
        private void ManagePoints()
        {
            DeviceInfo? device = SelectedDevice;
            if (!CanManagePoints() || device is null)
                return;

            PointManagementRequested?.Invoke(device);
        }

        /// <summary>管理员选中启用设备，且当前允许操作，才能管理点位。</summary>

        private bool CanManagePoints() => CanChoosePoint
            && SelectedDevice is { IsEnabled: true }
            && !_deviceTasks.IsRunning(SelectedDevice.Id);

        /// <summary>为点位窗口创建数据源，继续使用同一个用户和 DeviceStore。</summary>
        public PointManagementViewModel CreatePointManagementViewModel(DeviceInfo device)
        {
            ArgumentNullException.ThrowIfNull(device);
            // 开窗命令已检查设备状态；这里仅准备表单数据源。
            return new PointManagementViewModel(_currentUser, _deviceStore, device, _deviceTasks);
        }

        /// <summary>按最新配置读取选中的点一次，显示结果，不保存历史。</summary>

        [RelayCommand(CanExecute = nameof(CanReadOnce), IncludeCancelCommand = true)]
        private async Task ReadOnceAsync(CancellationToken cancellationToken)
        {
            DeviceInfo? device = SelectedDevice;
            PointInfo? point = SelectedPoint;
            if (!CanReadOnce() || device is null || point is null)
                return;

            // 登记本次测试目标，结束收尾前禁止其他页面删除设备。
            _deviceTasks.SingleReadDeviceId = device.Id;
            IsReading = true;
            ReadState = SingleReadState.Reading;
            ReadValueText = "—";
            ReadTimeText = "—";
            ReadTargetText = $"{device.Name} / {point.Name} · 地址 {point.Address}";
            ReadResultText = $"正在准备读取“{device.Name} / {point.Name}”…";
            try
            {
                // 读取前加载最新配置，不使用可能已过时的表格行直接通信。
                var config = await Task.Run(
                    () => LoadReadConfiguration(device.Id, point.Id), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                ReadResultText = $"正在读取“{config.Device.Name} / {config.Point.Name}”（地址 {config.Point.Address}）…";
                ReadTargetText = $"{config.Device.Name} / {config.Point.Name} · 地址 {config.Point.Address}";
                PageMessage = "正在等待设备响应，可以点击“取消读取”。";

                PointReadResult result;
                // 单点测试用临时对象，读完释放，不保存历史或自动轮询。
                await using (var reader = _createReader(config.Device, new[] { config.Point }))
                    result = (await reader.ReadAsync(cancellationToken))[0];
                cancellationToken.ThrowIfCancellationRequested();

                // 只有读取完整且未取消，才发布成功数值和时间。
                ReadValueText = result.ValueText;
                ReadTimeText = result.ReadAt.ToLocalTime().ToString("HH:mm:ss.fff");
                ReadResultText = $"实际值 {result.ValueText} · 原始整数 {result.DecodedValue} · {result.Point.ConversionDescription} · {result.Point.DataType} · 原始位 0x{result.RawValue:X4}";
                ReadState = SingleReadState.Succeeded;
                PageMessage = "读取完成。";
            }

            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReadResultText = "本次读取已取消，没有新的读数。";
                ReadState = SingleReadState.Canceled;
                PageMessage = "读取已取消，未保存历史记录。";
            }

            catch (TimeoutException ex)
            {
                AppLog.Warning("单点读取超时", $"设备={device.Id} 点位={point.Id}", ex);
                ReadResultText = ex.Message;
                ReadState = SingleReadState.TimedOut;
                PageMessage = "本次读取超时，没有有效读数，也未保存历史记录。";
            }
            catch (Exception ex)
            {
                AppLog.Warning("单点读取失败", $"设备={device.Id} 点位={point.Id}", ex);
                ReadResultText = ex.Message;
                ReadState = SingleReadState.Failed;
                PageMessage = "本次读取失败，没有有效读数，也未保存历史记录。";
            }
            finally
            {
                IsReading = false;
                // 完成资源收尾后，再解除跨页面的测试目标保护。
                _deviceTasks.SingleReadDeviceId = null;

                if (SelectedDevice?.Id != device.Id || SelectedPoint?.Id != point.Id)
                    ResetSingleReadResult();
            }
        }

        private bool CanReadOnce() => CanChoosePoint

            && SelectedDevice is { IsEnabled: true }
            && !_deviceTasks.IsRunning(SelectedDevice.Id)
            && SelectedPoint is not null
            && SelectedPoint.DeviceId == SelectedDevice.Id
            && Points.Contains(SelectedPoint);

        /// <summary>查回本次测试目标的最新配置，确认设备启用、点位仍存在。</summary>
        private (DeviceInfo Device, PointInfo Point) LoadReadConfiguration(long deviceId, long pointId)
        {
            DeviceInfo? device = _deviceStore.GetDevice(_currentUser, deviceId, enabledOnly: true);
            if (device is null)
                throw new InvalidOperationException("设备已停用或不存在，不能开始读取。");
            PointInfo? point = _deviceStore.GetPoints(_currentUser, device.Id)
                .FirstOrDefault(item => item.Id == pointId);
            if (point is null)
                throw new InvalidOperationException("选中的采集点已不存在，请刷新采集点。");
            return (device, point);
        }

        /// <summary>设备被删除后，清掉本页对应的设备和点位。</summary>
        private void DeviceTasks_DeviceDeleted(long deviceId)
        {
            if (_isShuttingDown)
                return;
            _deviceRemovalVersion++;
            bool wasSelected = SelectedDevice?.Id == deviceId;
            if (wasSelected)
            {
                ReadOnceCommand.Cancel();
                SelectedDevice = null;
            }
            DeviceInfo? removed = Devices.FirstOrDefault(device => device.Id == deviceId);
            if (removed is not null)
                Devices.Remove(removed);
            if (wasSelected)
                SelectedDevice = Devices.FirstOrDefault();
            PageMessage = "已移除删除的设备。";
        }

        /// <summary>设备任务状态变化后，重新判断本页按钮能不能点。</summary>
        private void DeviceTasks_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceTasks.ActiveSamplingCount) or nameof(DeviceTasks.IsShuttingDown)
                or nameof(DeviceTasks.IsDeletingDevices))
                NotifyOperationStateChanged();
        }

        /// <summary>更新能否选择、能否点击按钮，不执行查询或读取。</summary>
        private void NotifyOperationStateChanged()
        {
            OnPropertyChanged(nameof(CanChooseDevice));
            OnPropertyChanged(nameof(CanChoosePoint));
            OnPropertyChanged(nameof(ReadGuardText));
            ManagePointsCommand.NotifyCanExecuteChanged();
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            RefreshPointsCommand.NotifyCanExecuteChanged();
            ReadOnceCommand.NotifyCanExecuteChanged();
        }

        /// <summary>关闭本页操作：取消单次测试、等查询结束、解除订阅。</summary>
        public override async Task StopOperationsAsync()
        {
            _isShuttingDown = true;
            NotifyOperationStateChanged();
            ReadOnceCommand.Cancel();
            Task readTask = ReadOnceCommand.ExecutionTask ?? Task.CompletedTask;
            Task refreshDevices = RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask;
            Task refreshPoints = RefreshPointsCommand.ExecutionTask ?? Task.CompletedTask;

            await Task.WhenAll(_pointLoadTask, readTask, refreshDevices, refreshPoints);
            _deviceTasks.PropertyChanged -= DeviceTasks_PropertyChanged;
            _deviceTasks.DeviceDeleted -= DeviceTasks_DeviceDeleted;
        }
    }
}

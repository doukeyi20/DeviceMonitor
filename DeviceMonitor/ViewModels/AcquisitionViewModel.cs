using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Communication;
using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Sampling;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 采集点配置页：选设备、显示点位、打开管理窗口、单次测试。
    /// 单次测试不存历史，连续采集在实时监控页。
    /// </summary>
    public partial class AcquisitionViewModel : PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;
        // 共用管理器用来检查设备是否正在采集，避免单次测试与连续采集冲突。
        private readonly DeviceRuntimeManager _deviceRuntimeManager;
        // 真正连接设备、读取一个点的对象。
        private readonly ModbusPointReader _pointReader = new();
        private bool _isShuttingDown; // 退出后不再查新数据，也不回填旧查询结果。

        // 查询编号：只有最新查询能填表，避免切到 B 后又显示 A 的点位。
        private int _pointLoadVersion;
        private int _deviceListVersion; // 删除设备后递增，让删除前启动的设备查询作废。
        private Task _pointLoadTask = Task.CompletedTask; // 最近一次点位查询，刷新设备后要等它完成。
        // 记住所有未结束的点位查询，关窗时一起等完。
        private readonly List<Task> _pointLoadTasks = new();

        // 上方设备表绑定这里，每项是一个 DeviceInfo 设备配置。
        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        // 下方点位表绑定这里，每项是一个 PointInfo 配置，还没有真实读数。
        public ObservableCollection<PointInfo> Points { get; } = new();

        // 表格选择会写入 SelectedDevice，自动调用 OnSelectedDeviceChanged 加载点位。
        // 程序也要改 SelectedDevice 属性，直接改字段不会触发这些操作。
        [ObservableProperty]
        // 换了选中设备，就让“管理采集点”按钮重新判断能不能点。
        [NotifyCanExecuteChangedFor(nameof(ManagePointsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshPointsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ReadOnceCommand))]
        private DeviceInfo? _selectedDevice;

        // 下方表格选中的点，点击“读取一次”就测试它。
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ReadOnceCommand))]
        private PointInfo? _selectedPoint;

        private bool _isReading; // 单次测试还没结束时，禁止换设备和点位。
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

        // 只用于显示说明，不启动采集。
        public string ReadGuardText => SelectedDevice is not null && _deviceRuntimeManager.IsDeletingDevice(SelectedDevice.Id)
            ? "这台设备正在删除，暂时不能配置或测试，请选择其他设备。"
            : SelectedDevice is not null && _deviceRuntimeManager.IsRunning(SelectedDevice.Id)
            ? "此设备正在连续采集，请先到实时监控停止，再测试单个点。"
            : "单次测试只读选中点，不启动连续采集，也不保存历史记录。";

        private bool _isLoadingPoints; // 正在查点位配置，暂时不能测试未加载好的点。
        public bool IsLoadingPoints
        {
            get => _isLoadingPoints;
            private set
            {
                if (SetProperty(ref _isLoadingPoints, value))
                    NotifyOperationStateChanged();
            }
        }

        private bool _isRefreshingDevices; // 正在更新上方设备表。
        public bool IsRefreshingDevices
        {
            get => _isRefreshingDevices;
            private set
            {
                if (SetProperty(ref _isRefreshingDevices, value))
                    NotifyOperationStateChanged();
            }
        }

        // 读取期间先固定设备和点位，避免等待响应时把操作目标换掉。
        public bool CanChooseDevice => !_isShuttingDown && !_deviceRuntimeManager.IsShuttingDown
            && !IsReading && !IsRefreshingDevices;
        public bool CanChoosePoint => CanChooseDevice && !IsLoadingPoints
            && (SelectedDevice is null || !_deviceRuntimeManager.IsDeletingDevice(SelectedDevice.Id));

        // 点位表旁的提示，例如正在加载或没有配置。
        private string _pointMessage = "请先在上方选择一台设备。";
        public string PointMessage
        {
            get => _pointMessage;
            private set => SetProperty(ref _pointMessage, value);
        }

        // 单次测试区域显示的数值、时间或错误原因。
        private string _readResultText = "尚未读取。选设备和采集点后，点击“读取一次”。";
        public string ReadResultText
        {
            get => _readResultText;
            private set => SetProperty(ref _readResultText, value);
        }

        public event Action<DeviceInfo>? PointManagementRequested;

        private string _pageMessage = "正在准备已启用设备列表…";

        // 保留手写属性：界面可以读取，但只有本类能修改提示内容。
        public string PageMessage
        {
            get => _pageMessage;
            private set => SetProperty(ref _pageMessage, value);
        }

        /// <summary>
        /// 接住用户、配置数据库对象和共用管理器，页面加载后再查设备。
        /// </summary>
        public AcquisitionViewModel(
            UserAccount currentUser,
            DeviceStore deviceStore,
            DeviceRuntimeManager deviceRuntimeManager) : base("采集点配置")
        {
            _currentUser = currentUser
                ?? throw new ArgumentNullException(nameof(currentUser));

            _deviceStore = deviceStore
                ?? throw new ArgumentNullException(nameof(deviceStore));

            _deviceRuntimeManager = deviceRuntimeManager
                ?? throw new ArgumentNullException(nameof(deviceRuntimeManager));

            if (_currentUser.Role != UserRole.Administrator ||
                !_currentUser.IsEnabled)
            {
                throw new UnauthorizedAccessException(
                    "只有已启用的管理员可以进入采集管理。");
            }

            // 监控页启停设备后，本页重新判断单次测试能不能点，不显示采集数量。
            _deviceRuntimeManager.PropertyChanged += DeviceRuntimeManager_PropertyChanged;
            _deviceRuntimeManager.DeviceDeleted += DeviceRuntimeManager_DeviceDeleted;
        }

        /// <summary>
        /// 由 RefreshDevicesCommand 调用；AcquisitionView_Loaded 也会执行此命令。
        /// 查询启用设备并恢复原选择，再等这台设备的点位加载完成。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
        private async Task RefreshDevicesAsync()
        {
            if (!CanRefreshDevices())
                return;
            IsRefreshingDevices = true;
            int listVersion = _deviceListVersion;
            try
            {
                PageMessage = "正在读取已启用设备配置…";

                // 在后台查询启用设备；查询完再回来更新表格。
                var latestDevices = await Task.Run(
                    () => _deviceStore.GetDevices(_currentUser, enabledOnly: true));
                if (_isShuttingDown || _deviceRuntimeManager.IsShuttingDown || listVersion != _deviceListVersion)
                    return; // 关窗或删除设备后，旧查询不能把失效配置放回页面。

                // 记住选中编号，查询成功后再替换设备列表。
                long? selectedId = SelectedDevice?.Id;

                // 查询成功才清空旧列表；查询失败时保留原来的显示。
                Devices.Clear();
                foreach (DeviceInfo device in latestDevices)
                {
                    Devices.Add(device);
                }

                // 按编号恢复选择；原设备已删除或停用时，就不再选中它。
                SelectedDevice = selectedId.HasValue
                    ? Devices.FirstOrDefault(
                        device => device.Id == selectedId.Value)
                    : null;

                // 恢复设备选择后，它的点位也会自动查询；等这次查询结束再恢复按钮。
                await _pointLoadTask;

                PageMessage = Devices.Count == 0
                    ? "暂无已启用设备，请先在设备管理中启用设备。"
                    : $"已加载 {Devices.Count} 台已启用设备，设备配置已刷新。";
            }
            catch (Exception ex)
            {
                // 查询失败显示原因，原列表不清空。
                PageMessage = $"刷新设备失败：{ex.Message}";
            }
            finally
            {
                IsRefreshingDevices = false;
            }
        }

        // 判断现在能不能刷新设备。
        // 由 RefreshDevicesCommand 判断按钮能不能点时调用。
        private bool CanRefreshDevices() => CanChooseDevice;

        /// <summary>
        /// SelectedDevice 属性变化时自动调用：清掉旧点位，再加载新设备的点位。
        /// </summary>
        partial void OnSelectedDeviceChanged(DeviceInfo? value)
        {
            bool isClosing = _isShuttingDown || _deviceRuntimeManager.IsShuttingDown;
            // 检查按钮能否点击
            NotifyOperationStateChanged();

            // 换了设备，点位列表要重新加载。先清空旧列表，避免显示过期数据。
            SelectedPoint = null;
            Points.Clear();

            // 没在关闭就查这台设备的点位；已经开始关闭，就不再发起查询。
            _pointLoadTask = isClosing ? Task.CompletedTask : StartPointLoadAsync(value);
        }

        /// <summary>
        /// 由 OnSelectedDeviceChanged 和 RefreshPointsAsync 调用。
        /// 启动点位查询，并记住任务，关窗时统一等完。
        /// </summary>
        private Task StartPointLoadAsync(DeviceInfo? device)
        {
            // 已完成的查询不用再登记，只保留还没结束的。
            _pointLoadTasks.RemoveAll(task => task.IsCompleted);

            // 查询这个设备的点位列表，给第二张配置表使用。
            Task query = LoadPointsForDeviceAsync(device);

            // 把这次查询任务记下来，关窗时一起等待结束。
            _pointLoadTasks.Add(query);

            return query;
        }

        /// <summary>
        /// 由 StartPointLoadAsync 调用。
        /// 从数据库查这台设备的点位，填入 Points；旧查询结果不能覆盖新选择。
        /// </summary>
        private async Task LoadPointsForDeviceAsync(DeviceInfo? device)
        {
            // 给本次查询编号，并设置“加载中”状态
            int loadVersion = ++_pointLoadVersion;
            IsLoadingPoints = true;
            // 重置单次测试区域的提示。
            ReadResultText = "尚未读取。请选择一个采集点，再点击“读取一次”。";
            
            try
            {
                if (device is null)
                {
                    PointMessage = "请先在上方选择一台设备。";
                    return;
                }

                PointMessage = $"正在加载“{device.Name}”的采集点…";

                long? selectedId = SelectedPoint?.Id;

                // 后台按设备编号查点位。
                var latestPoints = await Task.Run(
                    () => _deviceStore.GetPoints(_currentUser, device.Id));

                // 等待期间可能换了设备。过期的查询结果不能覆盖当前列表。
                if (_isShuttingDown || _deviceRuntimeManager.IsShuttingDown
                    || loadVersion != _pointLoadVersion || !ReferenceEquals(device, SelectedDevice))
                    return;

                // 查询仍有效才替换表格，再按编号恢复原来选中的点。
                SelectedPoint = null;
                Points.Clear();

                foreach (PointInfo point in latestPoints)
                    Points.Add(point);
                SelectedPoint = selectedId.HasValue
                    ? Points.FirstOrDefault(point => point.Id == selectedId.Value)
                    : null;

                PointMessage = Points.Count == 0
                    ? $"“{device.Name}”尚无采集点，请点击“管理采集点”添加配置。"
                    : $"“{device.Name}”已加载 {Points.Count} 个采集点，请选择要读取的点位。";
            }
            catch (Exception ex)
            {
                // 只显示最新查询的错误，旧查询的错误不覆盖当前提示。
                if (loadVersion == _pointLoadVersion)
                    PointMessage = $"加载采集点失败：{ex.Message}";
            }
            finally
            {
                // 只有最新查询能解除加载标记，不能让旧查询提前恢复按钮。
                if (loadVersion == _pointLoadVersion)
                    IsLoadingPoints = false;
            }
        }

        /// <summary>
        /// 由 RefreshPointsCommand 调用。
        /// 重新查询当前设备的点位配置，不读取设备数值。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanRefreshPoints))]
        private async Task RefreshPointsAsync()
        {
            if (!CanRefreshPoints())
                return;

            // 手动刷新和关闭配置弹窗后，都走同一份点位加载逻辑。
            _pointLoadTask = StartPointLoadAsync(SelectedDevice);

            await _pointLoadTask;
        }

        // 选中启用设备且当前允许操作，才可以刷新点位。
        // 由 RefreshPointsCommand 判断按钮能不能点时调用。
        private bool CanRefreshPoints() => CanChoosePoint && SelectedDevice is { IsEnabled: true };



        /// <summary>
        /// 由 ManagePointsCommand 调用。
        /// 请求打开选中设备的采集点管理窗口。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanManagePoints))]
        private void ManagePoints()
        {
            // 先记住这次要操作的设备，后面把它传给窗口。
            DeviceInfo? device = SelectedDevice;
            if (!CanManagePoints() || device is null)
                return;

            // 让 AcquisitionView.OpenPointWindow 打开这个设备的点位窗口，关闭后刷新点位列表。
            PointManagementRequested?.Invoke(device);
        }


        /// <summary>
        /// 由 ManagePointsCommand 判断按钮能不能点时调用。
        /// 管理员选中启用设备，且当前允许操作，才能管理点位。
        /// </summary>
        private bool CanManagePoints()
        {
            return _currentUser.Role == UserRole.Administrator
                && _currentUser.IsEnabled
                && CanChoosePoint
                && SelectedDevice is { IsEnabled: true };
        }

        /// <summary>
        /// 由 AcquisitionView.OpenPointWindow 打开点位窗口前调用。
        /// 为点位窗口创建数据源，继续使用同一个用户和 DeviceStore。
        /// </summary>
        public PointViewModel CreatePointViewModel(DeviceInfo device)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (_deviceRuntimeManager.IsDeletingDevice(device.Id))
                throw new InvalidOperationException("这台设备正在删除，不能再打开它的采集点配置。");
            if (_currentUser.Role != UserRole.Administrator ||
                !_currentUser.IsEnabled || !device.IsEnabled)
            {
                throw new UnauthorizedAccessException("请先启用设备，再配置采集点。");
            }

            // 只新建窗口数据源，不新建数据库；device 决定管理哪台设备的点位。
            return new PointViewModel(_currentUser, _deviceStore, device);
        }


        /// <summary>
        /// 由 ReadOnceCommand 调用。
        /// 按最新配置读取选中的点一次，显示结果，不保存历史。
        /// </summary>
        // 工具同时生成 ReadOnceCommand 和 ReadOnceCancelCommand。
        [RelayCommand(CanExecute = nameof(CanReadOnce), IncludeCancelCommand = true)]
        private async Task ReadOnceAsync(CancellationToken cancellationToken)
        {
            // 得到点击时的设备和点位，避免等待响应期间用户换了选择。
            DeviceInfo? device = SelectedDevice;
            PointInfo? point = SelectedPoint;
            if (!CanReadOnce() || device is null || point is null)
                return;

            // 标记测试开始，期间不能更换操作目标。
            IsReading = true;
            ReadResultText = $"正在准备读取“{device.Name} / {point.Name}”…";
            try
            {
                // 从数据库拿最新的已保存配置。
                var config = await Task.Run(
                    () => LoadReadConfiguration(device.Id, point.Id), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // 等数据库返回时可能已在另一页删除设备，不要再发起新的测试连接。
                if (_deviceRuntimeManager.IsDeletingDevice(device.Id))
                    throw new InvalidOperationException("这台设备正在删除，本次测试不再开始。");
                ReadResultText = $"正在读取“{config.Device.Name} / {config.Point.Name}”（地址 {config.Point.Address}）…";
                PageMessage = "正在等待设备响应，可以点击“取消读取”。";

                // 设备提供连接地址，点位提供寄存器地址和类型；读取器负责通信。
                PointReadResult result = await _pointReader.ReadAsync(
                    config.Device, config.Point, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // 读取成功，把数值、类型和时间拼成文字显示在测试区域。
                ReadResultText =
                    $"{config.Device.Name} / {result.Point.Name}（编号 {result.Point.Id}，地址 {result.Point.Address}）：" +
                    $"{result.Value} [{result.Point.DataType}]；原始值 0x{result.RawValue:X4}；" +
                    $"时间 {result.ReadAt.ToLocalTime():HH:mm:ss.fff}。";
                PageMessage = "单次读取完成，临时连接已关闭；未启动连续采集，也未保存历史记录。";
            }
            // 收到取消信号后，读取抛出取消异常，走这里显示取消结果。
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReadResultText = "本次读取已取消，没有新的读数。";
                PageMessage = "读取已取消，临时连接已关闭。";
            }
            catch (TimeoutException ex)
            {
                AppLog.Warning("单次测试读取超时", exception: ex);
                ReadResultText = "本次读取超时，没有有效读数。";
                PageMessage = ex.Message;
            }
            catch (Exception ex)
            {
                AppLog.Warning("单次测试读取失败", exception: ex);
                ReadResultText = "本次读取失败，没有有效读数。";
                PageMessage = $"读取失败：{ex.Message}";
            }
            finally
            {
                IsReading = false;
            }
        }

        // 必须选中当前设备的点，而且这台设备没有正在连续采集。
        // 由 ReadOnceCommand 判断按钮能不能点时调用。
        private bool CanReadOnce() => CanChoosePoint
            // 同一台设备正在连续采集时，不再额外插入一次单点读取。
            && SelectedDevice is { IsEnabled: true }
            && !_deviceRuntimeManager.IsRunning(SelectedDevice.Id)
            && SelectedPoint is not null
            && SelectedPoint.DeviceId == SelectedDevice.Id
            && Points.Contains(SelectedPoint);

        /// <summary>
        /// 由 ReadOnceAsync 在真正读取之前调用。
        /// 查回本次测试目标的最新配置，确认设备启用、点位仍存在。
        /// </summary>
        private (DeviceInfo Device, PointInfo Point) LoadReadConfiguration(long deviceId, long pointId)
        {
            // 点击时已经记住设备编号，直接查询这一台的最新配置即可。
            DeviceInfo? device = _deviceStore.GetDevice(_currentUser, deviceId, enabledOnly: true);
            if (device is null)
                throw new InvalidOperationException("设备已停用或不存在，不能开始读取。");
            PointInfo? point = _deviceStore.GetPoints(_currentUser, device.Id)
                .FirstOrDefault(item => item.Id == pointId);
            if (point is null)
                throw new InvalidOperationException("选中的采集点已不存在，请刷新采集点。");
            return (device, point);
        }

        /// <summary>
        /// 由共用管理器的 DeviceDeleted 事件调用。
        /// 设备被删除后，清掉本页对应的设备和点位。
        /// </summary>
        private void DeviceRuntimeManager_DeviceDeleted(long deviceId)
        {
            if (_isShuttingDown) return;
            _deviceListVersion++;
            if (SelectedDevice?.Id == deviceId)
            {
                ReadOnceCommand.Cancel();
                SelectedDevice = null; // 自动清空点位，并让之前的点位查询作废。
            }
            DeviceInfo? removed = Devices.FirstOrDefault(device => device.Id == deviceId);
            if (removed is not null) Devices.Remove(removed);
            PageMessage = "已移除删除的设备，请重新选择要配置的设备。";
        }

        /// <summary>
        /// 由共用管理器的 PropertyChanged 事件调用。
        /// 管理器状态变化后，重新判断本页按钮能不能点。
        /// </summary>
        private void DeviceRuntimeManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceRuntimeManager.ActiveSamplingCount) or nameof(DeviceRuntimeManager.IsShuttingDown)
                or nameof(DeviceRuntimeManager.IsDeletingDevices))
                NotifyOperationStateChanged();
        }

        /// <summary>
        /// 由 IsReading 等状态属性变化、设备选择变化、管理器通知和退出处理调用。
        /// 更新能否选择、能否点击按钮，不执行查询或读取。
        /// </summary>
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

        /// <summary>
        /// 由 MainViewModel.StopBackgroundWorkAsync 在退出前调用。
        /// 关闭本页操作：取消单次测试、等查询结束、解除订阅。
        /// 连续采集由共用管理器另行停止。
        /// </summary>
        public async Task StopOperationsAsync()
        {
            _isShuttingDown = true;
            NotifyOperationStateChanged();
            ReadOnceCommand.Cancel();
            Task readTask = ReadOnceCommand.ExecutionTask ?? Task.CompletedTask;
            Task refreshDevices = RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask;
            Task refreshPoints = RefreshPointsCommand.ExecutionTask ?? Task.CompletedTask;
            // 等测试和所有旧查询结束，不只等最后一次；读取结束时会关闭临时连接。
            await Task.WhenAll(_pointLoadTasks.Concat(new[] { readTask, refreshDevices, refreshPoints }).ToArray());
            _pointLoadTasks.Clear();
            _deviceRuntimeManager.PropertyChanged -= DeviceRuntimeManager_PropertyChanged;
            _deviceRuntimeManager.DeviceDeleted -= DeviceRuntimeManager_DeviceDeleted;
        }
    }
}

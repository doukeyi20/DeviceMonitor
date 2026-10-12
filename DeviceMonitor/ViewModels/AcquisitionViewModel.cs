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
    /// “采集点配置”页的数据源：上表选启用设备，下表显示该设备的 PointInfo 配置。
    /// 管理按钮开点位表单；“读取一次”只测一个点，不存历史；连续采集在实时监控页。
    /// </summary>
    public partial class AcquisitionViewModel : PageViewModel
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;
        // 这里只向共用管理器问“这台设备是否正在采集”，防止单次测试与连续读取冲突。
        // 本页不创建、不开始、不停止连续采集任务。
        private readonly DeviceRuntimeManager _deviceRuntimeManager;
        // 真正连接设备、读取寄存器的对象。沿用原来的读取器，不另写通信代码。
        private readonly ModbusPointReader _pointReader = new();
        private bool _isShuttingDown;

        // 快速切换设备时，旧查询可能比新查询晚完成。
        // 只有最后一次查询可以更新表格，避免把 A 的点位显示到 B 下面。
        private int _pointLoadVersion;
        private int _deviceListVersion;
        private Task _pointLoadTask = Task.CompletedTask;
        // 快速换设备会有几次查询同时等待。最后一次只负责当前页面，关闭时却要等它们全部结束。
        private readonly List<Task> _pointLoadTasks = new();

        // 上方表格绑定这个集合；集合变化时，表格会更新。
        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        // 第二张表显示当前选中设备的采集点配置，不是连续采集的读数。
        public ObservableCollection<PointInfo> Points { get; } = new();

        // 上表 SelectedItem → 生成属性 SelectedDevice → 自动调用 OnSelectedDeviceChanged。
        // 程序恢复选择时也给 SelectedDevice 赋值，不直接改 _selectedDevice，才能触发点位查询和通知。
        [ObservableProperty]
        // 换了选中设备，就让“管理采集点”按钮重新判断能不能点。
        [NotifyCanExecuteChangedFor(nameof(ManagePointsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshPointsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ReadOnceCommand))]
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

        // 只显示说明，不启动任何操作；选设备或监控任务启停时会通知界面重新读取。
        public string ReadGuardText => SelectedDevice is not null && _deviceRuntimeManager.IsDeletingDevice(SelectedDevice.Id)
            ? "这台设备正在删除，暂时不能配置或测试，请选择其他设备。"
            : SelectedDevice is not null && _deviceRuntimeManager.IsRunning(SelectedDevice.Id)
            ? "此设备正在连续采集，请先到实时监控停止，再测试单个点。"
            : "单次测试只读选中点，不启动连续采集，也不保存历史记录。";

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

        // 读取期间先固定设备和点位，避免等待响应时把操作目标换掉。
        public bool CanChooseDevice => !_isShuttingDown && !_deviceRuntimeManager.IsShuttingDown
            && !IsReading && !IsRefreshingDevices;
        public bool CanChoosePoint => CanChooseDevice && !IsLoadingPoints
            && (SelectedDevice is null || !_deviceRuntimeManager.IsDeletingDevice(SelectedDevice.Id));

        private string _pointMessage = "请先在上方选择一台设备。";
        public string PointMessage
        {
            get => _pointMessage;
            private set => SetProperty(ref _pointMessage, value);
        }

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
        /// 由 MainViewModel 创建，并放进管理员的 Pages 中。
        /// 保存用户、查配置用的 DeviceStore，以及共用 DeviceRuntimeManager；设备列表等页面加载后再查询。
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
        /// 页面 Loaded加载时 或“刷新设备”通过 RefreshDevicesCommand 调用。后台查启用设备，回来更新 Devices 并恢复选择。
        /// 给 SelectedDevice 赋新对象会自动进入 OnSelectedDeviceChanged；这里等那次点位查询结束。
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

                // 在 SQL 查询中只取已启用设备，不在界面层过滤。
                // GetDevices 是同步查询，把它放到后台线程执行。
                // 后台只查询数据库，不操作界面绑定的 Devices 集合。
                var latestDevices = await Task.Run(
                    () => _deviceStore.GetDevices(_currentUser, enabledOnly: true));
                if (_isShuttingDown || _deviceRuntimeManager.IsShuttingDown || listVersion != _deviceListVersion)
                    return; // 关窗或删除设备后，旧查询不能把失效配置放回页面。

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

                // 恢复设备选择后，它的点位也会自动查询；等这次查询结束再恢复按钮。
                await _pointLoadTask;

                PageMessage = Devices.Count == 0
                    ? "暂无已启用设备，请先在设备管理中启用设备。"
                    : $"已加载 {Devices.Count} 台已启用设备，设备配置已刷新。";
            }
            catch (Exception ex)
            {
                // 捕获查询异常，显示提示，避免命令出错导致程序退出。
                PageMessage = $"刷新设备失败：{ex.Message}";
            }
            finally
            {
                IsRefreshingDevices = false;
            }
        }

        // RefreshDevicesCommand 用它判断按钮能不能点；RefreshDevicesAsync 也会再检查一次。返回 false 时不允许刷新。
        private bool CanRefreshDevices() => CanChooseDevice;

        /// <summary>
        /// 自动入口：当在设备表选择不同设备时，上表 SelectedItem 更新生成属性 SelectedDevice 后，它的 setter 自动调用这里。
        /// 先清旧点位，再调用 StartPointLoadAsync 查询新设备的配置；不把 DeviceInfo 转成 PointInfo，也不创建采集任务。
        /// </summary>
        partial void OnSelectedDeviceChanged(DeviceInfo? value)
        {
            bool isClosing = _isShuttingDown || _deviceRuntimeManager.IsShuttingDown;
            // 检查按钮能否点击
            NotifyOperationStateChanged();

            // 换了设备，点位列表要重新加载。先清空旧列表，避免显示过期数据。
            SelectedPoint = null;
            Points.Clear();

            // 查询这个设备的点位列表，给第二张配置表使用。
            // 没在关窗或退出收尾，就执行后面的点位查询；正在收尾，就不再启动查询。
            _pointLoadTask = isClosing ? Task.CompletedTask : StartPointLoadAsync(value);
        }

        /// <summary>
        /// 采集点的查询
        /// 选择设备或刷新点位时调用。启动 LoadPointsForDeviceAsync，并记住 Task，关窗时要连旧查询一起等完。
        /// </summary>
        private Task StartPointLoadAsync(DeviceInfo? device)
        {
            // 旧查询可能还在等待，关窗时要等它们全部结束。清掉已完成的旧查询，避免列表无限增长。
            _pointLoadTasks.RemoveAll(task => task.IsCompleted);

            // 查询这个设备的点位列表，给第二张配置表使用。
            Task query = LoadPointsForDeviceAsync(device);

            // 把查询任务记下来，关窗时要等它们全部结束。快速换设备查询时，旧查询可能比新查询晚完成。
            // 为了让关窗时统一等待执行完，所以要登记
            _pointLoadTasks.Add(query);

            return query;
        }

        /// <summary>
        /// 真正调用数据库查询点位的方法
        /// StartPointLoadAsync 调用：按 device.Id 查 DeviceStore.GetPoints，再把 PointInfo 放入 Points。
        /// await 期间可能换设备，只有当前这次查询能填表；旧结果不能覆盖后来选中的设备。
        /// </summary>
        private async Task LoadPointsForDeviceAsync(DeviceInfo? device)
        {
            // 给本次查询编号，并设置“加载中”状态
            int loadVersion = ++_pointLoadVersion;
            IsLoadingPoints = true;
            // 该属性让前端绑定使用
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

                // 数据库中查询设备采集点
                var latestPoints = await Task.Run(
                    () => _deviceStore.GetPoints(_currentUser, device.Id));

                // 等待期间可能换了设备。过期的查询结果不能覆盖当前列表。
                if (_isShuttingDown || _deviceRuntimeManager.IsShuttingDown
                    || loadVersion != _pointLoadVersion || !ReferenceEquals(device, SelectedDevice))
                    return;

                SelectedPoint = null;
                // 清除表格旧数据
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
                // 属性改变时启动的查询也在这里处理错误，不让异常导致程序退出。
                if (loadVersion == _pointLoadVersion)
                    PointMessage = $"加载采集点失败：{ex.Message}";
            }
            finally
            {
                if (loadVersion == _pointLoadVersion)
                    IsLoadingPoints = false;
            }
        }

        /// <summary>
        /// “刷新采集点”或点位弹窗关闭后调用。沿用当前 SelectedDevice，重新查点位配置，不读取寄存器。
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

        // RefreshPointsCommand 用它判断能不能刷新点位：当前可操作，并且已选中一台启用设备。
        private bool CanRefreshPoints() => CanChoosePoint && SelectedDevice is { IsEnabled: true };



        /// <summary>
        /// 管理采集点按钮调用，有开窗。
        /// 把点击时的 DeviceInfo 通过事件交给 AcquisitionView.OpenPointWindow；这里不直接创建窗口。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanManagePoints))]
        private void ManagePoints()
        {
            // 先记住这次要操作的设备，后面把它传给窗口。
            DeviceInfo? device = SelectedDevice;
            if (!CanManagePoints() || device is null)
                return;

            // 调用 AcquisitionView.xaml.cs 的 OpenPointWindow：把选中的 device 传给后台。
            // 后台调用本类的 CreatePointViewModel 准备数据源，再打开 PointWindow 管理这台设备的点位。
            // 点位窗口关闭后，后台还会执行 RefreshPointsCommand，让本页显示最新配置。
            PointManagementRequested?.Invoke(device);
        }


        /// <summary>
        /// 管理按钮判断：已启用管理员、已选启用设备、当前允许选点；返回 false 时按钮禁用。
        /// </summary>
        private bool CanManagePoints()
        {
            return _currentUser.Role == UserRole.Administrator
                && _currentUser.IsEnabled
                && CanChoosePoint
                && SelectedDevice is { IsEnabled: true };
        }

        /// <summary>
        /// AcquisitionView.OpenPointWindow 调用。给弹窗新建 PointViewModel，传同一个用户、DeviceStore 和目标设备。
        /// 新建的是表单数据源，不是新数据库；设备 Id 决定管理哪台设备的点。
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

            // 新建的是窗口自己的 ViewModel，不是新的数据库。
            // 同一个 DeviceStore 继续传下去；device.Id 决定查哪台设备的点位。
            return new PointViewModel(_currentUser, _deviceStore, device);
        }


        /// <summary>
        /// 点击“读取一次”后，ReadOnceCommand 调用这里；“取消读取”使用工具生成的 ReadOnceCancelCommand。
        /// 先用 LoadReadConfiguration 查询最新配置，再调用 ModbusPointReader.ReadAsync 读所选点一次，把结果写入页面提示。
        /// 这是单点测试，不启动连续采集，也不把读数写进历史数据库。
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

            // 先记住本次读取的设备和点位编号，后面一直使用这对目标。
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
                ReadResultText =
                    $"{config.Device.Name} / {result.Point.Name}（编号 {result.Point.Id}，地址 {result.Point.Address}）：" +
                    $"{result.Value} [{result.Point.DataType}]；原始值 0x{result.RawValue:X4}；" +
                    $"时间 {result.ReadAt.ToLocalTime():HH:mm:ss.fff}。";
                PageMessage = "单次读取完成，临时连接已关闭；未启动连续采集，也未保存历史记录。";
            }
            // 取消读取时，ReadOnceCancelCommand 会触发 CancellationToken，抛出 OperationCanceledException。
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

        // ReadOnceCommand 和 ReadOnceAsync 用它检查：选中了属于当前设备的点位、当前可操作，而且这台设备没在连续采集。
        private bool CanReadOnce() => CanChoosePoint
            // 同一台设备正在连续采集时，不再额外插入一次单点读取。
            && SelectedDevice is { IsEnabled: true }
            && !_deviceRuntimeManager.IsRunning(SelectedDevice.Id)
            && SelectedPoint is not null
            && SelectedPoint.DeviceId == SelectedDevice.Id
            && Points.Contains(SelectedPoint);

        /// <summary>
        /// 由 ReadOnceAsync 通过 Task.Run 放到后台调用，传入点击时记住的设备 Id 和点位 Id。
        /// 从 DeviceStore 重新查最新配置，确认设备仍启用、点位仍存在；只返回配置，真正通信交给 ModbusPointReader。
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
        /// 共用管理器删完设备后通知这里，把本页失效的设备/点位清掉，防止继续测试已删除的配置。
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
        /// 构造时订阅了管理器的状态通知。任务启停、开始退出或设备删除时，让本页按钮重新检查条件。
        /// </summary>
        private void DeviceRuntimeManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceRuntimeManager.ActiveSamplingCount) or nameof(DeviceRuntimeManager.IsShuttingDown)
                or nameof(DeviceRuntimeManager.IsDeletingDevices))
                NotifyOperationStateChanged();
        }

        /// <summary>
        /// 选设备、查询或测试状态变化后调用：更新能否选择的属性和按钮条件，不执行数据库或读取操作。
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
        /// 主窗口关闭时，MainWindow.OnClosing → MainViewModel.StopBackgroundWorkAsync → 这里。
        /// 只取消本页的单次测试、等待配置查询，并解除本页订阅。
        /// 连续采集由 MainViewModel 另行通知共用管理器停止，不再由配置页负责。
        /// </summary>
        public async Task StopOperationsAsync()
        {
            _isShuttingDown = true;
            NotifyOperationStateChanged();
            ReadOnceCommand.Cancel();
            Task readTask = ReadOnceCommand.ExecutionTask ?? Task.CompletedTask;
            Task refreshDevices = RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask;
            Task refreshPoints = RefreshPointsCommand.ExecutionTask ?? Task.CompletedTask;
            // 等取消真正完成，读取器的 using 才能释放尚未关闭的连接。
            // 快速换过几次设备时，不能只等最后一次查询；旧查询也要在关窗前结束。
            await Task.WhenAll(_pointLoadTasks.Concat(new[] { readTask, refreshDevices, refreshPoints }).ToArray());
            _pointLoadTasks.Clear();
            _deviceRuntimeManager.PropertyChanged -= DeviceRuntimeManager_PropertyChanged;
            _deviceRuntimeManager.DeviceDeleted -= DeviceRuntimeManager_DeviceDeleted;
        }
    }
}

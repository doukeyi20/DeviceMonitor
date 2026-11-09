// 管理设备的点位表单、配置保存和单点测试。
using DeviceMonitor.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel
{
    /// <summary>维护当前设备的点位表单；保存只改配置，ReadOnceAsync 才真正访问设备。</summary>
    public partial class PointEditorViewModel : ObservableObject, IDisposable
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;
        private readonly SamplingTasks? _samplingTasks;
        // 旧点位编辑器已不用的标记：切换设备或退出时在 Dispose 中打上，拦住后续配置操作和重复清理。
        private bool _isClosed;

        // 一次测试只保留一份结果；大数字、单位和说明都从它取得。
        private PointReadResult? _testResult;
        private Func<DeviceInfo, IReadOnlyList<PointInfo>, ModbusPointReader> _createReader =
            (device, points) => new ModbusPointReader(device, points);

        [ObservableProperty]
        // 点位编辑器的忙碌标记：加载、保存、删除或单点测试时打上，避免操作撞在一起；finally 中清掉。
        private bool _isBusy;

        public bool CanChoosePoint => !IsBusy;

        public DeviceInfo Device { get; }

        public ObservableCollection<PointInfo> Points { get; } = new();

        private PointInfo? _selectedPoint;

        public PointInfo? SelectedPoint
        {
            get => _selectedPoint;
            set
            {
                if (IsBusy || ReferenceEquals(_selectedPoint, value))
                    return;
                if (value is not null && (value.DeviceId != Device.Id || !Points.Contains(value)))
                    return;
                // 选中就载入；切换会放弃未保存输入，不再维护选择回退流程。
                SelectPointAndLoadForm(value);
            }
        }

        public event Func<PointInfo, bool>? DeleteConfirmationRequested;

        public bool IsEditing => SelectedPoint is not null;
        public bool CanEditConfiguration => CanChangeConfiguration();
        public string FormTitle => SelectedPoint is null
            ? "新增采集点"
            : $"编辑：{SelectedPoint.Name}（编号 {SelectedPoint.Id}）";
        public string SaveButtonText => IsEditing ? "保存修改" : "保存新增采集点";

        [ObservableProperty]
        private string _pointName = string.Empty;

        [ObservableProperty]
        private string _addressText = string.Empty;

        [ObservableProperty]
        private RegisterType? _selectedRegisterType;

        [ObservableProperty]
        private PointDataType? _selectedDataType;

        [ObservableProperty]
        private string _lowerLimitText = string.Empty;

        [ObservableProperty]
        private string _upperLimitText = string.Empty;

        [ObservableProperty]
        private string _scaleText = "1";

        [ObservableProperty]
        private string _offsetText = "0";

        [ObservableProperty]
        private string _unitText = string.Empty;

        private string _pageMessage = string.Empty;

        public string PageMessage
        {
            get => _pageMessage;
            private set => SetProperty(ref _pageMessage, value);
        }

        // 单次测试的显示属性；只有成功结果保存在 _testResult 中。
        [ObservableProperty]
        private SingleReadState _readState;

        [ObservableProperty]
        private string _readMessage = "";

        public string ReadValueText => _testResult?.ValueText ?? "—";
        public string ReadDetails => _testResult is null ? ReadMessage
            : $"原始值 {_testResult.DecodedValue} · {_testResult.Point.ConversionDescription} · {_testResult.ReadAt.LocalDateTime:HH:mm:ss.fff}";
        public string ReadStatusText => ReadState switch
        {
            SingleReadState.Reading => "读取中",
            SingleReadState.Succeeded => "读取成功",
            SingleReadState.TimedOut => "读取超时",
            SingleReadState.Failed => "读取失败",
            SingleReadState.Canceled => "已取消",
            _ => "单点测试"
        };
        public PointEditorViewModel(UserAccount currentUser, DeviceStore deviceStore, DeviceInfo device,
            SamplingTasks? deviceTasks = null)
        {
            _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            Device = device ?? throw new ArgumentNullException(nameof(device));
            _samplingTasks = deviceTasks;
            if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以配置采集点。");

            if (_samplingTasks is not null)
                _samplingTasks.PropertyChanged += SamplingTasks_PropertyChanged;

            // 载入配置，传入null时，清空配置单
            LoadForm(null);
        }

     /// <summary>
     /// 刷新点位列表的按钮
     /// </summary>
     /// <returns></returns>
        [RelayCommand(CanExecute = nameof(CanRefreshPoints))]
        private async Task RefreshPointsAsync()
        {
            if (!CanRefreshPoints())
                return;
            try
            {
                IsBusy = true;
                // 不仅查询还恢复选择
                await LoadPointsAsync();
                PageMessage = Points.Count == 0
                    ? "当前设备尚无采集点。"
                    : $"已刷新 {Points.Count} 个采集点。";
            }
            catch (Exception ex)
            {
                PageMessage = $"读取采集点失败：{ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanRefreshPoints() => !_isClosed && !IsBusy;

        /// <summary>
        /// 查询这台设备的点位并填入 Points,让左侧列表展示，再按选择的点位的编号恢复展示选择的点位的展示
        /// </summary>
        /// <param name="savedPointName"></param>
        /// <returns></returns>
        private async Task LoadPointsAsync(string? savedPointName = null)
        {
            var latestPoints = await Task.Run(() => _deviceStore.GetPoints(_currentUser, Device.Id));

            long? selectedId = SelectedPoint?.Id;

            Points.Clear();
            foreach (PointInfo point in latestPoints)
                Points.Add(point);

            // 如果选择了某个点位同时刷新列表，刷新后尽量保持原来选中的点位，并继续在右侧显示它的配置。
            PointInfo? selected = savedPointName is not null
                ? Points.FirstOrDefault(point => point.Name == savedPointName)
                : selectedId.HasValue
                ? Points.FirstOrDefault(point => point.Id == selectedId.Value)
                : null;
            // 相当于重新选择了相同的点位，执行了一次载入
            SelectPointAndLoadForm(selected);
        }

        /// <summary>
        /// 新增点位按钮，直接给SelectPointAndLoadForm传入null，清空表单
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanChangeConfiguration))]
        private void NewPoint()
        {
            if (!CheckConfigurationCanChange())
                return;
            SelectPointAndLoadForm(null);
            PageMessage = string.Empty;
        }

        /// <summary>
        /// 保存点位配置按钮
        /// </summary>
        /// <returns></returns>
        [RelayCommand(CanExecute = nameof(CanChangeConfiguration))]
        private async Task SavePointAsync()
        {
            if (!CheckConfigurationCanChange())
                return;

            string name = PointName?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                PageMessage = "请填写采集点名称。";
                return;
            }
            if (SelectedRegisterType is not RegisterType registerType ||
                !Enum.IsDefined(registerType))
            {
                PageMessage = "请选择寄存器类型。";
                return;
            }

            if (!ushort.TryParse(AddressText?.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out ushort address))
            {
                PageMessage = "协议地址必须是 0 到 65535 的十进制整数。";
                return;
            }
            PointInfo? duplicatePoint = Points.FirstOrDefault(point => point.Id != SelectedPoint?.Id
                && point.RegisterType == registerType && point.Address == address);
            if (duplicatePoint is not null)
            {
                // 指出哪个点占用了地址；只清地址，其他输入保留，方便改正后重存。
                PageMessage = $"未保存：{duplicatePoint.RegisterTypeDescription}地址 {address} 已被“{duplicatePoint.Name}”使用。" +
                    "地址已清空，请重新输入未占用的地址。";
                AddressText = string.Empty;
                return;
            }
            if (SelectedDataType is not PointDataType dataType || !Enum.IsDefined(dataType))
            {
                PageMessage = "请选择数据类型。";
                return;
            }

            if (!double.TryParse(ScaleText?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) ||
                !double.TryParse(OffsetText?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double offset))
            {
                PageMessage = "倍率和偏移量请填写数字，小数点使用 .；不换算时填写 1 和 0。";
                return;
            }
            string unit = UnitText?.Trim() ?? string.Empty;
            try
            {
                PointInfo.ValidateConversion(dataType, scale, offset, unit);
            }
            catch (ArgumentException ex)
            {
                PageMessage = ex.Message;
                return;
            }

            if (!TryReadLimit(LowerLimitText, "下限", out double? lowerLimit) ||
                !TryReadLimit(UpperLimitText, "上限", out double? upperLimit))
                return;
            if (lowerLimit.HasValue && upperLimit.HasValue && lowerLimit.Value >= upperLimit.Value)
            {
                PageMessage = "下限必须小于上限；相等也不能保存。";
                return;
            }

            // 先完成类型、换算和上下限校验，再固定本次保存输入。
            PointInput input = new PointInput
            {
                Name = name,
                RegisterType = registerType,
                Address = address,
                DataType = dataType,
                Scale = scale,
                Offset = offset,
                Unit = unit,
                LowerLimit = lowerLimit,
                UpperLimit = upperLimit
            };

            // 按原编号区分编辑与新增，不依赖刷新后的表格位置。
            long? pointIdToUpdate = SelectedPoint?.Id;

            try
            {
                IsBusy = true;

                bool saved = await Task.Run(() => pointIdToUpdate.HasValue
                    ? _deviceStore.TryUpdatePoint(_currentUser, Device.Id, pointIdToUpdate.Value, input)
                    : _deviceStore.TryAddPoint(_currentUser, Device.Id, input));

                if (!saved)
                {
                    PageMessage = pointIdToUpdate.HasValue
                        ? "未保存：点位已不存在、名称重复，或设备不存在。输入已保留。"
                        : "未保存：本设备已有同名采集点，或设备不存在。";
                    return;
                }

                try
                {
                    await LoadPointsAsync(pointIdToUpdate.HasValue ? null : name);
                    PageMessage = pointIdToUpdate.HasValue
                        ? $"已保存“{name}”的修改。"
                        : $"已新增采集点“{name}”。";
                }
                catch (Exception ex)
                {
                    PageMessage = $"采集点已保存，但刷新列表失败，请重试刷新：{ex.Message}";
                }
            }
            catch (Exception ex)
            {
                PageMessage = $"保存采集点失败：{ex.Message}";
            }

            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>留空表示不设限；填写时必须是有限数字，数值按换算后的单位理解。</summary>
        private bool TryReadLimit(string text, string label, out double? limit)
        {
            limit = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            if (!double.TryParse(text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            {
                PageMessage = $"{label}请填写换算后的有限数值，小数点使用 .，或留空不设置。";
                return false;
            }
            limit = value;
            return true;
        }

        /// <summary>
        /// 撤销输入的按钮，恢复选择的点位配置
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanChangeConfiguration))]
        private void RevertPoint()
        {
            if (!CheckConfigurationCanChange())
                return;
            LoadForm(SelectedPoint);
            PageMessage = IsEditing ? "已恢复为保存的配置。" : "已清空新增输入。";
        }

        /// <summary>
        /// 选择某点时载入其配置,传入null时清空表单,用于修改配置或者清空表单新增点位
        /// </summary>
        /// <param name="point"></param>
        private void LoadForm(PointInfo? point)
        {
            PointName = point?.Name ?? string.Empty;
            AddressText = point?.Address.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            SelectedRegisterType = point?.RegisterType ?? RegisterType.Holding;
            SelectedDataType = point?.DataType ?? PointDataType.UInt16;
            ScaleText = point?.Scale.ToString(CultureInfo.InvariantCulture) ?? "1";
            OffsetText = point?.Offset.ToString(CultureInfo.InvariantCulture) ?? "0";
            UnitText = point?.Unit ?? string.Empty;
            LowerLimitText = point?.LowerLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            UpperLimitText = point?.UpperLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>
        /// 统一处理选点后的变化,选择不同点位时和各种操作都会调用
        /// </summary>
        /// <param name="point"></param>
        private void SelectPointAndLoadForm(PointInfo? point)
        {
            _selectedPoint = point;
            // 加载点位配置
            LoadForm(point);
            OnPropertyChanged(nameof(SelectedPoint));
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(FormTitle));
            OnPropertyChanged(nameof(SaveButtonText));
            DeletePointCommand.NotifyCanExecuteChanged();
            ReadOnceCommand.NotifyCanExecuteChanged();
            _testResult = null;
            ReadState = SingleReadState.NotRead;
            ReadMessage = "";
            NotifyReadResult();
        }

        /// <summary>确认后删除选中点位的配置，再刷新列表；不清除设备寄存器里的数值。</summary>
        [RelayCommand(CanExecute = nameof(CanDeletePoint))]
        private async Task DeletePointAsync()
        {
            if (!CheckConfigurationCanChange())
                return;
            PointInfo? point = SelectedPoint;
            if (!CanDeletePoint() || point is null)
                return;

            try
            {
                // point 已经是本次要删除的对象；窗口只需要返回“是否确认”。
                if (DeleteConfirmationRequested?.Invoke(point) != true)
                {
                    PageMessage = "未确认删除，采集点配置没有改变。";
                    return;
                }
                if (!CheckConfigurationCanChange())
                    return;

                IsBusy = true;

                if (!await Task.Run(() => _deviceStore.TryDeletePoint(_currentUser, Device.Id, point.Id)))
                {
                    PageMessage = "未删除：点位已不存在，或设备不存在。请刷新列表。";
                    return;
                }
                // 清空配置表单
                SelectPointAndLoadForm(null);

                try
                {
                    await LoadPointsAsync();
                    PageMessage = $"已删除采集点“{point.Name}”（编号 {point.Id}）。";
                }
                catch (Exception ex)
                {
                    PageMessage = $"采集点已删除，但刷新列表失败，请重试刷新：{ex.Message}";
                }
            }
            catch (Exception ex)
            {
                PageMessage = $"删除采集点失败：{ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>选中本设备的点就能删除，不必先退出编辑；删除前仍有单独确认。</summary>
        private bool CanDeletePoint() => CanChangeConfiguration()
            && SelectedPoint is not null && SelectedPoint.DeviceId == Device.Id
            && Points.Contains(SelectedPoint);

        private bool CanChangeConfiguration() => !_isClosed && !IsBusy
            && (_samplingTasks is null
                || (!_samplingTasks.IsShuttingDown
                    && !_samplingTasks.IsDeletingDevice(Device.Id)
                    && !_samplingTasks.IsRunning(Device.Id)));

        private bool CheckConfigurationCanChange()
        {
            if (CanChangeConfiguration())
                return true;
            PageMessage = "暂时不能修改配置。请先停止这台设备，等停止完成后再保存或删除采集点。";
            return false;
        }

        private void SamplingTasks_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SamplingTasks.ActiveSamplingCount)
                or nameof(SamplingTasks.IsShuttingDown) or nameof(SamplingTasks.IsDeletingDevices))
                NotifyConfigurationCommands();
        }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanChoosePoint));
            NotifyConfigurationCommands();
        }

        partial void OnReadStateChanged(SingleReadState value) => OnPropertyChanged(nameof(ReadStatusText));
        partial void OnReadMessageChanged(string value) => OnPropertyChanged(nameof(ReadDetails));

        private void NotifyConfigurationCommands()
        {
            OnPropertyChanged(nameof(CanEditConfiguration));
            NewPointCommand.NotifyCanExecuteChanged();
            RevertPointCommand.NotifyCanExecuteChanged();
            SavePointCommand.NotifyCanExecuteChanged();
            DeletePointCommand.NotifyCanExecuteChanged();
            RefreshPointsCommand.NotifyCanExecuteChanged();
            ReadOnceCommand.NotifyCanExecuteChanged();
        }

        /// <summary>测试的是已保存配置；不保存历史，也不启动连续采集。</summary>
        [RelayCommand(CanExecute = nameof(CanReadOnce), IncludeCancelCommand = true)]
        private async Task ReadOnceAsync(CancellationToken token)
        {
            var point = SelectedPoint;
            if (point is null || !CanReadOnce())
                return;
            IsBusy = true;
            _testResult = null;
            ReadState = SingleReadState.Reading;
            ReadMessage = "等待设备响应…";
            NotifyReadResult();
            if (_samplingTasks is not null)
                // 得到设备编号
                _samplingTasks.SingleReadDeviceId = Device.Id;
            try
            {
                // 得到设备配置，知道连接参数和寄存器类型；如果设备已被删除，抛出异常。
                var device = await Task.Run(() => _deviceStore.GetDevice(_currentUser, Device.Id), token)
                    ?? throw new InvalidOperationException("设备配置已不存在。");
                // 只把选中点交给共用通信代码；await using 会在本次测试结束后释放连接。
                await using var reader = _createReader(device, [point]);
                _testResult = (await reader.ReadAsync(token))[0];
                token.ThrowIfCancellationRequested();
                ReadState = SingleReadState.Succeeded;
                ReadMessage = "";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                ReadState = SingleReadState.Canceled;
                ReadMessage = "读取已取消，未保存历史。";
            }
            catch (TimeoutException ex)
            {
                ReadState = SingleReadState.TimedOut;
                ReadMessage = ex.Message;
            }
            catch (Exception ex)
            {
                ReadState = SingleReadState.Failed;
                ReadMessage = ex.Message;
            }
            finally
            {
                if (ReadState != SingleReadState.Succeeded)
                    _testResult = null;
                if (_samplingTasks is not null)
                    _samplingTasks.SingleReadDeviceId = null;
                IsBusy = false;
                NotifyReadResult();
            }
        }

        private bool CanReadOnce() => CanChangeConfiguration() && SelectedPoint is not null;
        private void NotifyReadResult()
        {
            OnPropertyChanged(nameof(ReadValueText));
            OnPropertyChanged(nameof(ReadDetails));
        }

        /// <summary>解除任务状态通知；父页面已等测试完成后才调用，不在这里强行中断读写。</summary>
        public void Dispose()
        {
            if (_isClosed)
                return;
            _isClosed = true;
            if (_samplingTasks is not null)
                _samplingTasks.PropertyChanged -= SamplingTasks_PropertyChanged;
            NotifyConfigurationCommands();
        }
    }
}

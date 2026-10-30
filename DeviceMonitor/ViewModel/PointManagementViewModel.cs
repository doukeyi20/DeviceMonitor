// 校验并保存寄存器、数值换算和报警配置。
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel
{
    public partial class PointManagementViewModel : ObservableObject, IDisposable
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;
        private readonly DeviceTasks? _deviceTasks;
        private bool _isClosed;

        [ObservableProperty]
        private bool _isBusy;
        public bool CanChoosePoint => !IsBusy;

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanChoosePoint));
            NotifyConfigurationCommands();
        }

        public DeviceInfo Device { get; }

        public string WindowTitle => $"{Device.Name} - 采集点管理";

        public string DeviceEndpoint =>
            $"{Device.CommunicationText} · {Device.Endpoint} · {(Device.CommunicationMode == DeviceCommunicationMode.Rtu ? "站号" : "Unit ID")}：{Device.UnitId}";

        public ObservableCollection<PointInfo> Points { get; } = new();

        private PointInfo? _selectedPoint;

        public PointInfo? SelectedPoint
        {
            get => _selectedPoint;
            set
            {
                if (ReferenceEquals(_selectedPoint, value))
                    return;
                if (value is not null && (value.DeviceId != Device.Id || !Points.Contains(value)))
                    return;
                // 选中就载入；切换会放弃未保存输入，不再维护选择回退流程。
                SelectPointAndLoadForm(value);
            }
        }

        public event Action<PointDeleteRequest>? DeleteConfirmationRequested;

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

        public PointManagementViewModel(UserAccount currentUser, DeviceStore deviceStore, DeviceInfo device,
    DeviceTasks? deviceTasks = null)
        {
            _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            Device = device ?? throw new ArgumentNullException(nameof(device));
            _deviceTasks = deviceTasks;
            if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以配置采集点。");

            if (_deviceTasks is not null)
                _deviceTasks.PropertyChanged += DeviceTasks_PropertyChanged;

            LoadForm(null);
        }

        /// <summary>查询最新配置；按编号保留选择，表单同步载入新配置。</summary>
        [RelayCommand(CanExecute = nameof(CanRefreshPoints))]
        private async Task RefreshPointsAsync()
        {
            if (!CanRefreshPoints())
                return;
            try
            {
                IsBusy = true;
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

        /// <summary>查询这台设备的点位并填入 Points，再按编号恢复选择。</summary>
        private async Task LoadPointsAsync(string? savedPointName = null)
        {
            var latestPoints = await Task.Run(() => _deviceStore.GetPoints(_currentUser, Device.Id));

            long? selectedId = SelectedPoint?.Id;

            Points.Clear();
            foreach (PointInfo point in latestPoints)
                Points.Add(point);

            PointInfo? selected = savedPointName is not null
                ? Points.FirstOrDefault(point => point.Name == savedPointName)
                : selectedId.HasValue
                ? Points.FirstOrDefault(point => point.Id == selectedId.Value)
                : null;
            SelectPointAndLoadForm(selected);
        }

        /// <summary>明确切到新增，不会把原来的选中点误当成新增。</summary>
        [RelayCommand(CanExecute = nameof(CanChangeConfiguration))]
        private void NewPoint()
        {
            if (!CheckConfigurationCanChange())
                return;
            SelectPointAndLoadForm(null);
            PageMessage = string.Empty;
        }

        /// <summary>检查输入，新增或修改点位配置；成功后选中保存的点，表单与列表始终指向同一条。</summary>
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
                        ? "未保存：点位已不存在、名称重复，或设备已停用。输入已保留。"
                        : "未保存：本设备已有同名采集点，或设备已停用 / 不存在。";
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

        [RelayCommand(CanExecute = nameof(CanChangeConfiguration))]
        private void RevertPoint()
        {
            if (!CheckConfigurationCanChange())
                return;
            LoadForm(SelectedPoint);
            PageMessage = IsEditing ? "已恢复为保存的配置。" : "已清空新增输入。";
        }

        /// <summary>选中点就载入原配置；新增时填空表单和默认倍率。这里不访问数据库。</summary>
        private void LoadForm(PointInfo? point)
        {
            PointName = point?.Name ?? string.Empty;
            AddressText = point?.Address.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            SelectedRegisterType = point?.RegisterType;
            SelectedDataType = point?.DataType;
            ScaleText = point?.Scale.ToString(CultureInfo.InvariantCulture) ?? "1";
            OffsetText = point?.Offset.ToString(CultureInfo.InvariantCulture) ?? "0";
            UnitText = point?.Unit ?? string.Empty;
            LowerLimitText = point?.LowerLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            UpperLimitText = point?.UpperLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>统一更新选择、输入、标题和按钮，避免各处漏改。</summary>
        private void SelectPointAndLoadForm(PointInfo? point)
        {
            _selectedPoint = point;
            LoadForm(point);
            OnPropertyChanged(nameof(SelectedPoint));
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(FormTitle));
            OnPropertyChanged(nameof(SaveButtonText));
            DeletePointCommand.NotifyCanExecuteChanged();
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

            PointDeleteRequest request = new PointDeleteRequest(Device, point);
            try
            {
                DeleteConfirmationRequested?.Invoke(request);
                if (!request.Confirmed)
                {
                    PageMessage = "未确认删除，采集点配置没有改变。";
                    return;
                }
                if (!CheckConfigurationCanChange())
                    return;

                IsBusy = true;

                if (!await Task.Run(() => _deviceStore.TryDeletePoint(_currentUser, Device.Id, point.Id)))
                {
                    PageMessage = "未删除：点位已不存在，或设备已停用 / 不存在。请刷新列表。";
                    return;
                }

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
            && Device.IsEnabled
            && (_deviceTasks is null
                || (!_deviceTasks.IsShuttingDown
                    && !_deviceTasks.IsDeletingDevice(Device.Id)
                    && !_deviceTasks.IsRunning(Device.Id)));

        private bool CheckConfigurationCanChange()
        {
            if (CanChangeConfiguration())
                return true;
            PageMessage = "暂时不能修改配置。请先停止这台设备，等停止完成后再保存或删除采集点。";
            return false;
        }

        private void DeviceTasks_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceTasks.ActiveSamplingCount)
                or nameof(DeviceTasks.IsShuttingDown) or nameof(DeviceTasks.IsDeletingDevices))
                NotifyConfigurationCommands();
        }

        private void NotifyConfigurationCommands()
        {
            OnPropertyChanged(nameof(CanEditConfiguration));
            NewPointCommand.NotifyCanExecuteChanged();
            RevertPointCommand.NotifyCanExecuteChanged();
            SavePointCommand.NotifyCanExecuteChanged();
            DeletePointCommand.NotifyCanExecuteChanged();
            RefreshPointsCommand.NotifyCanExecuteChanged();
        }

        public void Dispose()
        {
            if (_isClosed)
                return;
            _isClosed = true;
            if (_deviceTasks is not null)
                _deviceTasks.PropertyChanged -= DeviceTasks_PropertyChanged;
            NotifyConfigurationCommands();
        }
    }
}

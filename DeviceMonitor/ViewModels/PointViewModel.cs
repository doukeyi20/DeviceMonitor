using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Sampling;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 管理一台设备的采集点配置，不负责读取或连续采集。
    /// </summary>
    public partial class PointViewModel : ObservableObject, IDisposable
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;
        private readonly DeviceRuntimeManager? _deviceRuntimeManager;
        private bool _isClosed; // 窗口已关闭，不再接受保存或删除。

        // 本窗口一直管理这台设备，只有 get，不能中途换设备。
        public DeviceInfo Device { get; }

        // 窗口顶部标题，例如“T1 - 采集点管理”。
        public string WindowTitle => $"{Device.Name} - 采集点管理";

        // 采集点窗口顶部显示当前设备的连接方式、地址和目标编号。
        public string DeviceEndpoint =>
            $"{Device.CommunicationText} · {Device.Endpoint} · {(Device.CommunicationMode == DeviceCommunicationMode.Rtu ? "站号" : "Unit ID")}：{Device.UnitId}";

        // 左侧配置表绑定这里，每项是一个 PointInfo 点位配置，不是读数。
        public ObservableCollection<PointInfo> Points { get; } = new();

        // 表格选中哪一行，就把那一行的 PointInfo 传到这个属性。
        // 选中项改变时，重新判断编辑、删除按钮能不能点击。
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(BeginEditPointCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeletePointCommand))]
        private PointInfo? _selectedPoint;

        public event Action<PointDeleteRequest>? DeleteConfirmationRequested;

        // 固定本次编辑的点，之后选中另一行也不能改变保存目标。
        private PointInfo? _editingPoint;
        public bool IsEditing => _editingPoint is not null;
        public string FormTitle => _editingPoint is null
            ? "新增采集点"
            : $"正在编辑：{_editingPoint.Name}（编号 {_editingPoint.Id}）";
        public string SaveButtonText => IsEditing ? "保存修改" : "保存新增采集点";

        // 工具自动生成公开属性和通知。界面输入改变后，下面的命令读这些属性。
        [ObservableProperty]
        private string _pointName = string.Empty;

        [ObservableProperty]
        private string _addressText = string.Empty;

        // 默认 null，表示尚未选择；不替用户猜寄存器类型或数据类型。
        [ObservableProperty]
        private RegisterType? _selectedRegisterType;

        [ObservableProperty]
        private PointDataType? _selectedDataType;

        // 两个输入框都留空，表示这个点不做数值报警；只填一侧也可以。
        // 先保存文字，点击保存时再判断它是不是当前数据类型允许的整数。
        [ObservableProperty]
        private string _lowerLimitText = string.Empty;

        [ObservableProperty]
        private string _upperLimitText = string.Empty;

        private string _pageMessage = string.Empty;
        // 只有本类能修改提示，界面只能读取。
        public string PageMessage
        {
            get => _pageMessage;
            private set => SetProperty(ref _pageMessage, value);
        }

        /// <summary>
        /// 由 AcquisitionViewModel.CreatePointViewModel 创建，作为点位窗口的数据源。
        /// 接住目标设备和数据库对象，并加载这台设备的点位。
        /// </summary>
        public PointViewModel(UserAccount currentUser, DeviceStore deviceStore, DeviceInfo device,
            DeviceRuntimeManager? deviceRuntimeManager = null)
        {
            _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            Device = device ?? throw new ArgumentNullException(nameof(device));
            _deviceRuntimeManager = deviceRuntimeManager;
            if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以配置采集点。");

            if (_deviceRuntimeManager is not null)
                _deviceRuntimeManager.PropertyChanged += DeviceRuntimeManager_PropertyChanged;

            // 创建窗口的数据源时就查询，不必先点“刷新”才能看到原来的配置。
            RefreshPoints();
        }

        /// <summary>
        /// 由 RefreshPointsCommand 调用；本类构造方法也会调用一次。
        /// 刷新点位列表，不清空正在编辑的输入。
        /// </summary>
        [RelayCommand]
        private void RefreshPoints()
        {
            try
            {
                LoadPoints();
                PageMessage = IsEditing
                    ? "列表已刷新，正在编辑的输入已保留；保存目标仍是上方显示的点位。"
                    : Points.Count == 0
                        ? "当前设备尚无采集点，请先填写配置并保存。"
                        : $"已加载当前设备的 {Points.Count} 个采集点。";
            }
            catch (Exception ex)
            {
                PageMessage = $"读取采集点失败：{ex.Message}";
            }
        }

        /// <summary>
        /// 由 RefreshPoints、SavePoint 和 DeletePoint 调用。
        /// 查询这台设备的点位并填入 Points，再按编号恢复选择。
        /// </summary>
        private void LoadPoints(long? pointToSelect = null)
        {
            var latestPoints = _deviceStore.GetPoints(_currentUser, Device.Id);
            long? selectedId = pointToSelect ?? SelectedPoint?.Id;
            // 查询成功才替换列表，查询失败时保留原来显示的记录。
            Points.Clear();
            foreach (PointInfo point in latestPoints)
                Points.Add(point);

            // 查询产生了新的 PointInfo 对象，按编号找回对应的新对象，不能继续用旧行。
            SelectedPoint = selectedId.HasValue
                ? Points.FirstOrDefault(point => point.Id == selectedId.Value)
                : null;
        }

        /// <summary>
        /// 由 BeginEditPointCommand 调用。
        /// 记住要编辑的点，并把原配置填入输入框；此时不修改数据库。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanBeginEditPoint))]
        private void BeginEditPoint()
        {
            if (!CheckConfigurationCanChange())
                return;
            // 取出表格当前选中的点位配置。
            PointInfo? point = SelectedPoint;
            if (!CanBeginEditPoint() || point is null)
                return;

            // 先固定编辑目标点位对象。
            SetEditingPoint(point);

            // 把点位原配置填入右侧表单，之后用户修改这些属性。
            PointName = point.Name;
            AddressText = point.Address.ToString(CultureInfo.InvariantCulture);
            SelectedRegisterType = point.RegisterType;
            SelectedDataType = point.DataType;
            LowerLimitText = point.LowerLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            UpperLimitText = point.UpperLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            PageMessage = "配置已带入输入区。修改后点击“保存修改”，或取消编辑。";
        }

        /// <summary>
        /// 由 BeginEditPointCommand 判断按钮能不能点时调用。
        /// 没有正在编辑，且选中了本设备的有效点位，才能开始编辑或删除。
        /// </summary>
        private bool CanBeginEditPoint()
        {
            return CanChangeConfiguration() && !IsEditing
                && SelectedPoint is not null
                && SelectedPoint.DeviceId == Device.Id
                && Points.Contains(SelectedPoint);
        }

        /// <summary>
        /// 由 SavePointCommand 调用。
        /// 检查输入，新增或修改点位配置；成功后清空表单并刷新列表。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanChangeConfiguration))]
        private void SavePoint()
        {
            // 窗口打开后也可能开始采集，保存前仍要确认设备已停止。
            if (!CheckConfigurationCanChange())
                return;
            // 先取出表单属性并检查名称、类型和地址。
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
            // 把地址文字转成整数，允许 0，不允许负数或非数字。
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

            // 检查上下限是否符合数据类型；例如 UInt16 不能填负数。
            if (!TryReadLimit(LowerLimitText, "下限", dataType, out int? lowerLimit) ||
                !TryReadLimit(UpperLimitText, "上限", dataType, out int? upperLimit))
                return;
            if (lowerLimit.HasValue && upperLimit.HasValue && lowerLimit.Value >= upperLimit.Value)
            {
                PageMessage = "下限必须小于上限；相等也不能保存。";
                return;
            }

            // 把输入装成 PointInput；设备编号另传，不能由输入框改掉。
            PointInput input = new PointInput
            {
                Name = name,
                RegisterType = registerType,
                Address = address,
                DataType = dataType,
                LowerLimit = lowerLimit,
                UpperLimit = upperLimit
            };

            // 编辑用之前记住的编号，不用当前选择；新增时没有编号。
            long? editingId = _editingPoint?.Id;
            try
            {
                // 有正在编辑的编号就修改，没有就新增。
                bool saved;
                if (editingId.HasValue)
                    saved = _deviceStore.TryUpdatePoint(_currentUser, Device.Id, editingId.Value, input);
                else
                    saved = _deviceStore.TryAddPoint(_currentUser, Device.Id, input);

                if (!saved)
                {
                    PageMessage = editingId.HasValue
                        ? "未保存：点位已不存在、名称重复，或设备已停用。输入已保留。"
                        : "未保存：本设备已有同名采集点，或设备已停用 / 不存在。";
                    return;
                }
            }
            catch (Exception ex)
            {
                PageMessage = $"保存采集点失败：{ex.Message}";
                return;
            }

            // 保存成功才清空表单，失败时保留输入。
            ResetForm();

            try
            {
                // 重查保存后的配置，编辑时恢复选中被修改的点。
                LoadPoints(editingId);
                PageMessage = editingId.HasValue
                    ? $"已修改采集点“{name}”（编号 {editingId.Value}），未新增记录。"
                    : $"已保存采集点“{name}”。这里只保存配置，还未开始采集。";
            }
            catch (Exception ex)
            {
                // 已保存成功，只是刷新失败，不能提示成保存失败。
                PageMessage = $"采集点已保存，但刷新列表失败，请重试刷新：{ex.Message}";
            }
        }

        // 保存按钮的输入检查。空白不是 0，而是“不设置这一侧的报警界限”。
        // 由 SavePoint 检查上下限输入时调用。
        private bool TryReadLimit(string text, string label, PointDataType dataType, out int? limit)
        {
            limit = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;
            // Int16 允许负数，UInt16 不允许；按所选类型确定上下界。
            int minimum = dataType == PointDataType.Int16 ? short.MinValue : ushort.MinValue;
            int maximum = dataType == PointDataType.Int16 ? short.MaxValue : ushort.MaxValue;
            if (!int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
            {
                PageMessage = $"{label}请填写 {minimum} 到 {maximum} 的整数，或留空不设置。";
                return false;
            }
            limit = value;
            return true;
        }

        // 放弃尚未保存的输入并退出编辑，不改变已保存数据。
        [RelayCommand(CanExecute = nameof(CanCancelEditPoint))]
        // 由 CancelEditPointCommand 调用。
        private void CancelEditPoint()
        {
            if (!IsEditing)
                return;
            ResetForm();
            PageMessage = "已取消编辑，数据库中的原配置没有改变。";
        }

        // 只有正在编辑，才能取消编辑。
        // 由 CancelEditPointCommand 判断按钮能不能点时调用。
        private bool CanCancelEditPoint() => IsEditing;

        /// <summary>
        /// 由 SavePoint 保存成功后或 CancelEditPoint 取消编辑时调用。
        /// 清空输入并退出编辑；保存失败时不调用，保留输入供修改。
        /// </summary>
        private void ResetForm()
        {
            SetEditingPoint(null);
            PointName = string.Empty;
            AddressText = string.Empty;
            SelectedRegisterType = null;
            SelectedDataType = null;
            LowerLimitText = string.Empty;
            UpperLimitText = string.Empty;
        }

        /// <summary>
        /// 由 BeginEditPoint 和 ResetForm 调用。
        /// 记住要修改的点位，并切换到编辑状态。
        /// </summary>
        private void SetEditingPoint(PointInfo? point)
        {
            // 记住固定目标，再更新表单标题、保存文字和相关按钮。
            _editingPoint = point;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(FormTitle));
            OnPropertyChanged(nameof(SaveButtonText));
            BeginEditPointCommand.NotifyCanExecuteChanged();
            DeletePointCommand.NotifyCanExecuteChanged();
            CancelEditPointCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 由 DeletePointCommand 调用。
        /// 确认后删除选中点位的配置，再刷新列表；不清除设备寄存器里的数值。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDeletePoint))]
        private void DeletePoint()
        {
            if (!CheckConfigurationCanChange())
                return;
            PointInfo? point = SelectedPoint;
            if (!CanDeletePoint() || point is null)
                return;

            // 固定本次删除目标，不能弹框后再取选择，避免删错点。
            PointDeleteRequest request = new PointDeleteRequest(Device, point);
            try
            {
                // 让 PointWindow.ConfirmDeletePoint 弹框，把是否点“是”写回 request.Confirmed。
                DeleteConfirmationRequested?.Invoke(request);
                if (!request.Confirmed)
                {
                    PageMessage = "未确认删除，采集点配置没有改变。";
                    return;
                }
                if (IsEditing)
                {
                    PageMessage = "请先保存或取消编辑，再删除采集点。";
                    return;
                }
                if (!CheckConfigurationCanChange())
                    return;

                // 确认后按设备编号、点位编号删除配置。
                if (!_deviceStore.TryDeletePoint(_currentUser, Device.Id, point.Id))
                {
                    PageMessage = "未删除：点位已不存在，或设备已停用 / 不存在。请刷新列表。";
                    return;
                }
            }
            catch (Exception ex)
            {
                PageMessage = $"删除采集点失败：{ex.Message}";
                return;
            }

            try
            {
                // 重查列表，清掉已删除点的选择，不清空另一份尚未保存的新增输入。
                LoadPoints();
                PageMessage = $"已删除采集点“{point.Name}”（编号 {point.Id}）。";
            }
            catch (Exception ex)
            {
                // 删除已经完成，不能把刷新失败误报成“没有删除”。
                PageMessage = $"采集点已删除，但刷新列表失败，请重试刷新：{ex.Message}";
            }
        }

        /// <summary>
        /// 由 DeletePointCommand 判断按钮能不能点时调用。
        /// 删除和开始编辑使用相同的选中检查。
        /// </summary>
        private bool CanDeletePoint() => CanBeginEditPoint();

        // 采集和停止收尾都没结束时，点位、地址和上下限不能修改。
        private bool CanChangeConfiguration() => !_isClosed
            && _currentUser.Role == UserRole.Administrator && _currentUser.IsEnabled
            && Device.IsEnabled
            && (_deviceRuntimeManager is null
                || (!_deviceRuntimeManager.IsShuttingDown
                    && !_deviceRuntimeManager.IsDeletingDevice(Device.Id)
                    && !_deviceRuntimeManager.IsRunning(Device.Id)));

        // 按钮和真正的写入入口都检查；不满足条件时保留输入。
        private bool CheckConfigurationCanChange()
        {
            if (CanChangeConfiguration())
                return true;
            PageMessage = "暂时不能修改配置。请先停止这台设备，等停止完成后再保存或删除采集点。";
            return false;
        }

        // 运行状态变了，编辑、保存和删除按钮跟着更新。
        private void DeviceRuntimeManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceRuntimeManager.ActiveSamplingCount)
                or nameof(DeviceRuntimeManager.IsShuttingDown) or nameof(DeviceRuntimeManager.IsDeletingDevices))
                NotifyConfigurationCommands();
        }

        private void NotifyConfigurationCommands()
        {
            BeginEditPointCommand.NotifyCanExecuteChanged();
            SavePointCommand.NotifyCanExecuteChanged();
            DeletePointCommand.NotifyCanExecuteChanged();
        }

        // 由 PointWindow.OnClosed 调用，关闭窗口后解除共用管理器的通知。
        public void Dispose()
        {
            if (_isClosed)
                return;
            _isClosed = true;
            if (_deviceRuntimeManager is not null)
                _deviceRuntimeManager.PropertyChanged -= DeviceRuntimeManager_PropertyChanged;
            NotifyConfigurationCommands();
        }
    }
}

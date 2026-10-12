using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 一台设备的点位表单数据源：刷新列表、新增、带入编辑、保存修改和删除。
    /// Device 一旦传入就不换；这里只改配置，不连接设备，不开始采集。
    /// </summary>
    public partial class PointViewModel : ObservableObject
    {
        private readonly UserAccount _currentUser;
        private readonly DeviceStore _deviceStore;

        // 打开窗口时传入的是哪台设备，这里就一直保存那台设备。
        // 只有 get，表示窗口不能把它换成另一台。
        public DeviceInfo Device { get; }

        // 窗口顶部标题，例如“T1 - 采集点管理”。
        public string WindowTitle => $"{Device.Name} - 采集点管理";

        // 把设备地址、端口和 Unit ID 拼成一行，方便界面显示。
        public string DeviceEndpoint =>
            $"{Device.Host}:{Device.Port} · Unit ID：{Device.UnitId}";

        // 表格显示这个集合。查询得到的新对象会放进这里，不直接绑定数据库。
        public ObservableCollection<PointInfo> Points { get; } = new();

        // 表格选中哪一行，就把那一行的 PointInfo 传到这个属性。
        // 选中项改变时，重新判断编辑、删除按钮能不能点击。
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(BeginEditPointCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeletePointCommand))]
        private PointInfo? _selectedPoint;

        public event Action<PointDeleteRequest>? DeleteConfirmationRequested;

        // 记住点击“编辑”那一刻的点位，作为本次保存的固定目标。
        // 它与 SelectedPoint 分开：随后点到另一行，不应改变正在编辑的记录。
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
        /// AcquisitionViewModel.CreatePointViewModel 创建这里，再由页面后台传给 PointWindow。
        /// 接住用户、DeviceStore 和目标 DeviceInfo；构造时 RefreshPoints 加载这台设备的点位。
        /// </summary>
        public PointViewModel(UserAccount currentUser, DeviceStore deviceStore, DeviceInfo device)
        {
            _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            Device = device ?? throw new ArgumentNullException(nameof(device));
            if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
                throw new UnauthorizedAccessException("只有已启用的管理员可以配置采集点。");

            // 创建窗口的数据源时就查询，不必先点“刷新”才能看到原来的配置。
            RefreshPoints();
        }

        /// <summary>
        /// 构造函数直接调用；点击采集点窗口的“刷新”按钮时，RefreshPointsCommand 也调用这里。
        /// 通过 LoadPoints 重查列表并更新提示；正在编辑的输入不会被清空。
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

        // 新增、编辑和手动刷新都调用这里，加载采集点
        /// <summary>
        /// 由 RefreshPoints、SavePoint 和 DeletePoint 调用，是它们共用的列表更新方法。
        /// 调用 DeviceStore.GetPoints，只查 Device.Id 对应的点位；填入 Points，并按点位 Id 恢复选择。
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
        /// 编辑选中点位按钮的命令。
        /// 用 SetEditingPoint 固定本次编辑目标，再把它的配置填回输入属性；数据库此时还没有修改。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanBeginEditPoint))]
        private void BeginEditPoint()
        {
            // 设备的点位对象
            PointInfo? point = SelectedPoint;
            if (!CanBeginEditPoint() || point is null)
                return;

            // 先固定编辑目标点位对象。
            SetEditingPoint(point);

            // 把输入的值填入
            PointName = point.Name;
            AddressText = point.Address.ToString(CultureInfo.InvariantCulture);
            SelectedRegisterType = point.RegisterType;
            SelectedDataType = point.DataType;
            LowerLimitText = point.LowerLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            UpperLimitText = point.UpperLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            PageMessage = "配置已带入输入区。修改后点击“保存修改”，或取消编辑。";
        }

        /// <summary>
        /// 编辑和删除共用的选中检查：没在编辑，SelectedPoint 属于本设备，且仍在当前 Points 列表中。
        /// </summary>
        private bool CanBeginEditPoint()
        {
            return !IsEditing
                && SelectedPoint is not null
                && SelectedPoint.DeviceId == Device.Id
                && Points.Contains(SelectedPoint);
        }

        /// <summary>
        /// 点击采集点窗口的保存按钮后，SavePointCommand 调用这里；新增和编辑共用它。
        /// 检查输入并组装 PointInput，再按是否正在编辑调用 DeviceStore.TryAddPoint 或 TryUpdatePoint。
        /// 保存成功才 ResetForm 清空表单，并 LoadPoints 更新列表；这里只保存配置，不读取设备。
        /// </summary>
        [RelayCommand]
        private void SavePoint()
        {

            // 先检查输入，空白、负数、非数字都不能直接拿去保存。
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
            // 地址输入先保持字符串，空白、负数、非数字都不能直接拿去保存。
            // 0 是有效协议地址，不用“大于 0”来校验它。
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

            // SavePoint 调用这里：先检查上下限，再交给数据库保存。
            // 例如 UInt16 不能填负数；Int16 可以填负数，但不能超过它的范围。
            if (!TryReadLimit(LowerLimitText, "下限", dataType, out int? lowerLimit) ||
                !TryReadLimit(UpperLimitText, "上限", dataType, out int? upperLimit))
                return;
            if (lowerLimit.HasValue && upperLimit.HasValue && lowerLimit.Value >= upperLimit.Value)
            {
                PageMessage = "下限必须小于上限；相等也不能保存。";
                return;
            }

            // 把分散的输入属性组装成一个对象，交给 DeviceStore。
            // Device.Id 单独传入，用户不能在输入框中改成别的设备编号。
            PointInput input = new PointInput
            {
                Name = name,
                RegisterType = registerType,
                Address = address,
                DataType = dataType,
                LowerLimit = lowerLimit,
                UpperLimit = upperLimit
            };

            // 新增时没有点位编号；编辑时使用进入编辑那一刻记住的编号。
            // 不拿此时 SelectedPoint 的编号保存，避免选中另一行后误改记录。
            long? editingId = _editingPoint?.Id;
            try
            {
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

            // 写入成功才退出编辑并清空表单，失败时让用户保留输入继续修改。
            ResetForm();

            try
            {
                // 再查询数据库，显示真正保存的值；编辑后重新选中被修改的那条记录。
                LoadPoints(editingId);
                PageMessage = editingId.HasValue
                    ? $"已修改采集点“{name}”（编号 {editingId.Value}），未新增记录。"
                    : $"已保存采集点“{name}”。这里只保存配置，还未开始采集。";
            }
            catch (Exception ex)
            {
                // 写入和刷新是两件事，不能把刷新失败误报成“没有保存”。
                PageMessage = $"采集点已保存，但刷新列表失败，请重试刷新：{ex.Message}";
            }
        }

        // 保存按钮的输入检查。空白不是 0，而是“不设置这一侧的报警界限”。
        private bool TryReadLimit(string text, string label, PointDataType dataType, out int? limit)
        {
            limit = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;
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

        // 点击“取消编辑”后，CancelEditPointCommand 调用这里。
        // 调用 ResetForm 放弃输入并退出编辑；不撤销已保存数据，因为这些编辑输入还没写入数据库。
        [RelayCommand(CanExecute = nameof(CanCancelEditPoint))]
        private void CancelEditPoint()
        {
            if (!IsEditing)
                return;
            ResetForm();
            PageMessage = "已取消编辑，数据库中的原配置没有改变。";
        }

        // CancelEditPointCommand 判断能不能取消编辑时调用；只有已经进入编辑模式才返回 true。
        private bool CanCancelEditPoint() => IsEditing;

        /// <summary>
        /// 保存成功或取消编辑时调用，退出编辑并清空输入；数据库失败时不清空，用户还可以修正后重试。
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
        ///记住这次要修改哪个采集点，并把界面切换成编辑状态保存目标。
        /// </summary>
        private void SetEditingPoint(PointInfo? point)
        {
            _editingPoint = point;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(FormTitle));
            OnPropertyChanged(nameof(SaveButtonText));
            BeginEditPointCommand.NotifyCanExecuteChanged();
            DeletePointCommand.NotifyCanExecuteChanged();
            CancelEditPointCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 点击“删除选中点位”后，DeletePointCommand 调用这里。
        /// 先触发 DeleteConfirmationRequested，让 PointWindow.ConfirmDeletePoint 弹框并把结果写回；确认后才调用 DeviceStore.TryDeletePoint。
        /// 删除成功后 LoadPoints 更新列表；删除的是系统保存的配置，不是设备寄存器。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDeletePoint))]
        private void DeletePoint()
        {
            PointInfo? point = SelectedPoint;
            if (!CanDeletePoint() || point is null)
                return;

            // 记住点击删除那一刻的点位。确认框显示的和最后删除的必须是同一条，
            // 不能弹框后再去读 SelectedPoint，以免删到后来选中的另一行。
            PointDeleteRequest request = new PointDeleteRequest(Device, point);
            try
            {
                // 调用 PointWindow.xaml.cs 的 ConfirmDeletePoint：显示 request 中这台设备、这个点位的删除确认框。
                // 后台把是否点“是”填回 request.Confirmed；关闭确认框后，才继续下面的数据库删除判断。
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
                // 删除成功后再查数据库；原来选中的是被删除行时，选择恢复为 null。
                // 不清空新增表单，避免丢掉用户还没保存的另一份输入。
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
        /// 删除条件复用 CanBeginEditPoint：选中本设备点位，并且没有未结束的编辑。
        /// </summary>
        private bool CanDeletePoint() => CanBeginEditPoint();
    }
}

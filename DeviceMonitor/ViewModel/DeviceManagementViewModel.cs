// 同一页管理设备参数、点位和单点测试；选中设备即载入表单。
using DeviceMonitor.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public partial class DeviceManagementViewModel : PageViewModel
{
    private readonly UserAccount _currentUser;
    private readonly DeviceStore _store;
    private readonly SamplingTasks _tasks;
    // 设备配置页开始退出收尾时打上，拦住新操作，也让晚返回的查询不再替换页面数据；以后不再清掉。
    private bool _isClosed;

    // 记住选中设备后的点位加载，退出时等待；刷新列表不再等它来恢复选择。
    private Task _selectionTask = Task.CompletedTask;
    // 保存最近发起的设备查询或保存任务，退出时用来等待数据库操作结束；任务做完后仍可留在这里。
    private Task _databaseWork = Task.CompletedTask;

    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    
    // 左侧设备列表的select 
    [ObservableProperty] private DeviceInfo? _selectedDevice;

    // 设备编辑界面的viewmodel，标签页的view绑定了这个属性作为viewmodel，切换设备时会更新viewmodel从而刷新界面
    [ObservableProperty] private DeviceEditorViewModel? _editor;

    // 点位编辑界面的viewmodel，同上
    [ObservableProperty] private PointEditorViewModel? _pointEditor;

    // 设备配置页的忙碌标记：刷新、保存或删除期间打上，防止重复提交和切换设备；结束后在 finally 清掉。
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty] private string _pageMessage = "";

    // 	判断现在能不能切换左侧设备
    public bool CanChooseDevice => !_isClosed && !IsBusy
        && Editor?.IsSaving != true && PointEditor?.IsBusy != true;

    // 当前设备是否处于允许修改配置的状态
    public bool CanEditSelectedDevice => !_isClosed && PointEditor?.IsBusy != true &&
        (SelectedDevice is null || (!_tasks.IsRunning(SelectedDevice.Id)
            && !_tasks.IsDeletingDevice(SelectedDevice.Id) && _tasks.SingleReadDeviceId != SelectedDevice.Id));

    // 	能不能打开“采集点与测试”标签
    public bool CanOpenPoints => HasSavedDevice && !IsBusy && Editor?.IsSaving != true;
  
    public bool HasSavedDevice => SelectedDevice is not null;
    public string ConfigurationHint => !CanEditSelectedDevice
        ? "设备正在采集或测试，停止后可以修改；当前仍可查看已保存配置。" : "";
    public event Func<string, string, bool>? ConfirmationRequested;

    public DeviceManagementViewModel(UserAccount currentUser, DeviceStore store, SamplingTasks tasks)
        : base("设备配置")
    {
        if (!currentUser.IsEnabled || currentUser.Role != UserRole.Administrator)
            throw new UnauthorizedAccessException("只有管理员可以修改设备配置。");
        _currentUser = currentUser;
        _store = store;
        _tasks = tasks;

        // 当采集管理器对象的某个属性发生变化就执行 TasksChanged
        _tasks.PropertyChanged += TasksChanged;
    }

    /// <summary>查询设备命令，查询成功后才替换设备名单，刷新失败保留原列表。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    public async Task RefreshDevicesAsync()
    {
        IsBusy = true;
        try
        {
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            PageMessage = $"加载设备失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 几个按钮的共用判断条件
    private bool CanOperate() => CanChooseDevice && !_tasks.IsShuttingDown;

    /// <summary>
    /// 刷新设备列表
    /// </summary>
    /// <returns></returns>
    private async Task LoadDevicesAsync()
    {
        // 查询数据库，并记住查询任务，拿出查询结果
        var loading = Task.Run(() => _store.GetDevices(_currentUser));
        _databaseWork = loading;
        var latest = await loading;

        if (_isClosed)
            return;

        // 已选设备时，清空选择会自动清理表单；新增状态本来就是 null，要直接清理。
        if (SelectedDevice is null)
            DetachEditors();
        else
            SelectedDevice = null;

        Devices.Clear();
        foreach (var device in latest)
            Devices.Add(device);
        PageMessage = Devices.Count == 0
            ? "暂无设备，点击“新增设备”填写连接参数。"
            : "设备列表已刷新，请选择设备。";
    }

    // ObservableProperty 生成的 SelectedDevice.set方法 会调用这里；不需要在 XAML 再写点击事件。
    // 只有用户选中设备才创建两个表单并查询点位；清空选择时只清理，不查库。
    /// <summary>
    /// 当选择的设备改变，对应的表单也会更新，value就是选择的设备对象DeviceInfo
    /// </summary>
    /// <param name="value"></param>
    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        // 先解除之前旧设备的订阅
        DetachEditors();
        if (value is null)
            return;

        // 用户已经作出选择，撤掉刷新后的“请选择设备”提示。
        PageMessage = string.Empty;

        // 表单对应的viewmodel变化，赋值新的，设置了通知属性，所以会通知对应的view刷新，这里已经更新了界面，跟切换功能界面自动更新一样，由WPF实现
        Editor = new DeviceEditorViewModel(this, value);  // 传入设备对象用于初始化对应数据，而不是空白

        // 设定viewmodel的任何属性变化会触发 EditorChanged
        Editor.PropertyChanged += EditorChanged;

        // 同上，给点位viewmodel传入
        PointEditor = new PointEditorViewModel(_currentUser, _store, value, _tasks);
        PointEditor.PropertyChanged += EditorChanged;

        // 通知点位表的的刷新命令执行，读取当前设备点位配置，因为是通知命令执行，所以结果在调用方PointEditor接收，而不是这里
        _selectionTask = PointEditor.RefreshPointsCommand.ExecuteAsync(null);
        NotifyState();
    }

    /// <summary>
    /// 新增设备按钮，会自动打开设备编辑的表单，然后更换空的viewmodel，更新空表单
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void AddDevice()
    {
        // SelectedTabIndex = 0 或者 1 决定了显示连接表单还是点位表单，新增时只显示连接表单
        SelectedTabIndex = 0;
        if (SelectedDevice is null)
            DetachEditors();
        else
            SelectedDevice = null;

        // 更换viewmodel从而更新view，同时传入devicemanageviewmodel，因为要调用保存方法，保存方法在devicemanageviewmodel中
        Editor = new DeviceEditorViewModel(this);
        Editor.PropertyChanged += EditorChanged;
        PageMessage = "";
        NotifyState();
    }

    /// <summary>
    /// 撤销输入的命令
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void ResetDevice()
    {
        if (Editor is not null)
            Editor.PropertyChanged -= EditorChanged;

        // 传入当前设备DeviceInfo，为了复原界面
        Editor = new DeviceEditorViewModel(this, SelectedDevice);
        Editor.PropertyChanged += EditorChanged;
        NotifyState();
    }

    // 是被DeviceEditorViewModel调用的
    /// <summary>
    /// 由设备编辑界面传入编辑好的设备的id和本身,把DeviceInput输入，变成真正的设备对象DeviceInfo，保存到数据库
    /// </summary>
    /// <param name="id"></param>
    /// <param name="input"></param>
    /// <returns></returns>
    public async Task<bool> SaveDeviceAsync(long? id, DeviceInput input)
    {
        if (_isClosed || IsBusy || !CanEditSelectedDevice)
            return false;
        IsBusy = true;
        try
        {
            var saving = Task.Run(() => id.HasValue
                ? _store.TryUpdateDevice(_currentUser, id.Value, input)
                : _store.TryAddDevice(_currentUser, input));
            _databaseWork = saving;
            bool saved = await saving;
            if (saved && !_isClosed)
            {
                // 保存和刷新分开提示，不能把“已存但刷新失败”误报成未保存。
                try
                {
                    await LoadDevicesAsync();
                    PageMessage = $"已保存“{input.Name.Trim()}”，请从列表重新选择设备。";
                }
                catch (Exception ex)
                {
                    PageMessage = $"设备已保存，刷新失败：{ex.Message}";
                }
            }
            return saved;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>只删除停止的设备配置；历史读数和报警记录不删除。</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteDeviceAsync()
    {
        DeviceInfo? device = SelectedDevice;
        if (device is null || !CanDelete())
            return;
        bool confirmed = ConfirmationRequested?.Invoke(
            $"删除“{device.Name}”及其采集点配置？已有历史读数和报警会保留。", "删除设备") == true;
        if (!confirmed || !CanDelete())
            return;
        IsBusy = true;
        try
        {
            await _tasks.DeleteDeviceAsync(device.Id);
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            PageMessage = $"删除失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDelete() => CanOperate() && SelectedDevice is not null && CanEditSelectedDevice;

    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 虽然所有属性变化都会调用，但只关心这俩属性，只有这俩属性变化才会调用 NotifyState()。
        if (e.PropertyName is nameof(DeviceEditorViewModel.IsSaving) or nameof(PointEditorViewModel.IsBusy))
            NotifyState();
    }

    private void TasksChanged(object? sender, PropertyChangedEventArgs e) => NotifyState();
    partial void OnIsBusyChanged(bool value) => NotifyState();
    partial void OnEditorChanged(DeviceEditorViewModel? value) => NotifyState();
    partial void OnPointEditorChanged(PointEditorViewModel? value) => NotifyState();

    /// <summary>
    /// 通知界面重新判断一些操作
    /// </summary>
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanChooseDevice));
        OnPropertyChanged(nameof(CanEditSelectedDevice));
        OnPropertyChanged(nameof(ConfigurationHint));
        OnPropertyChanged(nameof(HasSavedDevice));
        OnPropertyChanged(nameof(CanOpenPoints));
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        AddDeviceCommand.NotifyCanExecuteChanged();
        ResetDeviceCommand.NotifyCanExecuteChanged();
        DeleteDeviceCommand.NotifyCanExecuteChanged();
        Editor?.RefreshPermission();
    }

    /// <summary>
    /// 断开旧编辑表单viewmodel的订阅
    /// </summary>
    private void DetachEditors()
    {
        if (Editor is not null)
            Editor.PropertyChanged -= EditorChanged;
        if (PointEditor is not null)
        {
            PointEditor.PropertyChanged -= EditorChanged;
            PointEditor.Dispose();
        }
        Editor = null;
        PointEditor = null;
    }

    /// <summary>退出时先阻止新操作，再等待保存、加载和测试结束，最后解除通知。</summary>
    public override async Task StopOperationsAsync()
    {
        _isClosed = true;
        NotifyState();
        PointEditor?.ReadOnceCommand.Cancel();
        try
        {
            await Task.WhenAll(_selectionTask, _databaseWork,
                Editor?.SaveCommand.ExecutionTask ?? Task.CompletedTask,
                PointEditor?.ReadOnceCommand.ExecutionTask ?? Task.CompletedTask,
                PointEditor?.SavePointCommand.ExecutionTask ?? Task.CompletedTask,
                PointEditor?.DeletePointCommand.ExecutionTask ?? Task.CompletedTask,
                RefreshDevicesCommand.ExecutionTask ?? Task.CompletedTask,
                DeleteDeviceCommand.ExecutionTask ?? Task.CompletedTask);
        }
        finally
        {
            // 即使等待的任务报错，也解除旧表单和采集状态通知。
            DetachEditors();
            _tasks.PropertyChanged -= TasksChanged;
        }
    }
}

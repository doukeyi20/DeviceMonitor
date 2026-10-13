using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.Sampling;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 主窗口数据源：保存用户、页面列表和共用管理器，CurrentPage 决定显示哪页。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private PageViewModel _currentPage;
    private Task? _stopBackgroundWorkTask; // 退出时的共用等待任务，避免重复停止全部工作。
    private bool _isStoppingBackgroundWork; // 开始退出后，禁止再打开账户菜单中的操作。
    private readonly UserStore _userStore;
    private PasswordEditViewModel? _passwordEditor; // 当前修改密码窗口的数据源，没打开时为 null。

    public event Action? LogoutRequested;
    public event Action<PasswordEditViewModel>? PasswordEditorRequested;
    public event Action? LogsFolderRequested;

    public string WindowTitle { get; } = "设备监测与数据追溯系统";
    public UserAccount CurrentUser { get; }
    public string CurrentUserRoleName => CurrentUser.Role == UserRole.Administrator ? "管理员" : "操作员";

    // 各页面共用这一份管理器，避免同一设备出现两套采集任务。
    public DeviceRuntimeManager DeviceRuntime { get; }
    public IReadOnlyList<PageViewModel> Pages { get; } // 左侧导航列表，每项是一个页面的 ViewModel。

    // 左侧导航把选中页面写到这里；通知发出后，右侧 ContentControl 切换显示。
    public PageViewModel CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value is not null)
                SetProperty(ref _currentPage, value);
        }
    }

    /// <summary>
    /// 由 MainWindow 构造方法创建，作为主窗口的数据源。
    /// 准备管理器和各页面。管理员有全部页面，操作员只有监控页，不自动采集。
    /// </summary>
    public MainViewModel(UserAccount currentUser, UserStore userStore, DeviceStore deviceStore,
        HistoryStore historyStore)
    {
        CurrentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        ArgumentNullException.ThrowIfNull(deviceStore);
        ArgumentNullException.ThrowIfNull(historyStore);

        // 先准备共用对象，再创建页面。不要分别在两个页面里各 new 一个管理器。
        
        // 报警数据库操作对象，与读数历史使用同一个数据库文件。
        var alarmStore = new AlarmStore(historyStore.DatabasePath);

        // 创建设备运行管理器，传入当前用户、设备数据库和历史数据库
        DeviceRuntime = new DeviceRuntimeManager(CurrentUser, deviceStore, historyStore, alarmStore);

        // 创建监控页的数据源，传入同一个运行管理器。
        var monitoringPage = new MonitoringViewModel(CurrentUser, deviceStore, DeviceRuntime);

        if (CurrentUser.Role == UserRole.Administrator)
        {
            // Pages视图模型列表，包含监控页、设备管理页、采集页、历史页和用户管理页
            Pages = new PageViewModel[]
            {
                monitoringPage,
                new DeviceManagementViewModel(CurrentUser, deviceStore, DeviceRuntime),
                new AcquisitionViewModel(CurrentUser, deviceStore, DeviceRuntime),
                // 采集任务写历史，历史页面查历史；继续沿用同一个 HistoryStore。
                new HistoryViewModel(CurrentUser, historyStore, alarmStore),
                new UserManagementViewModel(CurrentUser, userStore)
            };
        }
        else
        {
            // 操作员在监控页手动启停，但不能进入设备、点位和用户配置页面；登录不自动采集。
            Pages = new PageViewModel[] { monitoringPage };
        }

        // 指定默认显示监控页。管理员和操作员都能看到这个页面。
        _currentPage = monitoringPage;
    }

    /// <summary>
    /// 由 LogoutCommand 调用。
    /// 请求窗口处理退出登录，不直接关窗口。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void Logout()
    {
        if (CanLogout())
            // MainWindow.HandleLogoutRequested 会确认、收尾并返回登录窗口；这里只发请求。
            LogoutRequested?.Invoke();
    }

    /// <summary>
    /// 由 LogoutCommand、ChangePasswordCommand、OpenLogsCommand 判断按钮能不能点时调用。
    /// 没在退出或修改密码，才允许操作账户菜单。
    /// </summary>
    private bool CanLogout() => !_isStoppingBackgroundWork && _passwordEditor is null;

    /// <summary>
    /// 由 ChangePasswordCommand 调用。
    /// 准备修改密码的数据源，再请求窗口显示表单。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void ChangePassword()
    {
        if (!CanLogout() || PasswordEditorRequested is null)
            return;
        _passwordEditor = new PasswordEditViewModel(CurrentUser, _userStore);
        NotifyAccountCommands();
        // MainWindow.OpenPasswordEditor 打开密码窗口，关闭后再恢复账户菜单。
        try { PasswordEditorRequested.Invoke(_passwordEditor); }
        finally
        {
            _passwordEditor = null;
            NotifyAccountCommands();
        }
    }

    // 请求主窗口打开日志文件夹。
    [RelayCommand(CanExecute = nameof(CanLogout))]
    // 由 OpenLogsCommand 调用。
    private void OpenLogs()
    {
        if (CanLogout())
            // MainWindow.xaml.cs 的 OpenLogsFolder 用资源管理器打开目录，失败就弹窗说明。
            LogsFolderRequested?.Invoke();
    }

    /// <summary>
    /// 由 ChangePassword 和 StopBackgroundWorkAsync 调用。
    /// 让账户菜单和退出按钮重新判断能不能点。
    /// </summary>
    private void NotifyAccountCommands()
    {
        LogoutCommand.NotifyCanExecuteChanged();
        ChangePasswordCommand.NotifyCanExecuteChanged();
        OpenLogsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 由 MainWindow.StopBackgroundWorkAndHandleAlarmsAsync 在退出前调用。
    /// 退出前等采集、保存、查询和导出收尾，并解除页面订阅。
    /// </summary>
    public Task StopBackgroundWorkAsync()
    {
        // 退出登录期间再点关闭，也只做同一次收尾，不重复停止或重复保存。
        if (_stopBackgroundWorkTask is not null)
            return _stopBackgroundWorkTask;
        _isStoppingBackgroundWork = true;
        NotifyAccountCommands();

        // 无论什么角色，都通过共用管理器停止连续采集。
        Task stopSampling = DeviceRuntime.StopAllAsync();

        // 各页面也停掉自己的测试、查询、导出；WhenAll 等它们和采集任务全部收尾。
        _stopBackgroundWorkTask = Task.WhenAll(Pages.OfType<HistoryViewModel>()
            .Select(page => page.StopOperationsAsync())
            .Concat(Pages.OfType<MonitoringViewModel>().Select(page => page.StopOperationsAsync()))
            .Concat(Pages.OfType<AcquisitionViewModel>().Select(page => page.StopOperationsAsync()))
            .Concat(Pages.OfType<DeviceManagementViewModel>().Select(page => page.StopOperationsAsync()))
            .Concat(Pages.OfType<UserManagementViewModel>().Select(page => page.StopOperationsAsync()))
            .Prepend(_passwordEditor?.StopOperationsAsync() ?? Task.CompletedTask)
            .Prepend(stopSampling));
        return _stopBackgroundWorkTask;
    }
}

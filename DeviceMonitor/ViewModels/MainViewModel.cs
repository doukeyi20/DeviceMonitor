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
/// 主窗口的数据源：保存当前用户，准备左侧 Pages，CurrentPage 决定右侧显示哪个页面。
/// 同一次登录只建一份 DeviceRuntimeManager，各页面沿用它；构造完成不等于启动采集。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private PageViewModel _currentPage;
    private Task? _stopBackgroundWorkTask;
    private bool _isStoppingBackgroundWork;
    private readonly UserStore _userStore;
    private PasswordEditViewModel? _passwordEditor;

    public event Action? LogoutRequested;
    public event Action<PasswordEditViewModel>? PasswordEditorRequested;
    public event Action? LogsFolderRequested;

    public string WindowTitle { get; } = "设备监测与数据追溯系统";
    public UserAccount CurrentUser { get; }
    public string CurrentUserRoleName => CurrentUser.Role == UserRole.Administrator ? "管理员" : "操作员";

    // 这个主窗口只创建一份。监控页用它启停；配置页只问运行状态，避免单次测试冲突。
    public DeviceRuntimeManager DeviceRuntime { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

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
    /// MainWindow 构造时创建这里。接住用户和 Store，先创建共用设备运行管理器，再创建各页的数据源。
    /// 管理员有配置/用户/历史页；操作员只有监控页。默认进入监控，但不自动勾选或采集。
    /// </summary>
    public MainViewModel(UserAccount currentUser, UserStore userStore, DeviceStore deviceStore,
        HistoryStore historyStore)
    {
        CurrentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        ArgumentNullException.ThrowIfNull(deviceStore);
        ArgumentNullException.ThrowIfNull(historyStore);

        // 先准备共用对象，再创建页面。不要分别在两个页面里各 new 一个管理器。
        
        // 创建报警管理器，传入历史数据库路径
        var alarmStore = new AlarmStore(historyStore.DatabasePath);

        // 创建设备运行管理器，传入当前用户、设备数据库和历史数据库
        DeviceRuntime = new DeviceRuntimeManager(CurrentUser, deviceStore, historyStore, alarmStore);

        // 创建监控页的viewmodel，传入当前用户、设备数据库和设备运行管理器
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
    /// 右上角“退出登录”按钮调用。这里只通知窗口，不直接关闭窗口或操作密码框。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void Logout()
    {
        if (CanLogout())
            // 调用 MainWindow.xaml.cs 的 HandleLogoutRequested：弹出退出确认框。
            // 确认后由后台等待采集等任务结束，再显示登录窗口、关闭主窗口；这个事件不会等待异步收尾完成。
            LogoutRequested?.Invoke();
    }

    /// <summary>
    /// 账户菜单和退出命令共用这个条件：没在退出，也没打开自己的密码表单，才允许新操作。
    /// </summary>
    private bool CanLogout() => !_isStoppingBackgroundWork && _passwordEditor is null;

    /// <summary>
    /// 右上角账户菜单调用。先为这次弹窗准备数据源，再通知 MainWindow 真正显示窗口。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void ChangePassword()
    {
        if (!CanLogout() || PasswordEditorRequested is null)
            return;
        _passwordEditor = new PasswordEditViewModel(CurrentUser, _userStore);
        NotifyAccountCommands();
        // 调用 MainWindow.xaml.cs 的 OpenPasswordEditor：用 _passwordEditor 作为数据源打开 PasswordWindow。
        // 密码窗口关闭后才回来执行 finally，恢复账户菜单和退出按钮。
        try { PasswordEditorRequested.Invoke(_passwordEditor); }
        finally
        {
            _passwordEditor = null;
            NotifyAccountCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void OpenLogs()
    {
        if (CanLogout())
            // 调用 MainWindow.xaml.cs 的 OpenLogsFolder：让资源管理器打开日志文件夹，失败则弹出提示。
            LogsFolderRequested?.Invoke();
    }

    /// <summary>
    /// 密码表单打开/关闭、主窗口开始收尾时调用，让账户菜单和退出按钮重新判断能不能点。
    /// </summary>
    private void NotifyAccountCommands()
    {
        LogoutCommand.NotifyCanExecuteChanged();
        ChangePasswordCommand.NotifyCanExecuteChanged();
        OpenLogsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 关闭程序或退出登录时，MainWindow 都调用这里：等采集、保存和查询真正结束。
    /// 共用管理器统一停止连续采集；采集管理页只取消单次测试、等待自己的配置查询。
    /// 历史页也要取消/等待导出，监控页要等设备查询并解除订阅，不能关窗后还回填页面。
    /// </summary>
    public Task StopBackgroundWorkAsync()
    {
        // 退出登录期间再点关闭，也只做同一次收尾，不重复停止或重复保存。
        if (_stopBackgroundWorkTask is not null)
            return _stopBackgroundWorkTask;
        _isStoppingBackgroundWork = true;
        NotifyAccountCommands();

        // 不依赖有没有采集管理页：管理员和操作员关窗都由这里通知管理器停止。
        Task stopSampling = DeviceRuntime.StopAllAsync();

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

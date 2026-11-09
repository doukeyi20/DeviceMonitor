// 管理当前用户、功能页面和共享采集任务。
using DeviceMonitor.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public partial class MainViewModel : ObservableObject
{
    // CurrentPage 只决定右侧显示哪个页面，不决定设备是否继续采集。
    private PageViewModel _currentPage;

    // 用于判断是否正在停止后台工作
    // 这里存的是整轮退出收尾的 Task；再次请求退出时返回同一份任务，让调用方等它结束。
    // 非空只说明已经发起过收尾，不说明已经完成；完成后也会保留，不重做一轮。
    private Task? _stopBackgroundWorkTask;

    // 决定是否可以关闭窗口，避免重复触发关闭事件。
    // 实际是会话退出标记：收尾一开始就打上，禁用退出登录、修改密码、打开日志等账户命令。
    // 这个会话以后不再恢复使用，所以不清回 false；它不直接决定窗口能否关闭。
    private bool _isStoppingBackgroundWork;
    private readonly UserStore _userStore;

    public event Action? LogoutRequested;
    public event Action<PasswordEditViewModel>? PasswordEditorRequested;
    public event Action? LogsFolderRequested;

    public string WindowTitle { get; } = "设备监测与数据追溯系统";
    public UserAccount CurrentUser { get; }
    public string CurrentUserRoleName => CurrentUser.Role == UserRole.Administrator ? "管理员" : "操作员";

    // 采集任务管理器
    public SamplingTasks SamplingTasks { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value is not null)
                SetProperty(ref _currentPage, value);
        }
    }

    public MainViewModel(UserAccount currentUser, UserStore userStore, DeviceStore deviceStore,
        HistoryStore historyStore)
    {
        CurrentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        ArgumentNullException.ThrowIfNull(deviceStore);
        ArgumentNullException.ThrowIfNull(historyStore);

        var alarmStore = new AlarmStore(historyStore.DatabasePath);

        // 创建采集任务管理器时，传入当前账户和共用的 Store
        // 整个登录会话共用这一份任务名单；切页不会重新创建采集任务。
        SamplingTasks = new SamplingTasks(CurrentUser, deviceStore, historyStore, alarmStore);

        var monitoringPage = new MonitoringViewModel(CurrentUser, deviceStore, SamplingTasks);

        if (CurrentUser.Role == UserRole.Administrator)
        {
            Pages = new PageViewModel[]
            {
                monitoringPage,
                new DeviceManagementViewModel(CurrentUser, deviceStore, SamplingTasks),

                new HistoryViewModel(CurrentUser, historyStore, alarmStore),
                new UserManagementViewModel(CurrentUser, userStore)
            };
        }
        else
        {
            Pages = new PageViewModel[] { monitoringPage, new HistoryViewModel(CurrentUser, historyStore, alarmStore) };
        }

        _currentPage = monitoringPage;
    }

    /// <summary>请求窗口处理退出登录，不直接关窗口。</summary>
    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void Logout()
    {
        if (CanLogout())
            LogoutRequested?.Invoke();
    }

    /// <summary>退出收尾期间不再接受账户菜单操作；模态弹窗由 WPF 限制父窗口。</summary>
    private bool CanLogout() => !_isStoppingBackgroundWork;


    /// <summary>准备修改密码的数据源，再请求窗口显示表单。</summary>
    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void ChangePassword()
    {
        if (!CanLogout() || PasswordEditorRequested is null)
            return;

        var editor = new PasswordEditViewModel(CurrentUser, _userStore);
        PasswordEditorRequested.Invoke(editor);
    }


    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void OpenLogs()
    {
        if (CanLogout())
            LogsFolderRequested?.Invoke();
    }

    /// <summary>让账户菜单和退出按钮重新判断canexcute，能不能点。</summary>
    private void NotifyAccountCommands()
    {
        LogoutCommand.NotifyCanExecuteChanged();
        ChangePasswordCommand.NotifyCanExecuteChanged();
        OpenLogsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>退出前等采集、保存、查询和导出收尾，并解除页面订阅。</summary>
    public Task StopBackgroundWorkAsync()
    {
        if (_stopBackgroundWorkTask is not null)
            return _stopBackgroundWorkTask;

        _isStoppingBackgroundWork = true;
        NotifyAccountCommands();

        Task stopSampling = SamplingTasks.StopAllAsync();

        // 页面都提供同一个收尾入口，不再按页面类型分别查找。
        _stopBackgroundWorkTask = Task.WhenAll(Pages.Select(page => page.StopOperationsAsync())
            .Prepend(stopSampling));
        // 返回正在进行的收尾任务，避免重复触发。
        return _stopBackgroundWorkTask;
    }
}

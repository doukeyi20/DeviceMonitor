// 管理当前用户、功能页面和共享采集任务。
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
    private PageViewModel _currentPage;
    private Task? _stopBackgroundWorkTask;
    private bool _isStoppingBackgroundWork;
    private readonly UserStore _userStore;

    public event Action? LogoutRequested;
    public event Action<PasswordEditViewModel>? PasswordEditorRequested;
    public event Action? LogsFolderRequested;

    public string WindowTitle { get; } = "设备监测与数据追溯系统";
    public UserAccount CurrentUser { get; }
    public string CurrentUserRoleName => CurrentUser.Role == UserRole.Administrator ? "管理员" : "操作员";

    public DeviceTasks DeviceTasks { get; }
    public IReadOnlyList<PageViewModel> Pages
    {
        get;
    }

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

        DeviceTasks = new DeviceTasks(CurrentUser, deviceStore, historyStore, alarmStore);

        var monitoringPage = new MonitoringViewModel(CurrentUser, deviceStore, DeviceTasks);

        if (CurrentUser.Role == UserRole.Administrator)
        {
            Pages = new PageViewModel[]
            {
                monitoringPage,
                new DeviceManagementViewModel(CurrentUser, deviceStore, DeviceTasks),
                new PointConfigurationViewModel(CurrentUser, deviceStore, DeviceTasks),

                new HistoryViewModel(CurrentUser, historyStore, alarmStore),
                new UserManagementViewModel(CurrentUser, userStore)
            };
        }
        else
        {
            Pages = new PageViewModel[] { monitoringPage };
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

    /// <summary>让账户菜单和退出按钮重新判断能不能点。</summary>
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

        Task stopSampling = DeviceTasks.StopAllAsync();

        // 页面都提供同一个收尾入口，不再按页面类型分别查找。
        _stopBackgroundWorkTask = Task.WhenAll(Pages.Select(page => page.StopOperationsAsync())
            .Prepend(stopSampling));
        return _stopBackgroundWorkTask;
    }
}

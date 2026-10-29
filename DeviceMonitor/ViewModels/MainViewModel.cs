// 管理当前用户、功能页面和共享采集任务。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels;

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

    /// <summary>没在退出或修改密码，才允许操作账户菜单。</summary>
    private bool CanLogout() => !_isStoppingBackgroundWork && _passwordEditor is null;

    /// <summary>准备修改密码的数据源，再请求窗口显示表单。</summary>
    [RelayCommand(CanExecute = nameof(CanLogout))]
    private void ChangePassword()
    {
        if (!CanLogout() || PasswordEditorRequested is null)
            return;
        _passwordEditor = new PasswordEditViewModel(CurrentUser, _userStore);
        NotifyAccountCommands();

        try
        {
            PasswordEditorRequested.Invoke(_passwordEditor);
        }
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

        _stopBackgroundWorkTask = Task.WhenAll(Pages.OfType<HistoryViewModel>()
            .Select(page => page.StopOperationsAsync())
            .Concat(Pages.OfType<MonitoringViewModel>().Select(page => page.StopOperationsAsync()))
            .Concat(Pages.OfType<PointConfigurationViewModel>().Select(page => page.StopOperationsAsync()))
            .Concat(Pages.OfType<DeviceManagementViewModel>().Select(page => page.StopOperationsAsync()))
            .Concat(Pages.OfType<UserManagementViewModel>().Select(page => page.StopOperationsAsync()))
            .Prepend(_passwordEditor?.StopOperationsAsync() ?? Task.CompletedTask)
            .Prepend(stopSampling));
        return _stopBackgroundWorkTask;
    }
}

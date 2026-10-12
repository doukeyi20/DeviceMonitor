using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.ViewModels;
using DeviceMonitor.Views;

namespace DeviceMonitor;

/// <summary>
/// 主窗口后台只做窗口和提示：打开密码表单、打开日志目录、退出登录和关窗等待。
/// 页面、权限和采集对象由 MainViewModel 准备；本文件不写 SQL，也不读取设备。
/// </summary>
public partial class MainWindow : Window
{
    // 返回登录窗口时继续用这三个对象：数据库没换，只是换了登录账户。
    private readonly UserStore _userStore;
    private readonly DeviceStore _deviceStore;
    private readonly HistoryStore _historyStore;
    private readonly MainViewModel _viewModel;
    private bool _isLeavingWindow;
    private bool _canClose;

    /// <summary>
    /// LoginWindow.OpenMainWindow 登录成功后创建这里。用传入的用户和同一份 Store 创建 MainViewModel。
    /// 订阅它的开窗/退出请求，再设 DataContext；右侧页面之后按 CurrentPage 显示。
    /// </summary>
    public MainWindow(UserAccount currentUser, UserStore userStore, DeviceStore deviceStore,
        HistoryStore historyStore)
    {
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        InitializeComponent();

        _viewModel = new MainViewModel(currentUser, userStore, deviceStore, historyStore);
        _viewModel.LogoutRequested += HandleLogoutRequested;
        _viewModel.PasswordEditorRequested += OpenPasswordEditor;
        _viewModel.LogsFolderRequested += OpenLogsFolder;
        DataContext = _viewModel;
    }

    // 左键点账户区域也能展开菜单。这只是控件显示，不代替任何业务命令。
    private void AccountMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// MainViewModel.ChangePassword 触发 PasswordEditorRequested 后调用这里；显示传来的密码表单数据源。
    /// Owner 指向当前主窗口；ShowDialog 返回前，主窗口不能继续点其他业务按钮。
    /// </summary>
    private void OpenPasswordEditor(PasswordEditViewModel viewModel)
    {
        var dialog = new PasswordWindow(viewModel) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>
    /// OpenLogsCommand 触发 LogsFolderRequested 后调用：让资源管理器打开日志目录；失败则显示原因。
    /// </summary>
    private void OpenLogsFolder()
    {
        try
        {
            string? directory = AppLog.DirectoryPath;
            if (directory is null)
                throw new InvalidOperationException("运行日志尚未初始化。");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            if (AppLog.LastWriteError is { } error)
                MessageBox.Show(this, $"日志最近一次写入失败：{error}。请检查日志文件夹是否可写。", "运行日志");
        }
        catch (Exception ex)
        {
            AppLog.Error("打开日志文件夹失败", exception: ex);
            MessageBox.Show(this, $"无法打开日志文件夹：{ex.Message}", "运行日志");
        }
    }

    /// <summary>
    /// LogoutCommand 触发事件后执行这里：先询问，确认后收尾，再显示一个全新的登录窗口。
    /// 这是界面事件的处理方法，所以用 async void；普通异步方法仍返回 Task。
    /// </summary>
    private async void HandleLogoutRequested()
    {
        // 确认框出现时也算“正在处理”，防止连续点击或点标题栏关闭造成重复切换。
        if (_isLeavingWindow || _canClose)
            return;
        _isLeavingWindow = true;
        LoginWindow? preparedLogin = null;
        try
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "退出登录会停止所有设备的采集，并等待正在保存、查询的操作结束。\n\n" +
                "设备、采集点和历史记录不会删除。确定返回登录窗口吗？",
                "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return; // 取消时还没有停止任何任务，原来的采集可以继续。

            // 先创建、暂不显示。万一数据库打不开，这里失败时原来的采集还没被停掉。
            preparedLogin = new LoginWindow(_userStore, _deviceStore, _historyStore);
            IsEnabled = false;
            await StopBackgroundWorkAndHandleAlarmsAsync("退出登录");

            ShowLoginWindowOrExit(preparedLogin);
            preparedLogin = null; // 已经交给应用显示，或者失败后已在下面的方法里关闭。
            _canClose = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("退出登录失败", $"UserId={_viewModel.CurrentUser.Id}", ex);
            preparedLogin?.Close();
            // 没有完成收尾时，不直接切到另一个账户，避免旧账户的任务还在后台运行。
            MessageBox.Show(this,
                $"未能完成退出登录：{ex.Message}\n\n" +
                "当前窗口已保留。如果采集已经开始停止，不会自动恢复；请关闭程序后重新登录。",
                "退出登录失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!_canClose)
            {
                _isLeavingWindow = false;
                IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// 上面收尾结束后调用。必须先显示登录窗口，再关旧主窗口，否则应用可能直接退出。
    /// </summary>
    private void ShowLoginWindowOrExit(LoginWindow preparedLogin)
    {
        LoginWindow? candidate = preparedLogin;
        while (true)
        {
            try
            {
                candidate ??= new LoginWindow(_userStore, _deviceStore, _historyStore);
                // 不设置 Owner 为旧主窗口：否则旧窗口关闭时，登录窗口也可能跟着被关闭。
                Application.Current.MainWindow = candidate;
                candidate.Show();
                return;
            }
            catch (Exception ex)
            {
                Application.Current.MainWindow = this;
                candidate?.Close();
                candidate = null;

                // 此时采集已永久停止。不能假装回到原来的工作状态，明确选择重试或退出。
                MessageBoxResult retry = MessageBox.Show(this,
                    $"登录窗口未能打开：{ex.Message}\n\n" +
                    "采集已经停止。选择“是”重试打开登录窗口；选择“否”退出程序。",
                    "返回登录窗口失败", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.Yes);
                if (retry != MessageBoxResult.Yes)
                    return;
            }
        }
    }

    /// <summary>
    /// 退出登录和关闭程序共用。数据库写入失败时，仍保留原来的“重试 / 明确放弃”提示。
    /// </summary>
    private async Task StopBackgroundWorkAndHandleAlarmsAsync(string actionText)
    {
        await _viewModel.StopBackgroundWorkAsync();
        var alarms = _viewModel.DeviceRuntime.Alarms;
        while (alarms.PendingSaveCount > 0)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"还有 {alarms.PendingSaveCount} 条报警记录未保存。\n\n" +
                $"选择“是”：重试保存。\n选择“否”：放弃这些未保存的记录并继续{actionText}。\n\n" +
                alarms.PersistenceStatusText,
                "报警记录尚未保存", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes)
                break;
            // 只重试保存，不重新启动采集，也不再调用一次全部停止。
            await alarms.FlushAsync();
        }

        if (alarms.DroppedUpdateCount > 0)
            MessageBox.Show(this,
                $"数据库长时间无法保存，曾超过待保存上限，有 {alarms.DroppedUpdateCount} 次报警更新未能保留。\n" +
                "这些更新无法通过重试找回，请检查数据库是否可写。",
                "报警保存提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 点标题栏关闭时由 WPF 调用。先拦住关闭，等上面的共用收尾方法结束，再真正关闭。
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _canClose)
            return;
        e.Cancel = true;
        if (_isLeavingWindow)
            return;

        _isLeavingWindow = true;
        IsEnabled = false;
        try
        {
            await StopBackgroundWorkAndHandleAlarmsAsync("退出程序");
            _canClose = true;
        }
        catch (Exception ex)
        {
            // 意外错误也要说明白，不让 async void 的异常直接把程序打崩。
            _canClose = MessageBox.Show(this,
                $"等待后台操作结束时出错：{ex.Message}\n\n" +
                "无法确认全部操作已结束。仍要退出程序吗？",
                "关闭程序失败", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        finally
        {
            if (_canClose)
                // 排到本次关闭事件之后，避免在同一次关闭里再次调用 Close。
                _ = Dispatcher.BeginInvoke(new Action(Close));
            else
            {
                _isLeavingWindow = false;
                IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// WPF 在主窗口真正关闭后调用，解除本窗口订阅，记录关窗日志；不是在这里再次停止采集。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.LogoutRequested -= HandleLogoutRequested;
        _viewModel.PasswordEditorRequested -= OpenPasswordEditor;
        _viewModel.LogsFolderRequested -= OpenLogsFolder;
        AppLog.Info("关闭主窗口", $"UserId={_viewModel.CurrentUser.Id}");
        base.OnClosed(e);
    }
}

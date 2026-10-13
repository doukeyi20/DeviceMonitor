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
/// 主窗口的界面操作：弹窗、打开日志目录、退出登录和关窗收尾。
/// 页面和采集管理器由 MainViewModel 准备。
/// </summary>
public partial class MainWindow : Window
{
    // 返回登录窗口时继续用这三个对象：数据库没换，只是换了登录账户。
    private readonly UserStore _userStore;
    private readonly DeviceStore _deviceStore;
    private readonly HistoryStore _historyStore;
    private readonly MainViewModel _viewModel; // 主窗口绑定的数据源。
    private bool _isLeavingWindow; // 正在退出或关窗，防止重复发起收尾。
    private bool _canClose; // 收尾结束后改为 true，下一次 Close 才真正关闭。

    /// <summary>
    /// 由 LoginWindow.OpenMainWindow 在登录成功后创建。
    /// 登录成功后创建主窗口，创建数据源、订阅界面请求，并设置 DataContext。
    /// </summary>
    public MainWindow(UserAccount currentUser, UserStore userStore, DeviceStore deviceStore,
        HistoryStore historyStore)
    {
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        InitializeComponent();

        // 创建页面数据和管理器，再把 ViewModel 的界面请求接到下面的方法。
        _viewModel = new MainViewModel(currentUser, userStore, deviceStore, historyStore);
        _viewModel.LogoutRequested += HandleLogoutRequested;
        _viewModel.PasswordEditorRequested += OpenPasswordEditor;
        _viewModel.LogsFolderRequested += OpenLogsFolder;
        DataContext = _viewModel;
    }

    // 点账户按钮时，展开它的菜单。
    private void AccountMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// 由 MainViewModel 的 PasswordEditorRequested 事件调用。
    /// 用传来的数据源打开密码窗口，关掉弹窗后再返回。
    /// </summary>
    private void OpenPasswordEditor(PasswordEditViewModel viewModel)
    {
        var dialog = new PasswordWindow(viewModel) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>
    /// 由 MainViewModel 的 LogsFolderRequested 事件调用。
    /// 用资源管理器打开日志目录，失败就弹窗说明。
    /// </summary>
    private void OpenLogsFolder()
    {
        try
        {
            string? directory = AppLog.DirectoryPath;
            if (directory is null)
                throw new InvalidOperationException("运行日志尚未初始化。");
            // 确保目录存在，再让系统打开它。
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
    /// 由 MainViewModel 的 LogoutRequested 事件调用。
    /// 确认退出登录后，等后台工作停止，再显示新登录窗口并关闭主窗口。
    /// 事件处理方法用 async void，内部负责捕获异常。
    /// </summary>
    private async void HandleLogoutRequested()
    {
        // 从确认弹窗开始就标记正在退出，防止重复操作。
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

            // 先准备登录窗口，创建失败时不影响原来的采集。
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
            // 收尾失败就保留当前窗口，不带着旧任务切换账户。
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
    /// 由 HandleLogoutRequested 完成退出收尾后调用。
    /// 先显示登录窗口，再允许旧主窗口关闭，避免程序提前退出。
    /// </summary>
    private void ShowLoginWindowOrExit(LoginWindow preparedLogin)
    {
        LoginWindow? candidate = preparedLogin;
        while (true)
        {
            try
            {
                candidate ??= new LoginWindow(_userStore, _deviceStore, _historyStore);
                // 新登录窗口不属于旧主窗口，避免跟着旧窗口一起关闭。
                Application.Current.MainWindow = candidate;
                candidate.Show();
                return;
            }
            catch (Exception ex)
            {
                Application.Current.MainWindow = this;
                candidate?.Close();
                candidate = null;

                // 采集已停止，打开登录窗口失败后，只能重试或退出。
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
    /// 由 HandleLogoutRequested 或 OnClosing 在退出前调用。
    /// 等后台工作停止；还有未保存报警时，询问重试还是放弃。
    /// </summary>
    private async Task StopBackgroundWorkAndHandleAlarmsAsync(string actionText)
    {
        // 先等所有任务收尾，再检查报警是否全部保存。
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
            // 只重试保存，不重新采集。
            await alarms.FlushAsync();
        }

        // 超过待保存上限而丢掉的更新无法补回，要明确提醒。
        if (alarms.DroppedUpdateCount > 0)
            MessageBox.Show(this,
                $"数据库长时间无法保存，曾超过待保存上限，有 {alarms.DroppedUpdateCount} 次报警更新未能保留。\n" +
                "这些更新无法通过重试找回，请检查数据库是否可写。",
                "报警保存提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 点关闭时先拦住窗口，等后台工作收尾，再真正关闭。
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _canClose)
            return;
        e.Cancel = true; // 这次先不关，收尾后再调用 Close。
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
            // 收尾出错就询问是否仍要退出，不让异常直接导致崩溃。
            _canClose = MessageBox.Show(this,
                $"等待后台操作结束时出错：{ex.Message}\n\n" +
                "无法确认全部操作已结束。仍要退出程序吗？",
                "关闭程序失败", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        finally
        {
            if (_canClose)
                // 等本次关闭事件返回后，再发起真正关闭。
                _ = Dispatcher.BeginInvoke(new Action(Close));
            else
            {
                _isLeavingWindow = false;
                IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// 真正关闭后解除事件订阅，再写一条关窗日志。
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

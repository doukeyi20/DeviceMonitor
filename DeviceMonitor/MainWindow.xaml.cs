// 处理主窗口弹窗、日志目录、退出登录和关闭收尾。
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DeviceMonitor.Core;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using DeviceMonitor.ViewModels;
using DeviceMonitor.Views;

namespace DeviceMonitor;

public partial class MainWindow : Window
{
    private readonly UserStore _userStore;
    private readonly DeviceStore _deviceStore;
    private readonly HistoryStore _historyStore;
    private readonly MainViewModel _viewModel;
    private bool _isLeavingWindow;
    private bool _canClose;

    public MainWindow(UserAccount currentUser, UserStore userStore, DeviceStore deviceStore,
    HistoryStore historyStore)
    {
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        InitializeComponent();
        FitWindowToWorkArea();

        _viewModel = new MainViewModel(currentUser, userStore, deviceStore, historyStore);
        _viewModel.LogoutRequested += HandleLogoutRequested;
        _viewModel.PasswordEditorRequested += OpenPasswordEditor;
        _viewModel.LogsFolderRequested += OpenLogsFolder;
        DataContext = _viewModel;
    }

    private void FitWindowToWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width - 24);
        Height = Math.Min(Height, workArea.Height - 24);
    }

    /// <summary>保留最大化能力，禁用窗口边框拖动缩放。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        const int styleIndex = -16;
        const int maximizeButton = 0x00010000;
        const int sizingBorder = 0x00040000;
        int style = GetWindowLong(handle, styleIndex);
        int fixedStyle = (style | maximizeButton) & ~sizingBorder;
        if (SetWindowLong(handle, styleIndex, fixedStyle) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        const uint refreshFrameOnly = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020;
        if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, refreshFrameOnly))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void AccountMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>用传来的数据源打开密码窗口，关掉弹窗后再返回。</summary>
    private void OpenPasswordEditor(PasswordEditViewModel viewModel)
    {
        var dialog = new PasswordWindow(viewModel) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>用系统资源管理器打开日志目录，失败就弹窗说明。</summary>
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

    /// <summary>确认后先等后台工作结束，再打开一次登录窗口。</summary>
    private async void HandleLogoutRequested()
    {
        if (_isLeavingWindow || _canClose)
            return;
        _isLeavingWindow = true;
        LoginWindow? login = null;
        try
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "退出登录会停止所有设备采集，并等待正在保存、查询的操作结束。\n\n确定返回登录窗口吗？",
                "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;

            IsEnabled = false;
            await StopBeforeExitAsync();

            login = new LoginWindow(_userStore, _deviceStore, _historyStore);
            Application.Current.MainWindow = login;
            login.Show();
            _canClose = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("退出登录失败", $"UserId={_viewModel.CurrentUser.Id}", ex);

            Application.Current.MainWindow = this;
            login?.Close();
            MessageBox.Show(this, $"未能返回登录窗口：{ex.Message}\n\n程序将退出，请重新启动后登录。",
                "退出登录", MessageBoxButton.OK, MessageBoxImage.Error);
            _canClose = true;
            Close();
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

    /// <summary>退出登录和关闭程序共用：等采集、保存、查询及导出结束。</summary>
    private async Task StopBeforeExitAsync()
    {
        // 先等待采集、保存和查询收尾，再检查是否仍有报警未保存。
        await _viewModel.StopBackgroundWorkAsync();
        var alarms = _viewModel.DeviceTasks.Alarms;
        if (alarms.PendingSaveCount == 0 && alarms.DroppedUpdateCount == 0)
            return;

        MessageBox.Show(this,
            $"还有 {alarms.PendingSaveCount} 条报警未保存，另有 {alarms.DroppedUpdateCount} 次更新未能保留。\n\n" +
            "将继续退出当前主窗口；未保存的部分不会自动补回，请检查数据库是否可写。",
            "报警保存提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>点关闭先拦住这次关窗，等后台正常收尾，再发起真正关闭。</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _canClose)
            return;
        // 拦截首次关闭，完成异步收尾后由第二次 Close 真正退出。
        e.Cancel = true;
        if (_isLeavingWindow)
            return;

        _isLeavingWindow = true;
        IsEnabled = false;
        try
        {
            await StopBeforeExitAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("退出收尾失败", $"UserId={_viewModel.CurrentUser.Id}", ex);
            MessageBox.Show(this, $"退出收尾时出错：{ex.Message}\n\n程序将退出，详情已记入运行日志。",
                "退出程序", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _canClose = true;

            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    /// <summary>真正关闭后解除事件订阅，再写一条关窗日志。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.LogoutRequested -= HandleLogoutRequested;
        _viewModel.PasswordEditorRequested -= OpenPasswordEditor;
        _viewModel.LogsFolderRequested -= OpenLogsFolder;
        AppLog.Info("关闭主窗口", $"UserId={_viewModel.CurrentUser.Id}");
        base.OnClosed(e);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}

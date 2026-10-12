using System;
using System.ComponentModel;
using System.Windows;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views;

/// <summary>
/// 历史趋势弹窗：接住外面建好的 HistoryTrendViewModel，显示后执行一次加载命令。
/// 关窗先等查询结束；这里只控制窗口，曲线由控件根据绑定的 Records 绘制。
/// </summary>
public partial class HistoryTrendWindow : Window
{
    private readonly HistoryTrendViewModel _viewModel;
    private bool _isClosing;
    private bool _canClose;

    /// <summary>
    /// HistoryView.OpenTrendWindow 创建这里。接住准备好的趋势 ViewModel 设为 DataContext，不另建 Store 或开始采集。
    /// </summary>
    public HistoryTrendWindow(HistoryTrendViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel; // 沿用父页面传来的对象，不在窗口中再 new Store 或找数据库路径。
    }

    // 窗口显示后查一次；构造函数只准备对象，不堵住打开窗口的过程。
    private async void HistoryTrendWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadTrendCommand.CanExecute(null))
            await _viewModel.LoadTrendCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 关闭按钮只请求 Close；是否还要等查询，由下面 OnClosing 统一处理。
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // 用户关窗时先拦一次，查完后再关；不会中途丢下仍在回填界面的后台任务。
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _canClose) return;
        e.Cancel = true;
        if (_isClosing) return;
        _isClosing = true;
        IsEnabled = false;
        try { await _viewModel.StopOperationsAsync(); }
        finally
        {
            _canClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}

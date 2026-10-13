using System;
using System.ComponentModel;
using System.Windows;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views;

/// <summary>
/// 历史趋势窗口，接收数据源、显示后查询，关闭前等查询结束。
/// 曲线由控件根据 Records 绘制，后台不直接画图。
/// </summary>
public partial class HistoryTrendWindow : Window
{
    private readonly HistoryTrendViewModel _viewModel;
    private bool _isClosing; // 已开始关窗等待，不重复收尾。
    private bool _canClose; // 查询收尾后设为 true，下次 Close 才真正关闭。

    /// <summary>
    /// 把历史页传来的趋势数据源设为 DataContext。
    /// </summary>
    public HistoryTrendWindow(HistoryTrendViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
    }

    // 窗口显示后查一次；构造函数只准备对象，不堵住打开窗口的过程。
    private async void HistoryTrendWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadTrendCommand.CanExecute(null))
            await _viewModel.LoadTrendCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 请求关闭，是否等待查询由 OnClosing 处理。
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // 先拦住关闭，等查询结束后再关窗。
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _canClose) return;
        e.Cancel = true; // 这次先不关，避免查询还没结束。
        if (_isClosing) return;
        _isClosing = true;
        IsEnabled = false;
        try { await _viewModel.StopOperationsAsync(); }
        finally
        {
            _canClose = true;
            // 等本次关闭事件返回，再真正关闭。
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}

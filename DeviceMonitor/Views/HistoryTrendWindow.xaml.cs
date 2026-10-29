// 管理历史趋势窗口的查询和关闭。
using System;
using System.ComponentModel;
using System.Windows;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views;

public partial class HistoryTrendWindow : Window
{
    private readonly HistoryTrendViewModel _viewModel;
    private bool _isClosing;
    private bool _canClose;

    public HistoryTrendWindow(HistoryTrendViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void HistoryTrendWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadTrendCommand.CanExecute(null))
            await _viewModel.LoadTrendCommand.ExecuteAsync(null);
    }

    /// <summary>请求关闭，是否等待查询由 OnClosing 处理。</summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _canClose)
            return;
        e.Cancel = true;
        if (_isClosing)
            return;
        _isClosing = true;
        IsEnabled = false;
        try
        {
            await _viewModel.StopOperationsAsync();
        }
        finally
        {
            _canClose = true;

            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}

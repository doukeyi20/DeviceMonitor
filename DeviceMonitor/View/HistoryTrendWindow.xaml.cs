// 管理历史趋势窗口的查询和关闭。
using System;
using System.Windows;
using DeviceMonitor.ViewModel;

namespace DeviceMonitor.View;

public partial class HistoryTrendWindow : Window
{
    private readonly HistoryTrendViewModel _viewModel;

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

    /// <summary>趋势窗口只读数据，直接关闭，不套用设备采集的停止流程。</summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.StopUpdating();
        base.OnClosed(e);
    }
}

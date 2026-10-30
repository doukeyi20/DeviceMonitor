// 处理历史页面初始化、导出路径和趋势窗口。
using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModel;
using Microsoft.Win32;

namespace DeviceMonitor.View;

public partial class HistoryView : UserControl
{
    private HistoryViewModel? _subscribedViewModel;

    public HistoryView() => InitializeComponent();

    /// <summary>显示页面时接上事件；首次进入才自动查询，返回页面保留原结果。</summary>
    private async void HistoryView_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeViewModel();
        if (DataContext is HistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    private void SubscribeViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, DataContext))
            return;
        UnsubscribeViewModel();
        if (DataContext is HistoryViewModel viewModel)
        {
            _subscribedViewModel = viewModel;

            viewModel.ExportPathRequested += ChooseExportPath;
            viewModel.TrendRequested += OpenTrendWindow;
        }
    }

    /// <summary>弹出保存文件窗口，选好返回路径，取消返回 null；这里还不写文件。</summary>
    private string? ChooseExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出历史查询结果",
            Filter = "CSV 文件 (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = $"历史记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        Window? owner = Window.GetWindow(this);

        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }

    /// <summary>用准备好的趋势数据源打开窗口。</summary>
    private void OpenTrendWindow(HistoryTrendViewModel viewModel)
    {
        var dialog = new HistoryTrendWindow(viewModel);
        Window? owner = Window.GetWindow(this);
        if (owner is not null)
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    /// <summary>离开页面时解除订阅并取消导出，不停止设备采集。</summary>
    private void HistoryView_Unloaded(object sender, RoutedEventArgs e) => UnsubscribeViewModel();

    /// <summary>取消旧导出并断开界面请求，不清空历史记录。</summary>
    private void UnsubscribeViewModel()
    {
        if (_subscribedViewModel is null)
            return;
        _subscribedViewModel.ExportRecordsCommand.Cancel();
        _subscribedViewModel.ExportPathRequested -= ChooseExportPath;
        _subscribedViewModel.TrendRequested -= OpenTrendWindow;
        _subscribedViewModel = null;
    }

}

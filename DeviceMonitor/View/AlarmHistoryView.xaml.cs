// 处理报警历史页面初始化、导出路径和日期控件。
using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModel;
using Microsoft.Win32;

namespace DeviceMonitor.View;

public partial class AlarmHistoryView : UserControl
{
    private AlarmHistoryViewModel? _subscribedViewModel;

    public AlarmHistoryView() => InitializeComponent();

    private async void AlarmHistoryView_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeViewModel();
        if (DataContext is AlarmHistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    private void SubscribeViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, DataContext))
            return;
        UnsubscribeViewModel();
        if (DataContext is AlarmHistoryViewModel viewModel)
        {
            _subscribedViewModel = viewModel;
            viewModel.ExportPathRequested += ChooseExportPath;
        }
    }

    /// <summary>弹出保存文件窗口，返回路径；取消返回 null，不在这里写 CSV。</summary>
    private string? ChooseExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出报警历史查询结果",
            Filter = "CSV 文件 (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = $"报警历史-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        Window? owner = Window.GetWindow(this);
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }

    private void AlarmHistoryView_Unloaded(object sender, RoutedEventArgs e) => UnsubscribeViewModel();

    /// <summary>取消旧导出，断开界面请求，不影响采集或历史数据。</summary>
    private void UnsubscribeViewModel()
    {
        if (_subscribedViewModel is null)
            return;

        _subscribedViewModel.ExportRecordsCommand.Cancel();
        _subscribedViewModel.ExportPathRequested -= ChooseExportPath;
        _subscribedViewModel = null;
    }

}

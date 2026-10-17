using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;
using Microsoft.Win32;

namespace DeviceMonitor.Views;

/// <summary>
/// 读数历史页面后台，负责首次查询、选择导出路径和打开趋势窗口。
/// 查询数据在 ViewModel 中，后台只处理界面操作。
/// </summary>
public partial class HistoryView : UserControl
{
    private HistoryViewModel? _subscribedViewModel; // 当前已订阅的数据源，离开时用它解除订阅。

    /// <summary>
    /// 创建页面控件，数据源由主窗口传入。
    /// </summary>
    public HistoryView() => InitializeComponent();

    /// <summary>显示页面时接上事件；首次进入才自动查询，返回页面保留原结果。</summary>
    private async void HistoryView_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeViewModel();
        if (DataContext is HistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 已显示的页面换了数据源，就重新订阅，必要时进行首次查询。
    /// </summary>
    private async void HistoryView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SubscribeViewModel();
        if (e.NewValue is HistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    // 同一数据源不重复订阅，换对象时先断开旧事件。
    private void SubscribeViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, DataContext)) return;
        UnsubscribeViewModel();
        if (DataContext is HistoryViewModel viewModel)
        {
            _subscribedViewModel = viewModel;
            // 订阅界面请求事件，导出和趋势窗口都在后台处理。
            viewModel.ExportPathRequested += ChooseExportPath;
            viewModel.TrendRequested += OpenTrendWindow;
        }
    }

    /// <summary>
    /// 由 HistoryViewModel 的 ExportPathRequested 事件调用。
    /// 弹出保存文件窗口，选好返回路径，取消返回 null；这里还不写文件。
    /// </summary>
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
        // 只把用户选的路径交回导出命令。
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }

    /// <summary>
    /// 由 HistoryViewModel 的 TrendRequested 事件调用。
    /// 用准备好的趋势数据源打开窗口。
    /// </summary>
    private void OpenTrendWindow(HistoryTrendViewModel viewModel)
    {
        var dialog = new HistoryTrendWindow(viewModel);
        Window? owner = Window.GetWindow(this);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog(); // 关掉后才返回原历史列表。
    }

    /// <summary>离开页面时解除订阅并取消导出，不停止设备采集。</summary>
    private void HistoryView_Unloaded(object sender, RoutedEventArgs e) => UnsubscribeViewModel();

    /// <summary>
    /// 取消旧导出并断开界面请求，不清空历史记录。
    /// </summary>
    private void UnsubscribeViewModel()
    {
        if (_subscribedViewModel is null) return;
        _subscribedViewModel.ExportRecordsCommand.Cancel();
        _subscribedViewModel.ExportPathRequested -= ChooseExportPath;
        _subscribedViewModel.TrendRequested -= OpenTrendWindow;
        _subscribedViewModel = null;
    }

    // 日期只从日历选择，避免手填无效日期时仍使用上次日期。
    private void DatePicker_Loaded(object sender, RoutedEventArgs e) => LockDateInput((DatePicker)sender);

    /// <summary>
    /// 日期控件重新启用后，再把手工输入锁住。
    /// </summary>
    private void DatePicker_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(new Action(() => LockDateInput((DatePicker)sender)));
    }

    /// <summary>
    /// 找到日期控件内部的输入框，设为只读，仍可用日历选日期。
    /// </summary>
    private static void LockDateInput(DatePicker picker)
    {
        // 先生成控件内部结构，才能找到 PART_TextBox 输入框。
        picker.ApplyTemplate();
        if (picker.Template.FindName("PART_TextBox", picker) is TextBox textBox)
            textBox.IsReadOnly = true;
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;
using Microsoft.Win32;

namespace DeviceMonitor.Views;

/// <summary>
/// 采集读数历史页的后台：页面首次显示后查询，离开再回来保留已有结果。
/// 订阅选择导出路径和打开趋势窗口的方法；日期控件只管输入方式，数据库查询在 ViewModel/Store。
/// </summary>
public partial class HistoryView : UserControl
{
    private HistoryViewModel? _subscribedViewModel;

    /// <summary>
    /// 主窗口模板创建页面时调用。这里只加载 XAML，使用外面传来的 ViewModel 作为 DataContext。
    /// </summary>
    public HistoryView() => InitializeComponent();

    /// <summary>Loaded 先订阅界面方法；只在首次进入时查询，切回来保留原来的结果和页码。</summary>
    private async void HistoryView_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeViewModel();
        if (DataContext is HistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 页面已显示时换了数据源，重新订阅并让新对象按首次进入规则查询；未加载时等 Loaded。
    /// </summary>
    private async void HistoryView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SubscribeViewModel();
        if (e.NewValue is HistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    // 数据源未变就不重复登记；变了先解除旧事件，再把后台方法登记到新对象。
    private void SubscribeViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, DataContext)) return;
        UnsubscribeViewModel();
        if (DataContext is HistoryViewModel viewModel)
        {
            _subscribedViewModel = viewModel;
            viewModel.ExportPathRequested += ChooseExportPath;
            viewModel.TrendRequested += OpenTrendWindow;
        }
    }

    /// <summary>ExportRecords 命令触发 ExportPathRequested 后调用；选择成功返回完整路径，取消返回 null。</summary>
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

    /// <summary>ShowTrend 命令触发事件后调用。ViewModel 已准备好数据源，这里只打开窗口。</summary>
    private void OpenTrendWindow(HistoryTrendViewModel viewModel)
    {
        var dialog = new HistoryTrendWindow(viewModel);
        Window? owner = Window.GetWindow(this);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog(); // 模态窗口关闭后再回到原历史列表，不启动新的采集任务。
    }

    /// <summary>离开历史页面时移除选择文件的订阅，并取消正在导出的任务；不停止设备采集。</summary>
    private void HistoryView_Unloaded(object sender, RoutedEventArgs e) => UnsubscribeViewModel();

    /// <summary>
    /// 换数据源或离开时调用。取消尚未结束的导出，解除界面请求；不会停止设备采集，也不清空历史记录。
    /// </summary>
    private void UnsubscribeViewModel()
    {
        if (_subscribedViewModel is null) return;
        _subscribedViewModel.ExportRecordsCommand.Cancel();
        _subscribedViewModel.ExportPathRequested -= ChooseExportPath;
        _subscribedViewModel.TrendRequested -= OpenTrendWindow;
        _subscribedViewModel = null;
    }

    // 日期控件加载后调用。只能用右侧日历选日期，避免输入错误时仍查询上一次日期。
    // 这是控件外观/输入方式的设置，不查询数据库，也不创建新的 ViewModel。
    private void DatePicker_Loaded(object sender, RoutedEventArgs e) => LockDateInput((DatePicker)sender);

    /// <summary>
    /// 查询前后日期控件禁用/启用时调用，再次锁住内部输入框，避免 WPF 恢复控件后又变成可手填。
    /// </summary>
    private void DatePicker_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(new Action(() => LockDateInput((DatePicker)sender)));
    }

    /// <summary>
    /// 日期控件加载/重新启用时调用。锁住内部手工输入，只让日历选日期；不处理 SQL 查询条件。
    /// </summary>
    private static void LockDateInput(DatePicker picker)
    {
        // 时间筛选最初是隐藏的；先准备控件内部结构，才能找到真正的日期输入框。
        picker.ApplyTemplate();
        if (picker.Template.FindName("PART_TextBox", picker) is TextBox textBox)
            textBox.IsReadOnly = true;
    }
}

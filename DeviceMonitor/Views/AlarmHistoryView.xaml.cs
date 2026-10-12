using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;
using Microsoft.Win32;

namespace DeviceMonitor.Views;

/// <summary>
/// 报警历史页的后台：页面首次显示后查询，离开再回来保留已有结果。
/// 订阅选择导出路径的方法；日期控件只管输入方式，数据库查询在 ViewModel/Store。
/// </summary>
public partial class AlarmHistoryView : UserControl
{
    private AlarmHistoryViewModel? _subscribedViewModel;

    /// <summary>
    /// 主窗口模板创建页面时调用。这里只加载 XAML，使用外面传来的 ViewModel 作为 DataContext。
    /// </summary>
    public AlarmHistoryView() => InitializeComponent();

    // 首次打开自动查询；切走再回来保留结果和页码，需要新数据时再点“查询”。
    private async void AlarmHistoryView_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeViewModel();
        if (DataContext is AlarmHistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 页面已显示时换了数据源，重新订阅并让新对象按首次进入规则查询；未加载时等 Loaded。
    /// </summary>
    private async void AlarmHistoryView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SubscribeViewModel();
        if (e.NewValue is AlarmHistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    // 从 DataContext 接住同一个数据源，不重新 new；Loaded 重复发生也不会重复订阅。
    private void SubscribeViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, DataContext)) return;
        UnsubscribeViewModel();
        if (DataContext is AlarmHistoryViewModel viewModel)
        {
            _subscribedViewModel = viewModel;
            viewModel.ExportPathRequested += ChooseExportPath;
        }
    }

    /// <summary>导出命令请求保存位置时调用。只显示保存窗口，不在这里查询数据库或写 CSV。</summary>
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

    /// <summary>
    /// 换数据源或离开时调用。取消尚未结束的导出，解除界面请求；不会停止设备采集，也不清空历史记录。
    /// </summary>
    private void UnsubscribeViewModel()
    {
        if (_subscribedViewModel is null) return;
        // 切换标签/页面或换数据源时取消旧导出；导出器会清理自己的临时文件，不删原 CSV。
        _subscribedViewModel.ExportRecordsCommand.Cancel();
        _subscribedViewModel.ExportPathRequested -= ChooseExportPath;
        _subscribedViewModel = null;
    }

    // 日期只从日历选择，避免输错日期时控件还偷偷保留上一次日期。
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
        picker.ApplyTemplate();
        if (picker.Template.FindName("PART_TextBox", picker) is TextBox textBox)
            textBox.IsReadOnly = true;
    }
}

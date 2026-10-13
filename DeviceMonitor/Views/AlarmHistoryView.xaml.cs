using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;
using Microsoft.Win32;

namespace DeviceMonitor.Views;

/// <summary>
/// 报警历史页面后台，负责首次查询、选择导出路径和日期控件设置。
/// 真正查询和导出由 ViewModel 处理。
/// </summary>
public partial class AlarmHistoryView : UserControl
{
    private AlarmHistoryViewModel? _subscribedViewModel; // 当前已订阅的数据源，离开时解除它的订阅。

    /// <summary>
    /// 创建页面控件，数据源由主窗口传入。
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
    /// 已显示的页面换了数据源，就重新订阅，必要时进行首次查询。
    /// </summary>
    private async void AlarmHistoryView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SubscribeViewModel();
        if (e.NewValue is AlarmHistoryViewModel viewModel && !viewModel.HasLoadedRecords
            && viewModel.RefreshRecordsCommand.CanExecute(null))
            await viewModel.RefreshRecordsCommand.ExecuteAsync(null);
    }

    // 接上当前数据源，同一对象不重复订阅。
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

    /// <summary>
    /// 由 AlarmHistoryViewModel 的 ExportPathRequested 事件调用。
    /// 弹出保存文件窗口，返回路径；取消返回 null，不在这里写 CSV。
    /// </summary>
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

    // 离开标签时解除订阅并取消旧导出。
    private void AlarmHistoryView_Unloaded(object sender, RoutedEventArgs e) => UnsubscribeViewModel();

    /// <summary>
    /// 取消旧导出，断开界面请求，不影响采集或历史数据。
    /// </summary>
    private void UnsubscribeViewModel()
    {
        if (_subscribedViewModel is null) return;
        // 导出器收到取消后清理临时文件，不删除原 CSV。
        _subscribedViewModel.ExportRecordsCommand.Cancel();
        _subscribedViewModel.ExportPathRequested -= ChooseExportPath;
        _subscribedViewModel = null;
    }

    // 日期只从日历选择，避免输错日期时控件还偷偷保留上一次日期。
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
    /// 找到内部日期输入框设为只读，仍可用日历选日期。
    /// </summary>
    private static void LockDateInput(DatePicker picker)
    {
        // 先生成内部结构，才能找到 PART_TextBox 输入框。
        picker.ApplyTemplate();
        if (picker.Template.FindName("PART_TextBox", picker) is TextBox textBox)
            textBox.IsReadOnly = true;
    }
}

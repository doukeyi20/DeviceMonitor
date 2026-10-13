using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views;

/// <summary>
/// 实时监控页面后台，显示页面后刷新设备配置；其他操作由绑定的命令处理。
/// </summary>
public partial class MonitoringView : UserControl
{

    /// <summary>
    /// 创建页面控件，数据源由主窗口的 DataTemplate 传入。
    /// </summary>
    public MonitoringView()
    {
        InitializeComponent();
    }

    // XAML 的 Loaded 调用：首次进入、切走后回来，都刷新一次设备名称和启用配置。
    private async void MonitoringView_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentViewModelAsync();
    }

    // 已显示的页面换了数据源，也加载新对象的数据；未显示时等 Loaded 即可。
    private async void MonitoringView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
            await RefreshCurrentViewModelAsync();
    }

    /// <summary>
    /// 取现有数据源并执行设备刷新命令，命令忙时跳过。
    /// </summary>
    private async Task RefreshCurrentViewModelAsync()
    {
        if (DataContext is MonitoringViewModel viewModel && viewModel.RefreshDevicesCommand.CanExecute(null))
            await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
        // 离开本页不停止任务，只是暂时不看监控。
    }
}

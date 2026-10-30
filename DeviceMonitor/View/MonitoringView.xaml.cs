// 初始化监控页并刷新设备配置。
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModel;

namespace DeviceMonitor.View;

public partial class MonitoringView : UserControl
{
    public MonitoringView()
    {
        InitializeComponent();
    }

    private async void MonitoringView_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentViewModelAsync();
    }

    /// <summary>取现有数据源并执行设备刷新命令，命令忙时跳过。</summary>
    private async Task RefreshCurrentViewModelAsync()
    {
        if (DataContext is MonitoringViewModel viewModel && viewModel.RefreshDevicesCommand.CanExecute(null))
            await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
    }
}

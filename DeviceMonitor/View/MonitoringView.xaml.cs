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

    /// <summary>小窗口保留一行卡片，把高度留给曲线；大窗口显示两行。</summary>
    private void LayoutChanged(object sender, SizeChangedEventArgs e)
    {
        double available = ActualHeight - DeviceHeader.ActualHeight - 290;
        ReadingCards.MaxHeight = available >= 256 ? 256 : 128;
    }

    private void RevealSelectedItem(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is not null)
            list.ScrollIntoView(list.SelectedItem);
    }

    /// <summary>
    /// 取出viewmodel并执行刷新命令
    /// </summary>
    /// <returns></returns>
    private async Task RefreshCurrentViewModelAsync()
    {
        if (DataContext is MonitoringViewModel viewModel && viewModel.RefreshDevicesCommand.CanExecute(null))
            await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
    }
}

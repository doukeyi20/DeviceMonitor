using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views;

/// <summary>
/// 实时监控页的后台只负责页面显示后加载设备配置。
/// 开始、停止、勾选及报警筛选都已绑定到 MonitoringViewModel，不在这里再写一套点击逻辑。
/// </summary>
public partial class MonitoringView : UserControl
{

    /// <summary>
    /// 主窗口根据 MonitoringViewModel 的 DataTemplate 创建页面，InitializeComponent 加载 XAML；数据源由模板传入。
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
    /// Loaded 或已显示页面换数据源时调用。取现有 ViewModel，执行 RefreshDevicesCommand；刷新
    /// </summary>
    private async Task RefreshCurrentViewModelAsync()
    {
        if (DataContext is MonitoringViewModel viewModel && viewModel.RefreshDevicesCommand.CanExecute(null))
            await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
        // 没有 Unloaded 停止操作：离开本页只是暂时不看，原来的采集任务仍继续。
    }
}

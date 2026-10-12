using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// “采集点配置”页的后台：加载设备列表、订阅开点位窗口的请求。
    /// 沿用 DataContext 中的 AcquisitionViewModel；离开只取消单次测试，不停止监控页的连续采集。
    /// </summary>
    public partial class AcquisitionView : UserControl
    {
        // 记住当前订阅的是哪个 ViewModel，离开页面时才能解除订阅。
        // 它与页面 DataContext 指向同一个对象，不是另建一个。
        private AcquisitionViewModel? _subscribedViewModel;

        /// <summary>
        /// WPF 根据 DataTemplate 创建采集页面时调用这里，只负责加载 XAML。
        /// 页面的 DataContext 来自当前 AcquisitionViewModel；这里不另外 new 一个页面 ViewModel。
        /// </summary>
        public AcquisitionView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 采集页面进入显示区域时，由 XAML 的 Loaded 事件调用；返回页面时也可能再调用。
        /// 先 SubscribeViewModel 登记开窗方法，再执行 ViewModel 的 RefreshDevicesCommand 自动加载设备。
        /// </summary>
        private async void AcquisitionView_Loaded(object sender, RoutedEventArgs e)
        {
            // 先接好开窗事件，后面点击“管理采集点”才有人响应。
            SubscribeViewModel();

            // 先执行刷新命令，自动加载设备列表；如果正在刷新就不再重复执行。
            if (DataContext is AcquisitionViewModel viewModel &&
                viewModel.RefreshDevicesCommand.CanExecute(null))
            {
                // 正在刷新时命令不可执行，避免自动加载与手动刷新重叠。
                await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
            }
        }

        /// <summary>
        /// 页面的 DataContext 换成另一个对象时，由 XAML 的 DataContextChanged 事件调用。
        /// 页面已加载才重新 SubscribeViewModel；还没加载就等 Loaded 来处理，不在这里创建新 ViewModel。
        /// </summary>
        private void AcquisitionView_DataContextChanged(
            object sender, DependencyPropertyChangedEventArgs e)
        {
            // 已经显示的页面换了数据源，就重新订阅新的 ViewModel。
            // 页面还没加载时，等 Loaded 再订阅即可。
            if (IsLoaded)
                SubscribeViewModel();
        }

        /// <summary>
        /// Loaded 或数据源变化时调用：取已有 AcquisitionViewModel，把 OpenPointWindow 登记到 PointManagementRequested。
        /// </summary>
        private void SubscribeViewModel()
        {
            // 先移除旧订阅，避免同一个方法登记两次，一次点击却开两个窗口。
            UnsubscribeViewModel();

            if (DataContext is AcquisitionViewModel viewModel)
            {
                _subscribedViewModel = viewModel;
                viewModel.PointManagementRequested += OpenPointWindow;
            }
        }

        /// <summary>
        /// 管理采集点命令触发请求后到这里：先调用 CreatePointViewModel 准备弹窗的数据源，再显示 PointWindow。
        /// ShowDialog 返回后执行 RefreshPointsCommand，让主页面看到刚保存的点位配置。
        /// </summary>
        private void OpenPointWindow(DeviceInfo device)
        {
            // this 是采集管理页面；找到承载它的主窗口，让弹窗居中显示。
            Window? owner = Window.GetWindow(this);
            try
            {
                // 传递采集点界面对应的数据源
                AcquisitionViewModel? viewModel = _subscribedViewModel;
                if (viewModel is null)
                    return;

                // 采集页面的 ViewModel 已经保存了账户和 DeviceStore。
                // 弹窗对应的viewmdoel由采集点配置界面的viewmodel的方法创建
                PointViewModel pointViewModel =
                    viewModel.CreatePointViewModel(device);
                // 后台只负责创建和显示窗口；点位查询、保存由窗口的数据源负责。
                PointWindow dialog = new PointWindow(pointViewModel);
                if (owner is not null)
                    dialog.Owner = owner;

                // 这句才真正显示窗口；关闭弹窗后，本方法才返回。
                dialog.ShowDialog();
                // 弹窗只改配置。关掉后自动刷新主页面，新增、修改、删除就能在这里看到。
                if (ReferenceEquals(_subscribedViewModel, viewModel) &&
                    viewModel.RefreshPointsCommand.CanExecute(null))
                    viewModel.RefreshPointsCommand.Execute(null);
            }
            catch (Exception ex)
            {
                // 窗口没能打开时，只提示原因，不让程序直接退出。
                string message = $"打开采集点窗口失败：{ex.Message}";
                if (owner is not null)
                    MessageBox.Show(owner, message, "打开失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                else
                    MessageBox.Show(message, "打开失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 切换到其他页面、当前采集页面从显示区域移除时，由 XAML 的 Unloaded 事件调用。
        /// 调用 UnsubscribeViewModel 清理开窗订阅、取消单次测试；监控页启动的连续采集不受影响。
        /// </summary>
        private void AcquisitionView_Unloaded(object sender, RoutedEventArgs e)
        {
            // 切换走时，不再让这个页面响应 ViewModel 的开窗请求。
            UnsubscribeViewModel();
        }

        /// <summary>
        /// 重新订阅前，以及 AcquisitionView_Unloaded 离开页面时调用。
        /// 取消尚未结束的单次读取，移除开窗事件订阅并清掉后台引用；不会停止已启动的连续采集。
        /// </summary>
        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel is null)
                return;

            // 离开页面或换了数据源，就取消这次未完成的读取，释放临时连接。
            _subscribedViewModel.ReadOnceCommand.Cancel();
            _subscribedViewModel.PointManagementRequested -= OpenPointWindow;
            _subscribedViewModel = null;
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 采集点配置页面后台，负责加载设备列表和打开点位管理窗口。
    /// 离开只取消单次测试，不停止监控页的连续采集。
    /// </summary>
    public partial class AcquisitionView : UserControl
    {
        // 记住已订阅的数据源，离开页面时才能解除它的订阅；与 DataContext 是同一对象。
        private AcquisitionViewModel? _subscribedViewModel;

        /// <summary>
        /// 创建页面控件，数据源由主窗口的 DataTemplate 传入。
        /// </summary>
        public AcquisitionView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 页面显示时，先接上开窗事件，再执行刷新命令加载设备。
        /// </summary>
        private async void AcquisitionView_Loaded(object sender, RoutedEventArgs e)
        {
            // 先接好开窗事件，后面点击“管理采集点”才有人响应。
            SubscribeViewModel();

            // 命令允许执行时才自动刷新，避免与正在进行的刷新重复。
            if (DataContext is AcquisitionViewModel viewModel &&
                viewModel.RefreshDevicesCommand.CanExecute(null))
            {
                await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
            }
        }

        /// <summary>
        /// 已显示的页面换了数据源，就重新接上新对象的事件。
        /// </summary>
        private void AcquisitionView_DataContextChanged(
            object sender, DependencyPropertyChangedEventArgs e)
        {
            // 还没显示就等 Loaded 再处理。
            if (IsLoaded)
                SubscribeViewModel();
        }

        /// <summary>
        /// 先断开旧订阅，再把 OpenPointWindow 接到新数据源的开窗事件。
        /// </summary>
        private void SubscribeViewModel()
        {
            // 先解除旧订阅，避免一次点击开两个窗口。
            UnsubscribeViewModel();

            if (DataContext is AcquisitionViewModel viewModel)
            {
                _subscribedViewModel = viewModel;
                viewModel.PointManagementRequested += OpenPointWindow;
            }
        }

        /// <summary>
        /// 由 AcquisitionViewModel 的 PointManagementRequested 事件调用。
        /// 为指定设备准备 PointViewModel 并打开点位窗口，关闭后刷新页面点位表。
        /// </summary>
        private void OpenPointWindow(DeviceInfo device)
        {
            // 找到页面所属主窗口，作为弹窗的 Owner。
            Window? owner = Window.GetWindow(this);
            try
            {
                // 取出当前页面的数据源，它保存着共用的账户和 DeviceStore。
                AcquisitionViewModel? viewModel = _subscribedViewModel;
                if (viewModel is null)
                    return;

                // 调用页面数据源的方法，为这台设备创建弹窗自己的 PointViewModel。
                PointViewModel pointViewModel =
                    viewModel.CreatePointViewModel(device);

                // 把准备好的数据源交给弹窗，弹窗设置它为 DataContext。
                PointWindow dialog = new PointWindow(pointViewModel);
                if (owner is not null)
                    dialog.Owner = owner;

                // 显示弹窗，关掉后才继续下面的刷新。
                dialog.ShowDialog();
                // 仍是同一数据源且允许刷新时，加载刚修改的点位配置。
                if (ReferenceEquals(_subscribedViewModel, viewModel) &&
                    viewModel.RefreshPointsCommand.CanExecute(null))
                    viewModel.RefreshPointsCommand.Execute(null);
            }
            catch (Exception ex)
            {
                // 打开失败只提示原因，不让异常导致程序退出。
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
        /// 离开页面时断开开窗事件，并取消未完成的单次测试。
        /// </summary>
        private void AcquisitionView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeViewModel();
        }

        /// <summary>
        /// 取消单次测试并解除旧数据源的订阅，不影响连续采集。
        /// </summary>
        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel is null)
                return;

            // Cancel 发出取消信号，读取方法结束时会释放临时连接。
            _subscribedViewModel.ReadOnceCommand.Cancel();
            _subscribedViewModel.PointManagementRequested -= OpenPointWindow;
            _subscribedViewModel = null;
        }
    }
}

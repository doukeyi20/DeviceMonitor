// 初始化采集点页并打开点位管理窗口。
using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModels;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Views
{
    public partial class PointConfigurationView : UserControl
    {
        private PointConfigurationViewModel? _subscribedViewModel;

        public PointConfigurationView()
        {
            InitializeComponent();
        }

        /// <summary>页面显示时，先接上开窗事件，再执行刷新命令加载设备。</summary>
        private async void PointConfigurationView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeViewModel();

            if (DataContext is PointConfigurationViewModel viewModel &&
                viewModel.Devices.Count == 0 &&
                viewModel.RefreshDevicesCommand.CanExecute(null))
            {
                await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
            }
        }

        /// <summary>已显示的页面换了数据源，就重新接上新对象的事件。</summary>
        private void PointConfigurationView_DataContextChanged(
            object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsLoaded)
                SubscribeViewModel();
        }

        /// <summary>先断开旧订阅，再把 OpenPointManagementWindow 接到新数据源的开窗事件。</summary>
        private void SubscribeViewModel()
        {
            UnsubscribeViewModel();

            if (DataContext is PointConfigurationViewModel viewModel)
            {
                _subscribedViewModel = viewModel;
                viewModel.PointManagementRequested += OpenPointManagementWindow;
            }
        }

        /// <summary>为指定设备准备 PointManagementViewModel 并打开点位窗口，关闭后刷新页面点位表。</summary>
        private void OpenPointManagementWindow(DeviceInfo device)
        {
            Window? owner = Window.GetWindow(this);
            try
            {
                PointConfigurationViewModel? viewModel = _subscribedViewModel;
                if (viewModel is null)
                    return;

                PointManagementViewModel pointViewModel =
                    viewModel.CreatePointManagementViewModel(device);

                PointManagementWindow dialog = new PointManagementWindow(pointViewModel, viewModel.SelectedPoint?.Id);
                if (owner is not null)
                    dialog.Owner = owner;

                dialog.ShowDialog();

                if (ReferenceEquals(_subscribedViewModel, viewModel) &&
                    viewModel.RefreshPointsCommand.CanExecute(null))
                    viewModel.RefreshPointsCommand.Execute(null);
            }
            catch (Exception ex)
            {
                string message = $"打开采集点窗口失败：{ex.Message}";
                if (owner is not null)
                    MessageBox.Show(owner, message, "打开失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                else
                    MessageBox.Show(message, "打开失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>离开页面时断开开窗事件，并取消未完成的单次测试。</summary>
        private void PointConfigurationView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeViewModel();
        }

        /// <summary>取消单次测试并解除旧数据源的订阅，不影响连续采集。</summary>
        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel is null)
                return;

            _subscribedViewModel.ReadOnceCommand.Cancel();
            _subscribedViewModel.PointManagementRequested -= OpenPointManagementWindow;
            _subscribedViewModel = null;
        }
    }
}

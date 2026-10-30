// 处理设备编辑窗口和确认提示。
using DeviceMonitor.ViewModel;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.View
{
    public partial class DeviceManagementView : UserControl
    {
        private DeviceManagementViewModel? _subscribedViewModel;

        public DeviceManagementView()
        {
            InitializeComponent();
        }

        /// <summary>双击设备行执行原来的编辑命令；表头、滚动条和空白处不打开表单。</summary>
        private void DevicesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(DevicesGrid, source) is not DataGridRow
                || DataContext is not DeviceManagementViewModel viewModel
                || !viewModel.EditDeviceCommand.CanExecute(null))
                return;

            viewModel.EditDeviceCommand.Execute(null);
            e.Handled = true;
        }

        /// <summary>页面显示时，接上 ViewModel 的界面请求。</summary>
        private async void DeviceManagementView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeViewModel();
            if (DataContext is DeviceManagementViewModel vm && vm.RefreshDevicesCommand.CanExecute(null))
                await vm.RefreshDevicesCommand.ExecuteAsync(null);
        }

        /// <summary>先解除旧订阅，再接上新数据源的事件；订阅时不会立刻弹窗。</summary>
        private void SubscribeViewModel()
        {
            UnsubscribeViewModel();

            if (DataContext is DeviceManagementViewModel viewModel)
            {
                _subscribedViewModel = viewModel;
                viewModel.DeviceEditorRequested += OpenDeviceEditor;
                viewModel.MessageRequested += ShowMessage;
                viewModel.ConfirmationRequested += ConfirmAction;
            }
        }

        /// <summary>打开新增或编辑表单，device 为 null 表示新增，有设备对象表示编辑。</summary>
        private void OpenDeviceEditor(DeviceInfo? device)
        {
            if (_subscribedViewModel is null)
                return;

            try
            {
                DeviceEditorWindow dialog = new DeviceEditorWindow(_subscribedViewModel, device);

                Window? owner = Window.GetWindow(this);

                if (owner is not null)
                    dialog.Owner = owner;

                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowMessage($"打开设备表单失败：{ex.Message}", "打开失败");
            }
        }

        private void ConfirmAction(ConfirmationRequest request)
        {
            Window? owner = Window.GetWindow(this);
            MessageBoxResult result = owner is not null
                ? MessageBox.Show(owner, request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            request.Confirmed = result == MessageBoxResult.Yes;
        }

        /// <summary>找到页面所属窗口，弹出消息提示。</summary>
        private void ShowMessage(string message, string title)
        {
            Window? owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner, message, title);
            else
                MessageBox.Show(message, title);
        }

        /// <summary>离开页面时解除订阅，ViewModel 中的数据仍保留。</summary>
        private void DeviceManagementView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeViewModel();
        }

        /// <summary>解除旧数据源的事件订阅，不再响应它的弹窗请求。</summary>
        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel is not null)
            {
                _subscribedViewModel.DeviceEditorRequested -= OpenDeviceEditor;
                _subscribedViewModel.MessageRequested -= ShowMessage;
                _subscribedViewModel.ConfirmationRequested -= ConfirmAction;
                _subscribedViewModel = null;
            }
        }
    }
}

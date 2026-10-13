using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.Core.Models;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 设备管理页面后台，负责打开表单、确认弹窗和提示。
    /// DataContext 是主窗口传来的 DeviceManagementViewModel，这里不另建一份。
    /// </summary>
    public partial class DeviceManagementView : UserControl
    {
        private DeviceManagementViewModel? _subscribedViewModel; // 当前已订阅的对象，离开页面时用它解除订阅。

        /// <summary>
        /// 创建页面控件，数据源由主窗口的 DataTemplate 传入。
        /// </summary>
        public DeviceManagementView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 页面显示时，接上 ViewModel 的界面请求。
        /// </summary>
        private void DeviceManagementView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeViewModel();
        }

        /// <summary>
        /// 数据源换了，已显示的页面要改为订阅新对象。
        /// </summary>
        private void DeviceManagementView_DataContextChanged(
            object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsLoaded)
                SubscribeViewModel();
        }

        /// <summary>
        /// 先解除旧订阅，再接上新数据源的事件；订阅时不会立刻弹窗。
        /// </summary>
        private void SubscribeViewModel()
        {
            // 先解除旧的订阅，避免重复订阅。
            UnsubscribeViewModel();

            if (DataContext is DeviceManagementViewModel viewModel)
            {
                _subscribedViewModel = viewModel; // 记住这个对象，后面才能解除它的订阅。
                viewModel.DeviceEditorRequested += OpenDeviceEditor;
                viewModel.MessageRequested += ShowMessage;
                viewModel.ConfirmationRequested += ConfirmAction; // 停用、删除都先问一次。
            }
        }



        /// <summary>
        /// 由 DeviceManagementViewModel 的 DeviceEditorRequested 事件调用。
        /// 打开新增或编辑表单，device 为 null 表示新增，有设备对象表示编辑。
        /// </summary>
        private void OpenDeviceEditor(DeviceInfo? device)
        {
            if (_subscribedViewModel is null)
                return;

            try
            {
                // 传入管理页对象供保存后刷新列表；表单会创建自己的 DeviceEditorViewModel。
                AddDeviceWindow dialog = new AddDeviceWindow(_subscribedViewModel, device);

                // 指定弹窗属于当前主窗口。
                Window? owner = Window.GetWindow(this);

                if (owner is not null)
                    dialog.Owner = owner;

                // 显示表单，关闭后这句才返回；弹窗期间界面仍可响应。
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowMessage($"打开设备表单失败：{ex.Message}", "打开失败");
            }
        }

        // 确认弹窗：选“是”就把 request.Confirmed 写成 true，否则为 false。
        // 由 DeviceManagementViewModel 的 ConfirmationRequested 事件调用。
        private void ConfirmAction(DeviceConfirmationRequest request)
        {
            Window? owner = Window.GetWindow(this);
            MessageBoxResult result = owner is not null
                ? MessageBox.Show(owner, request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            // 写回同一个对象，命令返回后就能检查这个结果。
            request.Confirmed = result == MessageBoxResult.Yes;
        }


        /// <summary>
        /// 由 DeviceManagementViewModel 的 MessageRequested 事件调用。
        /// 找到页面所属窗口，弹出消息提示。
        /// </summary>
        private void ShowMessage(string message, string title)
        {
            Window? owner = Window.GetWindow(this);
            if (owner is not null)
                MessageBox.Show(owner, message, title);
            else
                MessageBox.Show(message, title);
        }

        /// <summary>
        /// 离开页面时解除订阅，ViewModel 中的数据仍保留。
        /// </summary>
        private void DeviceManagementView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeViewModel();
        }

        /// <summary>
        /// 解除旧数据源的事件订阅，不再响应它的弹窗请求。
        /// </summary>
        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel is not null)
            {
                // -= 断开旧页面的方法，最后清掉保存的引用。
                _subscribedViewModel.DeviceEditorRequested -= OpenDeviceEditor;
                _subscribedViewModel.MessageRequested -= ShowMessage;
                _subscribedViewModel.ConfirmationRequested -= ConfirmAction;
                _subscribedViewModel = null;
            }
        }
    }
}

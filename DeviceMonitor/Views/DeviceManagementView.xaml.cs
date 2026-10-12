using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.Core.Models;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 设备管理页的后台：加载时把开表单、确认和提示方法订阅到已有 ViewModel。
    /// DataContext 来自主窗口的 DataTemplate，不在本文件另建第二个设备管理 ViewModel。
    /// </summary>
    public partial class DeviceManagementView : UserControl
    {
        private DeviceManagementViewModel? _subscribedViewModel;

        /// <summary>
        /// WPF 根据 DataTemplate 创建设备管理页面时调用这里，只加载 XAML。
        /// DataContext 会指向 MainViewModel.Pages 中已有的 DeviceManagementViewModel，不在这里重复创建。
        /// </summary>
        public DeviceManagementView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 页面显示时 WPF 调用：从 DataContext 取已有对象，再登记开表单/提示/确认方法；这里不刷新数据库。
        /// </summary>
        private void DeviceManagementView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeViewModel();
        }

        /// <summary>
        /// 页面的数据源更换时，由 XAML 的 DataContextChanged 事件调用。
        /// 如果页面已经加载，就重新 SubscribeViewModel，改为响应新的 ViewModel。
        /// </summary>
        private void DeviceManagementView_DataContextChanged(
            object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsLoaded)
                SubscribeViewModel();
        }

        /// <summary>
        /// Loaded 或换数据源时调用。先解除旧订阅，再把本页面的方法登记到新的 ViewModel 上。
        /// 登记用 +=；并不是登记这一刻就弹窗口。
        /// </summary>
        private void SubscribeViewModel()
        {
            // 先解除旧的订阅，避免重复订阅。
            UnsubscribeViewModel();

            if (DataContext is DeviceManagementViewModel viewModel)
            {
                _subscribedViewModel = viewModel;  // 传对象
                viewModel.DeviceEditorRequested += OpenDeviceEditor;  // 把弹出窗口的方法订阅
                viewModel.MessageRequested += ShowMessage;  // 把显示消息的方法订阅
                viewModel.ConfirmationRequested += ConfirmAction; // 停用、删除都先问一次。
            }
        }



        /// <summary>
        /// 已由 SubscribeViewModel 订阅到 DeviceEditorRequested；ViewModel 的 AddDevice 新增设备命令或 EditDevice 编辑设备命令触发事件时调用这里。
        /// null 表示新增，具体 DeviceInfo 表示编辑；创建 AddDeviceWindow，传入已有设备管理 ViewModel，再 ShowDialog 显示表单。
        /// </summary>
        private void OpenDeviceEditor(DeviceInfo? device)
        {
            if (_subscribedViewModel is null)
                return;

            try
            {
                // 把同一份设备管理 ViewModel 交给表单，让它保存后能刷新管理列表。
                // 表单会另建 DeviceEditorViewModel 作为自己的数据源；null 是新增，有设备对象就是编辑。
                // 创建编辑窗口
                AddDeviceWindow dialog = new AddDeviceWindow(_subscribedViewModel, device);

                // 设置窗口的 Owner 为当前页面所在的 Window，这样 ShowDialog 才会阻塞当前窗口。
                Window? owner = Window.GetWindow(this);

                if (owner is not null)
                    dialog.Owner = owner;

                // 显示窗口，阻塞当前线程直到窗口关闭；
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowMessage($"打开设备表单失败：{ex.Message}", "打开失败");
            }
        }

        // 验证弹窗：由 ViewModel 的 ConfirmationRequested 事件触发时调用，显示 MessageBox，用户点击 Yes/No 后把结果写回 request.Confirmed。
        private void ConfirmAction(DeviceConfirmationRequest request)
        {
            Window? owner = Window.GetWindow(this);
            MessageBoxResult result = owner is not null
                ? MessageBox.Show(owner, request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(request.Message, request.Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            request.Confirmed = result == MessageBoxResult.Yes;
        }


        /// <summary>
        /// 平时由 ViewModel 的 MessageRequested 事件调用；OpenDeviceEditor 打开失败时也直接调用。
        /// 找到承载页面的窗口，再显示 MessageBox；不进行数据库操作。
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
        /// 切换走、页面从显示区域移除时，由 XAML 的 Unloaded 事件调用。
        /// 只调用 UnsubscribeViewModel 解除后台方法的登记，不销毁 MainViewModel.Pages 中的页面数据对象。
        /// </summary>
        private void DeviceManagementView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeViewModel();
        }

        /// <summary>
        /// 离开页面或准备换订阅对象时调用，用 -= 解除原来的登记；页面数据源仍留在 MainViewModel.Pages 中。
        /// </summary>
        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel is not null)
            {
                // 离开页面时解除引用，防止 ViewModel 一直持有旧页面。
                _subscribedViewModel.DeviceEditorRequested -= OpenDeviceEditor;
                _subscribedViewModel.MessageRequested -= ShowMessage;
                _subscribedViewModel.ConfirmationRequested -= ConfirmAction;
                _subscribedViewModel = null;
            }
        }
    }
}

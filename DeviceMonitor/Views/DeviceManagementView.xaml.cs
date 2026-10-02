using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using DeviceMonitor.Core.Models;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{
    /// <summary>
    /// DeviceManagementView.xaml 的交互逻辑
    /// </summary>
    public partial class DeviceManagementView : UserControl
    {
        public DeviceManagementView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 页面显示时，关注测试状态；切换页面再回来也能显示正确的按钮状态。
        /// </summary>
        private void DeviceManagementView_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is DeviceManagementViewModel viewModel)
            {
                // 先移除再添加，避免页面重复加载时重复订阅。
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                viewModel.PropertyChanged += ViewModel_PropertyChanged;
            }

            UpdateConnectionTestButtonState();
        }

        private void DeviceManagementView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is DeviceManagementViewModel viewModel)
            {
                // 离开页面时取消订阅，避免 ViewModel 一直持有旧界面。
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeviceManagementViewModel.IsTestingConnection))
            {
                UpdateConnectionTestButtonState();
            }
        }

        /// <summary>
        /// 没选设备或正在测试时禁用按钮；设备停用不影响手动测试连接。
        /// </summary>
        private void UpdateConnectionTestButtonState()
        {
            bool isTesting = DataContext is DeviceManagementViewModel viewModel &&
                viewModel.IsTestingConnection;

            TestConnectionButton.IsEnabled =
                DevicesGrid.SelectedItem is DeviceInfo && !isTesting;
            TestConnectionButton.Content = isTesting ? "测试中…" : "测试连接";
        }

        private void AddDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DeviceManagementViewModel viewModel)
            {
                MessageBox.Show("设备管理页面尚未准备好。");
                return;
            }

            // 弹窗是独立窗口，不会自动继承当前页面的 DataContext，
            // 因此明确把同一个 ViewModel 对象传进去。
            AddDeviceWindow dialog = new AddDeviceWindow(viewModel);

            Window? owner = Window.GetWindow(this);
            if (owner != null)
            {
                dialog.Owner = owner;
            }

            dialog.ShowDialog();
        }


        /// <summary>
        /// 设备的选择，选择后才能允许启用操作或编辑设备。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DevicesGrid_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            DeviceInfo? selectedDevice =
                DevicesGrid.SelectedItem as DeviceInfo;

            EditDeviceButton.IsEnabled = selectedDevice != null;

            // 选中了未启用的设备，才能点击启用。
            EnableDeviceButton.IsEnabled =
                selectedDevice != null && !selectedDevice.IsEnabled;

            // 选中了已启用的设备，才能点击停用。
            DisableDeviceButton.IsEnabled =
                selectedDevice != null && selectedDevice.IsEnabled;

            UpdateConnectionTestButtonState();
        }

        /// <summary>
        /// 点击测试：取得设备配置，等待 ViewModel 完成一次 TCP 连接测试。
        /// </summary>
        private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DeviceManagementViewModel viewModel ||
                DevicesGrid.SelectedItem is not DeviceInfo selectedDevice)
            {
                MessageBox.Show("请先选择一台设备。", "测试连接");
                return;
            }

            try
            {
                // async void 仅用于界面事件；被调用的方法返回 Task，才能等待完成。
                // 等待网络时不会阻塞界面，网络通信本身不写在 View 后台。
                await viewModel.TestConnectionAsync(selectedDevice);
            }
            catch (Exception ex)
            {
                // 正常的连接失败由 ViewModel 显示；这里防止意外异常直接关闭程序。
                MessageBox.Show($"测试连接时发生错误：{ex.Message}", "测试连接");
            }
            finally
            {
                UpdateConnectionTestButtonState();
            }
        }

        /// <summary>
        /// 点击启用：取得选中设备，交给 ViewModel 保存。
        /// </summary>
        private void EnableDeviceButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (DataContext is not DeviceManagementViewModel viewModel ||
                DevicesGrid.SelectedItem is not DeviceInfo selectedDevice)
            {
                MessageBox.Show("请先选择一台设备。", "启用设备");
                return;
            }

            try
            {
                // 只传设备编号和目标状态，不需要再传数据库路径。
                bool changed = viewModel.TrySetDeviceEnabled(
                    selectedDevice.Id, true);

                if (!changed)
                {
                    MessageBox.Show(
                        "设备不存在，或已经处于启用状态。列表已刷新。",
                        "启用设备");
                }
            }
            catch (Exception ex)
            {
                // 捕获异常并提示，避免按钮操作出错时程序直接退出。
                MessageBox.Show(
                    $"启用设备时发生错误：{ex.Message}",
                    "操作失败");
            }
        }

        /// <summary>
        /// 点击停用：把选中设备的启用状态保存为 false。
        /// </summary>
        private void DisableDeviceButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (DataContext is not DeviceManagementViewModel viewModel ||
                DevicesGrid.SelectedItem is not DeviceInfo selectedDevice)
            {
                MessageBox.Show("请先选择一台设备。", "停用设备");
                return;
            }

            try
            {
                // 与启用使用同一个方法，区别只是这里传 false。
                bool changed = viewModel.TrySetDeviceEnabled(
                    selectedDevice.Id, false);

                if (!changed)
                {
                    MessageBox.Show(
                        "设备不存在，或已经处于停用状态。列表已刷新。",
                        "停用设备");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"停用设备时发生错误：{ex.Message}",
                    "操作失败");
            }
        }


        /// <summary>
        /// 编辑选中设备。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>

        private void EditDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DeviceManagementViewModel viewModel ||
                DevicesGrid.SelectedItem is not DeviceInfo selectedDevice)
            {
                MessageBox.Show("请先选择一台设备。", "编辑设备");
                return;
            }

            // 把选中的旧配置交给同一个表单；表单保存时再调用 ViewModel。
            AddDeviceWindow dialog = new AddDeviceWindow(viewModel, selectedDevice);
            Window? owner = Window.GetWindow(this);
            if (owner is not null)
            {
                dialog.Owner = owner;
            }

            dialog.ShowDialog();
        }


    }
}

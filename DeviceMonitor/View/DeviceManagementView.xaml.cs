// 确认弹窗由界面执行；设备表单直接绑定页面内的编辑对象。
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.Model;
using DeviceMonitor.ViewModel;

namespace DeviceMonitor.View;

public partial class DeviceManagementView : UserControl
{
    private DeviceManagementViewModel? _subscribed;

    public DeviceManagementView() => InitializeComponent();

    private async void DeviceManagementView_Loaded(object sender, RoutedEventArgs e)
    {
        // 先取消订阅，避免重复订阅。
        Unsubscribe();

        // 得到viewmodel后订阅事件
        _subscribed = DataContext as DeviceManagementViewModel;
        if (_subscribed is null)
            return;

        // 订阅删除设备时的弹窗
        _subscribed.ConfirmationRequested += ConfirmAction;

        // 判断是否需要自动刷新
        if (_subscribed.Devices.Count == 0 && _subscribed.Editor is null
            && _subscribed.RefreshDevicesCommand.CanExecute(null))
        {
            await _subscribed.RefreshDevicesCommand.ExecuteAsync(null);

        }
    }

    // 删除设备时弹窗确认
    private bool ConfirmAction(string message, string title) =>
        MessageBox.Show(Window.GetWindow(this),
            message, 
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning, 
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private void DeviceManagementView_Unloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    // 取消订阅事件
    private void Unsubscribe()
    {
        if (_subscribed is not null)
            _subscribed.ConfirmationRequested -= ConfirmAction;
        _subscribed = null;
    }
}

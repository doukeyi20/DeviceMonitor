// 确认弹窗留在界面；页面切换时解除订阅，不停止仍在执行的任务。
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.Model;
using DeviceMonitor.ViewModel;

namespace DeviceMonitor.View;

public partial class PointEditorView : UserControl
{
    private PointEditorViewModel? _subscribed;

    public PointEditorView() => InitializeComponent();

    private void ViewLoaded(object sender, RoutedEventArgs e) => Subscribe();
    private void ViewUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();
    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
            Subscribe();
    }

    /// <summary>
    /// 拆旧订阅，给新的viewmodel订阅
    /// </summary>
    private void Subscribe()
    {
        Unsubscribe();
        _subscribed = DataContext as PointEditorViewModel;
        if (_subscribed is not null)
            _subscribed.DeleteConfirmationRequested += ConfirmDelete;
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null)
            _subscribed.DeleteConfirmationRequested -= ConfirmDelete;
        _subscribed = null;
    }

    // 请求删除的弹窗事件
    private bool ConfirmDelete(PointInfo point)
    {
        return MessageBox.Show(Window.GetWindow(this),
            $"删除“{point.Name}”的配置？历史读数会保留，不会修改设备寄存器。",
            "删除采集点", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DeviceMonitor.Core.Models;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 采集点管理弹窗：接住外面准备好的 PointViewModel 作为 DataContext。
    /// 输入和按钮已绑定；本后台只处理删除确认及关闭后的解除订阅。
    /// </summary>
    public partial class PointWindow : Window
    {
        // 与 DataContext 是同一个对象，才能收到按钮命令发出的确认请求。
        private readonly PointViewModel _viewModel;

        /// <summary>
        /// 由 AcquisitionView.OpenPointWindow 创建；传入的 PointViewModel 已由 CreatePointViewModel 准备好。
        /// 不再创建新 ViewModel；保存同一个对象、订阅删除确认方法，再设为窗口 DataContext。
        /// </summary>
        public PointWindow(PointViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            InitializeComponent();

            // 弹窗不会自动继承父页面的数据源，需要明确设置。
            // 控件绑定的属性和命令，都从传来的这一个 PointViewModel 中查找。
            _viewModel = viewModel;
            _viewModel.DeleteConfirmationRequested += ConfirmDeletePoint;
            DataContext = _viewModel;
        }

        /// <summary>
        /// 构造时已订阅到 DeleteConfirmationRequested；PointViewModel.DeletePoint 触发事件时，把删除目标传到这里。
        /// 显示确认框，把用户选择写回 request.Confirmed；方法返回后，DeletePoint 才决定是否调用数据库删除。
        /// </summary>
        private void ConfirmDeletePoint(PointDeleteRequest request)
        {
            // 窗口只负责显示和返回用户选择，不在这里执行 SQL。
            // 默认选“否”，用户必须明确点“是”才会删除。
            string message =
                $"确认删除设备“{request.Device.Name}”的采集点“{request.Point.Name}”吗？\n\n" +
                $"点位编号：{request.Point.Id}；协议地址：{request.Point.Address}。\n" +
                "仅删除本系统保存的点位配置，不能撤销；不会修改设备寄存器。";
            request.Confirmed = MessageBox.Show(
                this, message, "删除采集点",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                == MessageBoxResult.Yes;
        }

        /// <summary>
        /// 采集点窗口真正关闭后，由 WPF 自动调用。
        /// 解除删除确认事件的订阅；主页面点位刷新由外面的 AcquisitionView.OpenPointWindow 在 ShowDialog 返回后处理。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            // 窗口关掉后，解除订阅，不让旧窗口继续响应确认请求。
            _viewModel.DeleteConfirmationRequested -= ConfirmDeletePoint;
            base.OnClosed(e);
        }
    }
}

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
    /// 采集点管理窗口，沿用传来的 PointViewModel，后台只处理删除确认。
    /// </summary>
    public partial class PointWindow : Window
    {
        // 与 DataContext 是同一个对象，才能收到按钮命令发出的确认请求。
        private readonly PointViewModel _viewModel;

        /// <summary>
        /// 由 AcquisitionView.OpenPointWindow 创建。
        /// 把传来的 PointViewModel 设为数据源，并接上删除确认事件。
        /// </summary>
        public PointWindow(PointViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            InitializeComponent();

            // 弹窗要明确设置数据源，控件和按钮都从这个对象取属性、命令。
            _viewModel = viewModel;
            _viewModel.DeleteConfirmationRequested += ConfirmDeletePoint;
            DataContext = _viewModel;
        }

        /// <summary>
        /// 由 PointViewModel 的 DeleteConfirmationRequested 事件调用。
        /// 弹出删除确认框，把选择写回 request.Confirmed，命令再据此决定是否删除。
        /// </summary>
        private void ConfirmDeletePoint(PointDeleteRequest request)
        {
            // 默认选“否”，只有明确点“是”才把 Confirmed 写成 true。
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
        /// 关窗后解除订阅；外面的 OpenPointWindow 会继续刷新页面点位表。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _viewModel.DeleteConfirmationRequested -= ConfirmDeletePoint;
            _viewModel.Dispose();
            base.OnClosed(e);
        }
    }
}

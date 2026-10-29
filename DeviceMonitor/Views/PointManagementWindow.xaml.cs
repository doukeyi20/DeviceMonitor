// 处理点位表格选择、确认提示和关闭行为。
using DeviceMonitor.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Views
{
    public partial class PointManagementWindow : Window
    {
        private readonly PointManagementViewModel _viewModel;

        private bool _isRestoringPointSelection;

        public PointManagementWindow(PointManagementViewModel viewModel, long? selectedPointId = null)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            InitializeComponent();

            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;
            MinHeight = Math.Min(MinHeight, MaxHeight);
            MinWidth = Math.Min(MinWidth, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
            Width = Math.Min(Width, MaxWidth);

            _viewModel = viewModel;
            _viewModel.DeleteConfirmationRequested += ConfirmDeletePoint;
            _viewModel.ConfirmationRequested += ConfirmDiscardChanges;
            DataContext = _viewModel;

            Loaded += async (_, _) =>
            {
                if (_viewModel.RefreshPointsCommand.CanExecute(null))
                    await _viewModel.RefreshPointsCommand.ExecuteAsync(null);

                if (selectedPointId.HasValue)
                    _viewModel.SelectedPoint = _viewModel.Points.FirstOrDefault(point => point.Id == selectedPointId.Value);
            };
        }

        private void PointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRestoringPointSelection || _viewModel is null || sender is not DataGrid table)
                return;
            _isRestoringPointSelection = true;
            try
            {
                _viewModel.SelectedPoint = table.SelectedItem as PointInfo;
                table.SetCurrentValue(Selector.SelectedItemProperty, _viewModel.SelectedPoint);
            }
            finally
            {
                _isRestoringPointSelection = false;
            }
        }

        private void ConfirmDiscardChanges(ConfirmationRequest request)
        {
            request.Confirmed = MessageBox.Show(this, request.Message, request.Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                == MessageBoxResult.Yes;
        }

        /// <summary>弹出删除确认框，把选择写回 request.Confirmed，命令再据此决定是否删除。</summary>
        private void ConfirmDeletePoint(PointDeleteRequest request)
        {
            string message =
                $"确认删除设备“{request.Device.Name}”的采集点“{request.Point.Name}”吗？\n\n" +
                $"点位编号：{request.Point.Id}；协议地址：{request.Point.Address}。\n" +
                "仅删除本系统保存的点位配置，不能撤销；不会修改设备寄存器。" +
                (_viewModel.HasUnsavedChanges ? "\n当前点尚未保存的修改也会一并放弃。" : string.Empty);
            request.Confirmed = MessageBox.Show(
                this, message, "删除采集点",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                == MessageBoxResult.Yes;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_viewModel.ConfirmDiscardChanges())
                e.Cancel = true;
            base.OnClosing(e);
        }

        /// <summary>关窗后解除订阅；外面的 OpenPointManagementWindow 会继续刷新页面点位表。</summary>
        protected override void OnClosed(EventArgs e)
        {
            _viewModel.DeleteConfirmationRequested -= ConfirmDeletePoint;
            _viewModel.ConfirmationRequested -= ConfirmDiscardChanges;
            _viewModel.Dispose();
            base.OnClosed(e);
        }
    }
}

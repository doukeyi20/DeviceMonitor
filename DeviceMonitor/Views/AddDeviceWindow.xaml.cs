using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.Core.Models;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 新增和编辑设备共用的窗口，负责错误提示、定位输入框和保存后关窗。
    /// 表单数据与保存命令在 DeviceEditorViewModel 中。
    /// </summary>
    public partial class AddDeviceWindow : Window
    {
        private readonly DeviceEditorViewModel _viewModel; // 这个表单自己的数据源。

        /// <summary>
        /// 由 DeviceManagementView.OpenDeviceEditor 创建，新增和编辑都用这个窗口。
        /// 创建表单数据源并设置绑定；deviceToEdit 为 null 是新增，否则是编辑。
        /// </summary>
        public AddDeviceWindow(DeviceManagementViewModel viewModel, DeviceInfo? deviceToEdit = null)
        {
            InitializeComponent();

            _viewModel = new DeviceEditorViewModel(viewModel, deviceToEdit);
            _viewModel.InputErrorRequested += ShowInputError;
            _viewModel.MessageRequested += ShowMessage;
            _viewModel.SaveSucceeded += CompleteSave;
            DataContext = _viewModel;
        }

        /// <summary>
        /// 由 DeviceEditorViewModel 的 SaveSucceeded 事件调用。
        /// 保存成功后，把弹窗结果设为 true，WPF 会关闭这个表单。
        /// </summary>
        private void CompleteSave()
        {
            DialogResult = true;
        }

        /// <summary>
        /// 由 DeviceEditorViewModel 的 InputErrorRequested 事件调用。
        /// 提示输入错误，再找到对应输入框，移过去并选中内容。
        /// </summary>
        private void ShowInputError(string message, string propertyName)
        {
            MessageBox.Show(this, message, "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);

            // 按属性名找到前端用 x:Name 命名的 TextBox。
            TextBox? input = propertyName switch
            {
                nameof(DeviceEditorViewModel.DeviceName) => DeviceNameInput,
                nameof(DeviceEditorViewModel.Host) => HostInput,
                nameof(DeviceEditorViewModel.PortText) => PortInput,
                nameof(DeviceEditorViewModel.UnitIdText) => UnitIdInput,
                nameof(DeviceEditorViewModel.SamplingIntervalText) => SamplingIntervalInput,
                nameof(DeviceEditorViewModel.ConnectTimeoutText) => ConnectTimeoutInput,
                nameof(DeviceEditorViewModel.ReadTimeoutText) => ReadTimeoutInput,
                nameof(DeviceEditorViewModel.WriteTimeoutText) => WriteTimeoutInput,
                _ => null
            };
            input?.Focus();
            input?.SelectAll();
        }

        /// <summary>
        /// 由 DeviceEditorViewModel 的 MessageRequested 事件调用。
        /// 在表单上弹出保存失败等错误消息。
        /// </summary>
        private void ShowMessage(string message, string title)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// 表单关闭后，解除它对数据源的事件订阅。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _viewModel.InputErrorRequested -= ShowInputError;
            _viewModel.MessageRequested -= ShowMessage;
            _viewModel.SaveSucceeded -= CompleteSave;
            base.OnClosed(e);
        }
    }
}

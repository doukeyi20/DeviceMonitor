// 处理设备表单窗口的校验定位和关闭行为。
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.Core.Models;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{
    public partial class DeviceEditorWindow : Window
    {
        private readonly DeviceEditorViewModel _viewModel;

        public DeviceEditorWindow(DeviceManagementViewModel viewModel, DeviceInfo? deviceToEdit = null)
        {
            InitializeComponent();

            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;
            MinHeight = Math.Min(MinHeight, MaxHeight);
            MinWidth = Math.Min(MinWidth, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
            Width = Math.Min(Width, MaxWidth);

            _viewModel = new DeviceEditorViewModel(viewModel, deviceToEdit);
            _viewModel.InputErrorRequested += ShowInputError;
            _viewModel.MessageRequested += ShowMessage;
            _viewModel.SaveSucceeded += CompleteSave;
            DataContext = _viewModel;
        }

        /// <summary>保存成功后，把弹窗结果设为 true，WPF 会关闭这个表单。</summary>
        private void CompleteSave()
        {
            DialogResult = true;
        }

        /// <summary>提示输入错误，再找到对应输入框，移过去并选中内容。</summary>
        private void ShowInputError(string message, string propertyName)
        {
            MessageBox.Show(this, message, "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);

            Control? input = propertyName switch
            {
                nameof(DeviceEditorViewModel.DeviceName) => DeviceNameInput,
                nameof(DeviceEditorViewModel.Host) => HostInput,
                nameof(DeviceEditorViewModel.PortText) => PortInput,
                nameof(DeviceEditorViewModel.SerialPortName) => SerialPortNameInput,
                nameof(DeviceEditorViewModel.BaudRateText) => BaudRateInput,
                nameof(DeviceEditorViewModel.UnitIdText) => UnitIdInput,
                nameof(DeviceEditorViewModel.SamplingIntervalText) => SamplingIntervalInput,
                nameof(DeviceEditorViewModel.ConnectTimeoutText) => ConnectTimeoutInput,
                nameof(DeviceEditorViewModel.ReadTimeoutText) => ReadTimeoutInput,
                nameof(DeviceEditorViewModel.WriteTimeoutText) => WriteTimeoutInput,
                _ => null
            };
            input?.Focus();
            if (input is TextBox textBox)
                textBox.SelectAll();
        }

        /// <summary>在表单上弹出保存失败等错误消息。</summary>
        private void ShowMessage(string message, string title)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (_viewModel.IsSaving)
                e.Cancel = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.InputErrorRequested -= ShowInputError;
            _viewModel.MessageRequested -= ShowMessage;
            _viewModel.SaveSucceeded -= CompleteSave;
            base.OnClosed(e);
        }
    }
}

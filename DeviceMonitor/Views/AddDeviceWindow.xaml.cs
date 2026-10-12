using System;
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.Core.Models;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{

    /// <summary>
    /// 新增和编辑设备共用的表单窗口：自己创建 DeviceEditorViewModel 并设为 DataContext。
    /// 输入通过绑定交给它；这里响应报错定位、提示和保存后关闭，不直接保存数据库。
    /// </summary>
    public partial class AddDeviceWindow : Window
    {
        private readonly DeviceEditorViewModel _viewModel;

        /// <summary>
        /// DeviceManagementView.OpenDeviceEditor 创建这里。传 null 是新增，传设备是编辑。
        /// 为表单创建 DeviceEditorViewModel，订阅界面请求，再设置数据源。
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
        /// 窗口构造时已订阅到 SaveSucceeded；DeviceEditorViewModel.Save 保存成功后触发该事件。
        /// 设置 DialogResult = true，WPF 随后关闭通过 ShowDialog 打开的表单；这里只关闭窗口，不再保存一次。
        /// </summary>
        private void CompleteSave()
        {
            // 窗口由 ShowDialog 打开；设置结果后，WPF 会关闭该模态窗口。
            DialogResult = true;
        }

        /// <summary>
        /// 已订阅到 InputErrorRequested；DeviceEditorViewModel.Save 或它的输入检查方法发现错误时调用这里。
        /// 先显示错误提示，再按传来的属性名找到对应 TextBox，把焦点移过去并选中内容。
        /// </summary>
        private void ShowInputError(string message, string propertyName)
        {
            MessageBox.Show(this, message, "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);

            // ViewModel 只传属性名，不直接接触 TextBox 控件。
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
        /// 已订阅到 MessageRequested；DeviceEditorViewModel.Save 捕获保存异常后触发该事件。
        /// 在当前表单上显示错误提示，不操作设备或数据库。
        /// </summary>
        private void ShowMessage(string message, string title)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// 设备表单真正关闭后，由 WPF 自动调用。
        /// 从 DeviceEditorViewModel 上移除本窗口登记的提示和关闭方法，避免旧窗口继续响应请求。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            // 不让已关闭的窗口继续接收 ViewModel 的界面请求。
            _viewModel.InputErrorRequested -= ShowInputError;
            _viewModel.MessageRequested -= ShowMessage;
            _viewModel.SaveSucceeded -= CompleteSave;
            base.OnClosed(e);
        }
    }
}

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
using System;
using System.Windows;
using System.Windows.Controls;

namespace DeviceMonitor.Views
{
    /// <summary>
    /// AddDeviceWindow.xaml 的交互逻辑
    /// </summary>
    public partial class AddDeviceWindow : Window
    {
        private readonly DeviceManagementViewModel _viewModel;

        public AddDeviceWindow(DeviceManagementViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel
    ?? throw new ArgumentNullException(nameof(viewModel));


        }

        private void SaveDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            string name = DeviceNameInput.Text.Trim();
            string host = HostInput.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                ShowInputError("请输入设备名称。", DeviceNameInput);
                return;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                ShowInputError("请输入主机地址或 IP。", HostInput);
                return;
            }

            if (!int.TryParse(PortInput.Text, out int port) ||
                port < 1 || port > 65535)
            {
                ShowInputError("TCP 端口必须是 1～65535 的整数。", PortInput);
                return;
            }

            // byte 的取值范围是 0～255；0 是合法值，但必须由用户输入。
            if (!byte.TryParse(UnitIdInput.Text, out byte unitId))
            {
                ShowInputError("Unit ID 必须是 0～255 的整数。", UnitIdInput);
                return;
            }

            if (!TryReadPositiveMilliseconds(
                    SamplingIntervalInput, "采集间隔", out int samplingInterval))
                return;

            if (!TryReadPositiveMilliseconds(
                    ConnectTimeoutInput, "连接超时", out int connectTimeout))
                return;

            if (!TryReadPositiveMilliseconds(
                    ReadTimeoutInput, "读取超时", out int readTimeout))
                return;

            if (!TryReadPositiveMilliseconds(
                    WriteTimeoutInput, "写入超时", out int writeTimeout))
                return;

            // 前面都检查通过后，才组装准备保存的数据。
            DeviceInput input = new DeviceInput
            {
                Name = name,
                Host = host,
                Port = port,
                UnitId = unitId,
                SamplingIntervalMilliseconds = samplingInterval,
                ConnectTimeoutMilliseconds = connectTimeout,
                ReadTimeoutMilliseconds = readTimeout,
                WriteTimeoutMilliseconds = writeTimeout
            };

            try
            {
                bool added = _viewModel.TryAddDevice(input);

                if (!added)
                {
                    // 名称重复时不关闭窗口，用户可以直接修改名称。
                    ShowInputError("设备名称已存在，请换一个名称。", DeviceNameInput);
                    return;
                }

                // 保存和列表刷新均成功，关闭模态弹窗。
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"保存设备失败：{ex.Message}",
                    "保存失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private bool TryReadPositiveMilliseconds(
           TextBox input,
           string fieldName,
           out int value)
        {
            if (int.TryParse(input.Text, out value) && value > 0)
            {
                return true;
            }

            ShowInputError(
                $"{fieldName}必须是大于 0 的整数（毫秒）。",
                input);
            return false;
        }

        private void ShowInputError(string message, TextBox input)
        {
            MessageBox.Show(
                this,
                message,
                "输入有误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            input.Focus();
            input.SelectAll();
        }
    }
}

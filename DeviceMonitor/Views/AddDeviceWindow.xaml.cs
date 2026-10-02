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
    /// AddDeviceWindow.xaml 的交互逻辑
    /// </summary>
    public partial class AddDeviceWindow : Window
    {
        private readonly DeviceManagementViewModel _viewModel;
        private readonly long? _editingDeviceId;

        // deviceToEdit 为空表示新增；有值表示编辑，并把旧配置填进表单。
        public AddDeviceWindow(
            DeviceManagementViewModel viewModel,
            DeviceInfo? deviceToEdit = null)
        {
            InitializeComponent();

            _viewModel = viewModel
                ?? throw new ArgumentNullException(nameof(viewModel));

            if (deviceToEdit is not null)
            {
                _editingDeviceId = deviceToEdit.Id;
                Title = "编辑设备";
                FormTitle.Text = "编辑 Modbus TCP 设备";
                FormDescription.Text = "修改配置后保存；设备编号保持不变。";
                SaveDeviceButton.Content = "保存修改";

                DeviceNameInput.Text = deviceToEdit.Name;
                HostInput.Text = deviceToEdit.Host;
                PortInput.Text = deviceToEdit.Port.ToString();
                UnitIdInput.Text = deviceToEdit.UnitId.ToString();
                SamplingIntervalInput.Text =
                    deviceToEdit.SamplingIntervalMilliseconds.ToString();
                ConnectTimeoutInput.Text =
                    deviceToEdit.ConnectTimeoutMilliseconds.ToString();
                ReadTimeoutInput.Text =
                    deviceToEdit.ReadTimeoutMilliseconds.ToString();
                WriteTimeoutInput.Text =
                    deviceToEdit.WriteTimeoutMilliseconds.ToString();
            }
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
                // 同一份表单负责收集、校验配置；只有保存动作按模式分流。
                bool saved;
                if (_editingDeviceId is null)
                {
                    saved = _viewModel.TryAddDevice(input);
                }
                else
                {
                    saved = _viewModel.TryUpdateDevice(_editingDeviceId.Value, input);
                }

                if (!saved)
                {
                    // 编辑时还可能是设备被别处删除；保留窗口让用户查看输入。
                    string message = _editingDeviceId is null
                        ? "设备名称已存在，请换一个名称。"
                        : "修改未保存：名称可能已被占用，或该设备已不存在。";
                    ShowInputError(message, DeviceNameInput);
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

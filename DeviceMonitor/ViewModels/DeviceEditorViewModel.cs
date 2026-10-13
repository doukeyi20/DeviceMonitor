using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 新增和编辑设备共用的表单：接收输入、检查并保存，成功才关闭。
    /// </summary>
    public partial class DeviceEditorViewModel : ObservableObject
    {
        private readonly DeviceManagementViewModel _deviceManagement; // 保存时调用原管理页的方法，成功后能刷新设备表。
        private readonly long? _editingDeviceId; // 有编号就是编辑该设备，为 null 就是新增。

        // 下面这些属性对应表单输入框，保存命令会取出用户当前填写的值。
        [ObservableProperty]
        private string _deviceName = string.Empty;

        [ObservableProperty]
        private string _host = string.Empty;

        // 数字输入先保存为文本：允许空白、输入过程中的不完整内容，保存时再转换。
        [ObservableProperty]
        private string _portText = string.Empty;

        [ObservableProperty]
        private string _unitIdText = string.Empty;

        [ObservableProperty]
        private string _samplingIntervalText = string.Empty;

        [ObservableProperty]
        private string _connectTimeoutText = string.Empty;

        [ObservableProperty]
        private string _readTimeoutText = string.Empty;

        [ObservableProperty]
        private string _writeTimeoutText = string.Empty;

        // 编辑模式在构造时确定，不会中途改变，因此这些只读属性无需属性通知。
        public string WindowTitle => _editingDeviceId.HasValue ? "编辑设备" : "新增设备";
        public string FormTitle => _editingDeviceId.HasValue
            ? "编辑 Modbus TCP 设备" : "新增 Modbus TCP 设备";
        public string FormDescription => _editingDeviceId.HasValue
            ? "修改配置后保存；设备编号保持不变。"
            : "按设备实际参数填写；所有字段都需要输入。";
        public string SaveButtonText => _editingDeviceId.HasValue ? "保存修改" : "保存设备";

        public event Action<string, string>? InputErrorRequested;
        public event Action<string, string>? MessageRequested;
        public event Action? SaveSucceeded;

        /// <summary>
        /// 由 AddDeviceWindow 构造方法创建，新增和编辑共用这个数据源。
        /// deviceToEdit 为 null 表示新增，否则把原配置填进表单。
        /// 保存时要调用设备管理页的方法，所以接住它的 ViewModel。
        /// </summary>
        public DeviceEditorViewModel(
            DeviceManagementViewModel deviceManagement, DeviceInfo? deviceToEdit = null)
        {
            _deviceManagement = deviceManagement
                ?? throw new ArgumentNullException(nameof(deviceManagement));

            if (deviceToEdit is null)
                return; // 新增时全部留空，不替用户猜测设备参数。

            // 记住编辑编号，再把原配置填进各输入框。
            _editingDeviceId = deviceToEdit.Id;
            DeviceName = deviceToEdit.Name;
            Host = deviceToEdit.Host;
            PortText = deviceToEdit.Port.ToString();
            UnitIdText = deviceToEdit.UnitId.ToString();
            SamplingIntervalText = deviceToEdit.SamplingIntervalMilliseconds.ToString();
            ConnectTimeoutText = deviceToEdit.ConnectTimeoutMilliseconds.ToString();
            ReadTimeoutText = deviceToEdit.ReadTimeoutMilliseconds.ToString();
            WriteTimeoutText = deviceToEdit.WriteTimeoutMilliseconds.ToString();
        }

        /// <summary>
        /// 由 SaveCommand 调用。
        /// 检查输入并新增或修改设备，成功后请求关闭表单。
        /// </summary>
        [RelayCommand]
        private void Save()
        {
            // Trim 去掉前后空格；输入不对就提示，不继续保存。
            string name = DeviceName.Trim();
            string host = Host.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                // 调用 AddDeviceWindow.xaml.cs 的 ShowInputError：弹出提示，并选中设备名称输入框。
                InputErrorRequested?.Invoke("请输入设备名称。", nameof(DeviceName));
                return;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                // 调用 AddDeviceWindow.xaml.cs 的 ShowInputError：弹出提示，并选中主机地址输入框。
                InputErrorRequested?.Invoke("请输入主机地址或 IP。", nameof(Host));
                return;
            }

            if (!int.TryParse(PortText, out int port) || port < 1 || port > 65535)
            {
                // 调用 AddDeviceWindow.xaml.cs 的 ShowInputError：弹出提示，并选中端口输入框。
                InputErrorRequested?.Invoke("TCP 端口必须是 1～65535 的整数。", nameof(PortText));
                return;
            }

            if (!byte.TryParse(UnitIdText, out byte unitId))
            {
                // 调用 AddDeviceWindow.xaml.cs 的 ShowInputError：弹出提示，并选中 Unit ID 输入框。
                InputErrorRequested?.Invoke("Unit ID 必须是 0～255 的整数。", nameof(UnitIdText));
                return;
            }

            // 采集间隔和三个超时都要检查正整数毫秒，所以共用这个方法。
            if (!TryReadPositiveMilliseconds(SamplingIntervalText, "采集间隔",
                    nameof(SamplingIntervalText), out int samplingInterval) ||
                !TryReadPositiveMilliseconds(ConnectTimeoutText, "连接超时",
                    nameof(ConnectTimeoutText), out int connectTimeout) ||
                !TryReadPositiveMilliseconds(ReadTimeoutText, "读取超时",
                    nameof(ReadTimeoutText), out int readTimeout) ||
                !TryReadPositiveMilliseconds(WriteTimeoutText, "写入超时",
                    nameof(WriteTimeoutText), out int writeTimeout))
                return;

            // 组装 DeviceInput，交给设备管理 ViewModel 保存。
            var input = new DeviceInput
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
                // 有原设备编号就修改，没有编号就新增。
                bool saved = _editingDeviceId.HasValue
                    ? _deviceManagement.TryUpdateDevice(_editingDeviceId.Value, input)
                    : _deviceManagement.TryAddDevice(input);

                if (!saved)
                {
                    // 让 AddDeviceWindow.ShowInputError 提示未保存，表单保持打开。
                    InputErrorRequested?.Invoke(_editingDeviceId.HasValue
                        ? "修改未保存：名称可能已被占用，或该设备已不存在。"
                        : "设备名称已存在，请换一个名称。", nameof(DeviceName));
                    return;
                }

                // 保存成功，让 AddDeviceWindow.CompleteSave 设置成功结果并关闭表单。
                SaveSucceeded?.Invoke();
            }
            catch (Exception ex)
            {
                // 调用 AddDeviceWindow.xaml.cs 的 ShowMessage：弹出保存失败的错误信息。
                MessageRequested?.Invoke($"保存设备失败：{ex.Message}", "保存失败");
            }
        }

        /// <summary>
        /// 由 Save 检查采集间隔和超时时间时调用。
        /// 把文字转为大于 0 的整数毫秒，不合格就提示对应输入框。
        /// </summary>
        private bool TryReadPositiveMilliseconds(
            string text, string fieldName, string propertyName, out int value)
        {
            // 转成正整数就通过，数字通过 out value 带回去。
            if (int.TryParse(text, out value) && value > 0)
                return true;

            // 让 AddDeviceWindow.ShowInputError 提示，propertyName 指明哪个输入框有问题。
            InputErrorRequested?.Invoke(
                $"{fieldName}必须是大于 0 的整数（毫秒）。", propertyName);
            return false;
        }
    }
}

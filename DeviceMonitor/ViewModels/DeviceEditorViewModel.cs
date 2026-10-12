using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 设备表单的数据源：保存输入文字，点保存时检查并组装 DeviceInput。
    /// 把 DeviceInput 交回设备管理 ViewModel 保存；成功才请求 AddDeviceWindow 关闭。
    /// </summary>
    public partial class DeviceEditorViewModel : ObservableObject
    {
        private readonly DeviceManagementViewModel _deviceManagement;
        private readonly long? _editingDeviceId;

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
        /// 由 AddDeviceWindow 的构造函数创建，再设为这个编辑窗口的 DataContext。
        /// 接收设备管理界面的 ViewModel 和可选的编辑设备对象；如果是新增则 deviceToEdit 为 null。需要设备管理viewmodel是因为保存时要调用 TryAddDevice 或 TryUpdateDevice。
        /// </summary>
        public DeviceEditorViewModel(
            DeviceManagementViewModel deviceManagement, DeviceInfo? deviceToEdit = null)
        {
            _deviceManagement = deviceManagement
                ?? throw new ArgumentNullException(nameof(deviceManagement));

            if (deviceToEdit is null)
                return; // 新增时全部留空，不替用户猜测设备参数。

            // 把传入的设备对象的参数赋值给字段或属性
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
        /// 点击设备表单的保存按钮后，SaveCommand 调用这里。
        /// 先检查输入，再组装 DeviceInput，按新增或编辑调用 DeviceManagementViewModel.TryAddDevice / TryUpdateDevice。
        /// 成功后触发 SaveSucceeded，由 AddDeviceWindow.CompleteSave 关闭表单；输入错误则请求后台提示并定位输入框。
        /// </summary>
        [RelayCommand]
        private void Save()
        {
            // 验证输入，Trim 去掉前后空格。空白或不合法就请求后台提示并定位输入框；不抛异常。
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

            // 这里之所以用 TryReadPositiveMilliseconds 方法，是因为采集间隔和三个超时都必须是大于 0 的整数毫秒。反复验证所以单独编写了方法
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
                // 检查设备编号是否存在，如果存在说明是修改设备调用 TryUpdateDevice 或，如果不存在则是新增设备调用TryAddDevice 保存到数据库。
                bool saved = _editingDeviceId.HasValue
                    ? _deviceManagement.TryUpdateDevice(_editingDeviceId.Value, input)
                    : _deviceManagement.TryAddDevice(input);

                if (!saved)
                {
                    // 调用 AddDeviceWindow.xaml.cs 的 ShowInputError：
                    // 说明为什么没保存，并选中设备名称输入框；表单保持打开。
                    InputErrorRequested?.Invoke(_editingDeviceId.HasValue
                        ? "修改未保存：名称可能已被占用，或该设备已不存在。"
                        : "设备名称已存在，请换一个名称。", nameof(DeviceName));
                    return;
                }

                // 数据库已经保存成功。调用 AddDeviceWindow.xaml.cs 的 CompleteSave：
                // 把窗口的 DialogResult 设为 true，关闭这次新增/编辑表单；这里不再写数据库。
                SaveSucceeded?.Invoke();
            }
            catch (Exception ex)
            {
                // 调用 AddDeviceWindow.xaml.cs 的 ShowMessage：弹出保存失败的错误信息。
                MessageRequested?.Invoke($"保存设备失败：{ex.Message}", "保存失败");
            }
        }

        /// <summary>
        /// Save 对采集间隔和三个超时的验证。把输入文字转为正整数毫秒；不合格就请求窗口提示并定位输入框。
        /// </summary>
        private bool TryReadPositiveMilliseconds(
            string text, string fieldName, string propertyName, out int value)
        {
            // 转成整数成功，而且大于 0：检查通过。
            // 转换出来的数字通过 value 带回调用处。
            if (int.TryParse(text, out value) && value > 0)
                return true;

            // 调用 AddDeviceWindow.xaml.cs 的 ShowInputError：显示下面的提示。
            // propertyName 告诉后台该选中哪个输入框，例如采集间隔或读取超时。
            InputErrorRequested?.Invoke(
                $"{fieldName}必须是大于 0 的整数（毫秒）。", propertyName);
            return false;
        }
    }
}

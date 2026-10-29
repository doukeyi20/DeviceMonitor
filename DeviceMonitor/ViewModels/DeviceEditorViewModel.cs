// 校验设备表单并提交新增或修改。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Communication;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

public partial class DeviceEditorViewModel : ObservableObject
{
    private readonly DeviceManagementViewModel _deviceManagement;
    private readonly long? _editingDeviceId;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isSaving;
    public bool CanEdit => !IsSaving;

    public KeyValuePair<DeviceCommunicationMode, string>[] CommunicationOptions { get; } =
    [new(DeviceCommunicationMode.Tcp, "Modbus TCP（网口）"), new(DeviceCommunicationMode.Rtu, "Modbus RTU（串口 / RS485）")];
    public KeyValuePair<Parity, string>[] ParityOptions { get; } =
    [new(Parity.None, "无校验 N"), new(Parity.Even, "偶校验 E"), new(Parity.Odd, "奇校验 O")];
    public KeyValuePair<StopBits, string>[] StopBitOptions { get; } =
    [new(StopBits.One, "1 位"), new(StopBits.Two, "2 位")];

    public ObservableCollection<string> SerialPorts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTcp), nameof(IsRtu), nameof(StationLabel))]
    private DeviceCommunicationMode _selectedCommunicationMode = DeviceCommunicationMode.Tcp;
    [ObservableProperty]
    private string _deviceName = "";

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private string _portText = "502";

    [ObservableProperty]
    private string _serialPortName = "";

    [ObservableProperty]
    private string _baudRateText = "4800";

    [ObservableProperty]
    private Parity _selectedParity = Parity.None;

    [ObservableProperty]
    private StopBits _selectedStopBits = StopBits.One;

    [ObservableProperty]
    private string _unitIdText = "1";

    [ObservableProperty]
    private string _samplingIntervalText = "1000";

    [ObservableProperty]
    private string _connectTimeoutText = "1000";

    [ObservableProperty]
    private string _readTimeoutText = "1000";

    [ObservableProperty]
    private string _writeTimeoutText = "1000";

    [ObservableProperty]
    private string _portHint = "";

    public bool IsTcp => SelectedCommunicationMode == DeviceCommunicationMode.Tcp;
    public bool IsRtu => SelectedCommunicationMode == DeviceCommunicationMode.Rtu;
    public string StationLabel => IsRtu ? "站号（1～247）" : "Unit ID（0～255）";
    public string WindowTitle => _editingDeviceId.HasValue ? "编辑设备" : "新增设备";
    public string SaveButtonText => _editingDeviceId.HasValue ? "保存修改" : "保存设备";
    public event Action<string, string>? InputErrorRequested;
    public event Action<string, string>? MessageRequested;
    public event Action? SaveSucceeded;

    public DeviceEditorViewModel(DeviceManagementViewModel deviceManagement, DeviceInfo? deviceToEdit = null)
    {
        _deviceManagement = deviceManagement ?? throw new ArgumentNullException(nameof(deviceManagement));
        RefreshSerialPorts();
        if (deviceToEdit is null)
            return;

        _editingDeviceId = deviceToEdit.Id;
        DeviceName = deviceToEdit.Name;
        SelectedCommunicationMode = deviceToEdit.CommunicationMode;
        Host = deviceToEdit.Host;
        PortText = deviceToEdit.Port.ToString();
        SerialPortName = deviceToEdit.SerialPortName;
        BaudRateText = deviceToEdit.BaudRate.ToString();
        SelectedParity = deviceToEdit.Parity;
        SelectedStopBits = deviceToEdit.StopBits;
        UnitIdText = deviceToEdit.UnitId.ToString();
        SamplingIntervalText = deviceToEdit.SamplingIntervalMilliseconds.ToString();
        ConnectTimeoutText = deviceToEdit.ConnectTimeoutMilliseconds.ToString();
        ReadTimeoutText = deviceToEdit.ReadTimeoutMilliseconds.ToString();
        WriteTimeoutText = deviceToEdit.WriteTimeoutMilliseconds.ToString();
    }

    /// <summary>通信方式变化后更新表单可见状态。</summary>
    partial void OnSelectedCommunicationModeChanged(DeviceCommunicationMode value)
    {
        if (value != DeviceCommunicationMode.Rtu)
            return;
        if (string.IsNullOrWhiteSpace(UnitIdText))
            UnitIdText = "1";
        if (string.IsNullOrWhiteSpace(SamplingIntervalText))
            SamplingIntervalText = "1000";
        if (string.IsNullOrWhiteSpace(ReadTimeoutText))
            ReadTimeoutText = "1000";
        if (string.IsNullOrWhiteSpace(WriteTimeoutText))
            WriteTimeoutText = "1000";
    }

    /// <summary>刷新可用串口列表，保留手动输入能力。</summary>
    [RelayCommand]
    private void RefreshSerialPorts()
    {
        string portNameBeforeRefresh = SerialPortName;
        try
        {
            string[] names = SerialPort.GetPortNames();
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            SerialPorts.Clear();
            foreach (string name in names)
                SerialPorts.Add(name);
            PortHint = names.Length == 0 ? "未发现串口，可手动输入。" : string.Empty;
        }
        catch (Exception ex)
        {
            PortHint = $"暂时无法列出串口，可手动填写：{ex.Message}";
        }
        finally
        {
            SerialPortName = portNameBeforeRefresh;
        }
    }

    /// <summary>校验所选通信方式及通用参数，保存成功后请求关闭窗口。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            InputError("请输入设备名称。", nameof(DeviceName));
            return;
        }

        // 仅检查当前通信方式的输入，隐藏的另一组参数不阻止保存。
        int port = 502;
        int baudRate = 4800;
        int connectTimeout = 1000;
        if (IsTcp)
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                InputError("请输入主机地址或 IP。", nameof(Host));
                return;
            }
            if (!int.TryParse(PortText, out port) || port is < 1 or > 65535)
            {
                InputError("TCP 端口必须是 1～65535 的整数。", nameof(PortText));
                return;
            }
            if (!TryReadMilliseconds(ConnectTimeoutText, "连接超时", nameof(ConnectTimeoutText), out connectTimeout))
                return;
        }
        else
        {
            if (!SerialConnectionSettings.IsPortNameValid(SerialPortName))
            {
                InputError("请输入串口名称，例如 COM3。", nameof(SerialPortName));
                return;
            }
            if (!int.TryParse(BaudRateText, out baudRate) || baudRate is < 1200 or > 115200)
            {
                InputError("波特率请填写 1200～115200 之间的整数，并与设备一致。", nameof(BaudRateText));
                return;
            }
        }

        if (!byte.TryParse(UnitIdText, out byte unitId) || (IsRtu && unitId is < 1 or > 247))
        {
            InputError(IsRtu ? "RTU 站号必须是 1～247。" : "Unit ID 必须是 0～255。", nameof(UnitIdText));
            return;
        }
        if (!TryReadMilliseconds(SamplingIntervalText, "采集间隔", nameof(SamplingIntervalText), out int interval) ||
            !TryReadMilliseconds(ReadTimeoutText, "读取超时", nameof(ReadTimeoutText), out int readTimeout) ||
            !TryReadMilliseconds(WriteTimeoutText, "发送超时", nameof(WriteTimeoutText), out int writeTimeout))
            return;

        // 校验通过后固定表单输入，再开始异步保存。
        var input = new DeviceInput
        {
            Name = DeviceName.Trim(),
            CommunicationMode = SelectedCommunicationMode,
            Host = IsTcp ? Host.Trim() : "",
            Port = port,
            UnitId = unitId,
            SerialPortName = IsRtu ? SerialConnectionSettings.NormalizePortName(SerialPortName) : "",
            BaudRate = baudRate,
            Parity = SelectedParity,
            StopBits = SelectedStopBits,
            SamplingIntervalMilliseconds = interval,
            ConnectTimeoutMilliseconds = connectTimeout,
            ReadTimeoutMilliseconds = readTimeout,
            WriteTimeoutMilliseconds = writeTimeout
        };
        try
        {
            IsSaving = true;
            // 编辑沿用原编号，新增由存储层分配编号。
            bool saved = _editingDeviceId.HasValue
                ? await _deviceManagement.TryUpdateDeviceAsync(_editingDeviceId.Value, input)
                : await _deviceManagement.TryAddDeviceAsync(input);
            if (!saved)
            {
                InputError("未保存：设备名称已存在，或原设备已不存在。", nameof(DeviceName));
                return;
            }
            // 保存成功后先解除关闭限制，再通知窗口关闭。

            IsSaving = false;
            SaveSucceeded?.Invoke();
        }
        catch (Exception ex)
        {
            MessageRequested?.Invoke($"保存设备失败：{ex.Message}", "保存失败");
        }

        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving;

    /// <summary>把采集间隔、超时文字转成正整数毫秒。</summary>
    private bool TryReadMilliseconds(string text, string label, string inputPropertyName, out int value)
    {
        if (int.TryParse(text, out value) && value > 0)
            return true;
        InputError($"{label}必须是大于 0 的整数（毫秒）。", inputPropertyName);
        return false;
    }

    private void InputError(string message, string inputPropertyName) =>
        InputErrorRequested?.Invoke(message, inputPropertyName);
}

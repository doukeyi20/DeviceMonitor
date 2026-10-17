using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Communication;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>新增、编辑设备窗口的数据源；检查表单后，交给设备管理页保存。</summary>
public partial class DeviceEditorViewModel : ObservableObject
{
    private readonly DeviceManagementViewModel _deviceManagement;
    private readonly long? _editingDeviceId; // 有编号是编辑；没有编号是新增。

    // 左边是保存的值，右边是下拉框显示的文字。
    public KeyValuePair<DeviceCommunicationMode, string>[] CommunicationOptions { get; } =
    [new(DeviceCommunicationMode.Tcp, "Modbus TCP（网口）"), new(DeviceCommunicationMode.Rtu, "Modbus RTU（串口 / RS485）")];
    public KeyValuePair<Parity, string>[] ParityOptions { get; } =
    [new(Parity.None, "无校验 N"), new(Parity.Even, "偶校验 E"), new(Parity.Odd, "奇校验 O")];
    public KeyValuePair<StopBits, string>[] StopBitOptions { get; } =
    [new(StopBits.One, "1 位"), new(StopBits.Two, "2 位")];
    public ObservableCollection<string> SerialPorts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTcp), nameof(IsRtu), nameof(StationLabel))]
    private DeviceCommunicationMode _selectedCommunicationMode = DeviceCommunicationMode.Tcp; // 控制显示、检查和保存哪套参数。
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _serialPortName = "";
    [ObservableProperty] private string _baudRateText = "4800";
    [ObservableProperty] private Parity _selectedParity = Parity.None; // 校验位，必须与传感器设置一致。
    [ObservableProperty] private StopBits _selectedStopBits = StopBits.One; // 每个字符结束时用 1 位或 2 位停止位。
    // 数字先存文字，保存时再检查，允许输入一半或暂时留空。
    [ObservableProperty] private string _unitIdText = "";
    [ObservableProperty] private string _samplingIntervalText = "";
    [ObservableProperty] private string _connectTimeoutText = "";
    [ObservableProperty] private string _readTimeoutText = "";
    [ObservableProperty] private string _writeTimeoutText = "";
    [ObservableProperty] private string _portHint = "";

    public bool IsTcp => SelectedCommunicationMode == DeviceCommunicationMode.Tcp;
    public bool IsRtu => SelectedCommunicationMode == DeviceCommunicationMode.Rtu;
    public string StationLabel => IsRtu ? "站号（1～247）" : "Unit ID（0～255）";
    public string WindowTitle => _editingDeviceId.HasValue ? "编辑设备" : "新增设备";
    public string FormTitle => WindowTitle;
    public string FormDescription => "一条设备配置使用一种连接方式。保存配置不会连接设备，也不会开始采集。";
    public string SaveButtonText => _editingDeviceId.HasValue ? "保存修改" : "保存设备";
    public event Action<string, string>? InputErrorRequested;
    public event Action<string, string>? MessageRequested;
    public event Action? SaveSucceeded;

    /// <summary>AddDeviceWindow 创建时调用；编辑时把原配置填回来，包括串口参数。</summary>
    public DeviceEditorViewModel(DeviceManagementViewModel deviceManagement, DeviceInfo? deviceToEdit = null)
    {
        _deviceManagement = deviceManagement ?? throw new ArgumentNullException(nameof(deviceManagement));
        RefreshSerialPorts();
        if (deviceToEdit is null) return;

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

    /// <summary>切换方式时由属性调用；只给空输入补建议值，不覆盖已经填写的内容。</summary>
    partial void OnSelectedCommunicationModeChanged(DeviceCommunicationMode value)
    {
        if (value != DeviceCommunicationMode.Rtu) return;
        if (string.IsNullOrWhiteSpace(UnitIdText)) UnitIdText = "1";
        if (string.IsNullOrWhiteSpace(SamplingIntervalText)) SamplingIntervalText = "1000";
        if (string.IsNullOrWhiteSpace(ReadTimeoutText)) ReadTimeoutText = "1000";
        if (string.IsNullOrWhiteSpace(WriteTimeoutText)) WriteTimeoutText = "1000";
    }

    /// <summary>刷新串口按钮调用；列出电脑发现的 COM 口，也允许手动填写。</summary>
    [RelayCommand]
    private void RefreshSerialPorts()
    {
        string selectedPort = SerialPortName;
        try
        {
            string[] names = SerialPort.GetPortNames();
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            SerialPorts.Clear();
            foreach (string name in names) SerialPorts.Add(name);
            PortHint = names.Length == 0 ? "未发现串口，可手动输入。" : string.Empty;
        }
        catch (Exception ex) { PortHint = $"暂时无法列出串口，可手动填写：{ex.Message}"; }
        finally
        {
            // 下拉列表重建时可能取消选择，恢复刚才填写的 COM，不替用户换端口。
            SerialPortName = selectedPort;
        }
    }

    /// <summary>保存按钮调用；只检查所选方式的字段，成功后通知窗口关闭。</summary>
    [RelayCommand]
    private void Save()
    {
        // 先检查名称，再检查所选连接方式用到的字段。
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            InputError("请输入设备名称。", nameof(DeviceName));
            return;
        }
        int port = 502, baudRate = 4800, connectTimeout = 1000;
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
            if (!TryReadMilliseconds(ConnectTimeoutText, "连接超时", nameof(ConnectTimeoutText), out connectTimeout)) return;
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
        // 站号、采集间隔、收发超时是两种连接都要用的设置。
        if (!byte.TryParse(UnitIdText, out byte unitId) || (IsRtu && unitId is < 1 or > 247))
        {
            InputError(IsRtu ? "RTU 站号必须是 1～247。" : "Unit ID 必须是 0～255。", nameof(UnitIdText));
            return;
        }
        if (!TryReadMilliseconds(SamplingIntervalText, "采集间隔", nameof(SamplingIntervalText), out int interval) ||
            !TryReadMilliseconds(ReadTimeoutText, "读取超时", nameof(ReadTimeoutText), out int readTimeout) ||
            !TryReadMilliseconds(WriteTimeoutText, "发送超时", nameof(WriteTimeoutText), out int writeTimeout)) return;

        // 两种连接最后都装成 DeviceInput，后面的新增、编辑流程不用分成两套。
        var input = new DeviceInput
        {
            Name = DeviceName.Trim(), CommunicationMode = SelectedCommunicationMode,
            Host = IsTcp ? Host.Trim() : "", Port = port, UnitId = unitId,
            SerialPortName = IsRtu ? SerialConnectionSettings.NormalizePortName(SerialPortName) : "",
            BaudRate = baudRate, Parity = SelectedParity, StopBits = SelectedStopBits,
            SamplingIntervalMilliseconds = interval, ConnectTimeoutMilliseconds = connectTimeout,
            ReadTimeoutMilliseconds = readTimeout, WriteTimeoutMilliseconds = writeTimeout
        };
        try
        {
            bool saved = _editingDeviceId.HasValue
                ? _deviceManagement.TryUpdateDevice(_editingDeviceId.Value, input)
                : _deviceManagement.TryAddDevice(input);
            if (!saved)
            {
                InputError("未保存：设备名称已存在，或原设备已不存在。", nameof(DeviceName));
                return;
            }
            SaveSucceeded?.Invoke();
        }
        catch (Exception ex) { MessageRequested?.Invoke($"保存设备失败：{ex.Message}", "保存失败"); }
    }

    /// <summary>Save 调用，把采集间隔、超时文字转成正整数毫秒。</summary>
    private bool TryReadMilliseconds(string text, string label, string property, out int value)
    {
        if (int.TryParse(text, out value) && value > 0) return true;
        InputError($"{label}必须是大于 0 的整数（毫秒）。", property);
        return false;
    }

    // 交给 AddDeviceWindow.ShowInputError 提示，并移到对应输入框。
    private void InputError(string message, string property) => InputErrorRequested?.Invoke(message, property);
}

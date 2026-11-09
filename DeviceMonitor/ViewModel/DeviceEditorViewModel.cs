// 编辑 TCP 或 RTU 连接参数；检查通过后交给设备配置页保存,展示
// 提供连接参数表单的数据
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

/// <summary>只处理连接表单；DeviceManagementViewModel 协调保存刷新，DeviceStore 真正写数据库。</summary>
public partial class DeviceEditorViewModel : ObservableObject
{
    private readonly DeviceManagementViewModel _deviceManagement;

    //当前编辑设备的编号 判断本次是新增还是修改
    private readonly long? _editingDeviceId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]  // 通知CanEdit属性判断
    // 连接参数正在保存的标记：提交前打上，保存和刷新结束或失败后清掉；期间禁用保存和表单编辑。
    private bool _isSaving;

    public bool CanEdit => !IsSaving && !_deviceManagement.IsBusy && _deviceManagement.CanEditSelectedDevice;

    // 下拉选项：显示中文，实际保存枚举
    public KeyValuePair<DeviceCommunicationMode, string>[] CommunicationOptions { get; } =
    [new(DeviceCommunicationMode.Tcp, "Modbus TCP（网口）"), new(DeviceCommunicationMode.Rtu, "Modbus RTU（串口 / RS485）")];
    public KeyValuePair<Parity, string>[] ParityOptions { get; } =
    [new(Parity.None, "无校验 N"), new(Parity.Even, "偶校验 E"), new(Parity.Odd, "奇校验 O")];
    public KeyValuePair<StopBits, string>[] StopBitOptions { get; } =
    [new(StopBits.One, "1 位"), new(StopBits.Two, "2 位")];

    // 存放可用串口列表，供下拉选择
    public ObservableCollection<string> SerialPorts { get; } = new();

    // 存放可选波特率列表，供下拉选择
    public int[] BaudRates { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    // 通信方式的选择，会随变化通知。
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
    private string _baudRateText = "9600";

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

    // 根据枚举值判断当前表单是 TCP 还是 RTU，方便绑定界面显示。
    public bool IsTcp => SelectedCommunicationMode == DeviceCommunicationMode.Tcp;
    public bool IsRtu => SelectedCommunicationMode == DeviceCommunicationMode.Rtu;
    public string StationLabel => IsRtu ? "站号（1～247）" : "Unit ID（0～255）";
    public string SaveButtonText => _editingDeviceId.HasValue ? "保存修改" : "保存设备";
    [ObservableProperty] private string _pageMessage = "";

    public DeviceEditorViewModel(DeviceManagementViewModel deviceManagement, DeviceInfo? deviceToEdit = null)
    {
        _deviceManagement = deviceManagement ?? throw new ArgumentNullException(nameof(deviceManagement));

        RefreshSerialPorts();
        // 如果传入了设备对象，则初始化表单为该设备的参数；否则表单为空，则没初始化任何参数，是空的表单
        // 这里 return 不会取消对象创建，只是不继续填写旧设备数据。
        if (deviceToEdit is null)
            return;

        // WPF 根据绑定让连接参数表单显示这些值
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

    /// <summary>刷新可用串口列表，保留手动输入能力。</summary>
    [RelayCommand]
    private void RefreshSerialPorts()
    {
        // 记住当前的串口输入，可以为空
        string portNameBeforeRefresh = SerialPortName;
        try
        {
            string[] names = SerialPort.GetPortNames();
            // 排序
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
            // 还原
            SerialPortName = portNameBeforeRefresh;
        }
    }

    /// <summary>保存编辑，添加设备</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            PageMessage = "请输入设备名称。";
            return;
        }

        // 仅检查当前通信方式的输入，隐藏的另一组参数不阻止保存。
        int port = 502;
        int baudRate = 9600;
        int connectTimeout = 1000;
        if (IsTcp)
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                PageMessage = "请输入主机地址或 IP。";
                return;
            }
            if (!int.TryParse(PortText, out port) || port is < 1 or > 65535)
            {
                PageMessage = "TCP 端口必须是 1～65535 的整数。";
                return;
            }
            if (!TryReadMilliseconds(ConnectTimeoutText, "连接超时", out connectTimeout))
                return;
        }
        else
        {
            if (!SerialConnectionSettings.IsPortNameValid(SerialPortName))
            {
                PageMessage = "请输入串口名称，例如 COM3。";
                return;
            }
            if (!int.TryParse(BaudRateText, out baudRate) || baudRate is < 1200 or > 115200)
            {
                PageMessage = "波特率请填写 1200～115200 之间的整数，并与设备一致。";
                return;
            }
        }

        if (!byte.TryParse(UnitIdText, out byte unitId) || (IsRtu && unitId is < 1 or > 247))
        {
            PageMessage = IsRtu ? "RTU 站号必须是 1～247。" : "Unit ID 必须是 0～255。";
            return;
        }
        if (!TryReadMilliseconds(SamplingIntervalText, "采集间隔", out int interval) ||
            !TryReadMilliseconds(ReadTimeoutText, "读取超时", out int readTimeout) ||
            !TryReadMilliseconds(WriteTimeoutText, "发送超时", out int writeTimeout))
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
            // 调用父页面的保存方法，传入编辑好的设备编辑对象和id。
            bool saved = await _deviceManagement.SaveDeviceAsync(_editingDeviceId, input);
            if (!saved)
            {
                PageMessage = "未保存：设备名称已存在，或原设备已不存在。";
                return;
            }
            // 父页面已刷新保存结果，再解除表单忙状态。

            IsSaving = false;
            PageMessage = "设备已保存。";
        }
        catch (Exception ex)
        {
            PageMessage = $"保存设备失败：{ex.Message}";
        }

        finally
        {
            IsSaving = false;
        }
    }

    // 父页面的采集或保存状态变化后，只刷新表单可操作状态。
    public void RefreshPermission()
    {
        // 通知WPF：重新读取CanEdit，更新输入区域是否可用
        OnPropertyChanged(nameof(CanEdit));

        // 通知保存按钮：重新检查命令能不能执行
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 多个时间输入共用的检查方法
    /// </summary>
    /// <param name="text"></param>
    /// <param name="label"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    private bool TryReadMilliseconds(string text, string label, out int value)
    {
        if (int.TryParse(text, out value) && value > 0)
            return true;
        PageMessage = $"{label}必须是大于 0 的整数（毫秒）。";
        return false;
    }

}

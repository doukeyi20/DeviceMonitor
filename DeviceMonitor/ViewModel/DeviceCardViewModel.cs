// 显示一台设备的连接信息与采集状态；选择状态由列表负责。
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public sealed class DeviceCardViewModel : ObservableObject
{
    public DeviceInfo Device { get; }
    public string ConnectionDetails => $"{Device.CommunicationText} · {Device.Endpoint} · " +
        $"{(Device.CommunicationMode == DeviceCommunicationMode.Rtu ? "站号" : "Unit ID")} {Device.UnitId}";

    // 采集对象
    private DeviceSamplingViewModel? _sampling;
    public DeviceSamplingViewModel? Sampling => _sampling;
    public bool HasReadFailure => Sampling?.Readings.Any(row => row.ReadState == SingleReadState.Failed) == true;
    public string ConnectionType => Device.CommunicationMode == DeviceCommunicationMode.Rtu ? "RTU" : "TCP";
    public string DeviceAddress => Device.CommunicationMode == DeviceCommunicationMode.Rtu
        ? $"{Device.SerialPortName} · 站号 {Device.UnitId}" : $"{Device.Host}:{Device.Port}";
    public string SamplingStateText => Sampling?.IsStopping == true ? "正在停止"
        : Sampling?.IsRunning == true
        ? HasReadFailure ? "通信异常 · 重试中"
        : Sampling.LastSuccessfulReadAt is null ? "等待数据" : "采集中"
        : Sampling is null ? "尚未开始" : "已停止";

    public string DataAgeText => Sampling?.LastSuccessfulReadAt is DateTimeOffset time
        ? $"最后成功 {time.ToLocalTime():HH:mm:ss}" : "尚无有效读数";

    public DeviceCardViewModel(DeviceInfo device, DeviceSamplingViewModel? sampling)
    {
        Device = device;
        AttachSampling(sampling);
    }

    /// <summary>设备任务只创建一次，页面刷新后仍关联原来的任务。</summary>
    internal void AttachSampling(DeviceSamplingViewModel? sampling)
    {
        if (ReferenceEquals(_sampling, sampling))
            return;
        DetachSampling();
        _sampling = sampling;
        if (_sampling is not null)
            _sampling.PropertyChanged += SamplingChanged;
        OnPropertyChanged(nameof(Sampling));
        OnPropertyChanged(nameof(SamplingStateText));
        OnPropertyChanged(nameof(DataAgeText));
    }

    private void SamplingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceSamplingViewModel.IsRunning)
            or nameof(DeviceSamplingViewModel.IsStopping) or nameof(DeviceSamplingViewModel.LastSuccessfulReadAt)
            or nameof(DeviceSamplingViewModel.StatusText))
        {
            OnPropertyChanged(nameof(SamplingStateText));
            OnPropertyChanged(nameof(HasReadFailure));
            OnPropertyChanged(nameof(DataAgeText));
        }
    }

    internal void DetachSampling()
    {
        if (_sampling is not null)
            _sampling.PropertyChanged -= SamplingChanged;
    }
}

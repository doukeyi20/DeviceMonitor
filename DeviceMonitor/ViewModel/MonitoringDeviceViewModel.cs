// 提供监控设备行的勾选状态和采集状态。
using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public sealed partial class MonitoringDeviceViewModel : ObservableObject
{
    public DeviceInfo Device { get; }
    public string EnabledText => Device.IsEnabled ? "已启用" : "已停用";

    public string ConnectionSummary => Device.CommunicationMode == DeviceCommunicationMode.Rtu
        ? $"RTU · {Device.SerialPortName} · 站号 {Device.UnitId}"
        : $"TCP · {Device.Host}:{Device.Port}";
    public string ConnectionDetails => $"{Device.CommunicationText} · {Device.Endpoint} · " +
        $"{(Device.CommunicationMode == DeviceCommunicationMode.Rtu ? "站号" : "Unit ID")} {Device.UnitId}";

    [ObservableProperty]
    private bool _isChecked;

    private DeviceSamplingViewModel? _sampling;
    public DeviceSamplingViewModel? Sampling
    {
        get => _sampling;
        private set => SetProperty(ref _sampling, value);
    }

    public string SamplingStateText => Sampling is null
        ? "尚未开始"
        : Sampling.IsRunning ? "采集中" : "未运行";

    private string _dataAgeText = "尚未开始采集";
    public string DataAgeText
    {
        get => _dataAgeText;
        private set => SetProperty(ref _dataAgeText, value);
    }

    public MonitoringDeviceViewModel(DeviceInfo device, DeviceSamplingViewModel? sampling)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        AttachSampling(sampling);
    }

    /// <summary>把设备行和采集任务关联起来，任务状态变化时更新显示。</summary>
    internal void AttachSampling(DeviceSamplingViewModel? sampling)
    {
        if (ReferenceEquals(Sampling, sampling))
            return;

        if (Sampling is not null)
            Sampling.PropertyChanged -= Sampling_PropertyChanged;

        Sampling = sampling;
        if (Sampling is not null)
            Sampling.PropertyChanged += Sampling_PropertyChanged;

        OnPropertyChanged(nameof(SamplingStateText));
        UpdateDataAge(DateTimeOffset.UtcNow);
    }

    private void Sampling_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceSamplingViewModel.IsRunning))
            OnPropertyChanged(nameof(SamplingStateText));
        if (e.PropertyName is nameof(DeviceSamplingViewModel.IsRunning)
            or nameof(DeviceSamplingViewModel.LastSuccessfulReadAt))
            UpdateDataAge(DateTimeOffset.UtcNow);
    }

    /// <summary>根据最后成功读数的时间，更新“多久没有新数据”的提示。</summary>
    public void UpdateDataAge(DateTimeOffset now)
    {
        if (Sampling is null)
        {
            DataAgeText = "尚未开始采集";
            return;
        }
        if (Sampling.LastSuccessfulReadAt is not DateTimeOffset readAt)
        {
            DataAgeText = Sampling.IsRunning ? "本次尚无成功读数，正在尝试读取" : "本次尚无成功读数";
            return;
        }
        if (!Sampling.IsRunning)
        {
            DataAgeText = $"任务已停止；最后成功：{readAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            return;
        }

        double secondsSinceLastRead = Math.Max(0, (now - readAt).TotalSeconds);

        double reminderAfterSeconds = Math.Max(5, Device.SamplingIntervalMilliseconds / 1000d * 3);
        bool isDataStale = secondsSinceLastRead > reminderAfterSeconds;
        DataAgeText = isDataStale
            ? $"已 {secondsSinceLastRead:0} 秒没有点位读取成功，可能仍在读取或读取失败"
            : $"最近一个成功读数：{secondsSinceLastRead:0} 秒前";
    }

    internal void DetachSampling()
    {
        if (Sampling is not null)
            Sampling.PropertyChanged -= Sampling_PropertyChanged;
    }
}

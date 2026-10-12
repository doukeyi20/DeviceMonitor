using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 监控左侧设备表的一行：Device 保存配置，Sampling 指向该设备已有的采集任务。
/// IsChecked 才控制批量操作和汇总；没有任务时 Sampling 可以是 null，不因此创建任务。
/// </summary>
public sealed partial class MonitoringDeviceViewModel : ObservableObject
{
    public DeviceInfo Device { get; }
    public string EnabledText => Device.IsEnabled ? "已启用" : "已停用";

    // 表格中的勾选框改这个属性。它决定批量启停和汇总哪些设备，不等于蓝色选中行。
    [ObservableProperty]
    private bool _isChecked;

    private DeviceSamplingViewModel? _sampling;
    public DeviceSamplingViewModel? Sampling
    {
        get => _sampling;
        private set => SetProperty(ref _sampling, value);
    }

    // 这里只描述任务有没有运行，不能据此判断设备在线；通信是否成功看下方每个点的读取状态。
    public string SamplingStateText => Sampling is null
        ? "尚未开始"
        : Sampling.IsRunning ? "采集中" : "未运行";

    private string _dataAgeText = "尚未开始采集";
    public string DataAgeText
    {
        get => _dataAgeText;
        private set => SetProperty(ref _dataAgeText, value);
    }

    private bool _isDataStale;
    public bool IsDataStale
    {
        get => _isDataStale;
        private set => SetProperty(ref _isDataStale, value);
    }

    /// <summary>
    /// MonitoringViewModel 刷新设备后逐行创建。传入找到的旧任务；找不到就传 null。
    /// 这里只接住对象，不 new 采集任务，也不会连接设备。
    /// </summary>
    public MonitoringDeviceViewModel(DeviceInfo device, DeviceSamplingViewModel? sampling)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        AttachSampling(sampling);
    }

    /// <summary>
    /// 刷新列表时，或实时监控后来为这台设备创建任务时，由 MonitoringViewModel 调用。
    /// Sampling 仍是原任务的引用；汇总表从这里找到原读数，不重新采集一遍。
    /// </summary>
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

    // AttachSampling 登记了这里。任务启停后，通知左侧设备表更新“采集中/未运行”。
    // 值和时间不经过这里复制，MonitoringReadingViewModel 会转发原读数行的通知。
    private void Sampling_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceSamplingViewModel.IsRunning))
            OnPropertyChanged(nameof(SamplingStateText));
        if (e.PropertyName is nameof(DeviceSamplingViewModel.IsRunning)
            or nameof(DeviceSamplingViewModel.LastSuccessfulReadAt))
            UpdateDataAge(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 任务启停/成功读取时，以及监控页每秒的计时器都会调用这里。
    /// 只算时间、改文字，不查库、不发网络请求，也不把上次的值填回失败行。
    /// </summary>
    public void UpdateDataAge(DateTimeOffset now)
    {
        if (Sampling is null)
        {
            IsDataStale = false;
            DataAgeText = "尚未开始采集";
            return;
        }
        if (Sampling.LastSuccessfulReadAt is not DateTimeOffset readAt)
        {
            IsDataStale = false;
            DataAgeText = Sampling.IsRunning ? "本次尚无成功读数，正在尝试读取" : "本次尚无成功读数";
            return;
        }
        if (!Sampling.IsRunning)
        {
            IsDataStale = false;
            DataAgeText = $"任务已停止；最后成功：{readAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            return;
        }

        double seconds = Math.Max(0, (now - readAt).TotalSeconds);
        // 这是提醒规则，不是设备离线判断：至少等 5 秒，采集间隔较长就等它的 3 倍。
        // 一轮还有逐点读取、超时和保存时间；超过这个值也可能只是本轮还没读完。
        double reminderAfterSeconds = Math.Max(5, Device.SamplingIntervalMilliseconds / 1000d * 3);
        IsDataStale = seconds > reminderAfterSeconds;
        DataAgeText = IsDataStale
            ? $"已 {seconds:0} 秒没有点位读取成功，可能仍在读取或读取失败"
            : $"最近一个成功读数：{seconds:0} 秒前";
    }

    // 刷新要丢弃旧行，或主窗口关闭时调用，解除旧行的订阅；不会停止任务、清空读数。
    internal void DetachSampling()
    {
        if (Sampling is not null)
            Sampling.PropertyChanged -= Sampling_PropertyChanged;
    }
}

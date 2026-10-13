using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 监控左侧的一台设备：Device 是配置，Sampling 是它的采集任务，没有任务时为 null。
/// IsChecked 决定这台设备是否参与批量启停和右侧读数汇总。
/// </summary>
public sealed partial class MonitoringDeviceViewModel : ObservableObject
{
    public DeviceInfo Device { get; }
    public string EnabledText => Device.IsEnabled ? "已启用" : "已停用";

    // 勾选框绑定这里；勾选和点击选中一行是两回事。
    [ObservableProperty]
    private bool _isChecked;

    private DeviceSamplingViewModel? _sampling;
    public DeviceSamplingViewModel? Sampling
    {
        get => _sampling;
        private set => SetProperty(ref _sampling, value);
    }

    // 这里只显示任务是否运行；设备是否读通，要看点位的读取状态。
    public string SamplingStateText => Sampling is null
        ? "尚未开始"
        : Sampling.IsRunning ? "采集中" : "未运行";

    // 距离最近一次成功读数过了多久。
    private string _dataAgeText = "尚未开始采集";
    public string DataAgeText
    {
        get => _dataAgeText;
        private set => SetProperty(ref _dataAgeText, value);
    }

    // 太久没有成功读数时，用它提醒界面；不代表已经确定设备离线。
    private bool _isDataStale;
    public bool IsDataStale
    {
        get => _isDataStale;
        private set => SetProperty(ref _isDataStale, value);
    }

    /// <summary>
    /// 创建一行设备显示，并接上这台设备已有的任务；这里不开始采集。
    /// </summary>
    public MonitoringDeviceViewModel(DeviceInfo device, DeviceSamplingViewModel? sampling)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        AttachSampling(sampling);
    }

    /// <summary>
    /// 由本类构造方法和 MonitoringViewModel.Sessions_CollectionChanged 调用。
    /// 把设备行和采集任务关联起来，任务状态变化时更新显示。
    /// </summary>
    internal void AttachSampling(DeviceSamplingViewModel? sampling)
    {
        // 已经是同一个任务，就不重复订阅。
        if (ReferenceEquals(Sampling, sampling))
            return;
        // 换任务前，先断开旧任务的通知。
        if (Sampling is not null)
            Sampling.PropertyChanged -= Sampling_PropertyChanged;

        // 保存原任务的引用，并监听它的属性变化。
        Sampling = sampling;
        if (Sampling is not null)
            Sampling.PropertyChanged += Sampling_PropertyChanged;
        // 接上任务后，立即刷新状态文字和读数时间。
        OnPropertyChanged(nameof(SamplingStateText));
        UpdateDataAge(DateTimeOffset.UtcNow);
    }

    // 收到任务的属性变化，更新设备行；点位数值由 MonitoringReadingViewModel 更新。
    // 由这台设备采集任务的 PropertyChanged 事件调用。
    private void Sampling_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只刷新这次变化影响到的显示。
        if (e.PropertyName == nameof(DeviceSamplingViewModel.IsRunning))
            OnPropertyChanged(nameof(SamplingStateText));
        if (e.PropertyName is nameof(DeviceSamplingViewModel.IsRunning)
            or nameof(DeviceSamplingViewModel.LastSuccessfulReadAt))
            UpdateDataAge(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 由 MonitoringViewModel.DataAgeTimer_Tick 每秒调用。
    /// 根据最后成功读数的时间，更新“多久没有新数据”的提示。
    /// 任务变化和每秒计时都会调用；这里只改显示，不读取设备。
    /// </summary>
    public void UpdateDataAge(DateTimeOffset now)
    {
        // 没有任务、还没读成功、已停止，分别显示对应提示。
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
        // 超过 5 秒或采集间隔的 3 倍就提醒；也可能只是这一轮还没读完。
        double reminderAfterSeconds = Math.Max(5, Device.SamplingIntervalMilliseconds / 1000d * 3);
        IsDataStale = seconds > reminderAfterSeconds;
        DataAgeText = IsDataStale
            ? $"已 {seconds:0} 秒没有点位读取成功，可能仍在读取或读取失败"
            : $"最近一个成功读数：{seconds:0} 秒前";
    }

    // 不再显示这行时断开通知；采集任务本身仍可继续运行。
    // 由 MonitoringViewModel 移除设备行或退出页面操作时调用。
    internal void DetachSampling()
    {
        if (Sampling is not null)
            Sampling.PropertyChanged -= Sampling_PropertyChanged;
    }
}

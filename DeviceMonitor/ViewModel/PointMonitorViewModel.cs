// 维护点位最新读数、报警状态和实时曲线缓存。
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public partial class PointMonitorViewModel : ObservableObject, IDisposable
{
    public const int MaximumTrendSamples = 300;
    public static TimeSpan TrendWindowDuration { get; } = TimeSpan.FromMinutes(5);
    // 曲线归点位任务所有，切换显示不清空，并限制时间范围和数量。
    private readonly List<LiveTrendSample> _trendSamples = new();
    private readonly AlarmMonitor? _alarms;
    private readonly DeviceSamplingViewModel? _samplingTask;
    private AlarmRecord? _displayedAlarm;
    private string? _readFailureMessage;

    public long DeviceId { get; }
    public string DeviceName { get; private set; }
    public PointInfo Point { get; }
    public string PointName => Point.Name;
    public string DisplayName => $"{DeviceName} / {PointName}";
    public ushort Address => Point.Address;
    public IReadOnlyList<LiveTrendSample> TrendSamples { get; }

    public event Action? TrendChanged;

    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _qualityText = "尚未读取";
    [ObservableProperty] private string _readTimeText = "—";

    // 逻辑使用明确状态，不从中文提示文字反推读取结果。
    public SingleReadState ReadState { get; private set; } = SingleReadState.NotRead;

    public bool IsAlert => _displayedAlarm is not null || _readFailureMessage is not null;
    public string StatusText
    {
        get
        {
            if (_samplingTask is { IsRunning: false })
                return IsAlert ? "已停止 · 不再监测" : "已停止";
            if (_samplingTask?.IsStopping == true)
                return "正在停止";
            if (_displayedAlarm is not null)
                return _displayedAlarm.State == AlarmState.NotMonitoring ? "不再监测" : _displayedAlarm.KindText;
            if (_readFailureMessage is not null)
                return "读取失败";
            return ReadState == SingleReadState.Succeeded ? "正常" : QualityText;
        }
    }
    public string DetailsText => $"{DisplayName}；地址 {Address}。{StatusText}。"
        + (_displayedAlarm is null ? string.Empty : $" {_displayedAlarm.Message}")
        + (_readFailureMessage is null ? string.Empty : $" {_readFailureMessage}");

    public PointMonitorViewModel(DeviceInfo device, PointInfo point,
    AlarmMonitor? alarms = null, DeviceSamplingViewModel? sampling = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        Point = point ?? throw new ArgumentNullException(nameof(point));
        DeviceId = device.Id;
        DeviceName = device.Name;
        _alarms = alarms;
        _samplingTask = sampling;
        TrendSamples = _trendSamples.AsReadOnly();
        if (_alarms is not null)
            ((INotifyCollectionChanged)_alarms.Records).CollectionChanged += Alarms_CollectionChanged;
        if (_samplingTask is not null)
            _samplingTask.PropertyChanged += Sampling_PropertyChanged;
        RefreshAlarm();
    }

    /// <summary>只改名称，不清读数或曲线。</summary>
    public void UpdateDevice(DeviceInfo device)
    {
        if (DeviceName == device.Name)
            return;
        DeviceName = device.Name;
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DetailsText));
    }

    /// <summary>进入读取状态，保留上次显示以避免闪烁。</summary>
    public void BeginRead()
    {
        ReadState = SingleReadState.Reading;
        QualityText = "读取中…";
        NotifyStatusChanged();
    }

    /// <summary>更新成功读数并追加曲线样本。</summary>
    public void ShowResult(PointReadResult result)
    {
        ReadState = SingleReadState.Succeeded;

        ValueText = result.ValueText;
        ReadTimeText = result.ReadAt.ToLocalTime().ToString("HH:mm:ss");
        QualityText = "成功";
        _readFailureMessage = null;
        NotifyStatusChanged();
        AppendTrendSample(result.ReadAt, result.Value);
        TrendChanged?.Invoke();
    }

    /// <summary>清掉有效数值，曲线留断点，不填假 0。</summary>
    public void ShowFailure(string message)
    {
        ReadState = SingleReadState.Failed;
        _readFailureMessage = message;
        ClearReading(message);
    }

    /// <summary>与设备故障分开，不靠判断提示文字来区分。</summary>
    public void ShowCanceled()
    {
        ReadState = SingleReadState.Canceled;
        _readFailureMessage = null;
        ClearReading("读取已取消");
    }

    private void ClearReading(string message)
    {
        ValueText = "—";
        ReadTimeText = "—";
        QualityText = message;
        NotifyStatusChanged();
        // 空值表示曲线断点，读取失败不能画成温度零点。
        AppendTrendSample(DateTimeOffset.UtcNow, null);
        TrendChanged?.Invoke();
    }

    private void AppendTrendSample(DateTimeOffset readAt, double? value)
    {
        _trendSamples.Add(new LiveTrendSample(readAt, value));

        // 仅裁剪显示缓存，原始历史由独立保存流程维护。
        DateTimeOffset earliest = readAt - TrendWindowDuration;
        _trendSamples.RemoveAll(sample => sample.ReadAt < earliest);
        if (_trendSamples.Count > MaximumTrendSamples)
            _trendSamples.RemoveRange(0, _trendSamples.Count - MaximumTrendSamples);
    }

    private void Alarms_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAlarm();

    private void RefreshAlarm()
    {
        if (_alarms is null)
            return;
        AlarmRecord? current = _alarms.GetCurrentAlarm(DeviceId, Point.Id);
        if (current is not null)
            _displayedAlarm = current;
        else if (_displayedAlarm is not null)
        {
            AlarmRecord? endedAlarm = _alarms.Records.FirstOrDefault(item => item.EventId == _displayedAlarm.EventId);
            if (endedAlarm is not null)
                _displayedAlarm = endedAlarm.State == AlarmState.NotMonitoring ? endedAlarm : null;
            else if (_displayedAlarm.State != AlarmState.NotMonitoring)
                _displayedAlarm = null;
        }
        NotifyStatusChanged();
    }

    private void Sampling_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceSamplingViewModel.IsRunning) or nameof(DeviceSamplingViewModel.IsStopping))
            NotifyStatusChanged();
    }

    private void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsAlert));
        OnPropertyChanged(nameof(DetailsText));
    }

    /// <summary>普通取消勾选不能断开这些通知。</summary>

    public void Dispose()
    {
        if (_alarms is not null)
            ((INotifyCollectionChanged)_alarms.Records).CollectionChanged -= Alarms_CollectionChanged;
        if (_samplingTask is not null)
            _samplingTask.PropertyChanged -= Sampling_PropertyChanged;
    }
}

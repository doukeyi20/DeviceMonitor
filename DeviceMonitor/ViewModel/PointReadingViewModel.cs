//采集点位的结果
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public partial class PointReadingViewModel : ObservableObject, IDisposable
{
    public const int MaximumTrendSamples = 300;
    public static TimeSpan TrendWindowDuration { get; } = TimeSpan.FromMinutes(5);
    // 曲线归点位任务所有，切换显示不清空，并限制时间范围和数量。
    private readonly List<LiveTrendSample> _trendSamples = new();
    private readonly AlarmMonitor? _alarms;
    private readonly DeviceSamplingViewModel? _samplingTask;
    private AlarmRecord? CurrentAlarm => _alarms?.GetCurrentAlarm(DeviceId, Point.Id);
    private string? _readFailureMessage;

    public long DeviceId { get; }
    public string DeviceName { get; private set; }
    public PointInfo Point { get; }
    public string PointName => Point.Name;
    public string DisplayName => $"{DeviceName} / {PointName}";
    public ushort Address => Point.Address;
    public IReadOnlyList<LiveTrendSample> TrendSamples { get; }

    public event Action? TrendChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private double? _value;

    // 数值和单位分开显示；读取失败时为空，不冒充有效读数。
    public string NumberText => Value?.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
    public string ValueText => Value.HasValue ? NumericValue.Format(Value.Value, Point.Unit) : "—";
    [ObservableProperty] private string _qualityText = "尚未读取";

    [ObservableProperty] private string _readTimeText = "—";

    // 逻辑使用明确状态，不从中文提示文字反推读取结果。
    public SingleReadState ReadState { get; private set; } = SingleReadState.NotRead;

    public bool IsAlert => CurrentAlarm is not null || _readFailureMessage is not null;
    public string StatusText
    {
        get
        {
            if (_samplingTask is { IsRunning: false })
                return IsAlert ? "已停止 · 不再监测" : "已停止";
            if (_samplingTask?.IsStopping == true)
                return "正在停止";
            if (CurrentAlarm is not null)
                return CurrentAlarm.KindText;
            if (_readFailureMessage is not null)
                return "读取失败";
            return ReadState == SingleReadState.Succeeded ? "正常" : QualityText;
        }
    }
    public string DetailsText => $"{DisplayName}；地址 {Address}。{StatusText}。"
        + (CurrentAlarm is null ? string.Empty : $" {CurrentAlarm.Message}")
        + (_readFailureMessage is null ? string.Empty : $" {_readFailureMessage}");

    public PointReadingViewModel(DeviceInfo device, PointInfo point,
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
    public void ShowResult(PointReadResult result, TimeSpan elapsed)
    {
        ReadState = SingleReadState.Succeeded;

        Value = result.Value;
        ReadTimeText = result.ReadAt.ToLocalTime().ToString("HH:mm:ss");
        QualityText = "成功";
        _readFailureMessage = null;
        NotifyStatusChanged();
        AppendTrendSample(elapsed, result.Value);
        TrendChanged?.Invoke();
    }

    /// <summary>清掉有效数值，曲线留断点，不填假 0。</summary>
    public void ShowFailure(string message, TimeSpan elapsed)
    {
        ReadState = SingleReadState.Failed;
        _readFailureMessage = message;
        ClearReading(message, elapsed);
    }

    /// <summary>与设备故障分开，不靠判断提示文字来区分。</summary>
    public void ShowCanceled(TimeSpan elapsed)
    {
        ReadState = SingleReadState.Canceled;
        _readFailureMessage = null;
        ClearReading("读取已取消", elapsed);
    }

    private void ClearReading(string message, TimeSpan elapsed)
    {
        Value = null;
        ReadTimeText = "—";
        QualityText = message;
        NotifyStatusChanged();
        // 空值表示曲线断点，读取失败不能画成温度零点。
        AppendTrendSample(elapsed, null);
        TrendChanged?.Invoke();
    }

    private void AppendTrendSample(TimeSpan elapsed, double? value)
    {
        _trendSamples.Add(new LiveTrendSample(elapsed, value));

        // 仅裁剪显示缓存，原始历史由独立保存流程维护。
        TimeSpan earliest = elapsed - TrendWindowDuration;
        _trendSamples.RemoveAll(sample => sample.Elapsed < earliest);
        if (_trendSamples.Count > MaximumTrendSamples)
            _trendSamples.RemoveRange(0, _trendSamples.Count - MaximumTrendSamples);
    }

    private void Alarms_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAlarm();

    // 报警状态只在 AlarmMonitor 维护，这里收到通知后重新读取，不再缓存另一份。
    private void RefreshAlarm() => NotifyStatusChanged();

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

    /// <summary>点位任务结束后解除通知；切换显示不会释放仍在采集的点位。</summary>
    public void Dispose()
    {
        if (_alarms is not null)
            ((INotifyCollectionChanged)_alarms.Records).CollectionChanged -= Alarms_CollectionChanged;
        if (_samplingTask is not null)
            _samplingTask.PropertyChanged -= Sampling_PropertyChanged;
    }
}

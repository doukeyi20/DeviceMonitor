using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 一个点的最新读数和本次波形：Point 是配置，文字属性供表格显示。
/// DeviceSamplingViewModel 负责读取，再调用这里的方法更新显示。
/// </summary>
public partial class PointReadingViewModel : ObservableObject
{
    public const int MaximumTrendSamples = 300;
    public static TimeSpan TrendWindowDuration { get; } = TimeSpan.FromMinutes(5);
    private readonly List<LiveTrendSample> _trendSamples = new(); // 这个点自己的曲线，不受页面选择或勾选影响。

    public PointInfo Point { get; }

    // 每个点从创建读数行开始缓存；外部只能查看，不直接增删。
    public IReadOnlyList<LiveTrendSample> TrendSamples { get; }
    public DateTimeOffset? TrendEndTime => _trendSamples.Count == 0 ? null : _trendSamples[^1].ReadAt;

    // 最近一次成功读到的完整结果，监控行用它取读取时间等信息；失败后清空。
    public PointReadResult? LatestResult { get; private set; }

    // 当前点的缓存更新后，通知正在显示它的图刷新；没选中时也照常缓存。
    public event Action<PointReadResult?>? ReadCompleted;

    [ObservableProperty]
    private string _valueText = "—";

    [ObservableProperty]
    private string _qualityText = "尚未读取";

    [ObservableProperty]
    private string _readTimeText = "—";

    /// <summary>
    /// 由 DeviceSamplingViewModel.PrepareRows 开始采集时创建。
    /// 保存配置，准备这个点自己的空波形；重新开始会创建新行，不沿用旧曲线。
    /// </summary>
    public PointReadingViewModel(PointInfo point)
    {
        Point = point;
        TrendSamples = _trendSamples.AsReadOnly();
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在读取这个点之前调用。
    /// 开始读取时更新状态；上一成功数值和时间先保留，避免每轮闪空。
    /// </summary>
    public void BeginRead()
    {
        // 只通知表格显示读取中，本方法不发读取请求。
        QualityText = "读取中…";
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在读取成功后调用。
    /// 读取成功后，把结果填到数值、时间和状态列；不在这里保存历史。
    /// </summary>
    public void ShowResult(PointReadResult result)
    {
        LatestResult = result;
        // 把读到的值转成表格显示的文字。
        ValueText = result.Value.ToString(CultureInfo.InvariantCulture);
        // 读取时间转成本地时间显示。
        ReadTimeText = result.ReadAt.ToLocalTime().ToString("HH:mm:ss.fff");
        QualityText = "成功";
        OnPropertyChanged(nameof(LatestResult));
        // 先缓存到这个点自己名下，是否选中它都一样；不等待历史保存。
        AppendTrendSample(result.ReadAt, result.Value);
        ReadCompleted?.Invoke(result);
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在读取失败后调用。
    /// 读取失败或取消时，清掉旧值并显示原因。
    /// </summary>
    public void ShowFailure(string message)
    {
        LatestResult = null;
        // 清掉旧值，避免把上次成功的读数当成这次的结果。
        ValueText = "—";
        ReadTimeText = "—";
        // 把传来的原因显示在状态列。
        QualityText = message;
        OnPropertyChanged(nameof(LatestResult));
        AppendTrendSample(DateTimeOffset.UtcNow, null);
        ReadCompleted?.Invoke(null);
    }

    /// <summary>
    /// 由 ShowResult 和 ShowFailure 调用。
    /// 每读完一次就缓存这个点的数值或断点，只保留最近五分钟、最多三百个。
    /// </summary>
    private void AppendTrendSample(DateTimeOffset readAt, double? value)
    {
        _trendSamples.Add(new LiveTrendSample(readAt, value));
        DateTimeOffset earliest = readAt - TrendWindowDuration;
        _trendSamples.RemoveAll(sample => sample.ReadAt < earliest);
        if (_trendSamples.Count > MaximumTrendSamples)
            _trendSamples.RemoveRange(0, _trendSamples.Count - MaximumTrendSamples);
    }
}

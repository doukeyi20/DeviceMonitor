using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 实时波形的显示数据：从选中点已有的缓存取数，不读取设备或查询历史。
/// 各点自己积累曲线，这里只决定当前显示哪一个点。
/// </summary>
public sealed class LiveTrendViewModel : ObservableObject, IDisposable
{
    public const int MaximumSamples = PointReadingViewModel.MaximumTrendSamples;
    private PointReadingViewModel? _reading; // 正在观察的原读数行，不另开采集任务。
    private bool _isDisposed;

    public TimeSpan WindowDuration => PointReadingViewModel.TrendWindowDuration;

    private IReadOnlyList<LiveTrendSample> _samples = Array.Empty<LiveTrendSample>();
    // 图表绑定这里。每次换成一份新的小列表，通知图表重画。
    public IReadOnlyList<LiveTrendSample> Samples
    {
        get => _samples;
        private set => SetProperty(ref _samples, value);
    }

    private DateTimeOffset? _endTime;
    public DateTimeOffset? EndTime
    {
        get => _endTime;
        private set => SetProperty(ref _endTime, value);
    }

    private string _axisTitle = "读数";
    public string AxisTitle
    {
        get => _axisTitle;
        private set => SetProperty(ref _axisTitle, value);
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// 由 MonitoringViewModel 的选中读数变化时调用。
    /// 切到一个点后，立即显示它已经缓存的曲线，不清空任何点的数据。
    /// </summary>
    public void Select(MonitoringReadingViewModel? row)
    {
        if (_isDisposed)
            return;

        PointReadingViewModel? next = row?.Reading;
        // 刷新设备名称时可能换了包装行，只要仍是原读数行，就保留曲线。
        if (ReferenceEquals(_reading, next))
            return;
        if (_reading is not null)
            _reading.ReadCompleted -= Reading_ReadCompleted;

        _reading = next;
        AxisTitle = row is null ? "读数" : row.PointName;
        if (_reading is not null)
            _reading.ReadCompleted += Reading_ReadCompleted;
        RefreshDisplay();
    }

    /// <summary>
    /// 由所选 PointReadingViewModel 的 ReadCompleted 事件调用。
    /// 原读数行已经更新了自己的缓存，这里只刷新正在看的图。
    /// </summary>
    private void Reading_ReadCompleted(PointReadResult? result)
    {
        if (_isDisposed)
            return;
        RefreshDisplay();
    }

    /// <summary>
    /// 由 Select 和 Reading_ReadCompleted 调用。
    /// 拿所选点缓存的一份副本给图表；只改显示，不删除点位缓存。
    /// </summary>
    private void RefreshDisplay()
    {
        Samples = _reading?.TrendSamples.ToArray() ?? Array.Empty<LiveTrendSample>();
        EndTime = _reading?.TrendEndTime;
        // 正常绘图不显示说明段落；停止和读取失败仍保留可见状态。
        StatusText = _reading is null ? string.Empty
            : _reading.QualityText == "读取已取消" ? "已停止"
            : Samples.Count > 0 && Samples[^1].Value is null ? "读取失败，曲线中断。"
            : string.Empty;
    }

    /// <summary>
    /// 由 MonitoringViewModel.StopOperationsAsync 在退出时调用。
    /// 只解除曲线订阅，采集任务仍由共用管理器负责停止。
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        if (_reading is not null)
            _reading.ReadCompleted -= Reading_ReadCompleted;
        _reading = null;
    }
}

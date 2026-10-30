// 选择点位并显示其已有的实时曲线缓存。
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public sealed class LiveTrendViewModel : ObservableObject, IDisposable
{
    private PointMonitorViewModel? _selectedPoint;
    private bool _isDisposed;

    public TimeSpan WindowDuration => PointMonitorViewModel.TrendWindowDuration;

    private IReadOnlyList<LiveTrendSample> _samples = Array.Empty<LiveTrendSample>();

    public IReadOnlyList<LiveTrendSample> Samples
    {
        get => _samples;
        private set
        {
            if (SetProperty(ref _samples, value))
                OnPropertyChanged(nameof(EndTime));
        }
    }

    public DateTimeOffset? EndTime => Samples.Count == 0 ? null : Samples[^1].ReadAt;

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

    /// <summary>切到一个点后，立即显示它已经缓存的曲线，不清空任何点的数据。</summary>
    public void Select(PointMonitorViewModel? row)
    {
        if (_isDisposed)
            return;

        if (ReferenceEquals(_selectedPoint, row))
            return;

        // 切换前退订旧点，防止旧曲线继续触发当前显示刷新。
        if (_selectedPoint is not null)
            _selectedPoint.TrendChanged -= SelectedPoint_TrendChanged;

        _selectedPoint = row;
        AxisTitle = row is null ? "读数" : row.PointName +
            (string.IsNullOrEmpty(row.Point.Unit) ? "" : $"（{row.Point.Unit}）");
        if (_selectedPoint is not null)
            _selectedPoint.TrendChanged += SelectedPoint_TrendChanged;
        RefreshDisplay();
    }

    /// <summary>原读数行已经更新了自己的缓存，这里只刷新正在看的图。</summary>
    private void SelectedPoint_TrendChanged()
    {
        if (_isDisposed)
            return;
        RefreshDisplay();
    }

    /// <summary>拿所选点缓存的一份副本给图表；只改显示，不删除点位缓存。</summary>
    private void RefreshDisplay()
    {
        // 取所选点的缓存副本显示，不重新采集或清空其他点。
        Samples = _selectedPoint?.TrendSamples.ToArray() ?? Array.Empty<LiveTrendSample>();

        if (_selectedPoint?.ReadState == SingleReadState.Canceled)
            StatusText = "已停止";
        else if (Samples.Count > 0 && Samples[^1].Value is null)
            StatusText = "读取失败，曲线中断。";
        else
            StatusText = string.Empty;
    }

    /// <summary>只解除曲线订阅，采集任务仍由共用设备任务负责停止。</summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        if (_selectedPoint is not null)
            _selectedPoint.TrendChanged -= SelectedPoint_TrendChanged;
        _selectedPoint = null;
    }
}

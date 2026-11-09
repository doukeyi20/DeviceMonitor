// 显示所选点位的实时趋势。
using System.Windows;
using System.Windows.Controls;
using DeviceMonitor.ViewModel;
using ScottPlot;
using ScottPlot.WPF;

namespace DeviceMonitor.View;

public sealed class TrendChart : UserControl
{
    private readonly WpfPlot _plot = new();
    private readonly TextBlock _empty = new()
    {
        Text = "暂无曲线数据",
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        Foreground = System.Windows.Media.Brushes.SlateGray,
        IsHitTestVisible = false
    };

    // 使用依赖属性接收 XAML Binding；数据源换了就调用 Redraw，不由图表自己读取设备。
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<LiveTrendSample>), typeof(TrendChart),
        new PropertyMetadata(null, Redraw));
    public static readonly DependencyProperty AxisTitleProperty = DependencyProperty.Register(
        nameof(AxisTitle), typeof(string), typeof(TrendChart), new PropertyMetadata("读数", Redraw));
    public static readonly DependencyProperty WindowDurationProperty = DependencyProperty.Register(
        nameof(WindowDuration), typeof(TimeSpan?), typeof(TrendChart), new PropertyMetadata(null, Redraw));

    public IReadOnlyList<LiveTrendSample>? Samples
    {
        get => (IReadOnlyList<LiveTrendSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }
    public string AxisTitle
    {
        get => (string)GetValue(AxisTitleProperty);
        set => SetValue(AxisTitleProperty, value);
    }
    public TimeSpan? WindowDuration
    {
        get => (TimeSpan?)GetValue(WindowDurationProperty);
        set => SetValue(WindowDurationProperty, value);
    }

    public TrendChart()
    {
        var grid = new Grid();
        grid.Children.Add(_plot);
        grid.Children.Add(_empty);
        Content = grid;
        MinHeight = 180;
        // 实时图自动跟随数据，关闭拖拽缩放。
        _plot.UserInputProcessor.Disable();
        _plot.Plot.Font.Set("Microsoft YaHei UI");
        _plot.Plot.Axes.Left.TickLabelStyle.FontSize = 14;
        _plot.Plot.Axes.Left.TickGenerator.MaxTickCount = 6;
        _plot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 13;
        _plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            IntegerTicksOnly = true,
            MinimumTickSpacing = 50
        };
        _plot.Plot.Axes.Left.Label.FontSize = 12;
        _plot.Plot.Axes.Bottom.Label.FontSize = 11;
        _plot.Plot.Grid.MajorLineColor = Color.FromHex("#E8EEF5");
        _plot.Plot.Grid.MinorLineColor = Colors.Transparent;
        _plot.Plot.Axes.Color(Color.FromHex("#687B8E"));
        // 留出横轴刻度和标题的空间。
        _plot.Plot.Layout.Fixed(new PixelPadding(left: 65, right: 38, bottom: 52, top: 16));
        Loaded += (_, _) => Draw();
    }

    private static void Redraw(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((TrendChart)sender).Draw();

    /// <summary>把点位样本交给 ScottPlot；空值分段绘制，不能把失败前后强行连起来。</summary>
    private void Draw()
    {
        _plot.Plot.Clear();
        _plot.Plot.YLabel(AxisTitle);
        _plot.Plot.XLabel("采集用时（秒）");
        var samples = Samples ?? [];
        double latestSeconds = samples.LastOrDefault()?.Elapsed.TotalSeconds ?? 0;
        double startSeconds = WindowDuration is { } duration
            ? Math.Max(0, latestSeconds - duration.TotalSeconds)
            : 0;
        // 刚启动时先留出 10 秒宽度，超过后跟着最新样本移动右边界。
        double endSeconds = Math.Max(10, latestSeconds);

        var segment = new List<LiveTrendSample>();
        bool hasValue = false;
        foreach (var sample in samples.Where(item => item.Elapsed.TotalSeconds >= startSeconds))
        {
            if (sample.Value is { } value && double.IsFinite(value))
            {
                segment.Add(sample);
                hasValue = true;
            }
            else
            {
                DrawSegment(segment);
                segment.Clear();
            }
        }
        DrawSegment(segment);
        _empty.Visibility = hasValue ? Visibility.Collapsed : Visibility.Visible;
        _plot.Plot.Axes.AutoScale();
        if (!hasValue)
            _plot.Plot.Axes.SetLimitsY(0, 1);
        _plot.Plot.Axes.SetLimitsX(startSeconds, endSeconds);
        _plot.Refresh();
    }

    private void DrawSegment(List<LiveTrendSample> samples)
    {
        if (samples.Count == 0)
            return;
        var line = _plot.Plot.Add.Scatter(
            samples.Select(item => item.Elapsed.TotalSeconds).ToArray(),
            samples.Select(item => item.Value!.Value).ToArray());
        line.Color = Color.FromHex("#526D86");
        line.LineWidth = 2;
        line.MarkerSize = samples.Count == 1 ? 6 : 3;
    }
}

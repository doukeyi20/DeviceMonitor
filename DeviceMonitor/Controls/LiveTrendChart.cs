using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeviceMonitor.Models;

namespace DeviceMonitor.Controls;

/// <summary>
/// 画所选点位的实时曲线，只使用页面传来的样本，不读取设备或数据库。
/// 横轴是最近一段时间，纵轴随这个点的数值调整；读取失败的位置不连线。
/// </summary>
public sealed class LiveTrendChart : FrameworkElement
{
    private const int MaximumSamples = 300;
    private const int VerticalTickCount = 5;
    private static readonly TimeSpan DefaultWindowDuration = TimeSpan.FromMinutes(5);
    private static readonly Typeface ChartTypeface = new("Segoe UI");
    private static readonly Brush TextBrush = CreateBrush(95, 105, 117);
    private static readonly Brush CurveBrush = CreateBrush(44, 112, 151);
    private static readonly Pen BorderPen = CreatePen(224, 228, 233, 1);
    private static readonly Pen GridPen = CreatePen(237, 240, 243, 1);
    private static readonly Pen AxisPen = CreatePen(173, 181, 190, 1);
    private static readonly Pen CurvePen = CreatePen(44, 112, 151, 1.8);

    // 页面换了样本列表或坐标设置后，WPF 会自动重画。
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<LiveTrendSample>), typeof(LiveTrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisTitleProperty = DependencyProperty.Register(
        nameof(AxisTitle), typeof(string), typeof(LiveTrendChart),
        new FrameworkPropertyMetadata("读数", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndTimeProperty = DependencyProperty.Register(
        nameof(EndTime), typeof(DateTimeOffset?), typeof(LiveTrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowDurationProperty = DependencyProperty.Register(
        nameof(WindowDuration), typeof(TimeSpan), typeof(LiveTrendChart),
        new FrameworkPropertyMetadata(DefaultWindowDuration, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is TimeSpan duration && duration > TimeSpan.Zero);

    // 所选点位的样本。新增样本后，页面要换成新列表，让图知道数据变了。
    public IReadOnlyList<LiveTrendSample>? Samples
    {
        get => (IReadOnlyList<LiveTrendSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    // 纵轴说明，例如“温度（℃）”；没有单位的点位可以直接写“读数”。
    public string AxisTitle
    {
        get => (string)GetValue(AxisTitleProperty);
        set => SetValue(AxisTitleProperty, value);
    }

    // 横轴右端的时间；页面不指定时，就使用最后一个样本的时间。
    public DateTimeOffset? EndTime
    {
        get => (DateTimeOffset?)GetValue(EndTimeProperty);
        set => SetValue(EndTimeProperty, value);
    }

    // 图上保留多长时间，默认最近 5 分钟。
    public TimeSpan WindowDuration
    {
        get => (TimeSpan)GetValue(WindowDurationProperty);
        set => SetValue(WindowDurationProperty, value);
    }

    public LiveTrendChart()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    // 由 WPF 调用：准备这段时间内的样本，画坐标，再画曲线。
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        if (width <= 0 || height <= 0)
            return;

        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        try
        {
            drawingContext.DrawRoundedRectangle(Brushes.White, BorderPen,
                new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)), 6, 6);
            if (width < 240 || height < 150)
            {
                DrawMessage(drawingContext, "区域太小，无法显示曲线。", new Rect(0, 0, width, height));
                return;
            }

            // 时间固定向前留一段，不因为刚读到一两个点就把横轴撑满。
            IReadOnlyList<LiveTrendSample>? samples = Samples;
            DateTimeOffset endTime = EndTime ?? (samples is { Count: > 0 }
                ? samples.Max(sample => sample.ReadAt) : DateTimeOffset.Now);
            long endTicks = endTime.UtcTicks;
            long startTicks = Math.Max(0, endTicks - WindowDuration.Ticks);
            LiveTrendSample[] visible = samples is null ? [] : samples
                .Where(sample => sample.ReadAt.UtcTicks >= startTicks && sample.ReadAt.UtcTicks <= endTicks)
                .OrderBy(sample => sample.ReadAt)
                .TakeLast(MaximumSamples)
                .ToArray();

            // 只有成功读到的数值参与纵轴计算；失败和无效数值都不能当成 0。
            double[] values = visible.Where(sample => IsValidValue(sample.Value))
                .Select(sample => sample.Value!.Value).ToArray();
            (double lowerBound, double upperBound) = GetValueBounds(values);
            string[] labels = Enumerable.Range(0, VerticalTickCount)
                .Select(index => FormatValue(lowerBound +
                    (upperBound - lowerBound) * index / (VerticalTickCount - 1)))
                .ToArray();
            double left = Math.Max(55, labels.Max(label => CreateText(label).Width) + 16);
            Rect plot = new(left, 30, Math.Max(1, width - left - 22), height - 78);
            if (plot.Width < 100 || plot.Height < 45)
            {
                DrawMessage(drawingContext, "区域太小，无法显示曲线。", new Rect(0, 0, width, height));
                return;
            }

            DrawAxes(drawingContext, plot, labels, startTicks, endTicks);
            if (values.Length == 0)
            {
                DrawMessage(drawingContext, samples is { Count: > 0 }
                    ? "这段时间没有有效读数。" : "暂无波形", plot);
                return;
            }

            // 曲线限定在坐标区域内，边缘的点也不会画到刻度文字上。
            drawingContext.PushClip(new RectangleGeometry(plot));
            try
            {
                DrawSamples(drawingContext, plot, visible, startTicks, endTicks, lowerBound, upperBound);
            }
            finally
            {
                drawingContext.Pop();
            }
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    // 由 WPF 在区域大小改变时调用，按新尺寸重新画。
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    // 由 OnRender 调用：无数据时给出普通刻度，恒值时上下各留 1。
    private static (double Lower, double Upper) GetValueBounds(double[] values)
    {
        if (values.Length == 0)
            return (0, 1);

        double minimum = values.Min();
        double maximum = values.Max();
        double padding = minimum == maximum ? 1 : (maximum - minimum) * 0.08;
        return (minimum - padding, maximum + padding);
    }

    // 由 OnRender 调用：画数值刻度、网格和本机时间。
    private void DrawAxes(DrawingContext drawingContext, Rect plot, string[] labels,
        long startTicks, long endTicks)
    {
        FormattedText title = CreateText(AxisTitle ?? "读数");
        title.MaxTextWidth = Math.Max(1, plot.Right - 12);
        drawingContext.DrawText(title, new Point(12, 7));
        for (int index = 0; index < VerticalTickCount; index++)
        {
            double y = plot.Bottom - plot.Height * index / (VerticalTickCount - 1);
            drawingContext.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = CreateText(labels[index]);
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
        drawingContext.DrawLine(AxisPen, plot.BottomLeft, plot.TopLeft);
        drawingContext.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);

        // 横轴显示开始、中间、结束三个时间，文字始终留在绘图区宽度内。
        string format = endTicks - startTicks >= TimeSpan.TicksPerDay ? "MM-dd HH:mm" : "HH:mm:ss";
        for (int index = 0; index < 3; index++)
        {
            long ticks = startTicks + (endTicks - startTicks) * index / 2;
            DateTimeOffset time = new(ticks, TimeSpan.Zero);
            FormattedText label = CreateText(time.LocalDateTime.ToString(format, CultureInfo.CurrentCulture), 11);
            double x = plot.Left + plot.Width * index / 2;
            double labelLeft = Math.Clamp(x - label.Width / 2,
                plot.Left, Math.Max(plot.Left, plot.Right - label.Width));
            drawingContext.DrawText(label, new Point(labelLeft, plot.Bottom + 8));
        }
        FormattedText timeTitle = CreateText("读取时间（本机时间）", 11);
        drawingContext.DrawText(timeTitle, new Point(plot.Right - timeTitle.Width, plot.Bottom + 28));
    }

    // 由 OnRender 调用：时间和数值换成屏幕位置，遇到失败样本就断开。
    private static void DrawSamples(DrawingContext drawingContext, Rect plot, LiveTrendSample[] samples,
        long startTicks, long endTicks, double lowerBound, double upperBound)
    {
        Point? previous = null;
        long elapsedTicks = Math.Max(1, endTicks - startTicks);
        double radius = samples.Count(sample => IsValidValue(sample.Value)) == 1 ? 3.5 : 1.8;
        foreach (LiveTrendSample sample in samples)
        {
            if (!IsValidValue(sample.Value))
            {
                previous = null;
                continue;
            }

            double timeRatio = (double)(sample.ReadAt.UtcTicks - startTicks) / elapsedTicks;
            double valueRatio = (sample.Value!.Value - lowerBound) / (upperBound - lowerBound);
            Point current = new(plot.Left + plot.Width * timeRatio, plot.Bottom - plot.Height * valueRatio);
            if (previous is Point last)
                drawingContext.DrawLine(CurvePen, last, current);
            drawingContext.DrawEllipse(CurveBrush, null, current, radius, radius);
            previous = current;
        }
    }

    // 由 OnRender 调用：在指定区域中间显示提示。
    private void DrawMessage(DrawingContext drawingContext, string message, Rect area)
    {
        FormattedText text = CreateText(message);
        text.MaxTextWidth = Math.Max(1, area.Width - 24);
        text.TextAlignment = TextAlignment.Center;
        drawingContext.DrawText(text, new Point(area.Left + 12, area.Top + Math.Max(4, (area.Height - text.Height) / 2)));
    }

    // 绘图方法共用：按屏幕缩放准备文字。
    private FormattedText CreateText(string text, double fontSize = 12) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, ChartTypeface,
        fontSize, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static bool IsValidValue(double? value) => value.HasValue && double.IsFinite(value.Value);

    // 只缩短坐标文字，不改变实际读数。
    private static string FormatValue(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    // 创建后不再修改，绘图时反复使用。
    private static Brush CreateBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(byte red, byte green, byte blue, double thickness)
    {
        Pen pen = new(CreateBrush(red, green, blue), thickness);
        pen.Freeze();
        return pen;
    }
}

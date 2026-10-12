using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Controls
{

    /// <summary>
    /// 只负责把已保存的 HistoryRecord 画成曲线的 WPF 控件，不连接设备也不查库。
    /// Records 由窗口绑定；数据改变或控件大小改变时 WPF 重画。第一遍只需看 Records 和 OnRender 入口。
    /// </summary>
    public sealed class HistoryTrendChart : FrameworkElement
    {
        private const int MaximumRecords = 1000;
        private const int VerticalTickCount = 5;
        private static readonly Brush BackgroundBrush = Brushes.White;
        private static readonly Brush TextBrush = CreateBrush(95, 105, 117);
        private static readonly Brush LineBrush = CreateBrush(68, 101, 126);
        private static readonly Pen BorderPen = CreatePen(224, 228, 233, 1);
        private static readonly Pen GridPen = CreatePen(237, 240, 243, 1);
        private static readonly Pen AxisPen = CreatePen(173, 181, 190, 1);
        private static readonly Pen CurvePen = CreatePen(68, 101, 126, 1.8);
        private static readonly Typeface ChartTypeface = new Typeface("Segoe UI");

        // AffectsRender 的意思是：页面给 Records 换了新数据，就让 WPF 重画图。
        public static readonly DependencyProperty RecordsProperty = DependencyProperty.Register(
            nameof(Records),
            typeof(IReadOnlyList<HistoryRecord>),
            typeof(HistoryTrendChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<HistoryRecord>? Records
        {
            get => (IReadOnlyList<HistoryRecord>?)GetValue(RecordsProperty);
            set => SetValue(RecordsProperty, value);
        }

        public HistoryTrendChart()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
        }

        /// <summary>
        /// WPF 要重画控件时自动调用。读取 Records，按时间排好并计算坐标，再画坐标轴、连线和圆点。
        /// 没有数据就画提示，不补假读数；这些连线也不证明相邻记录之间一直通信正常。
        /// </summary>
        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            double width = RenderSize.Width;
            double height = RenderSize.Height;
            if (width <= 0 || height <= 0)
                return;

            drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            drawingContext.DrawRoundedRectangle(BackgroundBrush, BorderPen,
                new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)), 6, 6);

            if (width < 240 || height < 130)
            {
                DrawCenteredMessage(drawingContext, "请放大窗口查看曲线。", width, height);
                drawingContext.Pop();
                return;
            }

            if (Records is not { Count: > 0 } records)
            {
                DrawCenteredMessage(drawingContext, "暂无可绘制的历史读数。", width, height);
                drawingContext.Pop();
                return;
            }

            // 查询结果可能是倒序。画图时按读取时间从左到右排列，不修改原来的记录列表。
            // 页面本来就只查询最多 1000 条；这里再限制一次，避免误传太多数据拖慢界面。
            HistoryRecord[] ordered = records.Take(MaximumRecords)
                .OrderBy(record => record.ReadAt)
                .ThenBy(record => record.Id)
                .ToArray();

            double minimum = ordered.Min(record => record.Value);
            double maximum = ordered.Max(record => record.Value);
            double padding = minimum == maximum ? 1 : (maximum - minimum) * 0.08;
            double lowerBound = minimum - padding;
            double upperBound = maximum + padding;

            // 所有读数相同也需要留一点上下空间，否则纵轴范围为 0，算不出点的位置。
            string[] tickLabels = Enumerable.Range(0, VerticalTickCount)
                .Select(index => FormatValue(lowerBound +
                    (upperBound - lowerBound) * index / (VerticalTickCount - 1)))
                .ToArray();
            double labelWidth = tickLabels.Max(label => CreateText(label).WidthIncludingTrailingWhitespace);
            Rect plot = new Rect(Math.Max(55, labelWidth + 18), 32,
                Math.Max(1, width - Math.Max(55, labelWidth + 18) - 24), height - 78);

            if (plot.Width < 90 || plot.Height < 45)
            {
                DrawCenteredMessage(drawingContext, "请放大窗口查看曲线。", width, height);
                drawingContext.Pop();
                return;
            }

            DrawAxes(drawingContext, plot, tickLabels, ordered[0].ReadAt, ordered[^1].ReadAt);
            DrawReadings(drawingContext, plot, ordered, lowerBound, upperBound);
            drawingContext.Pop();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            // 窗口拉大、缩小时，按新的可用宽高重新计算坐标，不拉伸旧图片。
            InvalidateVisual();
        }

        /// <summary>
        /// OnRender 调用，画纵轴刻度、网格和本机时间横轴；不计算或读取新的设备值。
        /// </summary>
        private void DrawAxes(DrawingContext drawingContext, Rect plot, string[] tickLabels,
            DateTimeOffset firstTime, DateTimeOffset lastTime)
        {
            drawingContext.DrawText(CreateText("读数"), new Point(12, 8));
            for (int index = 0; index < VerticalTickCount; index++)
            {
                double y = plot.Bottom - plot.Height * index / (VerticalTickCount - 1);
                drawingContext.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                FormattedText label = CreateText(tickLabels[index]);
                drawingContext.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
            }

            drawingContext.DrawLine(AxisPen, plot.BottomLeft, plot.TopLeft);
            drawingContext.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);

            long elapsedTicks = lastTime.UtcTicks - firstTime.UtcTicks;
            string timeFormat = elapsedTicks >= TimeSpan.TicksPerDay ? "MM-dd HH:mm"
                : elapsedTicks < TimeSpan.TicksPerSecond ? "HH:mm:ss.fff" : "HH:mm:ss";

            if (elapsedTicks == 0)
            {
                // 时间完全相同的点画在同一个位置，只标出这个真实时间，不虚构前后时间。
                DrawTimeLabel(drawingContext, firstTime, timeFormat, plot.Left + plot.Width / 2,
                    plot.Bottom + 9, plot, centered: true);
            }
            else
            {
                DrawTimeLabel(drawingContext, firstTime, timeFormat, plot.Left,
                    plot.Bottom + 9, plot, centered: false);
                DrawTimeLabel(drawingContext, firstTime.AddTicks(elapsedTicks / 2), timeFormat,
                    plot.Left + plot.Width / 2, plot.Bottom + 9, plot, centered: true);
                DrawTimeLabel(drawingContext, lastTime, timeFormat, plot.Right,
                    plot.Bottom + 9, plot, centered: false);
            }

            FormattedText axisTitle = CreateText("读取时间（本机时间）", 11);
            drawingContext.DrawText(axisTitle,
                new Point(plot.Right - axisTitle.Width, plot.Bottom + 29));
        }

        /// <summary>
        /// OnRender 调用。把记录时间/数值换算为控件内的坐标，再连接真实记录的点；不修改 Records。
        /// </summary>
        private void DrawReadings(DrawingContext drawingContext, Rect plot,
            HistoryRecord[] ordered, double lowerBound, double upperBound)
        {
            long firstTicks = ordered[0].ReadAt.UtcTicks;
            long elapsedTicks = ordered[^1].ReadAt.UtcTicks - firstTicks;
            Point[] points = ordered.Select(record =>
            {
                // 横轴按真实经过的时间算：隔十秒的点比隔一秒的点离得更远。
                // 相同时间的读数使用相同横坐标；只有一个时间时放在图中央。
                double timeRatio = elapsedTicks == 0 ? 0.5
                    : (double)(record.ReadAt.UtcTicks - firstTicks) / elapsedTicks;
                double valueRatio = (record.Value - lowerBound) / (upperBound - lowerBound);
                return new Point(plot.Left + plot.Width * timeRatio,
                    plot.Bottom - plot.Height * valueRatio);
            }).ToArray();

            if (points.Length > 1)
            {
                StreamGeometry curve = new StreamGeometry();
                using (StreamGeometryContext context = curve.Open())
                {
                    context.BeginFigure(points[0], isFilled: false, isClosed: false);
                    for (int index = 1; index < points.Length; index++)
                        context.LineTo(points[index], isStroked: true, isSmoothJoin: false);
                }
                curve.Freeze();
                drawingContext.DrawGeometry(null, CurvePen, curve);
            }

            // 单条记录也画一个清楚的圆点，不需要凑出第二条假数据才能看到图。
            double radius = points.Length == 1 ? 3.5 : 1.8;
            foreach (Point point in points)
                drawingContext.DrawEllipse(LineBrush, null, point, radius, radius);
        }

        /// <summary>
        /// DrawAxes 调用，把一个时间标签限制在绘图区宽度内，避免文字跑出边界。
        /// </summary>
        private void DrawTimeLabel(DrawingContext drawingContext, DateTimeOffset time, string format,
            double x, double y, Rect plot, bool centered)
        {
            FormattedText label = CreateText(time.LocalDateTime.ToString(format, CultureInfo.CurrentCulture), 11);
            double left = centered ? x - label.Width / 2 : x;
            left = Math.Clamp(left, plot.Left, Math.Max(plot.Left, plot.Right - label.Width));
            drawingContext.DrawText(label, new Point(left, y));
        }

        /// <summary>
        /// OnRender 在窗口太小或没有数据时调用，只画提示文字。
        /// </summary>
        private void DrawCenteredMessage(DrawingContext drawingContext, string message, double width, double height)
        {
            FormattedText text = CreateText(message);
            text.MaxTextWidth = Math.Max(1, width - 24);
            text.TextAlignment = TextAlignment.Center;
            drawingContext.DrawText(text, new Point(12, Math.Max(4, (height - text.Height) / 2)));
        }

        /// <summary>
        /// 绘制刻度和提示时共用，生成带当前屏幕 DPI 的文字，避免缩放后模糊。
        /// </summary>
        private FormattedText CreateText(string text, double fontSize = 12)
        {
            return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                ChartTypeface, fontSize, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        /// <summary>
        /// 生成纵轴刻度时调用，把数字显示成较短的刻度文字；不改变原始读数。
        /// </summary>
        private static string FormatValue(double value)
        {
            return value.ToString("#,0.##", CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// 创建画图颜色时调用，固定画刷后供多次绘制复用。
        /// </summary>
        private static Brush CreateBrush(byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 创建坐标轴/网格/曲线的画笔时调用，复用颜色和线宽。
        /// </summary>
        private static Pen CreatePen(byte red, byte green, byte blue, double thickness)
        {
            Pen pen = new Pen(CreateBrush(red, green, blue), thickness);
            pen.Freeze();
            return pen;
        }
    }
}

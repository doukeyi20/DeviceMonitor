// 绘制历史读数及时间、数值坐标轴。
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Views
{
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

        // 绘图数据通过绑定传入，查询仍由对应 ViewModel 完成。
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

        /// <summary>自动重画图表：按时间排列记录，计算坐标，再画轴、连线和圆点。</summary>
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

            HistoryRecord[] ordered = records.Take(MaximumRecords)
                .OrderBy(record => record.ReadAt)
                .ThenBy(record => record.Id)
                .ToArray();

            double minimum = ordered.Min(record => record.Value);
            double maximum = ordered.Max(record => record.Value);
            double padding = minimum == maximum ? 1 : (maximum - minimum) * 0.08;
            double lowerBound = minimum - padding;
            double upperBound = maximum + padding;

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
            InvalidateVisual();
        }

        /// <summary>画纵轴刻度、网格线和本机时间横轴。</summary>
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

        /// <summary>把每条记录的时间和数值换成屏幕坐标，再画连线和圆点。</summary>
        private void DrawReadings(DrawingContext drawingContext, Rect plot,
            HistoryRecord[] ordered, double lowerBound, double upperBound)
        {
            long firstTicks = ordered[0].ReadAt.UtcTicks;
            long elapsedTicks = ordered[^1].ReadAt.UtcTicks - firstTicks;
            Point[] points = ordered.Select(record =>
            {
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

            double radius = points.Length == 1 ? 3.5 : 1.8;
            foreach (Point point in points)
                drawingContext.DrawEllipse(LineBrush, null, point, radius, radius);
        }

        /// <summary>画一个时间标签，把文字限制在绘图区宽度内。</summary>
        private void DrawTimeLabel(DrawingContext drawingContext, DateTimeOffset time, string format,
            double x, double y, Rect plot, bool centered)
        {
            FormattedText label = CreateText(time.LocalDateTime.ToString(format, CultureInfo.CurrentCulture), 11);
            double left = centered ? x - label.Width / 2 : x;
            left = Math.Clamp(left, plot.Left, Math.Max(plot.Left, plot.Right - label.Width));
            drawingContext.DrawText(label, new Point(left, y));
        }

        /// <summary>在控件中间画提示文字。</summary>
        private void DrawCenteredMessage(DrawingContext drawingContext, string message, double width, double height)
        {
            FormattedText text = CreateText(message);
            text.MaxTextWidth = Math.Max(1, width - 24);
            text.TextAlignment = TextAlignment.Center;
            drawingContext.DrawText(text, new Point(12, Math.Max(4, (height - text.Height) / 2)));
        }

        /// <summary>按当前屏幕缩放准备绘图文字，避免高分屏上模糊。</summary>
        private FormattedText CreateText(string text, double fontSize = 12)
        {
            return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                ChartTypeface, fontSize, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        /// <summary>把数值换成简短的刻度文字，不改原始读数。</summary>
        private static string FormatValue(double value)
        {
            return value.ToString("#,0.##", CultureInfo.CurrentCulture);
        }

        /// <summary>按 RGB 创建画刷，Freeze 后不再修改，可反复使用。</summary>
        private static Brush CreateBrush(byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        /// <summary>按颜色和线宽创建可重复使用的画笔。</summary>
        private static Pen CreatePen(byte red, byte green, byte blue, double thickness)
        {
            Pen pen = new Pen(CreateBrush(red, green, blue), thickness);
            pen.Freeze();
            return pen;
        }
    }
}

//BEGIN_FILE Chart/ChartRender.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Chart;

public sealed class ChartRender : Control
{
    public double LeftMargin { get; set; } = 0.0;
    public double RightBandWidth { get; set; } = 70.0;
    public double TopMargin { get; set; } = 0.0;
    public double BottomAxisHeight { get; set; } = 21.0;
    public double BasePanelHeight { get; set; } = 210.0;
    public double PanelSeparator { get; set; } = 0.0;

    private static readonly IBrush s_gridBrush = new SolidColorBrush(Color.FromArgb(90, 180, 180, 180));
    private static readonly IBrush s_axisBg = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245));
    private static readonly IBrush s_chipBg = new SolidColorBrush(Color.FromArgb(255, 235, 235, 235));
    private static readonly IBrush s_sepBrush = new SolidColorBrush(Color.FromArgb(208, 208, 208, 208));
    private static readonly IBrush s_valBg = new SolidColorBrush(Color.Parse("#FDF6E3"));
    private static readonly IBrush s_crossBrush = new SolidColorBrush(Color.FromArgb(200, 255, 180, 0));

    private static readonly Typeface s_uiTypeface = new Typeface("Segoe UI");
    private const double s_legendFont = 11.0;
    private static readonly Typeface s_font = new Typeface("Segoe UI");

    public ChartStack? Stack { get; set; }

    private double _vOffset;
    public double VerticalOffset { get => _vOffset; set { if (Math.Abs(_vOffset - value) > double.Epsilon) { _vOffset = Math.Max(0, value); InvalidateVisual(); } } }
    private bool _showCross;
    public bool ShowCrosshair { get => _showCross; set { if (_showCross != value) { _showCross = value; InvalidateVisual(); } } }
    private Point _crossPos;
    public Point CrosshairPosition { get => _crossPos; set { if (_crossPos != value) { _crossPos = value; InvalidateVisual(); } } }

    public override void Render(DrawingContext ctx)
    {
        var bounds = Bounds;
        ctx.FillRectangle(Brushes.White, bounds);

        if (Stack == null || Stack.Stack.Count == 0) { DrawTextCentered(ctx, bounds, "No charts."); return; }
        if (bounds.Width < 50 || bounds.Height < 50) return;

        // 1. Layout
        Rect chartRect = new Rect(LeftMargin, TopMargin, Math.Max(0, bounds.Width - LeftMargin - RightBandWidth), Math.Max(0, bounds.Height - TopMargin - BottomAxisHeight));
        Rect rightBand = new Rect(chartRect.Right, TopMargin, RightBandWidth, chartRect.Height);
        Rect bottomBand = new Rect(LeftMargin, chartRect.Bottom, bounds.Width, BottomAxisHeight);

        ctx.FillRectangle(s_axisBg, rightBand);
        ctx.FillRectangle(s_axisBg, bottomBand);

        bool globalHasData = HasAnyData(Stack);
        var range = Stack.TimeRange;

        double xScale = 1.0;
        double xOffset = 0.0;
        if (range.Span > 0 && chartRect.Width > 0)
        {
            xScale = chartRect.Width / range.Span;
            xOffset = chartRect.Left - range.Start * xScale;
        }

        if (!double.IsFinite(xScale) || !double.IsFinite(xOffset)) { xScale = 1.0; xOffset = 0.0; }

        var panels = BuildPanels(Stack, chartRect);
        var ticks = ChartTimeUtils.GenerateTimeTicks(range.Start, range.End, chartRect.Width);

        using (ctx.PushClip(chartRect))
        {
            // Draw Separators
            var sepPen = new Pen(s_sepBrush, 2.0);
            for (int i = 0; i < panels.Count - 1; i++)
            {
                var p = panels[i];
                double y = p.Rect.Bottom + 1.0;
                ctx.DrawLine(sepPen, new Point(chartRect.Left, y), new Point(chartRect.Right, y));
            }

            // Draw Vertical Grid
            if (globalHasData && range.Span > 0)
                DrawVerticalGrid(ctx, ticks, chartRect.Top, chartRect.Bottom, xScale, xOffset);

            // Render Panels
            foreach (var p in panels)
            {
                bool panelHasData = p.Chart.Series.Any(s => s.Count > 0 && s.IsVisible);

                if (!panelHasData)
                {
                    DrawTextCentered(ctx, p.PlotRect, "Chart is empty");
                    continue;
                }

                var leftSeries = p.Chart.Series.Where(s => s.AxisSide == YAxisSide.Left).ToList();
                var rightSeries = p.Chart.Series.Where(s => s.AxisSide == YAxisSide.Right).ToList();

                AxisAlgo.FitAxisToData(p.Chart.LeftAxis, leftSeries, range.Start, range.End, p.PlotRect.Height);
                AxisAlgo.FitAxisToData(p.Chart.RightAxis, rightSeries, range.Start, range.End, p.PlotRect.Height);

                // Fix: Draw Horizontal Grid only for the primary axis (Right if used, else Left)
                if (rightSeries.Count > 0)
                {
                    DrawHorizontalGrid(ctx, p.Chart.RightAxis, p.PlotRect);
                }
                else
                {
                    DrawHorizontalGrid(ctx, p.Chart.LeftAxis, p.PlotRect);
                }

                var sc = new SeriesRenderContext { DrawingContext = ctx, PlotRect = p.PlotRect, XMin = range.Start, XMax = range.End, XScale = xScale, XOffset = xOffset };

                foreach (var s in p.Chart.Series.OrderBy(s => -(int)s.FileType))
                {
                    if (!s.IsVisible || s.Count == 0) continue;

                    var ax = p.Chart.GetAxis(s.AxisSide);
                    if (ax.Span <= 1e-9) continue;

                    sc.YMin = ax.Minimum; sc.YMax = ax.Maximum;
                    sc.YScale = p.PlotRect.Height / ax.Span;

                    if (!double.IsFinite(sc.YScale)) continue;

                    s.Render(ref sc);
                }

                DrawLegend(ctx, p.Chart, p.PlotRect);
            }
        }

        // 3. Draw Axes & Overlay
        foreach (var p in panels)
        {
            bool panelHasData = p.Chart.Series.Any(s => s.Count > 0 && s.IsVisible);
            if (panelHasData)
            {
                var labelAxis = p.Chart.Series.Any(s => s.AxisSide == YAxisSide.Right) ? p.Chart.RightAxis : p.Chart.LeftAxis;
                DrawYAxisLabels(ctx, labelAxis, p.PlotRect, rightBand);
                DrawLastValues(ctx, p.Chart, p.PlotRect, range.Start, range.End, rightBand);
            }
        }

        if (globalHasData && range.Span > 0)
            DrawXAxis(ctx, bottomBand, chartRect, ticks, xScale, xOffset);

        if (_showCross)
            DrawCrosshair(ctx, panels, range, xScale, xOffset);
    }

    private void DrawLegend(DrawingContext ctx, Chart panel, Rect plot)
    {
        double x = plot.Left + 4.0;
        double y = plot.Top + 2.0;

        foreach (ISeries s in panel.Series)
        {
            if (!s.IsVisible) continue;

            double lineY = y + 6.0;
            ctx.DrawLine(new Pen(s.Stroke, 2.0), new Point(x, lineY), new Point(x + 12.0, lineY));

            var text = new FormattedText(
                s.Name,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                s_uiTypeface,
                s_legendFont,
                Brushes.Black);

            ctx.DrawText(text, new Point(x + 16.0, y));
            x += 16.0 + text.Width + 12.0;

            if (x > plot.Right - 80.0)
                break;
        }
    }


    private void DrawVerticalGrid(DrawingContext ctx, ChartTimeUtils.TimeTicks ticks, double top, double bottom, double scale, double offset)
    {
        var pen = new Pen(s_gridBrush, 1.0);
        foreach (var t in ticks.Values)
        {
            double x = t * scale + offset;
            ctx.DrawLine(pen, new Point(x, top), new Point(x, bottom));
        }
    }

    private void DrawHorizontalGrid(DrawingContext ctx, YAxis axis, Rect plot)
    {
        if (axis.Span <= 0) return;
        var pen = new Pen(s_gridBrush, 1.0);
        double step = axis.TickStep;
        double scale = plot.Height / axis.Span;
        double start = Math.Ceiling(axis.Minimum / step) * step;

        for (double v = start; v <= axis.Maximum + double.Epsilon; v += step)
        {
            double y = plot.Bottom - ((v - axis.Minimum) * scale);
            if (y > plot.Top + 0.5 && y < plot.Bottom - 0.5)
                ctx.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
    }

    private void DrawYAxisLabels(DrawingContext ctx, YAxis axis, Rect plot, Rect band)
    {
        if (axis.Span <= 0) return;
        double step = axis.TickStep;
        double scale = plot.Height / axis.Span;
        double start = Math.Ceiling(axis.Minimum / step) * step;

        using (ctx.PushClip(band))
        {
            for (double v = start; v <= axis.Maximum + double.Epsilon; v += step)
            {
                double y = plot.Bottom - ((v - axis.Minimum) * scale);
                if (y < plot.Top - 1.0 || y > plot.Bottom + 1.0) continue;

                var txt = new FormattedText(v.ToString("N2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_font, 13, Brushes.Black);
                double chipY = y - txt.Height / 2;

                var chip = new Rect(band.Left + 2, chipY, Math.Max(40, txt.Width + 4), txt.Height);
                ctx.FillRectangle(s_chipBg, chip);
                ctx.DrawText(txt, new Point(band.Left + 4, chipY));
            }
        }
    }

    private void DrawLastValues(DrawingContext ctx, Chart chart, Rect plot, double xMin, double xMax, Rect band)
    {
        foreach (var s in chart.Series)
        {
            if (!s.IsVisible) continue;
            // Fix: Capture overrideBrush (e.g. for Buy/Sell color)
            if (!s.TryGetLastRenderedPoint(xMin, xMax, out _, out double yVal, out var overrideBrush)) continue;

            var axis = chart.GetAxis(s.AxisSide);
            double y = plot.Bottom - ((yVal - axis.Minimum) * (plot.Height / axis.Span));

            if (y < band.Top || y > band.Bottom) continue;

            var brush = overrideBrush ?? s.Stroke;
            var txt = new FormattedText(yVal.ToString("N2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_font, 13, brush);
            txt.SetFontWeight(FontWeight.Bold);
            double chipY = y - txt.Height / 2;

            if (chipY < band.Top) chipY = band.Top;
            if (chipY + txt.Height > band.Bottom) chipY = band.Bottom - txt.Height;

            var chip = new Rect(band.Left + 2, chipY, txt.Width + 4, txt.Height);
            ctx.FillRectangle(s_valBg, chip);
            ctx.DrawText(txt, new Point(band.Left + 4, chipY));
        }
    }

    private void DrawXAxis(DrawingContext ctx, Rect band, Rect chart, ChartTimeUtils.TimeTicks ticks, double scale, double offset)
    {
        var tickPen = new Pen(s_gridBrush, 1.0);
        var borderPen = new Pen(s_sepBrush, 1.0);
        ctx.DrawLine(borderPen, band.TopLeft, band.TopRight);

        for (int i = 0; i < ticks.Values.Length; i++)
        {
            double px = ticks.Values[i] * scale + offset;
            if (px < chart.Left || px > band.Right) continue;

            ctx.DrawLine(tickPen, new Point(px, band.Top), new Point(px, band.Top + 5));
            var txt = new FormattedText(ChartTimeUtils.FormatDateLabel(ticks.Values[i], ticks.Unit, ticks.Step), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_font, 13, Brushes.Black);

            ctx.DrawText(txt, new Point(px, band.Top + (band.Height - txt.Height) / 2));
        }
    }

    private void DrawCrosshair(DrawingContext ctx, List<PanelLayout> panels, TimeRange range, double scale, double offset)
    {
        Point pos = CrosshairPosition;
        for (int i = 0; i < panels.Count; i++)
        {
            var p = panels[i];
            if (!p.PlotRect.Contains(pos)) continue;

            bool panelHasData = p.Chart.Series.Any(s => s.Count > 0 && s.IsVisible);
            if (!panelHasData) return;

            double bottomY = p.PlotRect.Bottom;
            if (i < panels.Count - 1)
            {
                bottomY += PanelSeparator;
            }

            ctx.DrawLine(new Pen(s_crossBrush, 1.0), new Point(pos.X, p.PlotRect.Top), new Point(pos.X, bottomY));
            ctx.DrawLine(new Pen(s_crossBrush, 1.0), new Point(p.PlotRect.Left, pos.Y), new Point(p.PlotRect.Right, pos.Y));

            double k = (pos.X - p.PlotRect.Left) / Math.Max(1.0, p.PlotRect.Width);
            double dataX = range.Start + k * range.Span;

            var head = new FormattedText(ChartTimeUtils.FormatTimestampFull(dataX), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_font, 13, Brushes.Orange);
            var lines = new List<FormattedText>();
            double w = head.Width, h = head.Height;
            foreach (var s in p.Chart.Series)
            {
                if (!s.IsVisible) continue;
                if (s.TryGetNearest(dataX, range.Start, range.End, out _, out double ny))
                {
                    var ft = new FormattedText($"{s.Name}: {ny:0.###}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_font, 13, Brushes.White);
                    lines.Add(ft);
                    if (ft.Width > w) w = ft.Width;
                }
            }
            if (lines.Count == 0) return;

            double boxH = h * (lines.Count + 1) + 6;
            double bx = pos.X + 10, by = pos.Y + 10;
            if (bx + w + 8 > Bounds.Right) bx = pos.X - w - 14;
            if (by + boxH > Bounds.Bottom) by = pos.Y - boxH - 6;

            Rect bg = new Rect(bx, by, w + 8, boxH);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(220, 10, 10, 10)), bg);
            ctx.DrawRectangle(null, new Pen(s_crossBrush, 1.0), new RoundedRect(bg));

            ctx.DrawText(head, new Point(bx + 4, by + 2));
            double ty = by + 2 + h;
            foreach (var ln in lines) { ctx.DrawText(ln, new Point(bx + 4, ty)); ty += h; }
            return;
        }
    }

    private void DrawTextCentered(DrawingContext ctx, Rect r, string msg)
    {
        var txt = new FormattedText(msg, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_font, 14, Brushes.Gray);
        ctx.DrawText(txt, new Point(r.Center.X - txt.Width / 2, r.Center.Y - txt.Height / 2));
    }

    internal List<PanelLayout> GetPanelLayouts()
    {
        if (Stack == null) return new List<PanelLayout>();
        Rect chartRect = new Rect(LeftMargin, TopMargin, Math.Max(0, Bounds.Width - LeftMargin - RightBandWidth), Math.Max(0, Bounds.Height - TopMargin - BottomAxisHeight));
        return BuildPanels(Stack, chartRect);
    }

    public Chart? GetPanelAt(Point p)
    {
        var panels = GetPanelLayouts();
        foreach (var pl in panels) if (pl.Rect.Contains(p)) return pl.Chart;
        return null;
    }

    private List<PanelLayout> BuildPanels(ChartStack stack, Rect chart)
    {
        var list = new List<PanelLayout>();
        int count = stack.Stack.Count;
        double totalH = count * BasePanelHeight + (count - 1) * PanelSeparator;
        double factor = totalH < chart.Height ? chart.Height / totalH : 1.0;
        double h = BasePanelHeight * factor;
        double sep = PanelSeparator * factor;
        double y = chart.Top - VerticalOffset;

        foreach (var c in stack.Stack)
        {
            list.Add(new PanelLayout(c, new Rect(chart.Left, y, chart.Width, h)));
            y += h + sep;
        }
        return list;
    }

    private static bool HasAnyData(ChartStack stack)
    {
        foreach (var panel in stack.Stack)
            foreach (var series in panel.Series)
                if (series.IsVisible && series.Count > 0) return true;
        return false;
    }

    internal record struct PanelLayout(Chart Chart, Rect Rect) { public Rect PlotRect => Rect; }
}
//END_FILE Chart/ChartRender.cs
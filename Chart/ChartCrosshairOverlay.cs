//BEGIN_FILE Chart/ChartCrosshairOverlay.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Chart;

public sealed class ChartCrosshairOverlay : Control
{
    public ChartRender? Plot { get; set; }
    public ChartStack? Stack { get; set; }

    private bool _showCrosshair;
    public bool ShowCrosshair
    {
        get => _showCrosshair;
        set { if (_showCrosshair != value) { _showCrosshair = value; InvalidateVisual(); } }
    }

    private Point _crosshairPosition;
    public Point CrosshairPosition
    {
        get => _crosshairPosition;
        set { if (_crosshairPosition != value) { _crosshairPosition = value; if (_showCrosshair) InvalidateVisual(); } }
    }

    private FillHitInfo? _highlightedFill;
    public FillHitInfo? HighlightedFill
    {
        get => _highlightedFill;
        set { _highlightedFill = value; InvalidateVisual(); }
    }

    private const double s_labelFont = 13.0;
    private static readonly Typeface s_uiTypeface = new Typeface("Segoe UI");
    private static readonly IBrush s_crosshairBrush = new SolidColorBrush(Color.FromArgb(200, 255, 180, 0));
    private static readonly IBrush s_tooltipBackground = new SolidColorBrush(Color.FromArgb(220, 10, 10, 10));

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        if (HighlightedFill.HasValue)
        {
            DrawFillHighlight(ctx, HighlightedFill.Value);

            if (ShowCrosshair)
            {
                DrawCrosshairLines(ctx);
            }
            return;
        }

        if (!ShowCrosshair) return;

        var plot = Plot;
        var stack = Stack ?? plot?.Stack;
        if (plot == null || stack == null || stack.Stack.Count == 0) return;

        var bounds = Bounds;
        var r = stack.TimeRange;
        Rect chart = new Rect(plot.LeftMargin, plot.TopMargin, System.Math.Max(0.0, bounds.Width - plot.LeftMargin - plot.RightBandWidth), System.Math.Max(0.0, bounds.Height - plot.TopMargin - plot.BottomAxisHeight));

        if (chart.Width <= 0.0 || chart.Height <= 0.0) return;

        DrawCrosshairLines(ctx);

        Point pos = CrosshairPosition;
        if (chart.Contains(pos))
        {
            var panels = BuildPanels(stack, plot, chart);
            foreach (var t in panels)
            {
                if (t.PlotRect.Contains(pos))
                {
                    DrawTooltipForPanel(ctx, pos, t.Panel, t.PlotRect, r.Start, r.End, r.Span, bounds);
                    break;
                }
            }
        }
    }

    private void DrawCrosshairLines(DrawingContext ctx)
    {
        var plot = Plot;
        if (plot == null) return;
        var bounds = Bounds;
        Rect chart = new Rect(plot.LeftMargin, plot.TopMargin, System.Math.Max(0.0, bounds.Width - plot.LeftMargin - plot.RightBandWidth), System.Math.Max(0.0, bounds.Height - plot.TopMargin - plot.BottomAxisHeight));
        Point pos = CrosshairPosition;

        if (chart.Contains(pos))
        {
            var linePen = new Pen(s_crosshairBrush, 1.0);
            double vx = System.Math.Max(chart.Left, System.Math.Min(chart.Right, pos.X));
            ctx.DrawLine(linePen, new Point(vx, chart.Top), new Point(vx, chart.Bottom));

            var stack = Stack ?? Plot?.Stack;
            if (stack != null)
            {
                var panels = BuildPanels(stack, Plot!, chart);
                foreach (var p in panels)
                {
                    if (p.PlotRect.Contains(pos))
                    {
                        double hLeft = System.Math.Max(p.PlotRect.Left, chart.Left);
                        double hRight = System.Math.Min(p.PlotRect.Right, chart.Right);
                        ctx.DrawLine(linePen, new Point(hLeft, pos.Y), new Point(hRight, pos.Y));
                        break;
                    }
                }
            }
        }
    }

    private void DrawFillHighlight(DrawingContext ctx, FillHitInfo hit)
    {
        // Calculate brighter color locally to avoid referencing Widget project
        Color originalColor = (hit.Stroke as ISolidColorBrush)?.Color ?? Colors.White;
        Color brightColor = Lighten(originalColor, 0.3); // 30% brighter
        var brightBrush = new SolidColorBrush(brightColor);

        // Size: 30% larger than original marker
        double size = hit.MarkerSize * 1.5;
        double px = hit.ScreenX;
        double py = hit.ScreenY;

        var geometry = new StreamGeometry();
        using (var gCtx = geometry.Open())
        {
            if (hit.Quantity > 0) // Buy -> Up
            {
                gCtx.BeginFigure(new Point(px, py - size), true);
                gCtx.LineTo(new Point(px + size, py + size));
                gCtx.LineTo(new Point(px - size, py + size));
                gCtx.EndFigure(true);
            }
            else // Sell -> Down
            {
                gCtx.BeginFigure(new Point(px, py + size), true);
                gCtx.LineTo(new Point(px + size, py - size));
                gCtx.LineTo(new Point(px - size, py - size));
                gCtx.EndFigure(true);
            }
        }

        ctx.DrawGeometry(brightBrush, null, geometry);

        // Tooltip rendering
        double bx = hit.ScreenX + 15;
        double by = hit.ScreenY + 15;

        var head = new FormattedText(
            ChartTimeUtils.FormatTimestampFull(hit.Timestamp),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            s_uiTypeface,
            s_labelFont,
            Brushes.Orange);

        var info = new FormattedText(
            $"{hit.FillType} {(hit.Quantity > 0 ? "Buy" : "Sell")} {Math.Abs(hit.Quantity)} @ {hit.Price}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            s_uiTypeface,
            s_labelFont,
            Brushes.White);

        double w = Math.Max(head.Width, info.Width);
        double h = head.Height + info.Height + 4;

        if (bx + w + 8 > Bounds.Right) bx = hit.ScreenX - w - 15;
        if (by + h + 8 > Bounds.Bottom) by = hit.ScreenY - h - 15;

        Rect bg = new Rect(bx, by, w + 16, h + 8);
        ctx.FillRectangle(s_tooltipBackground, bg);
        ctx.DrawRectangle(null, new Pen(s_crosshairBrush, 1.0), new RoundedRect(bg));

        ctx.DrawText(head, new Point(bx + 8, by + 4));
        ctx.DrawText(info, new Point(bx + 8, by + 4 + head.Height));
    }

    private static Color Lighten(Color color, double factor)
    {
        return Color.FromRgb(
            (byte)Math.Min(255, color.R + (255 - color.R) * factor),
            (byte)Math.Min(255, color.G + (255 - color.G) * factor),
            (byte)Math.Min(255, color.B + (255 - color.B) * factor));
    }

    private static List<(Chart Panel, Rect PanelRect, Rect PlotRect)> BuildPanels(
        ChartStack stack,
        ChartRender plot,
        Rect chart)
    {
        int count = stack.Stack.Count;
        var panels = new List<(Chart, Rect, Rect)>(count);
        if (count == 0) return panels;

        double baseTotal = count * plot.BasePanelHeight + Math.Max(0, count - 1) * plot.PanelSeparator;
        double stretch = baseTotal < chart.Height && baseTotal > 0.0
            ? chart.Height / baseTotal
            : 1.0;
        double ph = plot.BasePanelHeight * stretch;
        double sep = plot.PanelSeparator * stretch;

        double y = chart.Top - plot.VerticalOffset;

        for (int i = 0; i < count; i++)
        {
            var p = stack.Stack[i];
            Rect panelRect = new Rect(chart.Left, y, chart.Width, ph);
            double gap = (i < count - 1) ? sep : 0.0;
            double plotHeight = ph - gap;
            Rect plotRect = new Rect(chart.Left, y, chart.Width, plotHeight);

            panels.Add((p, panelRect, plotRect));
            y += ph;
            if (i < count - 1) y += sep;
        }

        return panels;
    }

    private void DrawTooltipForPanel(
        DrawingContext ctx,
        Point pos,
        Chart panel,
        Rect plotRect,
        double xMin,
        double xMax,
        double span,
        Rect bounds)
    {
        var linePen = new Pen(s_crosshairBrush, 1.0);
        double k = (pos.X - plotRect.Left) / Math.Max(1.0, plotRect.Width);
        double dataX = xMin + k * span;

        var head = new FormattedText(
            ChartTimeUtils.FormatTimestampFull(dataX),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            s_uiTypeface,
            s_labelFont,
            Brushes.Orange);

        var lines = new List<FormattedText>();
        double boxWidth = head.Width;
        double lineHeight = head.Height;

        foreach (ISeries s in panel.Series)
        {
            if (!s.IsVisible) continue;
            if (s.TryGetNearest(dataX, xMin, xMax, out _, out double ny))
            {
                var ft = new FormattedText(
                    $"{s.Name}: {ny:0.###}",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    s_uiTypeface,
                    s_labelFont,
                    Brushes.White);

                lines.Add(ft);
                if (ft.Width > boxWidth) boxWidth = ft.Width;
            }
        }

        if (lines.Count == 0) return;

        double boxHeight = lineHeight * (lines.Count + 1) + 6.0;
        double bx = pos.X + 10.0;
        double by = pos.Y + 10.0;

        if (bx + boxWidth + 8.0 > bounds.Right) bx = pos.X - boxWidth - 14.0;
        if (by + boxHeight > bounds.Bottom) by = pos.Y - boxHeight - 6.0;

        Rect bg = new Rect(bx, by, boxWidth + 8.0, boxHeight);
        ctx.FillRectangle(s_tooltipBackground, bg);
        ctx.DrawRectangle(null, linePen, new RoundedRect(bg));

        double ty = by + 2.0;
        ctx.DrawText(head, new Point(bx + 4.0, ty));
        ty += lineHeight;

        for (int i = 0; i < lines.Count; i++)
        {
            ctx.DrawText(lines[i], new Point(bx + 4.0, ty));
            ty += lineHeight;
        }
    }
}
//END_FILE Chart/ChartCrosshairOverlay.cs
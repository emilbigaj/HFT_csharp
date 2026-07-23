//BEGIN_FILE Chart/PointSeries.cs
using System;
using System.Buffers;
using Avalonia;
using Avalonia.Media;
using Tools;

namespace Chart;

public sealed class PointSeries : ISeries, IDisposable
{
    private static readonly ArrayPool<double> s_pool = ArrayPool<double>.Shared;
    private double[] _xs;
    private double[] _ys;
    private int _length;

    public Data.FileType FileType => Data.FileType.Point;
    public string Name { get; }
    public bool IsVisible { get; set; } = true;
    public bool IsTick { get; set; } = true;
    public YAxisSide AxisSide { get; }
    public IBrush Stroke { get; }
    public double StrokeThickness { get; }
    public int Count => _length;
    public event Action<ISeries, double>? DataAppended;
    private bool _disposed = false;

    public PointSeries(string name, IBrush stroke, double strokeThickness = 1.0, YAxisSide axisSide = YAxisSide.Right, int initialCapacity = 1024)
    {
        Name = name ?? "";
        Stroke = stroke ?? Brushes.Black;
        StrokeThickness = strokeThickness;
        AxisSide = axisSide;
        _xs = s_pool.Rent(initialCapacity);
        _ys = s_pool.Rent(initialCapacity);
    }

    public void Append(double x, double y)
    {
        if (_disposed)
            return;
        if (!double.IsFinite(y))
            return;
        if (_xs.Length <= _length) Expand();
        if (_length > 0 && x < _xs[_length - 1]) return;
        _xs[_length] = x;
        _ys[_length] = y;
        _length++;
        DataAppended?.Invoke(this, x);
    }

    public void Append(Timestamp t, double y) => Append((double)t.NanosSinceEpoch, y);

    private void Expand()
    {
        int newSize = _xs.Length * 2;
        var nx = s_pool.Rent(newSize); var ny = s_pool.Rent(newSize);
        Array.Copy(_xs, nx, _length); Array.Copy(_ys, ny, _length);
        s_pool.Return(_xs); s_pool.Return(_ys);
        _xs = nx; _ys = ny;
    }

    public bool TryGetDomain(out double minX, out double maxX)
    {
        if (_length == 0) { minX = 0; maxX = 0; return false; }
        minX = _xs[0]; maxX = _xs[_length - 1];
        return true;
    }

    public bool TryGetRange(double xMin, double xMax, out double yMin, out double yMax)
    {
        yMin = 0; yMax = 0;
        if (!IsVisible || _length == 0) return false;

        int startIndex = MathUtils.BinarySearchLeft(_xs, _length, xMin);
        int endIndex = MathUtils.BinarySearchRight(_xs, _length, xMax);

        bool hasVisiblePoints = (endIndex >= startIndex);

        if (!hasVisiblePoints)
        {
            if (IsTick && startIndex > 0)
            {
                double val = _ys[startIndex - 1];
                {
                    yMin = val;
                    yMax = val;
                    return true;
                }
            }
            return false;
        }

        if (startIndex > 0) startIndex--;
        if (endIndex < _length) endIndex++;

        double mn = double.MaxValue, mx = double.MinValue;
        bool found = false;

        for (int i = startIndex; i < endIndex && i < _length; i++)
        {
            double v = _ys[i];
            if (v < mn) mn = v;
            if (v > mx) mx = v;
            found = true;
        }

        if (!found) return false;
        yMin = mn; yMax = mx;
        return true;
    }

    public void Render(ref SeriesRenderContext ctx)
    {
        if (!IsVisible || _length <= 1) return;
        if (ctx.XMax <= ctx.XMin || ctx.YMax <= ctx.YMin) return;

        int start = MathUtils.BinarySearchLeft(_xs, _length, ctx.XMin);
        int end = MathUtils.BinarySearchRight(_xs, _length, ctx.XMax);

        if (start > 0) start--;
        if (end < _length) end++;
        if (!IsTick && end < _length) end++;

        if (end - start <= 0) return;

        var pen = new Pen(Stroke, StrokeThickness);
        int pxWidth = (int)ctx.PlotRect.Width;
        int count = end - start;

        if (count <= pxWidth * 2)
            RenderPolyline(ctx.DrawingContext, pen, ctx.PlotRect, ctx.XMin, ctx.YMin, ctx.XScale, ctx.XOffset, ctx.YScale, start, end);
        else
            RenderMinMax(ctx.DrawingContext, pen, ctx.PlotRect, ctx.XScale, ctx.XOffset, ctx.YMin, ctx.YScale, start, end);
    }

    private void RenderPolyline(DrawingContext dc, Pen pen, Rect plot, double xMin, double yMin, double xScale, double xOffset, double yScale, int start, int end)
    {
        bool hasPrev = false;
        Point prev = default;

        int limit = _length;
        if (end > limit) end = limit;

        for (int i = start; i < end; i++)
        {
            if (i >= limit)
                break;

            double px = (_xs[i] * xScale) + xOffset;
            double py = plot.Bottom - ((_ys[i] - yMin) * yScale);
            Point pt = new Point(px, py);

            if (px < plot.Left) { prev = pt; hasPrev = true; continue; }

            if (hasPrev)
            {
                if (IsTick)
                {
                    Point corner = new Point(pt.X, prev.Y);
                    if (pt.X > plot.Right)
                    {
                        Point clamped = new Point(plot.Right, prev.Y);
                        dc.DrawLine(pen, prev, clamped);
                        prev = clamped;
                        break;
                    }
                    dc.DrawLine(pen, prev, corner);
                    dc.DrawLine(pen, corner, pt);
                }
                else
                {
                    dc.DrawLine(pen, prev, pt);
                }
            }
            prev = pt; hasPrev = true;
            if (px > plot.Right) break;
        }

        if (IsTick && hasPrev && prev.X < plot.Right)
            dc.DrawLine(pen, prev, new Point(plot.Right, prev.Y));
    }

    private void RenderMinMax(DrawingContext dc, Pen pen, Rect plot, double xScale, double xOffset, double yMin, double yScale, int start, int end)
    {
        bool hasCur = false;

        int curPx = int.MinValue;
        double curMin = double.MaxValue;
        double curMax = double.MinValue;
        double curOpen = 0;
        double curClose = 0;

        bool hasPrev = false;
        int prevPx = int.MinValue;
        double prevClose = 0;

        int count = end - start;
        int step = 1;

        int limit = _length;
        if (end > limit) end = limit;

        for (int i = start; i < end; i += step)
        {
            if (i >= limit) break;

            int px = (int)((_xs[i] * xScale) + xOffset);
            double val = _ys[i];

            if (hasCur && px == curPx)
            {
                if (val < curMin) curMin = val;
                if (val > curMax) curMax = val;
                curClose = val;
            }
            else
            {
                if (hasCur)
                {
                    DrawConnectedColumn(dc, pen, plot, curPx, curMin, curMax, curOpen, yMin, yScale, hasPrev, prevPx, prevClose);
                    hasPrev = true;
                    prevPx = curPx;
                    prevClose = curClose;
                }

                if (px > plot.Right + 2)
                {
                    hasCur = false;
                    break;
                }

                hasCur = true;
                curPx = px;
                curMin = val;
                curMax = val;
                curOpen = val;
                curClose = val;
            }
        }

        if (hasCur)
        {
            DrawConnectedColumn(dc, pen, plot, curPx, curMin, curMax, curOpen, yMin, yScale, hasPrev, prevPx, prevClose);
        }
    }

    private void DrawConnectedColumn(
        DrawingContext dc, Pen pen, Rect plot,
        int px, double min, double max, double open,
        double yMin, double yScale,
        bool hasPrev, int prevPx, double prevClose)
    {
        double plotBottom = plot.Bottom;
        double yColMin = plotBottom - ((min - yMin) * yScale);
        double yColMax = plotBottom - ((max - yMin) * yScale);
        double yOpen = plotBottom - ((open - yMin) * yScale);

        Point pTop = new Point(px + 0.5, yColMax);
        Point pBot = new Point(px + 0.5, yColMin);

        if (hasPrev)
        {
            double yPrevClose = plotBottom - ((prevClose - yMin) * yScale);
            Point pPrev = new Point(prevPx + 0.5, yPrevClose);

            if (IsTick)
            {
                Point corner = new Point(px + 0.5, yPrevClose);
                dc.DrawLine(pen, pPrev, corner);
                dc.DrawLine(pen, corner, new Point(px + 0.5, yOpen));
            }
            else
            {
                dc.DrawLine(pen, pPrev, new Point(px + 0.5, yOpen));
            }
        }

        dc.DrawLine(pen, pBot, pTop);
    }

    public bool TryGetLastVisible(double xMin, double xMax, out double x, out double y) => throw new NotImplementedException();

    public bool TryGetLastRenderedPoint(double xMin, double xMax, out double x, out double y, out IBrush? overrideBrush)
    {
        overrideBrush = null;
        x = 0.0; y = 0.0;
        if (!IsVisible || _length == 0) return false;

        int idx = MathUtils.BinarySearchRight(_xs, _length, xMax);

        if (idx < 0) return false;

        if (IsTick)
        {
            x = _xs[idx];
            y = _ys[idx];
            return true;
        }

        int end = idx;
        if (end < _length - 1 && _xs[end] < xMax) end++;
        x = _xs[idx]; y = _ys[idx];
        return true;
    }

    public bool TryGetNearest(double x, double xMin, double xMax, out double nx, out double ny)
    {
        nx = 0; ny = 0;
        if (!IsVisible || _length == 0) return false;
        int idx = MathUtils.BinarySearchLeft(_xs, _length, x);
        if (idx >= _length) idx = _length - 1;

        if (idx > 0 && Math.Abs(_xs[idx - 1] - x) < Math.Abs(_xs[idx] - x)) idx--;

        if (_xs[idx] >= xMin && _xs[idx] <= xMax) { nx = _xs[idx]; ny = _ys[idx]; return true; }
        return false;
    }

    public void Dispose() { }
}
//END_FILE Chart/PointSeries.cs
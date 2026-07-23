using System;
using System.Buffers;
using Avalonia;
using Avalonia.Media;
using Data;
using Tools;
using Point = Avalonia.Point;

namespace Chart;

public sealed class HistogramSeries : ISeries, IDisposable
{
    private static readonly ArrayPool<double> s_pool = ArrayPool<double>.Shared;

    private double[] _startXs; // Start times (ns)
    private double[] _endXs;   // End times (ns)
    private double[] _ys;      // Values
    private int _length;
    private bool _disposed;

    public FileType FileType => FileType.Histogram;
    public string Name { get; }
    public bool IsVisible { get; set; } = true;
    public bool IsTick { get; set; } = false;
    public YAxisSide AxisSide { get; }

    public IBrush UpFill { get; }
    public IBrush DownFill { get; }
    public IBrush Stroke => UpFill;
    public double StrokeThickness { get; } = 1.0;

    public int Count => _length;
    public event Action<ISeries, double>? DataAppended;

    public HistogramSeries(
        string name,
        IBrush upFill,
        IBrush downFill,
        YAxisSide axisSide = YAxisSide.Right,
        int initialCapacity = 1024)
    {
        Name = name ?? "";
        UpFill = upFill ?? Brushes.Green;
        DownFill = downFill ?? Brushes.Red;
        AxisSide = axisSide;

        _startXs = s_pool.Rent(initialCapacity);
        _endXs = s_pool.Rent(initialCapacity);
        _ys = s_pool.Rent(initialCapacity);
    }

    public void Append(Histogram h)
    {
        if (_disposed) return;
        double start = (double)h.Opened.NanosSinceEpoch;

        // Ensure strictly increasing start times for binary search integrity
        if (_length > 0 && start < _startXs[_length - 1]) return;
        if (_startXs.Length <= _length) Expand();

        _startXs[_length] = start;
        _endXs[_length] = (double)h.Closed.NanosSinceEpoch;
        _ys[_length] = h.Value;

        _length++;
        DataAppended?.Invoke(this, start);
    }

    private void Expand()
    {
        int newSize = _startXs.Length * 2;
        var ns = s_pool.Rent(newSize);
        var ne = s_pool.Rent(newSize);
        var ny = s_pool.Rent(newSize);

        Array.Copy(_startXs, ns, _length);
        Array.Copy(_endXs, ne, _length);
        Array.Copy(_ys, ny, _length);

        ReturnArrays();

        _startXs = ns;
        _endXs = ne;
        _ys = ny;
    }

    public bool TryGetDomain(out double minX, out double maxX)
    {
        if (_length == 0) { minX = 0; maxX = 0; return false; }
        minX = _startXs[0];
        maxX = _endXs[_length - 1];
        return true;
    }

    public bool TryGetRange(double xMin, double xMax, out double yMin, out double yMax)
    {
        yMin = 0; yMax = 0;
        if (!IsVisible || _length == 0) return false;

        int start = MathUtils.BinarySearchLeft(_startXs, _length, xMin);
        int end = MathUtils.BinarySearchRight(_startXs, _length, xMax);

        if (end < start) return false;
        if (start > 0) start--;
        if (end < _length) end++;

        // Histogram baseline is always 0
        double min = 0;
        double max = 0;
        bool found = false;

        for (int i = start; i < end && i < _length; i++)
        {
            double v = _ys[i];
            if (v < min) min = v;
            if (v > max) max = v;
            found = true;
        }

        if (!found) return false;
        yMin = min; yMax = max;
        return true;
    }

    public void Render(ref SeriesRenderContext ctx)
    {
        if (!IsVisible || _length == 0) return;
        if (ctx.XMax <= ctx.XMin || ctx.YMax <= ctx.YMin) return;

        int start = MathUtils.BinarySearchLeft(_startXs, _length, ctx.XMin);
        int end = MathUtils.BinarySearchRight(_startXs, _length, ctx.XMax);

        if (start > 0) start--;
        if (end < _length) end++;
        if (end - start <= 0) return;

        int pxWidth = (int)ctx.PlotRect.Width;
        int count = end - start;

        // Optimization: If the number of bars exceeds 2x the pixel width, 
        // we switch to MinMax aggregation to avoid overdraw and maintain performance.
        if (count > pxWidth * 2)
        {
            RenderMinMax(ctx.DrawingContext, ctx.PlotRect, ctx.XScale, ctx.XOffset, ctx.YMin, ctx.YScale, start, end);
        }
        else
        {
            RenderBars(ctx.DrawingContext, ctx.PlotRect, ctx.XScale, ctx.XOffset, ctx.YMin, ctx.YScale, start, end);
        }
    }

    private void RenderBars(DrawingContext dc, Rect plot, double xScale, double xOffset, double yMin, double yScale, int start, int end)
    {
        double zeroY = plot.Bottom - ((0.0 - yMin) * yScale);

        for (int i = start; i < end; i++)
        {
            if (i >= _length) break;

            double startPx = (_startXs[i] * xScale) + xOffset;
            double endPx = (_endXs[i] * xScale) + xOffset;
            double widthPx = endPx - startPx;

            // Visual separation:
            // If the bar is wide enough, subtract 1px gap. 
            // Otherwise, ensure at least 1px width so it doesn't disappear.
            double renderWidth = Math.Max(1.0, widthPx - 1.0);

            // Center the visual bar within the time slot
            double centerOffset = (widthPx - renderWidth) * 0.5;
            double drawX = startPx + centerOffset;

            if (drawX + renderWidth < plot.Left) continue;
            if (drawX > plot.Right) break;

            double val = _ys[i];
            double valY = plot.Bottom - ((val - yMin) * yScale);

            // Draw from Zero Line to Value
            double top = Math.Min(zeroY, valY);
            double height = Math.Abs(zeroY - valY);
            if (height < 1.0) height = 1.0; // Ensure 1px height visibility

            var brush = val >= 0 ? UpFill : DownFill;
            dc.FillRectangle(brush, new Rect(drawX, top, renderWidth, height));
        }
    }

    private void RenderMinMax(DrawingContext dc, Rect plot, double xScale, double xOffset, double yMin, double yScale, int start, int end)
    {
        // Aggregation Logic for Efficiency:
        // 1. Map time ranges to horizontal pixels.
        // 2. Track the Max Positive and Min Negative value for each pixel bucket.
        // 3. Draw a single vertical line per pixel representing the full range of activity.

        double zeroY = plot.Bottom - ((0.0 - yMin) * yScale);
        bool hasCur = false;
        int curPx = int.MinValue;

        double bucketMaxPos = 0;
        double bucketMinNeg = 0;

        for (int i = start; i < end; i++)
        {
            if (i >= _length) break;

            // Use the midpoint of the histogram bar to determine its pixel bucket
            double midX = (_startXs[i] + _endXs[i]) * 0.5;
            int px = (int)((midX * xScale) + xOffset);

            if (hasCur && px == curPx)
            {
                // We are still in the same pixel column, aggregate values
                double v = _ys[i];
                if (v > bucketMaxPos) bucketMaxPos = v;
                if (v < bucketMinNeg) bucketMinNeg = v;
            }
            else
            {
                // New pixel column reached, draw the previous bucket
                if (hasCur)
                {
                    DrawAggregateBar(dc, plot, curPx, bucketMaxPos, bucketMinNeg, zeroY, yMin, yScale);
                }

                if (px > plot.Right)
                {
                    hasCur = false;
                    break;
                }

                // Reset bucket for new pixel
                hasCur = true;
                curPx = px;
                double v = _ys[i];
                bucketMaxPos = v > 0 ? v : 0;
                bucketMinNeg = v < 0 ? v : 0;
            }
        }

        // Draw the final bucket
        if (hasCur)
        {
            DrawAggregateBar(dc, plot, curPx, bucketMaxPos, bucketMinNeg, zeroY, yMin, yScale);
        }
    }

    private void DrawAggregateBar(
        DrawingContext dc, Rect plot,
        int px, double maxPos, double minNeg,
        double zeroY, double yMin, double yScale)
    {
        // Draw Positive Extension (Green)
        if (maxPos > 0)
        {
            double yTop = plot.Bottom - ((maxPos - yMin) * yScale);
            double height = zeroY - yTop;
            if (height < 1.0) height = 1.0;
            dc.FillRectangle(UpFill, new Rect(px, yTop, 1.0, height));
        }

        // Draw Negative Extension (Red)
        if (minNeg < 0)
        {
            double yBot = plot.Bottom - ((minNeg - yMin) * yScale);
            double height = yBot - zeroY;
            if (height < 1.0) height = 1.0;
            dc.FillRectangle(DownFill, new Rect(px, zeroY, 1.0, height));
        }
    }

    public bool TryGetLastRenderedPoint(double xMin, double xMax, out double x, out double y, out IBrush? overrideBrush)
    {
        x = 0; y = 0; overrideBrush = null;
        if (!IsVisible || _length == 0) return false;

        int idx = MathUtils.BinarySearchRight(_startXs, _length, xMax);
        if (idx < 0) return false;

        x = _endXs[idx]; // Snap to end of the interval
        y = _ys[idx];
        overrideBrush = y >= 0 ? UpFill : DownFill;
        return true;
    }

    public bool TryGetNearest(double x, double xMin, double xMax, out double nearestX, out double nearestY)
    {
        nearestX = 0; nearestY = 0;
        if (!IsVisible || _length == 0) return false;

        // Find the index where start <= x
        int idx = MathUtils.BinarySearchLeft(_startXs, _length, x);
        if (idx >= _length) idx = _length - 1;

        if (idx > 0 && x < _startXs[idx]) idx--;

        // Check if cursor X falls strictly within the histogram bar's time range
        if (_startXs[idx] <= x && x <= _endXs[idx])
        {
            // Position tooltip in the center of the bar
            nearestX = (_startXs[idx] + _endXs[idx]) * 0.5;
            nearestY = _ys[idx];
            return true;
        }

        return false;
    }

    public bool TryGetLastVisible(double xMin, double xMax, out double x, out double y)
    {
        return TryGetLastRenderedPoint(xMin, xMax, out x, out y, out _);
    }

    private void ReturnArrays()
    {
        if (_startXs != null) s_pool.Return(_startXs);
        if (_endXs != null) s_pool.Return(_endXs);
        if (_ys != null) s_pool.Return(_ys);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ReturnArrays();
        }
    }
}
//BEGIN_FILE Chart/CandleSeries.cs
using System;
using System.Buffers;
using Avalonia;
using Avalonia.Media;
using Data;
using Tools;
using Point = Avalonia.Point;

namespace Chart;

public sealed class CandleSeries : ISeries, IDisposable
{
    private static readonly ArrayPool<double> s_pool = ArrayPool<double>.Shared;

    private double[] _widths;
    private double[] _xs;
    private double[] _opens;
    private double[] _highs;
    private double[] _lows;
    private double[] _closes;

    private int _length;
    private bool _disposed;

    public FileType FileType => FileType.Candle;
    public string Name { get; }
    public bool IsVisible { get; set; } = true;
    public bool IsTick { get; set; } = false;
    public YAxisSide AxisSide { get; }

    public IBrush UpStroke { get; }
    public IBrush DownStroke { get; }
    public IBrush Stroke => UpStroke;
    public double StrokeThickness { get; } = 1.0;

    public double CandleWidth { get; set; } = 5.0;

    public int Count => _length;
    public event Action<ISeries, double>? DataAppended;

    public CandleSeries(
        string name,
        IBrush upStroke,
        IBrush downStroke,
        YAxisSide axisSide = YAxisSide.Right,
        int initialCapacity = 1024)
    {
        Name = name ?? "";
        UpStroke = upStroke ?? Brushes.Green;
        DownStroke = downStroke ?? Brushes.Red;
        AxisSide = axisSide;

        _xs = s_pool.Rent(initialCapacity);
        _opens = s_pool.Rent(initialCapacity);
        _highs = s_pool.Rent(initialCapacity);
        _lows = s_pool.Rent(initialCapacity);
        _closes = s_pool.Rent(initialCapacity);
        _widths = s_pool.Rent(initialCapacity);
    }

    public void Append(Candle c)
    {
        if (_disposed) return;

        double start = (double)c.Opened.NanosSinceEpoch;
        double end = (double)c.Closed.NanosSinceEpoch;
        double width = end - start;

        if (_length > 0 && start < _xs[_length - 1]) return;
        if (_xs.Length <= _length) Expand();

        _xs[_length] = start;
        _widths[_length] = width;
        _opens[_length] = c.Open;
        _highs[_length] = c.High;
        _lows[_length] = c.Low;
        _closes[_length] = c.Close;

        _length++;
        DataAppended?.Invoke(this, start);
    }

    private void Expand()
    {
        int newSize = _xs.Length * 2;

        var nx = s_pool.Rent(newSize);
        var no = s_pool.Rent(newSize);
        var nh = s_pool.Rent(newSize);
        var nl = s_pool.Rent(newSize);
        var nc = s_pool.Rent(newSize);
        var nw = s_pool.Rent(newSize);

        Array.Copy(_widths, nw, _length);
        Array.Copy(_xs, nx, _length);
        Array.Copy(_opens, no, _length);
        Array.Copy(_highs, nh, _length);
        Array.Copy(_lows, nl, _length);
        Array.Copy(_closes, nc, _length);

        ReturnArrays();

        _xs = nx; _opens = no; _highs = nh; _lows = nl; _closes = nc; _widths = nw;
    }

    public bool TryGetDomain(out double minX, out double maxX)
    {
        if (_length == 0) { minX = 0; maxX = 0; return false; }
        minX = _xs[0];
        maxX = _xs[_length - 1];
        return true;
    }

    public bool TryGetRange(double xMin, double xMax, out double yMin, out double yMax)
    {
        yMin = 0; yMax = 0;
        if (!IsVisible || _length == 0) return false;

        int start = MathUtils.BinarySearchLeft(_xs, _length, xMin);
        int end = MathUtils.BinarySearchRight(_xs, _length, xMax);

        if (end < start) return false;

        if (start > 0) start--;
        if (end < _length) end++;

        double min = double.MaxValue;
        double max = double.MinValue;
        bool found = false;

        for (int i = start; i < end && i < _length; i++)
        {
            if (_lows[i] < min) min = _lows[i];
            if (_highs[i] > max) max = _highs[i];
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

        int start = MathUtils.BinarySearchLeft(_xs, _length, ctx.XMin);
        int end = MathUtils.BinarySearchRight(_xs, _length, ctx.XMax);

        if (start > 0) start--;
        if (end < _length) end++;

        if (end - start <= 0) return;

        int pxWidth = (int)ctx.PlotRect.Width;
        int count = end - start;

        if (count > pxWidth * 2)
        {
            RenderMinMax(ctx.DrawingContext, ctx.PlotRect, ctx.XScale, ctx.XOffset, ctx.YMin, ctx.YScale, start, end);
        }
        else
        {
            RenderCandles(ctx.DrawingContext, ctx.PlotRect, ctx.XScale, ctx.XOffset, ctx.YMin, ctx.YScale, start, end);
        }
    }

    private void RenderCandles(DrawingContext dc, Rect plot, double xScale, double xOffset, double yMin, double yScale, int start, int end)
    {
        var upPen = new Pen(UpStroke, StrokeThickness);
        var downPen = new Pen(DownStroke, StrokeThickness);

        for (int i = start; i < end; i++)
        {
            if (i >= _length) break;

            double startPx = (_xs[i] * xScale) + xOffset;
            double widthPx = _widths[i] * xScale;

            if (startPx + widthPx < plot.Left) continue;
            if (startPx > plot.Right) break;

            double o = _opens[i];
            double c = _closes[i];
            double h = _highs[i];
            double l = _lows[i];

            double yHigh = plot.Bottom - ((h - yMin) * yScale);
            double yLow = plot.Bottom - ((l - yMin) * yScale);
            double yOpen = plot.Bottom - ((o - yMin) * yScale);
            double yClose = plot.Bottom - ((c - yMin) * yScale);

            bool isUp = c >= o;
            var brush = isUp ? UpStroke : DownStroke;
            var pen = isUp ? upPen : downPen;

            double centerX = startPx + (widthPx * 0.5);

            dc.DrawLine(pen, new Point(centerX, yHigh), new Point(centerX, yLow));

            double bodyTop = Math.Min(yOpen, yClose);
            double bodyHeight = Math.Abs(yClose - yOpen);
            if (bodyHeight < 1.0) bodyHeight = 1.0;

            double renderWidth = Math.Max(1.0, widthPx);

            var bodyRect = new Rect(startPx, bodyTop, renderWidth, bodyHeight);
            dc.FillRectangle(brush, bodyRect);
        }
    }

    private void RenderMinMax(DrawingContext dc, Rect plot, double xScale, double xOffset, double yMin, double yScale, int start, int end)
    {
        bool hasCur = false;
        int curPx = int.MinValue;

        double bucketHigh = double.MinValue;
        double bucketLow = double.MaxValue;
        double bucketOpen = 0;
        double bucketClose = 0;

        var upPen = new Pen(UpStroke, 1.0);
        var downPen = new Pen(DownStroke, 1.0);

        for (int i = start; i < end; i++)
        {
            if (i >= _length) break;

            int px = (int)((_xs[i] * xScale) + xOffset);

            if (hasCur && px == curPx)
            {
                if (_highs[i] > bucketHigh) bucketHigh = _highs[i];
                if (_lows[i] < bucketLow) bucketLow = _lows[i];
                bucketClose = _closes[i];
            }
            else
            {
                if (hasCur)
                {
                    DrawAggregateCandle(dc, plot, curPx, bucketOpen, bucketHigh, bucketLow, bucketClose, yMin, yScale, upPen, downPen);
                }

                if (px > plot.Right)
                {
                    hasCur = false;
                    break;
                }

                hasCur = true;
                curPx = px;
                bucketOpen = _opens[i];
                bucketClose = _closes[i];
                bucketHigh = _highs[i];
                bucketLow = _lows[i];
            }
        }

        if (hasCur)
        {
            DrawAggregateCandle(dc, plot, curPx, bucketOpen, bucketHigh, bucketLow, bucketClose, yMin, yScale, upPen, downPen);
        }
    }

    private void DrawAggregateCandle(
        DrawingContext dc, Rect plot,
        int px, double open, double high, double low, double close,
        double yMin, double yScale,
        Pen upPen, Pen downPen)
    {
        double yHigh = plot.Bottom - ((high - yMin) * yScale);
        double yLow = plot.Bottom - ((low - yMin) * yScale);
        double yOpen = plot.Bottom - ((open - yMin) * yScale);
        double yClose = plot.Bottom - ((close - yMin) * yScale);

        bool isUp = close >= open;
        Pen pen = isUp ? upPen : downPen;
        dc.DrawLine(pen, new Point(px + 0.5, yHigh), new Point(px + 0.5, yLow));

        double bodyTop = Math.Min(yOpen, yClose);
        double bodyBot = Math.Max(yOpen, yClose);
        if (bodyBot - bodyTop < 1.0) bodyBot = bodyTop + 1.0;

        dc.FillRectangle(pen.Brush!, new Rect(px, bodyTop, 1.0, bodyBot - bodyTop));
    }

    public bool TryGetLastRenderedPoint(double xMin, double xMax, out double x, out double y, out IBrush? overrideBrush)
    {
        overrideBrush = null;
        x = 0; y = 0;
        if (!IsVisible || _length == 0) return false;

        int idx = MathUtils.BinarySearchRight(_xs, _length, xMax);
        if (idx < 0) return false;

        x = _xs[idx];
        y = _closes[idx];
        return true;
    }

    public bool TryGetNearest(double x, double xMin, double xMax, out double nearestX, out double nearestY)
    {
        nearestX = 0; nearestY = 0;
        if (!IsVisible || _length == 0) return false;

        int idx = MathUtils.BinarySearchLeft(_xs, _length, x);
        if (idx >= _length) idx = _length - 1;

        if (idx > 0 && Math.Abs(_xs[idx - 1] - x) < Math.Abs(_xs[idx] - x)) idx--;

        if (_xs[idx] >= xMin && _xs[idx] <= xMax)
        {
            nearestX = _xs[idx];
            nearestY = _closes[idx];
            return true;
        }
        return false;
    }

    public bool TryGetLastVisible(double xMin, double xMax, out double x, out double y)
    {
        // For compatibility with old interface calls, discarding brush
        return TryGetLastRenderedPoint(xMin, xMax, out x, out y, out _);
    }

    private void ReturnArrays()
    {
        if (_xs != null) s_pool.Return(_xs);
        if (_opens != null) s_pool.Return(_opens);
        if (_highs != null) s_pool.Return(_highs);
        if (_lows != null) s_pool.Return(_lows);
        if (_closes != null) s_pool.Return(_closes);
        if (_widths != null) s_pool.Return(_widths);
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
//END_FILE Chart/CandleSeries.cs
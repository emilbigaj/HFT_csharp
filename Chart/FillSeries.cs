//BEGIN_FILE Chart/FillSeries.cs
using System;
using System.Buffers;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Data;
using Tools;
using Point = Avalonia.Point;

namespace Chart;

public struct FillHitInfo
{
    public double Timestamp;
    public double Price;
    public int Quantity;
    public FillType FillType;
    public double ScreenX;
    public double ScreenY;
    public IBrush Stroke;
    public double MarkerSize;
}

public sealed class FillSeries : ISeries, IDisposable
{
    private static readonly ArrayPool<double> s_pool = ArrayPool<double>.Shared;
    private static readonly ArrayPool<byte> s_bytePool = ArrayPool<byte>.Shared;

    private double[] _xs;
    private double[] _ys;
    private double[] _qtys;
    private byte[] _fillTypes;

    private int _length;
    private bool _disposed;

    public FileType FileType => FileType.Fill;
    public string Name { get; }
    public bool IsVisible { get; set; } = true;
    public bool IsTick { get; set; } = false;
    public YAxisSide AxisSide { get; }

    public IBrush BuyStroke { get; set; }
    public IBrush SellStroke { get; set; }
    private static readonly IPen s_borderPen = new Pen(Brushes.Black, 0.5);
    public IBrush BuyInFocusStroke { get; set; }
    public IBrush SellInFocusStroke { get; set; }
    public double MarkerSize { get; set; } = 6.0;

    public IBrush Stroke => BuyStroke;
    public double StrokeThickness { get; } = 1.0;

    public int Count => _length;
    public event Action<ISeries, double>? DataAppended;

    public FillSeries(
        string name,
        IBrush buyStroke,
        IBrush buyInFocusStroke,
        IBrush sellStroke,
        IBrush sellInFocusStroke,
        YAxisSide axisSide = YAxisSide.Right,
        int initialCapacity = 1024)
    {
        Name = name ?? "";
        BuyStroke = buyStroke;
        BuyInFocusStroke = buyInFocusStroke;
        SellStroke = sellStroke;
        SellInFocusStroke = sellInFocusStroke;
        AxisSide = axisSide;

        _xs = s_pool.Rent(initialCapacity);
        _ys = s_pool.Rent(initialCapacity);
        _qtys = s_pool.Rent(initialCapacity);
        _fillTypes = s_bytePool.Rent(initialCapacity);
    }

    public void Append(Filld fill)
    {
        if (_disposed) return;

        double x = (double)fill.Timestamp.NanosSinceEpoch;

        if (_length > 0 && x < _xs[_length - 1]) return;

        if (_xs.Length <= _length) Expand();

        _xs[_length] = x;
        _ys[_length] = fill.Price;
        _qtys[_length] = fill.Quantity;
        _fillTypes[_length] = (byte)fill.FillType;

        _length++;
        DataAppended?.Invoke(this, x);
    }

    private void Expand()
    {
        int newSize = _xs.Length * 2;

        var nx = s_pool.Rent(newSize);
        var ny = s_pool.Rent(newSize);
        var nq = s_pool.Rent(newSize);
        var nft = s_bytePool.Rent(newSize);

        Array.Copy(_xs, nx, _length);
        Array.Copy(_ys, ny, _length);
        Array.Copy(_qtys, nq, _length);
        Array.Copy(_fillTypes, nft, _length);

        ReturnArrays();

        _xs = nx; _ys = ny; _qtys = nq; _fillTypes = nft;
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
            double v = _ys[i];
            if (v < min) min = v;
            if (v > max) max = v;
            found = true;
        }

        if (!found) return false;
        yMin = min; yMax = max;
        return true;
    }

    public bool HitTest(Point p, double hitRadius, Rect plot, double xScale, double xOffset, double yMin, double yScale, out FillHitInfo hit)
    {
        hit = default;
        if (!IsVisible || _length == 0) return false;

        double dataX = (p.X - xOffset) / xScale;
        int idx = MathUtils.BinarySearchLeft(_xs, _length, dataX);

        double minSqDist = hitRadius * hitRadius;
        int foundIdx = -1;

        if (idx >= _length) idx = _length - 1;

        for (int i = idx; i >= 0; i--)
        {
            double px = (_xs[i] * xScale) + xOffset;
            if (p.X - px > hitRadius) break;

            double py = plot.Bottom - ((_ys[i] - yMin) * yScale);
            double dx = p.X - px;
            double dy = p.Y - py;
            double sqDist = dx * dx + dy * dy;

            if (sqDist <= minSqDist)
            {
                minSqDist = sqDist;
                foundIdx = i;
            }
        }

        for (int i = idx + 1; i < _length; i++)
        {
            double px = (_xs[i] * xScale) + xOffset;
            if (px - p.X > hitRadius) break;

            double py = plot.Bottom - ((_ys[i] - yMin) * yScale);
            double dx = p.X - px;
            double dy = p.Y - py;
            double sqDist = dx * dx + dy * dy;

            if (sqDist <= minSqDist)
            {
                minSqDist = sqDist;
                foundIdx = i;
            }
        }

        if (foundIdx != -1)
        {
            double qty = _qtys[foundIdx];
            hit.Timestamp = _xs[foundIdx];
            hit.Price = _ys[foundIdx];
            hit.Quantity = (int)qty;
            hit.FillType = (FillType)_fillTypes[foundIdx];
            hit.ScreenX = (_xs[foundIdx] * xScale) + xOffset;
            hit.ScreenY = plot.Bottom - ((_ys[foundIdx] - yMin) * yScale);
            hit.Stroke = qty > 0 ? BuyInFocusStroke : SellInFocusStroke;
            hit.MarkerSize = MarkerSize;
            return true;
        }

        return false;
    }

    public void Render(ref SeriesRenderContext ctx)
    {
        if (!IsVisible || _length == 0) return;
        if (ctx.XMax <= ctx.XMin || ctx.YMax <= ctx.YMin) return;

        int start = MathUtils.BinarySearchLeft(_xs, _length, ctx.XMin);
        int end = MathUtils.BinarySearchRight(_xs, _length, ctx.XMax);

        // Fix for missing triangles: widen the lookback to catch markers partially on screen
        if (start > 0) start = Math.Max(0, start - 2);
        if (end < _length) end++;
        if (end - start <= 0) return;

        var buyGeometry = new StreamGeometry();
        var sellGeometry = new StreamGeometry();

        // Create Pens (Cache these if possible for performance)
        var buyPen = new Pen(BuyStroke, 1.0);
        var sellPen = new Pen(SellStroke, 1.0);

        // --- OPTIMIZATION: Aggregation ---
        // When zoomed out, thousands of fills may map to the same pixel column.
        // We track occupied Y-slots for the current X-column to avoid overdraw.
        int lastPx = int.MinValue;
        var drawnBuys = new HashSet<int>();
        var drawnSells = new HashSet<int>();

        // Quantization factor for Y. 
        // Using MarkerSize/2 ensures we don't draw markers that overlap significantly.
        double yQuantizer = Math.Max(1.0, MarkerSize * 0.5);

        using (var buyCtx = buyGeometry.Open())
        using (var sellCtx = sellGeometry.Open())
        {
            buyCtx.SetFillRule(FillRule.NonZero);
            sellCtx.SetFillRule(FillRule.NonZero);

            for (int i = start; i < end; i++)
            {
                if (i >= _length) break;

                // FIX 1: Pixel Snapping
                // Snap to nearest whole pixel to avoid anti-aliasing blur on flat edges
                double rawPx = (_xs[i] * ctx.XScale) + ctx.XOffset;
                double rawPy = ctx.PlotRect.Bottom - ((_ys[i] - ctx.YMin) * ctx.YScale);

                // Round to integers for clear pixel alignment and bucket logic
                int px = (int)Math.Round(rawPx);
                int py = (int)Math.Round(rawPy);

                if (px < ctx.PlotRect.Left - MarkerSize || px > ctx.PlotRect.Right + MarkerSize) continue;

                // --- Aggregation Check ---
                // If we moved to a new pixel column, reset the Y-buckets
                if (px != lastPx)
                {
                    drawnBuys.Clear();
                    drawnSells.Clear();
                    lastPx = px;
                }

                // Calculate Y bucket index
                int qy = (int)(py / yQuantizer);
                bool isBuy = _qtys[i] > 0;

                // If this bucket is already occupied by a marker of the same type, skip drawing
                if (isBuy)
                {
                    if (!drawnBuys.Add(qy)) continue;
                }
                else
                {
                    if (!drawnSells.Add(qy)) continue;
                }
                // -------------------------

                double size = MarkerSize;

                // Use center-pixel coordinates for sharp rendering
                double dpx = px;
                double dpy = py;

                var gCtx = isBuy ? buyCtx : sellCtx;

                if (isBuy)
                {
                    gCtx.BeginFigure(new Point(dpx, dpy - size), true);
                    gCtx.LineTo(new Point(dpx + size, dpy + size));
                    gCtx.LineTo(new Point(dpx - size, dpy + size));
                    gCtx.EndFigure(true);
                }
                else
                {
                    gCtx.BeginFigure(new Point(dpx, dpy + size), true);
                    gCtx.LineTo(new Point(dpx + size, dpy - size));
                    gCtx.LineTo(new Point(dpx - size, dpy - size));
                    gCtx.EndFigure(true);
                }
            }
        }

        // FIX 2: Draw with both Fill and Stroke (Pen)
        ctx.DrawingContext.DrawGeometry(BuyStroke, s_borderPen, buyGeometry);
        ctx.DrawingContext.DrawGeometry(SellStroke, s_borderPen, sellGeometry);
    }

    public bool TryGetLastRenderedPoint(double xMin, double xMax, out double x, out double y, out IBrush? overrideBrush)
    {
        x = 0; y = 0; overrideBrush = null;
        if (!IsVisible || _length == 0) return false;

        int idx = MathUtils.BinarySearchRight(_xs, _length, xMax);
        if (idx < 0) return false;

        x = _xs[idx];
        y = _ys[idx];
        // Requirement 4: Color based on Buy/Sell
        overrideBrush = _qtys[idx] > 0 ? BuyStroke : SellStroke;
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
        if (_xs != null) s_pool.Return(_xs);
        if (_ys != null) s_pool.Return(_ys);
        if (_qtys != null) s_pool.Return(_qtys);
        if (_fillTypes != null) s_bytePool.Return(_fillTypes);
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

//END_FILE Chart/FillSeries.cs
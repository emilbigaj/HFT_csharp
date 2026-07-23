using System;
using System.Collections.Generic;
using Tools;

namespace Chart;

public sealed class Chart
{
    private readonly List<ISeries> _series;
    public string Name { get; }
    public YAxis LeftAxis { get; } = new();
    public YAxis RightAxis { get; } = new();
    public IReadOnlyList<ISeries> Series => _series;

    public Chart(string name)
    {
        Name = name ?? "";
        _series = new List<ISeries>(4);
    }

    public void AddSeries(ISeries series)
    {
        if (series == null) throw new ArgumentNullException(nameof(series));
        _series.Add(series);
    }

    public void RemoveSeries(ISeries series)
    {
        if (series == null) return;
        _series.Remove(series);
    }

    public YAxis GetAxis(YAxisSide side) => side == YAxisSide.Left ? LeftAxis : RightAxis;
}

public sealed class ChartStack
{
    private readonly List<Chart> _stack;
    private TimeRange _timeRange;
    private double _domainStart, _domainEnd;
    private bool _hasDomain;
    private bool _viewPinnedToEnd;

    public event Action<Chart>? PanelAdded;
    public event Action<Chart>? PanelRemoved;

    public TimeRange TimeRange => _timeRange;
    public IReadOnlyList<Chart> Stack => _stack;
    public double DomainStart => _hasDomain ? _domainStart : 0.0;
    public double DomainEnd => _hasDomain ? _domainEnd : 0.0;
    public double MinimumViewSpanNs { get; set; } = Duration.NanosecondsPerSecond;

    public ChartStack()
    {
        _stack = new List<Chart>(4);
        _timeRange = new TimeRange(0.0, 0.0);
        _viewPinnedToEnd = true;
    }

    public void AddPanel(Chart panel)
    {
        _stack.Add(panel);
        PanelAdded?.Invoke(panel);
    }

    public void InsertPanel(int index, Chart panel)
    {
        if (index < 0) index = 0;
        if (index > _stack.Count) index = _stack.Count;
        _stack.Insert(index, panel);
        PanelAdded?.Invoke(panel);
    }

    public void RemovePanel(Chart panel)
    {
        if (_stack.Remove(panel))
        {
            PanelRemoved?.Invoke(panel);
            RecalculateDomainFromData();
        }
    }

    public void RecalculateDomainFromData()
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        bool found = false;

        foreach (var panel in _stack)
        {
            foreach (var series in panel.Series)
            {
                if (series.TryGetDomain(out var sMin, out var sMax))
                {
                    if (sMin < min) min = sMin;
                    if (sMax > max) max = sMax;
                    found = true;
                }
            }
        }

        if (!found)
        {
            _hasDomain = false;
            _domainStart = 0.0;
            _domainEnd = 0.0;
            ResetViewToDomain();
            return;
        }

        _hasDomain = true;
        _domainStart = min;
        _domainEnd = max;
        ResetViewToDomain();
    }

    public void ExpandDomainToInclude(double x)
    {
        if (!_hasDomain) { _hasDomain = true; _domainStart = x; _domainEnd = x; ResetViewToDomain(); return; }

        // Check if we are currently max zoomed out (viewing the entire domain)
        double oldDomainSpan = _domainEnd - _domainStart;
        bool wasMaxZoomedOut = oldDomainSpan <= 0.0 || _timeRange.Span >= oldDomainSpan * 0.999999;

        bool extended = false;
        if (x < _domainStart) { _domainStart = x; extended = true; }
        if (x > _domainEnd) { _domainEnd = x; extended = true; }

        if (!extended) { _timeRange.Clamp(_domainStart, _domainEnd, MinimumViewSpanNs); return; }

        if (wasMaxZoomedOut)
        {
            // If we were max zoomed out, stay max zoomed out by expanding the view to the new full domain
            _timeRange = new TimeRange(_domainStart, _domainEnd);
            _viewPinnedToEnd = true;
        }
        else if (_viewPinnedToEnd)
        {
            // If pinned to end (but zoomed in), slide the window
            double span = _timeRange.Span <= 0.0 ? MinimumViewSpanNs : _timeRange.Span;
            _timeRange = new TimeRange(_domainEnd - span, _domainEnd);
        }

        _timeRange.Clamp(_domainStart, _domainEnd, MinimumViewSpanNs);
    }

    public void Pan(double delta)
    {
        if (!_hasDomain || delta == 0.0) return;
        _timeRange.Pan(delta);
        _timeRange.Clamp(_domainStart, _domainEnd, MinimumViewSpanNs);
        _viewPinnedToEnd = false;
    }

    public void Zoom(double factor, double anchor)
    {
        if (!_hasDomain || factor <= 0.0) return;
        _timeRange.Zoom(factor, anchor);
        _timeRange.Clamp(_domainStart, _domainEnd, MinimumViewSpanNs);
        if (_viewPinnedToEnd) _timeRange = new TimeRange(_domainEnd - _timeRange.Span, _domainEnd);
    }

    public void ScrollToEnd()
    {
        if (!_hasDomain) return;
        _timeRange = new TimeRange(_domainEnd - _timeRange.Span, _domainEnd);
        _timeRange.Clamp(_domainStart, _domainEnd, MinimumViewSpanNs);
        _viewPinnedToEnd = true;
    }

    public void ResetViewToDomain()
    {
        // If no data, reset to 0,0 (Invalid). Renderer handles empty state.
        if (!_hasDomain)
        {
            _timeRange = new TimeRange(0.0, 0.0);
            _viewPinnedToEnd = true;
            return;
        }

        double start = _domainStart;
        double end = _domainEnd;
        double currentSpan = end - start;

        // If single point or tiny range, enforce minimum zoom (1 sec)
        // We anchor to the END (Latest data)
        if (currentSpan < MinimumViewSpanNs)
        {
            start = end - MinimumViewSpanNs;
        }

        _timeRange = new TimeRange(start, end);
        _viewPinnedToEnd = true;
    }
}
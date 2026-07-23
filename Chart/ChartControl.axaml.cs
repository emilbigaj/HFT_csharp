//BEGIN_FILE Chart/ChartControl.axaml.cs
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Chart;

public partial class ChartControl : UserControl
{
    public static readonly StyledProperty<ChartStack?> StackProperty =
        AvaloniaProperty.Register<ChartControl, ChartStack?>(nameof(Stack));

    public ChartStack? Stack
    {
        get => GetValue(StackProperty);
        set
        {
            var old = GetValue(StackProperty);
            if (ReferenceEquals(old, value))
            {
                Plot.Stack = value;
                Overlay.Stack = value;
                RefreshFromModel();
                return;
            }

            if (old is ChartStack oldStack)
                DetachStack(oldStack);

            SetValue(StackProperty, value);
            Plot.Stack = value;
            Overlay.Stack = value;

            if (value is ChartStack newStack)
                AttachStack(newStack);

            RefreshFromModel();
        }
    }

    private bool _updatingBars;
    private bool _refreshPending;

    // Horizontal interaction state
    private bool _isPanning;
    private Point _lastPanPoint;

    public ChartControl()
    {
        InitializeComponent();

        // Wire overlay to the plot so it can reuse layout + model state.
        Overlay.Plot = Plot;
        Overlay.Stack = Stack;

        VBar.PropertyChanged += OnVBarChanged;
        HBar.PropertyChanged += OnHBarChanged;

        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(PointerExitedEvent, OnPointerExited, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        AttachedToVisualTree += (_, __) =>
        {
            UpdateScrollbars(Plot.Bounds.Size);
            SnapHBarRightIfViewAtEnd();
        };

        // When the control resizes, recompute scrollbar ranges.
        SizeChanged += (_, __) => UpdateScrollbars(Plot.Bounds.Size);
    }

    /// <summary>
    /// Returns the chart panel at the given position relative to the ChartControl,
    /// or null if no panel is hit.
    /// </summary>
    public Chart? GetPanelAt(Point point)
    {
        var plotPos = point.Transform(Plot.TransformToVisual(this)!.Value);
        return Plot.GetPanelAt(plotPos);
    }

    // ------------------------ Stack Attach/Detach ------------------------
    private void AttachStack(ChartStack stack)
    {
        stack.PanelAdded += OnPanelAdded;
        stack.PanelRemoved += OnPanelRemoved;

        foreach (var panel in stack.Stack)
            AttachPanel(panel);
    }

    private void DetachStack(ChartStack stack)
    {
        stack.PanelAdded -= OnPanelAdded;
        stack.PanelRemoved -= OnPanelRemoved;

        foreach (var panel in stack.Stack)
            DetachPanel(panel);
    }

    private void OnPanelAdded(Chart panel)
    {
        AttachPanel(panel);
        RequestRefresh();
    }

    private void OnPanelRemoved(Chart panel)
    {
        DetachPanel(panel);
        RequestRefresh();
    }

    private void AttachPanel(Chart panel)
    {
        foreach (var s in panel.Series)
            AttachSeries(s);
    }

    private void DetachPanel(Chart panel)
    {
        foreach (var s in panel.Series)
            DetachSeries(s);
    }

    public void AttachSeries(ISeries series)
    {
        series.DataAppended += OnSeriesDataAppended;
    }

    public void DetachSeries(ISeries series)
    {
        series.DataAppended -= OnSeriesDataAppended;
    }

    // ------------------------ Auto Updates ------------------------
    private void OnSeriesDataAppended(ISeries series, double newLastX)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSeriesDataAppended(series, newLastX));
            return;
        }

        var stack = Stack;
        if (stack == null)
            return;

        stack.ExpandDomainToInclude(newLastX);
        RequestRefresh();
    }

    private void RequestRefresh()
    {
        if (_refreshPending)
            return;

        _refreshPending = true;

        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            RefreshFromModel();
        }, DispatcherPriority.Background);
    }

    private void RefreshFromModel()
    {
        UpdateScrollbars(Plot.Bounds.Size);
        Plot.InvalidateVisual();
    }

    // ------------------------ Scrollbars ------------------------
    private void UpdateScrollbars(Size canvasSize)
    {
        ChartStack? stack = Stack;
        _updatingBars = true;

        try
        {
            if (stack == null)
            {
                VBar.Minimum = 0.0;
                VBar.Maximum = 0.0;
                VBar.ViewportSize = 1.0;
                VBar.Value = 0.0;

                HBar.Minimum = 0.0;
                HBar.Maximum = 0.0;
                HBar.ViewportSize = 1.0;
                HBar.Value = 0.0;
                HBar.IsEnabled = false;
                return;
            }

            Rect chart = GetChartRect(canvasSize);

            // -------- Vertical scrollbar (panels) --------
            int count = stack.Stack.Count;
            if (count <= 0 || chart.Height <= 0.0)
            {
                Plot.VerticalOffset = 0.0;
                VBar.Minimum = 0.0;
                VBar.Maximum = 0.0;
                VBar.ViewportSize = 1.0;
                VBar.Value = 0.0;
            }
            else
            {
                double baseTotal = count * Plot.BasePanelHeight +
                                   Math.Max(0, count - 1) * Plot.PanelSeparator;

                double stretch = baseTotal > 0.0 && baseTotal < chart.Height
                    ? chart.Height / baseTotal
                    : 1.0;

                double ph = Plot.BasePanelHeight * stretch;
                double sep = Plot.PanelSeparator * stretch;
                double extent = count * ph + Math.Max(0, count - 1) * sep;
                double viewport = chart.Height;
                double overflow = Math.Max(0.0, extent - viewport);

                if (overflow <= 0.0)
                {
                    Plot.VerticalOffset = 0.0;
                    VBar.Minimum = 0.0;
                    VBar.Maximum = 0.0;
                    VBar.ViewportSize = viewport;
                    VBar.Value = 0.0;
                }
                else
                {
                    if (Plot.VerticalOffset > overflow)
                        Plot.VerticalOffset = overflow;

                    VBar.Minimum = 0.0;
                    VBar.Maximum = overflow;
                    VBar.ViewportSize = viewport;
                    VBar.Value = Plot.VerticalOffset;
                }
            }

            // -------- Horizontal scrollbar (time range) --------
            double domainStart = stack.DomainStart;
            double domainEnd = stack.DomainEnd;
            double domainSpan = domainEnd - domainStart;
            double viewSpan = stack.TimeRange.Span;

            if (domainSpan <= 0.0 || viewSpan <= 0.0 || viewSpan >= domainSpan)
            {
                HBar.Minimum = 0.0;
                HBar.Maximum = 0.0;
                HBar.ViewportSize = 1.0;
                HBar.Value = 0.0;
                HBar.IsEnabled = false;
            }
            else
            {
                HBar.IsEnabled = true;
                double maxOffset = domainSpan - viewSpan;

                HBar.Minimum = 0.0;
                HBar.Maximum = maxOffset;
                HBar.ViewportSize = viewSpan;

                double currentOffset = stack.TimeRange.Start - domainStart;

                if (currentOffset < 0.0) currentOffset = 0.0;
                if (currentOffset > maxOffset) currentOffset = maxOffset;

                HBar.Value = currentOffset;
            }
        }
        finally
        {
            _updatingBars = false;
        }
    }

    private void SnapHBarRightIfViewAtEnd()
    {
        var stack = Stack;
        if (stack == null)
            return;

        double span = stack.TimeRange.Span;
        if (span <= 0.0)
            return;

        double eps = Math.Max(1.0, span * 1e-6);
        if (Math.Abs(stack.TimeRange.End - stack.DomainEnd) <= eps)
        {
            _updatingBars = true;
            try
            {
                HBar.Value = HBar.Maximum;
            }
            finally
            {
                _updatingBars = false;
            }
        }
    }

    private void OnVBarChanged(object? s, AvaloniaPropertyChangedEventArgs e)
    {
        if (_updatingBars || e.Property != RangeBase.ValueProperty)
            return;

        var stack = Stack;
        if (stack == null)
            return;

        Rect chart = GetChartRect(Plot.Bounds.Size);
        int count = stack.Stack.Count;
        if (count <= 0 || chart.Height <= 0.0)
        {
            Plot.VerticalOffset = 0.0;
            return;
        }

        double baseTotal = count * Plot.BasePanelHeight +
                           Math.Max(0, count - 1) * Plot.PanelSeparator;

        double stretch = baseTotal > 0.0 && baseTotal < chart.Height
            ? chart.Height / baseTotal
            : 1.0;

        double ph = Plot.BasePanelHeight * stretch;
        double sep = Plot.PanelSeparator * stretch;
        double extent = count * ph + Math.Max(0, count - 1) * sep;
        double viewport = chart.Height;
        double overflow = Math.Max(0.0, extent - viewport);

        if (overflow <= 0.0)
        {
            Plot.VerticalOffset = 0.0;
            return;
        }

        double val = VBar.Value;
        if (val < 0.0) val = 0.0;
        if (val > overflow) val = overflow;

        Plot.VerticalOffset = val;
        Plot.InvalidateVisual();
    }

    private void OnHBarChanged(object? s, AvaloniaPropertyChangedEventArgs e)
    {
        if (_updatingBars || e.Property != RangeBase.ValueProperty)
            return;

        var stack = Stack;
        if (stack == null)
            return;

        double domainStart = stack.DomainStart;
        double domainEnd = stack.DomainEnd;
        double domainSpan = domainEnd - domainStart;
        double viewSpan = stack.TimeRange.Span;

        if (domainSpan <= 0.0 || viewSpan <= 0.0)
            return;

        double maxOffset = Math.Max(0.0, domainSpan - viewSpan);

        double desiredOffset = HBar.Value;
        if (desiredOffset < 0.0) desiredOffset = 0.0;
        if (desiredOffset > maxOffset) desiredOffset = maxOffset;

        double currentOffset = stack.TimeRange.Start - domainStart;
        double delta = desiredOffset - currentOffset;

        if (Math.Abs(delta) <= double.Epsilon)
            return;

        stack.Pan(delta);
        RequestRefresh();
    }

    // ------------------------ Pointer Helpers ------------------------
    private Rect GetChartRect(Size canvasSize)
    {
        return new Rect(
            Plot.LeftMargin,
            Plot.TopMargin,
            Math.Max(0.0, canvasSize.Width - Plot.LeftMargin - Plot.RightBandWidth),
            Math.Max(0.0, canvasSize.Height - Plot.TopMargin - Plot.BottomAxisHeight));
    }

    private bool IsInChartArea(Point posOnPlot)
    {
        return GetChartRect(Plot.Bounds.Size).Contains(posOnPlot);
    }

    // ------------------------ Pointer Events ------------------------
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var stack = Stack;
        if (stack == null)
            return;

        Point posOnPlot = e.GetPosition(Plot);

        if (IsInChartArea(posOnPlot))
        {
            _isPanning = true;
            _lastPanPoint = posOnPlot;
            Overlay.ShowCrosshair = false;
            Overlay.HighlightedFill = null;

            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else
        {
            double width = Plot.Bounds.Width;
            double height = Plot.Bounds.Height;
            double rightBandLeft = width - Plot.RightBandWidth;
            double bottomAxisTop = height - Plot.BottomAxisHeight;

            if (posOnPlot.X >= rightBandLeft && posOnPlot.X <= width &&
                posOnPlot.Y >= Plot.TopMargin && posOnPlot.Y <= bottomAxisTop)
            {
                stack.ScrollToEnd();
                RefreshFromModel();
                e.Handled = true;
            }
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPanning = false;
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            Overlay.ShowCrosshair = false;
            Overlay.HighlightedFill = null;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var stack = Stack;

        if (_isPanning)
        {
            if (stack == null)
                return;

            Rect chart = GetChartRect(Plot.Bounds.Size);
            if (chart.Width <= 0.0)
                return;

            Point pos = e.GetPosition(Plot);
            double dx = pos.X - _lastPanPoint.X;
            _lastPanPoint = pos;

            if (Math.Abs(dx) > double.Epsilon)
            {
                double span = stack.TimeRange.Span;
                if (span > 0.0)
                {
                    double unitsPerPixel = span / chart.Width;
                    double delta = -dx * unitsPerPixel;
                    stack.Pan(delta);
                    RefreshFromModel();
                }
            }

            Overlay.ShowCrosshair = false;
            Overlay.HighlightedFill = null;
            e.Handled = true;
            return;
        }

        Rect chartRect = GetChartRect(Plot.Bounds.Size);
        Point posOnPlot = e.GetPosition(Plot);
        Point posOnOverlay = e.GetPosition(Overlay);

        if (chartRect.Contains(posOnPlot))
        {
            Overlay.ShowCrosshair = true;
            Overlay.CrosshairPosition = posOnOverlay;

            // --- HIT TEST FOR FILLS ---
            // If stack matches
            if (stack != null && stack.Stack.Count > 0)
            {
                FillHitInfo? bestHit = null;

                // We need to iterate panels to find the one under cursor, then hit test its series
                // We use Plot helper to get the layout rectangles
                var panels = Plot.GetPanelLayouts();
                var range = stack.TimeRange;
                double xScale = 1.0;
                double xOffset = 0.0;
                if (range.Span > 0 && chartRect.Width > 0)
                {
                    xScale = chartRect.Width / range.Span;
                    xOffset = chartRect.Left - range.Start * xScale;
                }

                foreach (var p in panels)
                {
                    if (p.Rect.Contains(posOnPlot))
                    {
                        // Found the panel under cursor
                        // Check its series
                        foreach (var s in p.Chart.Series)
                        {
                            if (s is FillSeries fs && fs.IsVisible)
                            {
                                // Determine Y Scale for this series
                                var axis = p.Chart.GetAxis(s.AxisSide);
                                if (axis.Span > 1e-9)
                                {
                                    double yScale = p.Rect.Height / axis.Span;
                                    double yMin = axis.Minimum;

                                    if (fs.HitTest(posOnPlot, 5.0, p.Rect, xScale, xOffset, yMin, yScale, out var hit))
                                    {
                                        bestHit = hit;
                                        break; // Assuming one hit is enough, or prioritize?
                                    }
                                }
                            }
                        }
                        break; // Cursor is in one panel only
                    }
                }

                Overlay.HighlightedFill = bestHit;
            }
        }
        else
        {
            Overlay.ShowCrosshair = false;
            Overlay.HighlightedFill = null;
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var stack = Stack;

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            Overlay.ShowCrosshair = false;
            Overlay.HighlightedFill = null;
            if (stack == null)
                return;

            const double zoomStep = 1.1;
            double factor = e.Delta.Y > 0 ? 1.0 / zoomStep : zoomStep;

            double anchor = stack.TimeRange.End;
            stack.Zoom(factor, anchor);

            RefreshFromModel();
            e.Handled = true;
        }
        else
        {
            Overlay.ShowCrosshair = false;
            Overlay.HighlightedFill = null;
            Plot.VerticalOffset -= e.Delta.Y * 24.0;
            UpdateScrollbars(Plot.Bounds.Size);
            e.Handled = true;
        }
    }
}
//END_FILE Chart/ChartControl.axaml.cs
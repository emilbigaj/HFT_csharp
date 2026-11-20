using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Tools;

namespace Widget;

/// What the histogram's bars measure. One at a time, so the chart has one y axis; a click on that axis switches.
public enum MarkoutUnit { Ticks, Usd }

/// Draws the Markouts widget's rows as bars, one per row, grouped as the rows are: up from the zero line when the market
/// moved in the fill's favour, down when against. The y axis is labelled in the chosen unit (quantity-weighted ticks, or
/// total USD) and a click on it switches the unit. Each group is named under its bars, with names dropped where they would
/// overlap, and the bar under the pointer is read out in full at the top. UI thread; cold path (see Spec.md "Markouts").
public sealed class MarkoutHistogram : Control
{
    private const double s_axisWidth = 58;          // the strip left of the plot: the y axis labels, and what a click on switches the unit
    private const double s_headerHeight = 22;       // the strip above the plot: the unit on the left, the hovered bar or the durations on the right
    private const double s_labelHeight = 15;        // one row of labels under the plot
    private const double s_padding = 8;
    private const double s_plotInset = 6;           // between the y axis and the first bar, so the bar does not sit against the axis values
    private const double s_fontSize = 11;
    private const double s_maximumBarWidth = 24;    // a bar never fills its slot: the rest is air
    private const double s_barGap = 2;              // between neighbouring bars
    private const double s_narrowSlotWidth = 6;     // under this a 2px gap would leave no bar: the gap drops to 1px
    private const double s_groupGap = 10;           // between one group's bars and the next's
    private const double s_barCornerRadius = 3;     // on a bar's data end; its zero-line end stays square
    private const double s_gridLineSpacing = 44;    // about how far apart the y axis puts its gridlines
    private const double s_labelSpacing = 8;        // the least air between two group names

    private static readonly IBrush s_textBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly IBrush s_mutedTextBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));
    private static readonly IBrush s_hoverBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly IPen s_gridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)));
    private static readonly IPen s_zeroPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)));
    private static readonly Typeface s_boldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Cursor s_handCursor = new(StandardCursorType.Hand);

    private IReadOnlyList<WidgetMarkoutGroup> _widgetMarkoutGroups = [];
    private readonly List<int> _groupFirstRowIndexes = new();   // per group: its first row; the rows arrive group by group
    private int[] _groupIndexes = [];                           // per row: which group it is in
    private string _markoutDurationsText = "";                  // every markout duration on show, shortest first: "1s, 10s, 1m"
    private int _markoutDurationCount;
    private double _widestMarkoutDurationLabelWidth;
    public MarkoutUnit MarkoutUnit { get; set; } = MarkoutUnit.Ticks;   // what the bars measure: a click on the y axis switches it, and the widget saves it
    private int _hoveredRowIndex = -1;                         // the row whose bar the pointer is over; -1 = none
    private bool _isAxisHovered;

    /// Where the plot and its bars sit, for the control's size and rows as they are now.
    private readonly record struct PlotLayout(double Left, double Top, double Width, double Height, double SlotWidth, double BarWidth, double GroupGap, bool HasMarkoutDurationLabels)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
        public double GetSlotLeft(int rowIndex, int groupIndex) => Left + s_plotInset + rowIndex * SlotWidth + groupIndex * GroupGap;
    }

    /// The y axis: round numbers that cover every bar and zero, and how its labels are written.
    private readonly record struct ValueAxis(double Minimum, double Maximum, double Step, double LabelScale, string LabelSuffix, int LabelDecimals)
    {
        public double GetY(double value, in PlotLayout plotLayout) => plotLayout.Top + (Maximum - value) / (Maximum - Minimum) * plotLayout.Height;
        public string NewLabel(double value) => (value / LabelScale).ToString("N" + LabelDecimals, CultureInfo.CurrentCulture) + LabelSuffix;
    }

    /// Shows these rows as bars. The rows come group by group, each group's markout durations shortest first.
    public void Show(IReadOnlyList<WidgetMarkoutGroup> widgetMarkoutGroups)
    {
        // Step 1: the rows, and where each group starts
        _widgetMarkoutGroups = widgetMarkoutGroups;
        _groupFirstRowIndexes.Clear();
        _groupIndexes = new int[widgetMarkoutGroups.Count];
        SortedSet<Duration> markoutDurations = new();
        for (int rowIndex = 0; rowIndex < widgetMarkoutGroups.Count; rowIndex++)
        {
            bool isNewGroup = rowIndex == 0 || widgetMarkoutGroups[rowIndex].Group != widgetMarkoutGroups[rowIndex - 1].Group;
            if (isNewGroup)
                _groupFirstRowIndexes.Add(rowIndex);
            _groupIndexes[rowIndex] = _groupFirstRowIndexes.Count - 1;
            markoutDurations.Add(widgetMarkoutGroups[rowIndex].MarkoutDuration);
        }

        // Step 2: the markout durations on show, and how wide their widest label is
        List<string> markoutDurationLabels = new();
        _widestMarkoutDurationLabelWidth = 0;
        foreach (Duration markoutDuration in markoutDurations)
        {
            string markoutDurationLabel = NewMarkoutDurationLabel(markoutDuration);
            markoutDurationLabels.Add(markoutDurationLabel);
            _widestMarkoutDurationLabelWidth = Math.Max(_widestMarkoutDurationLabelWidth, NewText(markoutDurationLabel, s_mutedTextBrush).Width);
        }
        _markoutDurationCount = markoutDurations.Count;
        _markoutDurationsText = string.Join(", ", markoutDurationLabels);
        _hoveredRowIndex = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext drawingContext)
    {
        // Step 1: the whole control takes the pointer, not only where something is drawn
        drawingContext.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        // Step 2: nothing to plot says so
        PlotLayout plotLayout = NewPlotLayout();
        bool hasPlot = _widgetMarkoutGroups.Count > 0 && plotLayout.Width > 0 && plotLayout.Height > 0;
        if (!hasPlot)
        {
            FormattedText emptyText = NewText("No markouts for this selection", s_mutedTextBrush);
            drawingContext.DrawText(emptyText, new Point((Bounds.Width - emptyText.Width) / 2, (Bounds.Height - emptyText.Height) / 2));
            return;
        }

        // Step 3: the axis and its gridlines, the bars over them, then the names under and the header above
        ValueAxis valueAxis = NewValueAxis(in plotLayout);
        DrawValueAxis(drawingContext, in plotLayout, in valueAxis);
        DrawBars(drawingContext, in plotLayout, in valueAxis);
        DrawLabels(drawingContext, in plotLayout);
        DrawHeader(drawingContext, in plotLayout);
    }

    /// The plot's place and its bars' widths: a slot per row, a wider gap between groups, and room under the plot for the names.
    private PlotLayout NewPlotLayout()
    {
        // Step 1: across: a slot per row, and a gap between groups no wider than a slot
        double width = Bounds.Width - s_axisWidth - s_padding;
        int rowCount = Math.Max(_widgetMarkoutGroups.Count, 1);
        int groupGapCount = Math.Max(_groupFirstRowIndexes.Count - 1, 0);
        double slotsWidth = width - s_plotInset;
        double groupGap = groupGapCount > 0 ? Math.Min(s_groupGap, slotsWidth / rowCount) : 0;
        double slotWidth = (slotsWidth - groupGap * groupGapCount) / rowCount;
        double barGap = slotWidth >= s_narrowSlotWidth ? s_barGap : 1;
        double barWidth = Math.Clamp(slotWidth - barGap, 1, s_maximumBarWidth);

        // Step 2: down: a row of group names, and a row of markout durations when each fits under its own bar
        bool hasMarkoutDurationLabels = _markoutDurationCount > 1 && _widestMarkoutDurationLabelWidth + 2 <= slotWidth;
        double labelsHeight = s_labelHeight * (hasMarkoutDurationLabels ? 2 : 1) + s_padding / 2;
        double height = Bounds.Height - s_headerHeight - labelsHeight;
        return new PlotLayout(s_axisWidth, s_headerHeight, width, height, slotWidth, barWidth, groupGap, hasMarkoutDurationLabels);
    }

    /// The y axis for the chosen unit: from the lowest bar or zero to the highest bar or zero, widened to round steps.
    private ValueAxis NewValueAxis(in PlotLayout plotLayout)
    {
        // Step 1: the range the bars span, zero included
        double _minimum = 0;
        double _maximum = 0;
        foreach (WidgetMarkoutGroup widgetMarkoutGroup in _widgetMarkoutGroups)
        {
            double value = GetValue(widgetMarkoutGroup);
            if (double.IsNaN(value))
                continue;
            _minimum = Math.Min(_minimum, value);
            _maximum = Math.Max(_maximum, value);
        }
        if (_maximum == _minimum)
            _maximum = _minimum + 1;

        // Step 2: a step of 1, 2 or 5 times a power of ten, about one gridline per s_gridLineSpacing
        double roughStep = (_maximum - _minimum) / Math.Max(plotLayout.Height / s_gridLineSpacing, 2);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        double fraction = roughStep / magnitude;
        double step = (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * magnitude;
        double minimum = Math.Floor(_minimum / step) * step;
        double maximum = Math.Ceiling(_maximum / step) * step;

        // Step 3: labels in full under 10,000, in thousands or millions from there
        double largest = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
        double labelScale = largest >= 10_000_000 ? 1_000_000 : largest >= 10_000 ? 1_000 : 1;
        string labelSuffix = labelScale == 1_000_000 ? "M" : labelScale == 1_000 ? "K" : "";
        double scaledStep = step / labelScale;
        int labelDecimals = scaledStep >= 1 ? 0 : Math.Min((int)Math.Ceiling(-Math.Log10(scaledStep)), 4);
        return new ValueAxis(minimum, maximum, step, labelScale, labelSuffix, labelDecimals);
    }

    /// The gridlines with their values down the left, and the zero line a shade stronger.
    private void DrawValueAxis(DrawingContext drawingContext, in PlotLayout plotLayout, in ValueAxis valueAxis)
    {
        if (_isAxisHovered)
            drawingContext.FillRectangle(s_hoverBrush, new Rect(0, plotLayout.Top, plotLayout.Left - s_padding / 2, plotLayout.Height));
        int stepCount = (int)Math.Round((valueAxis.Maximum - valueAxis.Minimum) / valueAxis.Step);
        for (int stepIndex = 0; stepIndex <= stepCount; stepIndex++)
        {
            double value = valueAxis.Minimum + stepIndex * valueAxis.Step;
            bool isZero = Math.Abs(value) < valueAxis.Step / 2;
            double lineY = Math.Round(valueAxis.GetY(isZero ? 0 : value, in plotLayout)) + 0.5;
            drawingContext.DrawLine(isZero ? s_zeroPen : s_gridPen, new Point(plotLayout.Left, lineY), new Point(plotLayout.Right, lineY));
            FormattedText valueText = NewText(valueAxis.NewLabel(isZero ? 0 : value), s_mutedTextBrush);
            drawingContext.DrawText(valueText, new Point(plotLayout.Left - s_padding - valueText.Width, lineY - valueText.Height / 2));
        }
    }

    /// A bar per row from the zero line to its value, green above and red below, with the hovered row's slot shaded behind it.
    private void DrawBars(DrawingContext drawingContext, in PlotLayout plotLayout, in ValueAxis valueAxis)
    {
        double zeroY = Math.Round(valueAxis.GetY(0, in plotLayout));
        bool hasRoundedEnds = plotLayout.BarWidth >= 2 * s_barCornerRadius + 2;
        for (int rowIndex = 0; rowIndex < _widgetMarkoutGroups.Count; rowIndex++)
        {
            double slotLeft = plotLayout.GetSlotLeft(rowIndex, _groupIndexes[rowIndex]);
            if (rowIndex == _hoveredRowIndex)
                drawingContext.FillRectangle(s_hoverBrush, new Rect(slotLeft, plotLayout.Top, plotLayout.SlotWidth, plotLayout.Height));
            double value = GetValue(_widgetMarkoutGroups[rowIndex]);
            if (double.IsNaN(value) || value == 0)
                continue;

            // Why at least a pixel: a bar too small to scale still shows which side of zero it is on
            double valueY = Math.Round(valueAxis.GetY(value, in plotLayout));
            double barHeight = Math.Max(Math.Abs(valueY - zeroY), 1);
            double barTop = value > 0 ? zeroY - barHeight : zeroY + 1;
            Rect barRect = new(Math.Round(slotLeft + (plotLayout.SlotWidth - plotLayout.BarWidth) / 2), barTop, Math.Max(Math.Round(plotLayout.BarWidth), 1), barHeight);
            IBrush barBrush = Palette.GetProfitBrush(value);
            if (!hasRoundedEnds)
            {
                drawingContext.FillRectangle(barBrush, barRect);
                continue;
            }

            // Why clipped: the rounded rectangle runs past the zero line so that only the data end shows its corners
            using (drawingContext.PushClip(barRect))
            {
                Rect roundedRect = value > 0 ? barRect.WithHeight(barRect.Height + s_barCornerRadius) : new Rect(barRect.X, barRect.Y - s_barCornerRadius, barRect.Width, barRect.Height + s_barCornerRadius);
                drawingContext.DrawRectangle(barBrush, null, roundedRect, s_barCornerRadius, s_barCornerRadius);
            }
        }
    }

    /// Under the plot: each bar's markout duration when every one fits, then each group's name, dropping names that would overlap.
    private void DrawLabels(DrawingContext drawingContext, in PlotLayout plotLayout)
    {
        // Step 1: the markout duration under each bar
        double labelsTop = plotLayout.Bottom + s_padding / 2;
        if (plotLayout.HasMarkoutDurationLabels)
        {
            for (int rowIndex = 0; rowIndex < _widgetMarkoutGroups.Count; rowIndex++)
            {
                FormattedText markoutDurationText = NewText(NewMarkoutDurationLabel(_widgetMarkoutGroups[rowIndex].MarkoutDuration), s_mutedTextBrush);
                double slotCentre = plotLayout.GetSlotLeft(rowIndex, _groupIndexes[rowIndex]) + plotLayout.SlotWidth / 2;
                drawingContext.DrawText(markoutDurationText, new Point(slotCentre - markoutDurationText.Width / 2, labelsTop));
            }
            labelsTop += s_labelHeight;
        }

        // Step 2: each group's name centred under its bars, kept inside the plot and clear of the name before it
        double _previousLabelRight = double.NegativeInfinity;
        for (int groupIndex = 0; groupIndex < _groupFirstRowIndexes.Count; groupIndex++)
        {
            int firstRowIndex = _groupFirstRowIndexes[groupIndex];
            int nextFirstRowIndex = groupIndex + 1 < _groupFirstRowIndexes.Count ? _groupFirstRowIndexes[groupIndex + 1] : _widgetMarkoutGroups.Count;
            double groupLeft = plotLayout.GetSlotLeft(firstRowIndex, groupIndex);
            double groupRight = plotLayout.GetSlotLeft(nextFirstRowIndex - 1, groupIndex) + plotLayout.SlotWidth;
            FormattedText groupText = NewText(_widgetMarkoutGroups[firstRowIndex].Group, s_textBrush);
            double labelLeft = Math.Clamp((groupLeft + groupRight - groupText.Width) / 2, plotLayout.Left, Math.Max(plotLayout.Right - groupText.Width, plotLayout.Left));
            if (labelLeft < _previousLabelRight + s_labelSpacing)
                continue;
            drawingContext.DrawText(groupText, new Point(labelLeft, labelsTop));
            _previousLabelRight = labelLeft + groupText.Width;
        }
    }

    /// Above the plot: the unit and how to switch it on the left; on the right the hovered bar in full, or else the markout durations on show.
    private void DrawHeader(DrawingContext drawingContext, in PlotLayout plotLayout)
    {
        // Step 1: the unit, over the axis it labels
        bool isTicks = MarkoutUnit == MarkoutUnit.Ticks;
        FormattedText unitText = NewText(isTicks ? "Ticks" : "USD", s_textBrush, s_boldTypeface);
        double headerTextTop = (s_headerHeight - unitText.Height) / 2;
        drawingContext.DrawText(unitText, new Point(s_padding, headerTextTop));
        double unitRight = s_padding + unitText.Width;

        // Step 2: the hovered bar, or which markout durations the bars are
        string rightString = _markoutDurationCount == 1 ? _markoutDurationsText + " markout" : plotLayout.HasMarkoutDurationLabels ? "" : "each group, left to right: " + _markoutDurationsText;
        IBrush rightBrush = s_mutedTextBrush;
        if (_hoveredRowIndex >= 0)
        {
            WidgetMarkoutGroup widgetMarkoutGroup = _widgetMarkoutGroups[_hoveredRowIndex];
            rightString = $"{widgetMarkoutGroup.Group}  ·  {NewMarkoutDurationLabel(widgetMarkoutGroup.MarkoutDuration)}  ·  {widgetMarkoutGroup.Ticks:N3} ticks  ·  {widgetMarkoutGroup.Usd:N2} USD  ·  {widgetMarkoutGroup.Fills:N0} fills";
            rightBrush = s_textBrush;
        }
        FormattedText rightText = NewText(rightString, rightBrush);
        bool hasRoomForRight = plotLayout.Right - rightText.Width >= unitRight + s_labelSpacing;
        if (hasRoomForRight)
            drawingContext.DrawText(rightText, new Point(plotLayout.Right - rightText.Width, headerTextTop));

        // Step 3: how to switch the unit, when there is room left for it
        FormattedText hintText = NewText(isTicks ? "click axis for USD" : "click axis for ticks", s_mutedTextBrush);
        double hintLeft = unitRight + s_labelSpacing;
        double hintLimit = hasRoomForRight ? plotLayout.Right - rightText.Width - s_labelSpacing : plotLayout.Right;
        if (hintLeft + hintText.Width <= hintLimit)
            drawingContext.DrawText(hintText, new Point(hintLeft, headerTextTop));
    }

    /// Follows the pointer: the bar it is over is read out in the header, and over the y axis it becomes a hand.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        SetHover(GetRowIndexAt(position), isAxisHovered: IsOnValueAxis(position));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(hoveredRowIndex: -1, isAxisHovered: false);
    }

    /// A click on the y axis switches the bars between ticks and USD.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        bool isAxisClick = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsOnValueAxis(e.GetPosition(this));
        if (!isAxisClick)
            return;
        MarkoutUnit = MarkoutUnit == MarkoutUnit.Ticks ? MarkoutUnit.Usd : MarkoutUnit.Ticks;
        InvalidateVisual();
        e.Handled = true;
    }

    private void SetHover(int hoveredRowIndex, bool isAxisHovered)
    {
        Cursor = isAxisHovered ? s_handCursor : Cursor.Default;
        if (hoveredRowIndex == _hoveredRowIndex && isAxisHovered == _isAxisHovered)
            return;
        _hoveredRowIndex = hoveredRowIndex;
        _isAxisHovered = isAxisHovered;
        InvalidateVisual();
    }

    /// Whether this point is on the strip the y axis labels sit in, beside the plot.
    private bool IsOnValueAxis(Point position)
    {
        PlotLayout plotLayout = NewPlotLayout();
        return _widgetMarkoutGroups.Count > 0 && position.X >= 0 && position.X < plotLayout.Left && position.Y >= plotLayout.Top && position.Y <= plotLayout.Bottom;
    }

    /// The row whose slot this point is in, the whole height of the plot; -1 when it is in none.
    private int GetRowIndexAt(Point position)
    {
        PlotLayout plotLayout = NewPlotLayout();
        bool isInPlot = position.Y >= plotLayout.Top && position.Y <= plotLayout.Bottom;
        if (!isInPlot)
            return -1;
        for (int rowIndex = 0; rowIndex < _widgetMarkoutGroups.Count; rowIndex++)
        {
            double slotLeft = plotLayout.GetSlotLeft(rowIndex, _groupIndexes[rowIndex]);
            if (position.X >= slotLeft && position.X < slotLeft + plotLayout.SlotWidth)
                return rowIndex;
        }
        return -1;
    }

    private double GetValue(WidgetMarkoutGroup widgetMarkoutGroup) => MarkoutUnit == MarkoutUnit.Ticks ? widgetMarkoutGroup.Ticks : widgetMarkoutGroup.Usd;

    /// A markout duration as short text: 500ms, 10s, 5m.
    private static string NewMarkoutDurationLabel(Duration markoutDuration)
    {
        double seconds = markoutDuration.TotalSeconds;
        bool isWholeMinutes = seconds >= 60 && seconds % 60 == 0;
        if (isWholeMinutes)
            return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + "m";
        return seconds >= 1 ? seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s" : (seconds * 1000).ToString("0.###", CultureInfo.InvariantCulture) + "ms";
    }

    private static FormattedText NewText(string text, IBrush brush, Typeface? typeface = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface ?? Typeface.Default, s_fontSize, brush);
}

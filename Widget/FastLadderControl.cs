//BEGIN_FILE HFT/Widget/FastLadderControl.cs
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace Widget;

public sealed class RenderedLadderRow
{
    public int Ticks { get; set; }
    public string MyBuyQty { get; set; } = "";
    public string MyBuyAhead { get; set; } = "";
    public string BidQty { get; set; } = "";
    public string Price { get; set; } = "";
    public string AskQty { get; set; } = "";
    public string MySellQty { get; set; } = "";
    public string MySellAhead { get; set; } = "";

    // Metadata for interaction
    public ulong BuyOrderId { get; set; }
    public ulong SellOrderId { get; set; }

    internal TextLayout? TlMyBuyQty;
    internal TextLayout? TlMyBuyAhead;
    internal TextLayout? TlBidQty;
    internal TextLayout? TlPrice;
    internal TextLayout? TlAskQty;
    internal TextLayout? TlMySellQty;
    internal TextLayout? TlMySellAhead;

    /// <summary>
    /// Smart update: compares new values against current values.
    /// Invalidates specific TextLayout caches ONLY if the string has changed.
    /// </summary>
    public void Update(int ticks, string price, string bid, string ask, string myBuyQty, string myBuyAhead, string mySellQty, string mySellAhead, ulong buyOrderId, ulong sellOrderId)
    {
        // 1. Always update lightweight metadata (integers/ulongs are atomic/fast)
        Ticks = ticks;
        BuyOrderId = buyOrderId;
        SellOrderId = sellOrderId;

        // 2. Dirty Check strings to save TextLayout regeneration (Expensive)
        if (Price != price) { Price = price; TlPrice = null; }
        if (BidQty != bid) { BidQty = bid; TlBidQty = null; }
        if (AskQty != ask) { AskQty = ask; TlAskQty = null; }
        if (MyBuyQty != myBuyQty) { MyBuyQty = myBuyQty; TlMyBuyQty = null; }
        if (MyBuyAhead != myBuyAhead) { MyBuyAhead = myBuyAhead; TlMyBuyAhead = null; }
        if (MySellQty != mySellQty) { MySellQty = mySellQty; TlMySellQty = null; }
        if (MySellAhead != mySellAhead) { MySellAhead = mySellAhead; TlMySellAhead = null; }
    }

    public void ResetCache()
    {
        TlMyBuyQty = null;
        TlMyBuyAhead = null;
        TlBidQty = null;
        TlPrice = null;
        TlAskQty = null;
        TlMySellQty = null;
        TlMySellAhead = null;
        BuyOrderId = 0;
        SellOrderId = 0;
    }
}

public sealed class LadderRightClickEventArgs : EventArgs
{
    public Point Point { get; }
    public int Tick { get; }
    public RenderedLadderRow? Row { get; }
    public int ColumnIndex { get; } // 0=MyBuy, 1=Bid, 2=Price, 3=Ask, 4=MySell
    public bool IsHeader { get; }

    public LadderRightClickEventArgs(Point point, int tick, RenderedLadderRow? row, int columnIndex, bool isHeader)
    {
        Point = point;
        Tick = tick;
        Row = row;
        ColumnIndex = columnIndex;
        IsHeader = isHeader;
    }
}

public sealed class FastLadderControl : Control
{
    private const double RowHeight = 24.0;
    private const double HeaderHeight = 0.0;
    private const double FontSize = 18.0;
    private const double CellPadding = 2.0;
    private const double MinPriceColumnWidth = 80.0;

    #region Typography
    private static readonly Typeface s_tfRegular = Typeface.Default;
    private static readonly Typeface s_tfBold = new Typeface(s_tfRegular.FontFamily, FontStyle.Normal, FontWeight.Bold);
    #endregion

    // Interaction Brushes
    private static readonly IBrush s_hoverBrush = new ImmutableSolidColorBrush(Color.FromRgb(224, 224, 224)); // #E0E0E0
    private static readonly IBrush s_pressedBrush = new ImmutableSolidColorBrush(Color.FromRgb(192, 192, 192)); // #C0C0C0

    private readonly List<ColumnDefinition> _columns = new()
    {
        // 0: My Buys
        new("My Buys", 0, Palette.WorkEmptyLightGray, Palette.WorkTextBlack, s_tfBold, TextAlignment.Center),
        // 1: Bids
        new("Bids", 0, Palette.BidEmptyDarkBlue, Palette.BidTextCream, s_tfBold, TextAlignment.Right),
        // 2: Price
        new("Price", 0, Palette.PriceBackgroundLightGray, Palette.PriceTextBlack, s_tfRegular, TextAlignment.Center),
        // 3: Asks (Updated to Burgundy)
        new("Asks", 0, Palette.AskEmptyDarkPurple, Palette.AskTextCream, s_tfBold, TextAlignment.Left),
        // 4: My Sells
        new("My Sells", 0, Palette.WorkEmptyLightGray, Palette.WorkTextBlack, s_tfBold, TextAlignment.Center),
    };

    private readonly List<RenderedLadderRow> _rows = new();
    private bool _requiresTextCacheRebuild = true;
    private double _calculatedPriceWidth = MinPriceColumnWidth;
    private double _layoutWidth = -1;

    // Own-order cells are three sub-columns, number | chevron | number, so the chevrons line up whatever the digit counts.
    private readonly TextLayout _tlBuyChevron;
    private readonly TextLayout _tlSellChevron;
    private readonly double _chevronColumnWidth;

    // Interaction State
    private int _hoveredRowIndex = -1;
    private bool _isPressed = false;

    // --- NEW PROPERTIES FOR REUSE ---
    private bool _isCompactMode;
    public bool IsCompactMode
    {
        get => _isCompactMode;
        set { _isCompactMode = value; InvalidateVisual(); }
    }

    public event Action<int>? TickSelected;
    public event EventHandler<LadderRightClickEventArgs>? RightClick;

    public FastLadderControl()
    {
        ClipToBounds = true;
        _tlBuyChevron = CreateChevronLayout(">", _columns[0]);
        _tlSellChevron = CreateChevronLayout("<", _columns[4]);
        _chevronColumnWidth = Math.Max(_tlBuyChevron.Width, _tlSellChevron.Width) + 4 * CellPadding;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHoverState(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredRowIndex != -1)
        {
            _hoveredRowIndex = -1;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            HandleRightClick(point);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            _isPressed = true;
            UpdateHoverState(point); // Ensure hover index is sync before click logic

            // Simple hit testing for rows
            if (point.Y > HeaderHeight)
            {
                int rowIndex = (int)((point.Y - HeaderHeight) / RowHeight);
                if (rowIndex >= 0 && rowIndex < _rows.Count)
                {
                    TickSelected?.Invoke(_rows[rowIndex].Ticks);
                }
            }
            InvalidateVisual();
        }
    }

    private void HandleRightClick(Point p)
    {
        // 1. Determine Column
        double x = 0;
        int colIndex = -1;
        for (int i = 0; i < _columns.Count; i++)
        {
            if (_columns[i].Width <= 0) continue;
            if (p.X >= x && p.X < x + _columns[i].Width)
            {
                colIndex = i;
                break;
            }
            x += _columns[i].Width;
        }

        // 2. Determine Row / Header
        bool isHeader = p.Y <= HeaderHeight;
        int tick = 0;
        RenderedLadderRow? row = null;

        if (!isHeader)
        {
            int rowIndex = (int)((p.Y - HeaderHeight) / RowHeight);
            if (rowIndex >= 0 && rowIndex < _rows.Count)
            {
                row = _rows[rowIndex];
                tick = row.Ticks;
            }
        }

        RightClick?.Invoke(this, new LadderRightClickEventArgs(p, tick, row, colIndex, isHeader));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPressed)
        {
            _isPressed = false;
            InvalidateVisual();
        }
    }

    private void UpdateHoverState(Point p)
    {
        int newIndex = -1;
        if (p.Y > HeaderHeight)
        {
            int idx = (int)((p.Y - HeaderHeight) / RowHeight);
            if (idx >= 0 && idx < _rows.Count)
            {
                newIndex = idx;
            }
        }

        if (newIndex != _hoveredRowIndex)
        {
            _hoveredRowIndex = newIndex;
            InvalidateVisual();
        }
    }

    public void UpdateRows(List<RenderedLadderRow> newRowsBuffer)
    {
        _rows.Clear();
        _rows.AddRange(newRowsBuffer);
        RecalculatePriceWidth();
        _requiresTextCacheRebuild = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void RecalculatePriceWidth()
    {
        double maxW = MinPriceColumnWidth;

        foreach (var row in _rows)
        {
            if (string.IsNullOrEmpty(row.Price)) continue;
            var layout = new TextLayout(row.Price, s_tfRegular, FontSize, Palette.PriceTextBlack, textWrapping: TextWrapping.NoWrap);
            double w = layout.Width + (CellPadding * 4);
            if (w > maxW) maxW = w;
        }
        _calculatedPriceWidth = maxW;
    }

    private void RebuildTextCache()
    {
        if (!_requiresTextCacheRebuild) return;
        double myBuyNumberWidth = NumberWidth(_columns[0]);
        double mySellNumberWidth = NumberWidth(_columns[4]);
        foreach (var row in _rows)
        {
            if (!IsCompactMode)
            {
                // Buys read "quantity > ahead", sells "ahead < quantity": the numbers hug the chevron from either side.
                row.TlMyBuyQty ??= CreateTextLayout(row.MyBuyQty, _columns[0], myBuyNumberWidth, TextAlignment.Right);
                row.TlMyBuyAhead ??= CreateTextLayout(row.MyBuyAhead, _columns[0], myBuyNumberWidth, TextAlignment.Left);
                row.TlMySellAhead ??= CreateTextLayout(row.MySellAhead, _columns[4], mySellNumberWidth, TextAlignment.Right);
                row.TlMySellQty ??= CreateTextLayout(row.MySellQty, _columns[4], mySellNumberWidth, TextAlignment.Left);
            }
            row.TlBidQty ??= CreateTextLayout(row.BidQty, _columns[1]);
            row.TlPrice ??= CreateTextLayout(row.Price, _columns[2]);
            row.TlAskQty ??= CreateTextLayout(row.AskQty, _columns[3]);
        }
        _requiresTextCacheRebuild = false;
    }

    private TextLayout? CreateTextLayout(string text, ColumnDefinition colDef)
    {
        return CreateTextLayout(text, colDef, colDef.Width, colDef.TextAlignment);
    }

    private TextLayout? CreateTextLayout(string text, ColumnDefinition colDef, double width, TextAlignment textAlignment)
    {
        if (string.IsNullOrEmpty(text)) return null;
        double constrainedWidth = Math.Max(0, width - 2 * CellPadding);
        return new TextLayout(text, colDef.Typeface, FontSize, colDef.Foreground, textAlignment,
            maxWidth: constrainedWidth, maxHeight: RowHeight, textWrapping: TextWrapping.NoWrap);
    }

    // Natural-width glyph; DrawOrderCell centres it in the chevron sub-column.
    private static TextLayout CreateChevronLayout(string text, ColumnDefinition colDef)
    {
        return new TextLayout(text, colDef.Typeface, FontSize, colDef.Foreground, TextAlignment.Left,
            maxHeight: RowHeight, textWrapping: TextWrapping.NoWrap);
    }

    // Width of each number sub-column either side of the chevron.
    private double NumberWidth(ColumnDefinition colDef)
    {
        return Math.Max(0, (colDef.Width - _chevronColumnWidth) / 2);
    }

    public override void Render(DrawingContext context)
    {
        double actualWidth = Bounds.Width;
        double priceColWidth = _calculatedPriceWidth;
        double remainingWidth = actualWidth - priceColWidth;

        if (IsCompactMode)
        {
            // Compact Mode: Only Bids(1) and Asks(3) share remaining width.
            double colWidth = Math.Max(0, remainingWidth / 2.0);
            _columns[0].Width = 0;
            _columns[1].Width = colWidth;
            _columns[2].Width = priceColWidth;
            _columns[3].Width = colWidth;
            _columns[4].Width = 0;
        }
        else
        {
            // Full Mode: Distribution Logic
            double standardColWidth = Math.Max(0, remainingWidth / 5.0);
            double largeColWidth = standardColWidth * 1.5;

            _columns[0].Width = largeColWidth;
            _columns[1].Width = standardColWidth;
            _columns[2].Width = priceColWidth;
            _columns[3].Width = standardColWidth;
            _columns[4].Width = largeColWidth;
        }

        // Aligned text sits inside its layout's MaxWidth, so any width change rebuilds every layout.
        if (_layoutWidth != actualWidth || (_rows.Count > 0 && _rows[0].TlPrice?.MaxWidth != (priceColWidth - 2 * CellPadding)))
        {
            _layoutWidth = actualWidth;
            foreach (RenderedLadderRow r in _rows) r.ResetCache();
            _requiresTextCacheRebuild = true;
        }
        RebuildTextCache();

        Rect bounds = Bounds;
        context.FillRectangle(Brushes.White, bounds);

        double currentX = 0;
        for (int i = 0; i < _columns.Count; i++)
        {
            ColumnDefinition col = _columns[i];
            if (col.Width <= 0) continue;

            context.FillRectangle(col.Background, new Rect(currentX, HeaderHeight, col.Width, bounds.Height - HeaderHeight));
            currentX += col.Width;
        }

        // Calculate X coordinates for cell content
        double xMyBuy = 0;
        double xBid = xMyBuy + _columns[0].Width;
        double xPrice = xBid + _columns[1].Width;
        double xAsk = xPrice + _columns[2].Width;
        double xMySell = xAsk + _columns[3].Width;
        double myBuyNumberWidth = NumberWidth(_columns[0]);
        double mySellNumberWidth = NumberWidth(_columns[4]);

        double currentY = HeaderHeight;

        for (int r = 0; r < _rows.Count; r++)
        {
            RenderedLadderRow row = _rows[r];

            // 1. Draw Active Cell Backgrounds
            if (!IsCompactMode && !string.IsNullOrEmpty(row.MyBuyQty))
                context.FillRectangle(Palette.WorkActiveWhite, new Rect(xMyBuy, currentY, _columns[0].Width, RowHeight));

            if (!string.IsNullOrEmpty(row.BidQty))
                context.FillRectangle(Palette.BidActiveBlue, new Rect(xBid, currentY, _columns[1].Width, RowHeight));

            if (!string.IsNullOrEmpty(row.AskQty))
                context.FillRectangle(Palette.AskActivePurple, new Rect(xAsk, currentY, _columns[3].Width, RowHeight));

            if (!IsCompactMode && !string.IsNullOrEmpty(row.MySellQty))
                context.FillRectangle(Palette.WorkActiveWhite, new Rect(xMySell, currentY, _columns[4].Width, RowHeight));

            // 2. Draw Interaction Overlay (Hover/Press) on Price Column
            if (r == _hoveredRowIndex)
            {
                IBrush overlayBrush = _isPressed ? s_pressedBrush : s_hoverBrush;
                context.FillRectangle(overlayBrush, new Rect(xPrice, currentY, _columns[2].Width, RowHeight));
            }

            // 3. Draw Text
            if (!IsCompactMode) DrawOrderCell(context, row.TlMyBuyQty, _tlBuyChevron, row.TlMyBuyAhead, xMyBuy, myBuyNumberWidth, currentY);
            DrawCellDirect(context, row.TlBidQty, xBid, currentY);
            DrawCellDirect(context, row.TlPrice, xPrice, currentY);
            DrawCellDirect(context, row.TlAskQty, xAsk, currentY);
            if (!IsCompactMode) DrawOrderCell(context, row.TlMySellAhead, _tlSellChevron, row.TlMySellQty, xMySell, mySellNumberWidth, currentY);

            currentY += RowHeight;
        }

        RenderGridLines(context, bounds, actualWidth);
    }

    private void RenderGridLines(DrawingContext context, Rect bounds, double width)
    {
        double currentY = HeaderHeight;
        if (HeaderHeight >= 0)
            context.DrawLine(Palette.GridLineGrayPen, new Point(0, currentY - 0.5), new Point(width, currentY - 0.5));

        for (int r = 0; r < _rows.Count; r++)
        {
            currentY += RowHeight;
            context.DrawLine(Palette.GridLineGrayPen, new Point(0, currentY - 0.5), new Point(width, currentY - 0.5));
        }
        double currentX = 0;
        for (int i = 0; i < _columns.Count - 1; i++)
        {
            currentX += _columns[i].Width;
            if (_columns[i].Width > 0)
                context.DrawLine(Palette.GridLineGrayPen, new Point(currentX - 0.5, 0), new Point(currentX - 0.5, bounds.Height));
        }
    }

    private void DrawCellDirect(DrawingContext context, TextLayout? tl, double x, double y)
    {
        if (tl != null)
        {
            double textX = x + CellPadding;
            double textY = y + (RowHeight - tl.Height) / 2;
            tl.Draw(context, new Point(textX, textY));
        }
    }

    // Left number right-aligned against the chevron, chevron centred in its fixed sub-column, right number left-aligned after it.
    private void DrawOrderCell(DrawingContext context, TextLayout? left, TextLayout chevron, TextLayout? right, double x, double numberWidth, double y)
    {
        if (left == null) return;
        DrawCellDirect(context, left, x, y);
        double xChevron = x + numberWidth;
        chevron.Draw(context, new Point(xChevron + (_chevronColumnWidth - chevron.Width) / 2, y + (RowHeight - chevron.Height) / 2));
        DrawCellDirect(context, right, xChevron + _chevronColumnWidth, y);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double desiredHeight = HeaderHeight + (_rows.Count * RowHeight);
        double desiredWidth = availableSize.Width;
        if (double.IsInfinity(desiredWidth)) desiredWidth = 400;
        return new Size(desiredWidth, desiredHeight);
    }

    private class ColumnDefinition
    {
        public string Header { get; }
        public double Width { get; set; }
        public IBrush Background { get; }
        public IBrush Foreground { get; }
        public Typeface Typeface { get; }
        public TextAlignment TextAlignment { get; }

        public ColumnDefinition(string header, double width, IBrush background, IBrush foreground, Typeface typeface, TextAlignment alignment)
        {
            Header = header;
            Width = width;
            Background = background;
            Foreground = foreground;
            Typeface = typeface;
            TextAlignment = alignment;
        }
    }
}

//END_FILE HFT/Widget/FastLadderControl.cs
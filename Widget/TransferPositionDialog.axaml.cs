using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using Tools;

namespace Widget;

/// A book a position can move between: the house book or a strategy trading the instrument. UI thread, cold path.
public sealed class TransferBook(int strategyId, string name)
{
    public int StrategyId { get; } = strategyId;
    public string Name { get; } = name;
}

/// Moves part of one book's position on an instrument into another, opened from a server workspace's Positions widget: the
/// instrument (the row's to start with, any the server trades), From and To side by side with each book's position, then the
/// lots and the price, and a preview of both books after when the server's check (Context.CanTransferPosition) passes. Closes
/// with the ControlTransferPosition to send, or null on cancel. UI thread, cold path.
public partial class TransferPositionDialog : Window
{
    private readonly ServerContext _serverContext;
    private readonly List<Instrument> _instruments;
    private Instrument _instrument;                 // the picked instrument
    private List<TransferBook> _transferBooks;      // the books trading the picked instrument
    private string _priceFormat;                    // Why two more decimals than the instrument's: an average of fills falls between ticks

    public TransferPositionDialog()
    {
        InitializeComponent();
        _serverContext = null!;
        _instruments = [];
        _instrument = null!;
        _transferBooks = [];
        _priceFormat = "";
    }

    public TransferPositionDialog(ServerContext serverContext, Instrument instrument) : this()
    {
        // Step 1: every instrument the server trades, the row's picked
        _serverContext = serverContext;
        foreach (int instrumentId in serverContext.InstrumentIds)
            _instruments.Add(serverContext.GetInstrument(instrumentId));
        InstrumentPicker.ItemsSource = _instruments;
        SetInstrument(instrument);
        InstrumentPicker.SelectedItem = instrument;

        // Step 2: every edit shows its result
        InstrumentPicker.SelectionChanged += (_, _) => { if (InstrumentPicker.SelectedItem is Instrument pickedInstrument && pickedInstrument != _instrument) SetInstrument(pickedInstrument); };
        FromBookPicker.SelectionChanged += (_, _) => SetQuantityAndPrice();
        ToBookPicker.SelectionChanged += (_, _) => ShowTransfer();
        QuantityInput.TextChanged += (_, _) => ShowTransfer();
        PriceInput.TextChanged += (_, _) => ShowTransfer();
        AvgPriceButton.Click += (_, _) => PriceInput.Text = FormatPrice(GetPositionHeader(FromBook).AvgPrice);
        MidPriceButton.Click += (_, _) => { if (_serverContext.TryGetQuote(_instrument.InstrumentId, out Quote quote)) PriceInput.Text = FormatPrice(quote.MidPrice); };

        // Step 3: shown again four times a second. Why: a fill or an algo switch can change either book while the dialog is open
        DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        refreshTimer.Tick += (_, _) => ShowTransfer();
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
    }

    private TransferBook FromBook => _transferBooks[Math.Max(0, FromBookPicker.SelectedIndex)];
    private TransferBook ToBook => _transferBooks[Math.Max(0, ToBookPicker.SelectedIndex)];

    /// The instrument's books, From on the first that holds a position and To on the book after it, with From's whole position at its average price.
    private void SetInstrument(Instrument instrument)
    {
        _instrument = instrument;
        _priceFormat = $"N{instrument.TickDecimals + 2}";
        _transferBooks = NewTransferBooks(_serverContext, instrument.InstrumentId);
        FromBookPicker.ItemsSource = _transferBooks;
        ToBookPicker.ItemsSource = _transferBooks;
        int fromBookIndex = Math.Max(0, _transferBooks.FindIndex(transferBook => GetPositionHeader(transferBook).Quantity != 0));
        FromBookPicker.SelectedIndex = fromBookIndex;
        ToBookPicker.SelectedIndex = (fromBookIndex + 1) % _transferBooks.Count;
        SetQuantityAndPrice();
    }

    // The house book, then each strategy trading the instrument; never a GUI, which books to one of these.
    private static List<TransferBook> NewTransferBooks(ServerContext serverContext, int instrumentId)
    {
        List<TransferBook> transferBooks = [new TransferBook(OrderIdAllocator.ServerStrategyId, "Server")];
        Bitset64 clientIds = serverContext.GetClientIdsByInstrumentId(instrumentId).GetReadonlyRef();
        foreach (int clientId in clientIds)
        {
            string clientName = serverContext.GetSocketHeader(clientId).GetReadonlyRef().ClientName.ToString();
            if (clientId == OrderIdAllocator.ServerStrategyId || ManualClient.IsGuiName(clientName))
                continue;
            transferBooks.Add(new TransferBook(clientId, Path.GetFileName(clientName.TrimEnd('\\', '/'))));
        }
        return transferBooks;
    }

    private PositionHeader GetPositionHeader(TransferBook transferBook) => _serverContext.GetPositionHeader(transferBook.StrategyId, _instrument.InstrumentId).Read();

    // Plain digits, no grouping, so the text parses back as typed.
    private string FormatPrice(double price) => double.IsFinite(price) ? Math.Round(price, _instrument.TickDecimals + 2).ToString(CultureInfo.InvariantCulture) : "";

    /// The from book's whole position, at its average price.
    private void SetQuantityAndPrice()
    {
        PositionHeader fromPositionHeader = GetPositionHeader(FromBook);
        QuantityInput.Text = Math.Abs(fromPositionHeader.Quantity).ToString(CultureInfo.InvariantCulture);
        PriceInput.Text = FormatPrice(fromPositionHeader.AvgPrice);
        ShowTransfer();
    }

    /// Shows each book's position, then both books after the transfer when it can go ahead.
    private void ShowTransfer()
    {
        // Step 1: each book's position as it is now
        PositionHeader fromPositionHeader = GetPositionHeader(FromBook);
        PositionHeader toPositionHeader = GetPositionHeader(ToBook);
        FromPositionText.Text = FormatPosition(in fromPositionHeader);
        ToPositionText.Text = FormatPosition(in toPositionHeader);

        // Step 2: the transfer as typed, checked as the server will
        bool isTransferable = TryNewControlTransferPosition(out ControlTransferPosition controlTransferPosition) && _serverContext.CanTransferPosition(in controlTransferPosition);
        PreviewGrid.IsVisible = isTransferable;
        if (!isTransferable)
            return;

        // Step 3: both books after it, through the same arithmetic a fill uses on the server
        Timestamp now = Clock.Now;
        ShowPreviewRow(FromBook, in fromPositionHeader, controlTransferPosition.NewTransferFill(FromBook.StrategyId, -controlTransferPosition.Quantity, now), FromPreviewNameText, FromPreviewBeforeText, FromPreviewAfterText, FromPreviewRealizedText);
        ShowPreviewRow(ToBook, in toPositionHeader, controlTransferPosition.NewTransferFill(ToBook.StrategyId, controlTransferPosition.Quantity, now), ToPreviewNameText, ToPreviewBeforeText, ToPreviewAfterText, ToPreviewRealizedText);
    }

    /// The request as typed: false when the lots or the price do not parse.
    private bool TryNewControlTransferPosition(out ControlTransferPosition controlTransferPosition)
    {
        controlTransferPosition = new ControlTransferPosition
        {
            InstrumentId = _instrument.InstrumentId,
            FromStrategyId = FromBook.StrategyId,
            ToStrategyId = ToBook.StrategyId,
        };
        bool isQuantityTyped = int.TryParse(QuantityInput.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lots) && lots > 0;
        bool isPriceTyped = double.TryParse(PriceInput.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double price);

        // Why the from book's sign: a long moves as a long and a short as a short, whatever is typed
        controlTransferPosition.Quantity = Math.Sign(GetPositionHeader(FromBook).Quantity) * lots;
        controlTransferPosition.Price = price;
        return isQuantityTyped && isPriceTyped;
    }

    private string FormatPosition(in PositionHeader positionHeader) =>
        positionHeader.Quantity == 0 ? "flat" : $"{positionHeader.Quantity:+0;-0} @ {positionHeader.AvgPrice.ToString(_priceFormat)}";

    // One book's preview row: its name, its quantity before and after the fill, and the profit the fill realizes.
    private void ShowPreviewRow(TransferBook transferBook, in PositionHeader positionHeader, Fill transferFill, TextBlock nameText, TextBlock beforeText, TextBlock afterText, TextBlock realizedText)
    {
        PositionHeader positionHeaderAfter = positionHeader;
        positionHeaderAfter.OnFill(in transferFill, _instrument.Multiplier);
        nameText.Text = transferBook.Name;
        beforeText.Text = $"{positionHeader.Quantity:+0;-0;0}";
        afterText.Text = $"{positionHeaderAfter.Quantity:+0;-0;0}";
        realizedText.Text = $"realizes {positionHeaderAfter.RealizedProfit - positionHeader.RealizedProfit:N2}";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    /// Closes with the request when it can go ahead against the rows as they are now; otherwise does nothing.
    private void OnTransferClick(object? sender, RoutedEventArgs e)
    {
        if (!TryNewControlTransferPosition(out ControlTransferPosition controlTransferPosition) || !_serverContext.CanTransferPosition(in controlTransferPosition))
            return;
        Close(controlTransferPosition);
    }
}

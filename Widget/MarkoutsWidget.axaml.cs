using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Data;
using Execution;
using Provider;
using Tools;

namespace Widget;

/// How the Markouts widget shows its rows.
public enum MarkoutView { Table, Histogram }

/// What the Markouts widget totals the selected instrument's fills by, beside the markout duration. Day and TimeOfDay are
/// UTC, from the fill's exchange time; TimeOfDay is the hour of the day across every day.
public enum MarkoutGrouping { All, Day, TimeOfDay, Side, FillType, Tag }

/// What the Markouts widget measures at each markout duration: the mid of the best bid and ask, the strategy's fair
/// value, the best price on the fill's own side (the bid for a buy, the ask for a sell), or the best on the far side.
public enum MarkoutValue { Mid, FairValue, SameBest, FarBest }

/// What the Markouts widget measures it against: the fill's price, or the strategy's fair value at the fill, which is
/// the fill's record at markout duration zero.
public enum MarkoutReference { FillPrice, FairValueAtFill }

/// Names a fill in the Markouts widget: its symbol, which is the folder its markouts were read from and the name of its
/// fill file, with the id and exchange time that both records carry.
public readonly record struct MarkoutKey(string Symbol, ulong MarkoutId, Timestamp ExchangeTimestamp);

/// A markout with the symbol of the folder it was read from.
public readonly record struct SymbolMarkout(string Symbol, Markout Markout)
{
    public MarkoutKey MarkoutKey => new(Symbol, Markout.MarkoutId, Markout.ExchangeTimestamp);
}

/// The fills of one stretch of time, added up to be one sample of a row's t-statistic: their markout in ticks times
/// quantity, and their quantity.
public readonly record struct MarkoutBucket(double TicksTimesQuantity, int Quantity);

/// One row of the Markouts widget: the fills of one group at one markout duration. Ticks is their quantity-weighted average
/// markout and Usd their total. Mean and StdDev are quantity-weighted too, each fill counting in proportion to its
/// quantity, so Mean is Ticks. TStatistic asks whether Ticks differs from zero; its samples are buckets of time, not
/// fills, because fills close together share one price move. An observation with no quote is not counted.
/// UI thread; cold path (see Spec.md "Markouts").
public sealed class WidgetMarkoutGroup(string group, Duration markoutDuration) : INotifyPropertyChanged
{
    private const int s_bucketMarkoutDurations = 3;   // a bucket is this many markout durations long: 1 overstates t by about 22%, 3 by about 6%
    private const long s_minimumBucketNanoseconds = Duration.NanosecondsPerSecond;   // the shortest bucket: a sweep's fills land in one instant and are one sample at any duration

    public string Group { get; } = group;
    public Duration MarkoutDuration { get; } = markoutDuration;
    public double MarkoutSeconds => MarkoutDuration.TotalSeconds;   // the Duration column's sort key, and what the Duration box chooses by
    public string MarkoutDurationLabel { get; } = NewMarkoutDurationLabel(markoutDuration);   // the Duration column's text and the histogram's label
    public int Fills { get; private set; }
    public int Quantity { get; private set; }
    public double Ticks => Quantity == 0 ? double.NaN : _ticksTimesQuantity / Quantity;
    public double Usd { get; private set; }
    public double Mean => Ticks;
    public int Samples => _bucketsByIndex.Count;   // the buckets of time with a fill in them: what TStatistic counts as independent

    /// The quantity-weighted spread of the fills' markouts, sqrt(n / (n - 1) * sum(q (x - Ticks)²) / sum(q)); with every fill one lot it is the sample SD.
    public double StdDev
    {
        get
        {
            if (Fills < 2)
                return double.NaN;
            double ticks = Ticks;
            double squaredDeviations = _ticksSquaredTimesQuantity - ticks * ticks * Quantity;
            return Math.Sqrt(Math.Max(0, squaredDeviations) / Quantity * Fills / (Fills - 1));
        }
    }

    /// Ticks over its standard error, sqrt(n / (n - 1) * sum((s - Ticks * q)²)) / sum(q), over the n buckets, each with ticks times quantity s and quantity q.
    public double TStatistic
    {
        get
        {
            int sampleCount = Samples;
            if (sampleCount < 2)
                return double.NaN;
            double ticks = Ticks;
            double squaredDeviations = _bucketTicksTimesQuantitySquaredSum - 2 * ticks * _bucketTicksTimesQuantityTimesQuantitySum + ticks * ticks * _bucketQuantitySquaredSum;
            double standardError = Math.Sqrt(Math.Max(0, squaredDeviations) * sampleCount / (sampleCount - 1)) / Quantity;
            return standardError > 0 ? ticks / standardError : double.NaN;
        }
    }

    private double _ticksTimesQuantity;
    private double _ticksSquaredTimesQuantity;           // what the weighted standard deviation needs
    private readonly Dictionary<long, MarkoutBucket> _bucketsByIndex = new();   // by the fill's exchange time over the bucket's length
    private double _bucketTicksTimesQuantitySquaredSum;               // over the buckets: sum(s²), sum(s q) and sum(q²), kept as each fill lands
    private double _bucketTicksTimesQuantityTimesQuantitySum;
    private double _bucketQuantitySquaredSum;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Adds one observation of a fill of this quantity: how far, in price, the measured value stood from what it is
    /// measured against, in the fill's favour.
    public void Add(Timestamp fillExchangeTimestamp, double points, int absQuantity, double tickSize, double usdMultiplier)
    {
        // Step 1: an observation with no quote, or no fair value, is not counted
        if (double.IsNaN(points))
            return;

        // Step 2: the row's totals
        double ticks = points / tickSize;
        Fills++;
        Quantity += absQuantity;
        _ticksTimesQuantity += ticks * absQuantity;
        _ticksSquaredTimesQuantity += ticks * ticks * absQuantity;
        Usd += points * usdMultiplier * absQuantity;

        // Step 3: the fill joins its bucket of time: the bucket's old share comes out of the three sums and its new share goes in
        long bucketNanoseconds = Math.Max(Math.Abs(MarkoutDuration.TotalNanoseconds) * s_bucketMarkoutDurations, s_minimumBucketNanoseconds);
        long bucketIndex = fillExchangeTimestamp.NanosSinceEpoch / bucketNanoseconds;
        _bucketsByIndex.TryGetValue(bucketIndex, out MarkoutBucket markoutBucket);
        AddToBucketSums(in markoutBucket, sign: -1);
        markoutBucket = new MarkoutBucket(markoutBucket.TicksTimesQuantity + ticks * absQuantity, markoutBucket.Quantity + absQuantity);
        AddToBucketSums(in markoutBucket, sign: 1);
        _bucketsByIndex[bucketIndex] = markoutBucket;
    }

    /// Adds a bucket's share to the three sums TStatistic needs, or with sign -1 takes it out.
    private void AddToBucketSums(in MarkoutBucket markoutBucket, int sign)
    {
        _bucketTicksTimesQuantitySquaredSum += sign * markoutBucket.TicksTimesQuantity * markoutBucket.TicksTimesQuantity;
        _bucketTicksTimesQuantityTimesQuantitySum += sign * markoutBucket.TicksTimesQuantity * markoutBucket.Quantity;
        _bucketQuantitySquaredSum += sign * (double)markoutBucket.Quantity * markoutBucket.Quantity;
    }

    /// Tells the grid every column changed.
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));

    /// A markout duration as short text: -20µs, 0, 100ms, 10s, 5m.
    public static string NewMarkoutDurationLabel(Duration markoutDuration)
    {
        double seconds = markoutDuration.TotalSeconds;
        double absSeconds = Math.Abs(seconds);
        if (absSeconds == 0)
            return "0";
        bool isWholeMinutes = absSeconds >= 60 && absSeconds % 60 == 0;
        if (isWholeMinutes)
            return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + "m";
        if (absSeconds >= 1)
            return seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        return absSeconds >= 0.001 ? (seconds * 1_000).ToString("0.###", CultureInfo.InvariantCulture) + "ms" : (seconds * 1_000_000).ToString("0.###", CultureInfo.InvariantCulture) + "µs";
    }
}

/// The Markouts widget's saved state: its columns' layout, its six selections and the histogram's unit. Cold path: a workspace's save and load.
[RegisterJson]
public sealed class MarkoutsWidgetState
{
    public List<DataGridColumnState> Columns { get; set; } = new();
    public MarkoutView MarkoutView { get; set; }
    public MarkoutGrouping MarkoutGrouping { get; set; }
    public string? ShortSymbol { get; set; }       // null = saved before any instrument had appeared
    public double? MarkoutSeconds { get; set; }    // null = "All"
    public MarkoutValue MarkoutValue { get; set; }
    public MarkoutReference MarkoutReference { get; set; }
    public MarkoutUnit MarkoutUnit { get; set; }
}

/// A strategy's markouts for one instrument, which is always selected, totalled by the chosen grouping and markout
/// duration, as a table or a histogram. It reads every symbol's .markout history and .fill file, then each new line as
/// Logging writes it, and joins a markout to its fill by symbol, id and exchange time: the fill gives the price, side
/// and quantity. A row measures the chosen value (the mid, or the strategy's fair value) against the chosen reference
/// (the fill's price, or the fair value at the fill). New lines add to the rows in place; only a change of selection
/// totals again from the start. UI thread; the history parses on the thread pool. Cold path (see Spec.md "Markouts").
public sealed partial class MarkoutsWidget : UserControl, IWidget, IDisposable
{
    private const int s_historyBatchSize = 1000;
    private const string s_allChoices = "All";
    private static readonly TimeSpan s_symbolDirectoryPollInterval = TimeSpan.FromSeconds(1);

    private readonly Context _context;
    private readonly DispatcherTimer _symbolDirectoryTimer = new() { Interval = s_symbolDirectoryPollInterval };   // finds symbol folders Logging creates after the widget opened
    private readonly HashSet<string> _symbolDirectoryPaths = new();   // the symbol folders already being read
    private readonly List<LogReader> _logReaders = new();             // two per symbol: its markout folder and its fill file
    private readonly List<SymbolMarkout> _symbolMarkouts = new();     // every markout read; UI thread only
    private readonly List<SymbolMarkout> _waitingSymbolMarkouts = new();   // of the selection: not in a row yet, because their fill's line or the record they are measured against is unread
    private readonly Dictionary<MarkoutKey, Fill> _fillsByMarkoutKey = new();
    private readonly Dictionary<MarkoutKey, Markout> _markoutsAtFillByMarkoutKey = new();   // each fill's record at markout duration zero: what FairValueAtFill measures against
    private readonly Dictionary<string, Instrument?> _instrumentsBySymbol = new();   // null = this context does not list it
    private readonly Dictionary<(string Group, Duration MarkoutDuration), WidgetMarkoutGroup> _groupsByKey = new();
    private readonly SortedSet<double> _markoutSecondsChoices = new();   // every markout duration seen so far, in seconds: what the Duration box offers
    private readonly SortedSet<string> _shortSymbols = new(StringComparer.Ordinal);   // every instrument seen so far: what the Instrument box offers
    private bool _isShowingChoices;                                   // a selector is being refilled: its SelectionChanged is not the user's
    public AvaloniaList<WidgetMarkoutGroup> Rows { get; } = new();

    public string Title { get; private set; }
    public double DefaultWidth => 1000;
    public double DefaultHeight => 300;

    public MarkoutsWidget()
    {
        _context = null!;
        InitializeComponent();
        Title = "Markouts (Design)";
    }

    public MarkoutsWidget(Context context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Title = $"Markouts ({_context.DirectoryName})";

        // Step 1: the grid and the selectors
        InitializeComponent();
        MarkoutsGrid.ItemsSource = Rows;
        MarkoutDurationComboBox.ItemTemplate = new FuncDataTemplate<object>((choice, _) => new TextBlock { Text = choice is double markoutSeconds ? WidgetMarkoutGroup.NewMarkoutDurationLabel(Duration.FromSeconds(markoutSeconds)) : s_allChoices });
        _isShowingChoices = true;
        ViewComboBox.ItemsSource = Enum.GetValues<MarkoutView>();
        ViewComboBox.SelectedItem = MarkoutView.Table;
        GroupingComboBox.ItemsSource = Enum.GetValues<MarkoutGrouping>();
        GroupingComboBox.SelectedItem = MarkoutGrouping.Day;
        ValueComboBox.ItemsSource = Enum.GetValues<MarkoutValue>();
        ValueComboBox.SelectedItem = MarkoutValue.Mid;
        ReferenceComboBox.ItemsSource = Enum.GetValues<MarkoutReference>();
        ReferenceComboBox.SelectedItem = MarkoutReference.FillPrice;
        _isShowingChoices = false;
        ShowChoices();

        // Step 2: every symbol folder there is now, and each one that appears later
        StartNewLogReaders();
        _symbolDirectoryTimer.Tick += (_, _) => StartNewLogReaders();
        _symbolDirectoryTimer.Start();
    }

    /// Starts reading each symbol folder under Markouts that is not being read yet, and that symbol's fill file.
    private void StartNewLogReaders()
    {
        string markoutsDirectoryPath = Context.GetMarkoutsDirectoryPath(_context.DirectoryPath);
        if (!Directory.Exists(markoutsDirectoryPath))
            return;
        foreach (string symbolDirectoryPath in Directory.GetDirectories(markoutsDirectoryPath))
        {
            if (!_symbolDirectoryPaths.Add(symbolDirectoryPath))
                continue;
            string symbol = Path.GetFileName(symbolDirectoryPath);
            StartLogReader<Markout>(symbolDirectoryPath, "*.markout", markouts => AddMarkouts(symbol, markouts));
            StartLogReader<Fill>(_context.FillsDirectoryPath.Path, Path.GetFileName(Context.GetFillsFilePath(_context.DirectoryPath, symbol)), fills => AddFills(symbol, fills));
        }
    }

    /// Reads the lines of this folder's files of this pattern as records: the whole history, then each new line as Logging writes it.
    private void StartLogReader<TRecord>(string directoryPath, string pattern, Action<List<TRecord>> addRecords)
    {
        LogReader logReader = new LogReader(directoryPath, pattern);
        logReader.LiveLines += lines => Dispatcher.UIThread.Post(() => addRecords(ParseLines<TRecord>(lines)));
        logReader.Start();
        _ = LoadHistoryAsync(logReader, addRecords);
        _logReaders.Add(logReader);
    }

    /// Reads the whole history back, a batch at a time, then adds it at once.
    private static async Task LoadHistoryAsync<TRecord>(LogReader logReader, Action<List<TRecord>> addRecords)
    {
        List<TRecord> history = await Task.Run(() =>
        {
            List<TRecord> _history = new();
            for (List<string> lines = logReader.LoadHistory(s_historyBatchSize); lines.Count > 0; lines = logReader.LoadHistory(s_historyBatchSize))
                _history.AddRange(ParseLines<TRecord>(lines));
            return _history;
        });
        addRecords(history);
    }

    private static List<TRecord> ParseLines<TRecord>(List<string> lines)
    {
        List<TRecord> records = new(lines.Count);
        foreach (string line in lines)
        {
            // Why the catch: a torn last line must not take down the GUI; the line is skipped
            try { records.Add(Json.Deserialize<TRecord>(line)); }
            catch { }
        }
        return records;
    }

    /// Keeps these markouts of this symbol and adds them to the rows; a duration or instrument not seen before also joins its selector.
    private void AddMarkouts(string symbol, List<Markout> markouts)
    {
        // Step 1: keep them, each fill's record at the fill apart, noting any new choice
        List<SymbolMarkout> symbolMarkouts = new(markouts.Count);
        bool hasNewChoice = false;
        foreach (Markout markout in markouts)
        {
            // Why skipped: a line with no fill time is from a file written before a markout was joined to its fill
            if (markout.ExchangeTimestamp.NanosSinceEpoch == 0)
                continue;
            SymbolMarkout symbolMarkout = new(symbol, markout);
            symbolMarkouts.Add(symbolMarkout);
            if (markout.MarkoutDuration.TotalNanoseconds == 0)
                _markoutsAtFillByMarkoutKey[symbolMarkout.MarkoutKey] = markout;
            hasNewChoice |= _markoutSecondsChoices.Add(markout.MarkoutDuration.TotalSeconds);
        }
        _symbolMarkouts.AddRange(symbolMarkouts);
        if (symbolMarkouts.Count > 0 && TryGetInstrument(symbol, out Instrument instrument))
            hasNewChoice |= _shortSymbols.Add(instrument.ShortSymbol);

        // Step 2: a new choice refills the selectors; the first instrument to appear becomes the selection, so total from the start
        bool hadInstrument = InstrumentComboBox.SelectedItem != null;
        if (hasNewChoice)
            ShowChoices();
        if (hadInstrument)
            AddToGroups(symbolMarkouts);
        else
            ShowGroups();
    }

    /// Keeps these fills of this symbol, and adds the markouts that were waiting for them to the rows.
    private void AddFills(string symbol, List<Fill> fills)
    {
        foreach (Fill fill in fills)
            _fillsByMarkoutKey[new MarkoutKey(symbol, fill.FillId, fill.OrderHeader.ExchangeTimestamp)] = fill;
        AddToGroups([]);
    }

    /// Refills the Instrument box with every instrument seen so far and the Duration box with "All" and every markout
    /// duration seen so far, keeping what is selected. An instrument is always selected once there is one.
    private void ShowChoices()
    {
        _isShowingChoices = true;
        ShowChoices(InstrumentComboBox, _shortSymbols, hasAllChoice: false);
        ShowChoices(MarkoutDurationComboBox, _markoutSecondsChoices, hasAllChoice: true);
        _isShowingChoices = false;
    }

    /// Fills a selector, keeping its selection; with nothing selected it takes the first choice.
    private static void ShowChoices<T>(ComboBox comboBox, IEnumerable<T> choices, bool hasAllChoice) where T : notnull
    {
        object? selectedChoice = comboBox.SelectedItem;
        List<object> items = new();
        if (hasAllChoice)
            items.Add(s_allChoices);
        foreach (T choice in choices)
            items.Add(choice);
        comboBox.ItemsSource = items;
        comboBox.SelectedItem = selectedChoice ?? (items.Count > 0 ? items[0] : null);
    }

    /// A new grouping puts the table in its default order, then totals: days newest first, any other group in order, each
    /// group's markout durations shortest first. A click on a header sorts by that column instead, until the grouping changes.
    private void OnGroupingSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        DataGridSortDescriptionCollection sortDescriptions = MarkoutsGrid.CollectionView.SortDescriptions;
        sortDescriptions.Clear();
        if (GroupingComboBox.SelectedItem is MarkoutGrouping.Day)
        {
            sortDescriptions.Add(DataGridSortDescription.FromPath(nameof(WidgetMarkoutGroup.Group), ListSortDirection.Descending));
            sortDescriptions.Add(DataGridSortDescription.FromPath(nameof(WidgetMarkoutGroup.MarkoutSeconds)));
        }
        OnSelectionChanged(sender, e);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isShowingChoices)
            ShowGroups();
    }

    /// Totals every markout again from the start under the current selection, and shows the chosen view.
    private void ShowGroups()
    {
        _groupsByKey.Clear();
        Rows.Clear();
        _waitingSymbolMarkouts.Clear();
        AddToGroups(_symbolMarkouts);
        bool isTable = ViewComboBox.SelectedItem is MarkoutView.Table;
        MarkoutsGrid.IsVisible = isTable;
        MarkoutHistogram.IsVisible = !isTable;
    }

    /// Adds these markouts, and the ones that were waiting, to the rows of the current selection, making a row for each
    /// new group, and redraws. A markout waits while its fill's line, or the record it is measured against, is unread.
    private void AddToGroups(List<SymbolMarkout> symbolMarkouts)
    {
        if (GroupingComboBox.SelectedItem is not MarkoutGrouping markoutGrouping || InstrumentComboBox.SelectedItem is not string selectedShortSymbol)
            return;
        MarkoutValue markoutValue = (MarkoutValue)ValueComboBox.SelectedItem!;
        MarkoutReference markoutReference = (MarkoutReference)ReferenceComboBox.SelectedItem!;
        double? selectedMarkoutSeconds = MarkoutDurationComboBox.SelectedItem as double?;

        // Step 1: the markouts that waited are tried again, then the new ones
        List<SymbolMarkout> retriedSymbolMarkouts = new(_waitingSymbolMarkouts);
        _waitingSymbolMarkouts.Clear();
        bool _hasNewRow = false;
        foreach (SymbolMarkout symbolMarkout in retriedSymbolMarkouts)
            AddToGroup(in symbolMarkout);
        foreach (SymbolMarkout symbolMarkout in symbolMarkouts)
            AddToGroup(in symbolMarkout);

        // Step 2: the rows that changed redraw in place, and so do the bars
        // Why sorted again: the grid puts a new row anywhere among the rows that tie in its sort; sorting afresh leaves ties in the rows' own order
        if (_hasNewRow)
            MarkoutsGrid.CollectionView.Refresh();
        foreach (WidgetMarkoutGroup widgetMarkoutGroup in Rows)
            widgetMarkoutGroup.Refresh();
        MarkoutHistogram.Show(Rows);

        // Step 3: the fills still waiting are counted, so a fill file that has lost lines shows
        HashSet<MarkoutKey> unmatchedMarkoutKeys = new();
        foreach (SymbolMarkout symbolMarkout in _waitingSymbolMarkouts)
            unmatchedMarkoutKeys.Add(symbolMarkout.MarkoutKey);
        UnmatchedTextBlock.Text = unmatchedMarkoutKeys.Count == 0 ? "" : $"{unmatchedMarkoutKeys.Count:N0} fills unmatched";

        // A markout of the selected instrument and duration goes to its group's row, or waits for what it is valued with
        void AddToGroup(in SymbolMarkout symbolMarkout)
        {
            // Why skipped: an instrument this context does not list has no tick size or multiplier to convert with
            if (!TryGetInstrument(symbolMarkout.Symbol, out Instrument instrument))
                return;
            Markout markout = symbolMarkout.Markout;
            bool isOtherInstrument = instrument.ShortSymbol != selectedShortSymbol;
            bool isOtherMarkoutDuration = selectedMarkoutSeconds.HasValue && markout.MarkoutDuration.TotalSeconds != selectedMarkoutSeconds.Value;
            if (isOtherInstrument || isOtherMarkoutDuration)
                return;

            // The fill gives the price, side and quantity; against the fair value at the fill, its record at duration zero gives that
            MarkoutKey markoutKey = symbolMarkout.MarkoutKey;
            bool hasFill = _fillsByMarkoutKey.TryGetValue(markoutKey, out Fill fill);
            bool hasMarkoutAtFill = _markoutsAtFillByMarkoutKey.TryGetValue(markoutKey, out Markout markoutAtFill);
            bool isWaiting = !hasFill || (markoutReference == MarkoutReference.FairValueAtFill && !hasMarkoutAtFill);
            if (isWaiting)
            {
                _waitingSymbolMarkouts.Add(symbolMarkout);
                return;
            }
            double valuePrice = GetValuePrice(in fill, in markout, markoutValue);
            double referencePrice = markoutReference == MarkoutReference.FillPrice ? fill.Price : markoutAtFill.FairValuePrice;
            string group = GetGroup(in fill, in markout, markoutGrouping);
            if (!_groupsByKey.TryGetValue((group, markout.MarkoutDuration), out WidgetMarkoutGroup? widgetMarkoutGroup))
            {
                _groupsByKey[(group, markout.MarkoutDuration)] = widgetMarkoutGroup = new WidgetMarkoutGroup(group, markout.MarkoutDuration);
                InsertRow(widgetMarkoutGroup);
                _hasNewRow = true;
            }
            widgetMarkoutGroup.Add(markout.ExchangeTimestamp, fill.Sign * (valuePrice - referencePrice), Math.Abs(fill.Quantity), instrument.TickSize, instrument.UsdMultiplier);
        }
    }

    /// Puts a new row where it belongs: groups in order, each group's markout durations shortest first. The histogram draws
    /// this order, and the table keeps it among rows that tie in whatever it is sorted by.
    private void InsertRow(WidgetMarkoutGroup widgetMarkoutGroup)
    {
        int rowIndex = 0;
        while (rowIndex < Rows.Count && CompareRows(Rows[rowIndex], widgetMarkoutGroup) < 0)
            rowIndex++;
        Rows.Insert(rowIndex, widgetMarkoutGroup);
    }

    private static int CompareRows(WidgetMarkoutGroup left, WidgetMarkoutGroup right)
    {
        int groupOrder = string.CompareOrdinal(left.Group, right.Group);
        return groupOrder != 0 ? groupOrder : left.MarkoutDuration.CompareTo(right.MarkoutDuration);
    }

    /// The instrument of this symbol, looked up once; false when this context does not list it.
    private bool TryGetInstrument(string symbol, out Instrument instrument)
    {
        if (!_instrumentsBySymbol.TryGetValue(symbol, out Instrument? cachedInstrument))
        {
            foreach (Instrument listedInstrument in _context.EnumerateInstruments())
            {
                if (listedInstrument.Symbol == symbol)
                    cachedInstrument = listedInstrument;
            }
            _instrumentsBySymbol[symbol] = cachedInstrument;
        }
        instrument = cachedInstrument!;
        return cachedInstrument != null;
    }

    /// The row group this fill and its markout fall in under this grouping.
    public static string GetGroup(in Fill fill, in Markout markout, MarkoutGrouping markoutGrouping)
    {
        DateTime utcDateTime = markout.ExchangeTimestamp.ToDateTime;
        return markoutGrouping switch
        {
            MarkoutGrouping.Day => utcDateTime.ToString("yyyy-MM-dd"),
            MarkoutGrouping.TimeOfDay => utcDateTime.ToString("HH") + ":00",
            MarkoutGrouping.Side => fill.Side.ToString(),
            MarkoutGrouping.FillType => fill.FillType.ToString(),
            MarkoutGrouping.Tag => markout.MarkoutTag.ToString(),
            _ => "All",
        };
    }

    /// The price this markout is measured at under this choice of value; NaN when the record has no quote, or no fair value.
    public static double GetValuePrice(in Fill fill, in Markout markout, MarkoutValue markoutValue)
    {
        bool isBuy = fill.Sign > 0;
        return markoutValue switch
        {
            MarkoutValue.FairValue => markout.FairValuePrice,
            MarkoutValue.SameBest => isBuy ? markout.BidPrice : markout.AskPrice,
            MarkoutValue.FarBest => isBuy ? markout.AskPrice : markout.BidPrice,
            _ => markout.MidPrice(),
        };
    }

    /// The grid's menu: a checkbox per column, toggling whether it shows.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        contextMenu.Items.Clear();
        contextMenu.AddColumnVisibilityMenuItems(MarkoutsGrid);
    }

    /// The columns' layout, the six selections and the histogram's unit.
    public string? SaveStateJson() => Json.Serialize(new MarkoutsWidgetState
    {
        Columns = DataGridColumnState.NewDataGridColumnStates(MarkoutsGrid, DataGridColumnState.GetHeader),
        MarkoutView = (MarkoutView)ViewComboBox.SelectedItem!,
        MarkoutGrouping = (MarkoutGrouping)GroupingComboBox.SelectedItem!,
        ShortSymbol = InstrumentComboBox.SelectedItem as string,
        MarkoutSeconds = MarkoutDurationComboBox.SelectedItem as double?,
        MarkoutValue = (MarkoutValue)ValueComboBox.SelectedItem!,
        MarkoutReference = (MarkoutReference)ReferenceComboBox.SelectedItem!,
        MarkoutUnit = MarkoutHistogram.MarkoutUnit,
    });

    /// Applies a saved state. Why the catch: a layout file that no longer parses must not stop the widget opening; it keeps its defaults
    public void LoadStateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            MarkoutsWidgetState? markoutsWidgetState = Json.Deserialize<MarkoutsWidgetState>(json);
            if (markoutsWidgetState == null)
                return;

            // Step 1: the columns and the unit
            DataGridColumnState.ApplyDataGridColumnStates(MarkoutsGrid, markoutsWidgetState.Columns, DataGridColumnState.GetHeader);
            MarkoutHistogram.MarkoutUnit = markoutsWidgetState.MarkoutUnit;

            // Step 2: the saved instrument and duration become choices now, since their markouts are still being read
            if (markoutsWidgetState.ShortSymbol != null)
                _shortSymbols.Add(markoutsWidgetState.ShortSymbol);
            if (markoutsWidgetState.MarkoutSeconds.HasValue)
                _markoutSecondsChoices.Add(markoutsWidgetState.MarkoutSeconds.Value);
            ShowChoices();

            // Step 3: select what was saved, and total once
            _isShowingChoices = true;
            ViewComboBox.SelectedItem = markoutsWidgetState.MarkoutView;
            GroupingComboBox.SelectedItem = markoutsWidgetState.MarkoutGrouping;
            InstrumentComboBox.SelectedItem = markoutsWidgetState.ShortSymbol ?? InstrumentComboBox.SelectedItem;   // null keeps the choice ShowChoices made
            MarkoutDurationComboBox.SelectedItem = markoutsWidgetState.MarkoutSeconds ?? (object)s_allChoices;
            ValueComboBox.SelectedItem = markoutsWidgetState.MarkoutValue;
            ReferenceComboBox.SelectedItem = markoutsWidgetState.MarkoutReference;
            _isShowingChoices = false;
            ShowGroups();
        }
        catch { }
    }

    public void Dispose()
    {
        _symbolDirectoryTimer.Stop();
        foreach (LogReader logReader in _logReaders)
            logReader.Dispose();
    }
}

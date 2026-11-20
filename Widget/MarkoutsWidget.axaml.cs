using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
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
/// Chicago time, from the fill's exchange time; TimeOfDay is the hour of the day across every day.
public enum MarkoutGrouping { All, Day, TimeOfDay, Side, FillType, Tag }

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

    public string Group { get; } = group;
    public Duration MarkoutDuration { get; } = markoutDuration;
    public double MarkoutSeconds => MarkoutDuration.TotalSeconds;   // the Duration column's text and sort key, and the histogram's label
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

    /// Adds one observation: how far the mid had moved in the fill's favour at this markout duration.
    public void Add(in Markout markout, double tickSize, double usdMultiplier)
    {
        // Step 1: an observation with no quote is not counted
        double points = Math.Sign(markout.FillQuantity) * (markout.MidPrice() - markout.FillPrice);
        if (double.IsNaN(points))
            return;

        // Step 2: the row's totals
        double ticks = points / tickSize;
        int absQuantity = Math.Abs(markout.FillQuantity);
        Fills++;
        Quantity += absQuantity;
        _ticksTimesQuantity += ticks * absQuantity;
        _ticksSquaredTimesQuantity += ticks * ticks * absQuantity;
        Usd += points * usdMultiplier * absQuantity;

        // Step 3: the fill joins its bucket of time: the bucket's old share comes out of the three sums and its new share goes in
        long bucketNanoseconds = Math.Max(MarkoutDuration.TotalNanoseconds * s_bucketMarkoutDurations, 1);
        long bucketIndex = markout.FillExchangeTimestamp.NanosSinceEpoch / bucketNanoseconds;
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
}

/// The Markouts widget's saved state: its columns' layout, its four selections and the histogram's unit. Cold path: a workspace's save and load.
[RegisterJson]
public sealed class MarkoutsWidgetState
{
    public List<DataGridColumnState> Columns { get; set; } = new();
    public MarkoutView MarkoutView { get; set; }
    public MarkoutGrouping MarkoutGrouping { get; set; }
    public string? ShortSymbol { get; set; }       // null = saved before any instrument had appeared
    public double? MarkoutSeconds { get; set; }    // null = "All"
    public MarkoutUnit MarkoutUnit { get; set; }
}

/// A strategy's markouts for one instrument, which is always selected, totalled by the chosen grouping and markout
/// duration, as a table or a histogram: every symbol's .markout history, then each new line as Logging writes it. New
/// lines add to the rows in place; only a change of selection totals again from the start. UI thread; the history parses
/// on the thread pool. Cold path (see Spec.md "Markouts").
public sealed partial class MarkoutsWidget : UserControl, IWidget, IDisposable
{
    private const int s_historyBatchSize = 1000;
    private const string s_allChoices = "All";
    private static readonly TimeSpan s_symbolDirectoryPollInterval = TimeSpan.FromSeconds(1);

    private readonly Context _context;
    private readonly DispatcherTimer _symbolDirectoryTimer = new() { Interval = s_symbolDirectoryPollInterval };   // finds symbol folders Logging creates after the widget opened
    private readonly HashSet<string> _symbolDirectoryPaths = new();   // the symbol folders already being read
    private readonly List<LogReader> _logReaders = new();             // one per symbol folder
    private readonly List<Markout> _markouts = new();                 // UI thread only
    private readonly Dictionary<int, Instrument?> _instrumentsByInstrumentId = new();   // null = this context does not list it
    private readonly Dictionary<(string Group, Duration MarkoutDuration), WidgetMarkoutGroup> _groupsByKey = new();
    private readonly SortedSet<double> _markoutSecondsChoices = new();   // every markout duration seen so far, in seconds: what the Duration box offers
    private readonly SortedSet<string> _shortSymbols = new(StringComparer.Ordinal);   // every instrument seen so far: what the Instrument box offers
    private bool _isShowingChoices;                                   // a selector is being refilled: its SelectionChanged is not the user's
    public AvaloniaList<WidgetMarkoutGroup> Rows { get; } = new();

    public string Title { get; private set; }
    public double DefaultWidth => 800;
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
        _isShowingChoices = true;
        ViewComboBox.ItemsSource = Enum.GetValues<MarkoutView>();
        ViewComboBox.SelectedItem = MarkoutView.Table;
        GroupingComboBox.ItemsSource = Enum.GetValues<MarkoutGrouping>();
        GroupingComboBox.SelectedItem = MarkoutGrouping.Day;
        _isShowingChoices = false;
        ShowChoices();

        // Step 2: every symbol folder there is now, and each one that appears later
        StartNewLogReaders();
        _symbolDirectoryTimer.Tick += (_, _) => StartNewLogReaders();
        _symbolDirectoryTimer.Start();
    }

    /// Starts reading each symbol folder under Markouts that is not being read yet.
    private void StartNewLogReaders()
    {
        string markoutsDirectoryPath = Context.GetMarkoutsDirectoryPath(_context.DirectoryPath);
        if (!Directory.Exists(markoutsDirectoryPath))
            return;
        foreach (string symbolDirectoryPath in Directory.GetDirectories(markoutsDirectoryPath))
        {
            if (!_symbolDirectoryPaths.Add(symbolDirectoryPath))
                continue;
            LogReader logReader = new LogReader(symbolDirectoryPath, "*.markout");
            logReader.LiveLines += lines => Dispatcher.UIThread.Post(() => AddMarkouts(ParseLines(lines)));
            logReader.Start();
            _ = LoadHistoryAsync(logReader);
            _logReaders.Add(logReader);
        }
    }

    /// Reads the whole history back, a batch at a time, then adds it at once.
    private async Task LoadHistoryAsync(LogReader logReader)
    {
        List<Markout> history = await Task.Run(() =>
        {
            List<Markout> _history = new();
            for (List<string> lines = logReader.LoadHistory(s_historyBatchSize); lines.Count > 0; lines = logReader.LoadHistory(s_historyBatchSize))
                _history.AddRange(ParseLines(lines));
            return _history;
        });
        AddMarkouts(history);
    }

    private static List<Markout> ParseLines(List<string> lines)
    {
        List<Markout> markouts = new(lines.Count);
        foreach (string line in lines)
        {
            // Why the catch: a torn last line must not take down the GUI; the line is skipped
            try { markouts.Add(Json.Deserialize<Markout>(line)); }
            catch { }
        }
        return markouts;
    }

    /// Keeps these markouts and adds them to the rows; a duration or instrument not seen before also joins its selector.
    private void AddMarkouts(List<Markout> markouts)
    {
        // Step 1: keep them, noting any new choice
        _markouts.AddRange(markouts);
        bool hasNewChoice = false;
        foreach (Markout markout in markouts)
        {
            hasNewChoice |= _markoutSecondsChoices.Add(markout.MarkoutDuration.TotalSeconds);
            if (TryGetInstrument(markout.InstrumentId, out Instrument instrument))
                hasNewChoice |= _shortSymbols.Add(instrument.ShortSymbol);
        }

        // Step 2: a new choice refills the selectors; the first instrument to appear becomes the selection, so total from the start
        bool hadInstrument = InstrumentComboBox.SelectedItem != null;
        if (hasNewChoice)
            ShowChoices();
        if (hadInstrument)
            AddToGroups(markouts);
        else
            ShowGroups();
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
        AddToGroups(_markouts);
        bool isTable = ViewComboBox.SelectedItem is MarkoutView.Table;
        MarkoutsGrid.IsVisible = isTable;
        MarkoutHistogram.IsVisible = !isTable;
    }

    /// Adds these markouts to the rows of the current selection, making a row for each new group, and redraws.
    private void AddToGroups(List<Markout> markouts)
    {
        if (GroupingComboBox.SelectedItem is not MarkoutGrouping markoutGrouping || InstrumentComboBox.SelectedItem is not string selectedShortSymbol)
            return;

        // Step 1: each markout of the selected instrument and duration goes to its group's row
        double? selectedMarkoutSeconds = MarkoutDurationComboBox.SelectedItem as double?;
        foreach (Markout markout in markouts)
        {
            // Why skipped: an instrument this context does not list has no tick size or multiplier to convert with
            if (!TryGetInstrument(markout.InstrumentId, out Instrument instrument))
                continue;
            bool isOtherInstrument = instrument.ShortSymbol != selectedShortSymbol;
            bool isOtherMarkoutDuration = selectedMarkoutSeconds.HasValue && markout.MarkoutDuration.TotalSeconds != selectedMarkoutSeconds.Value;
            if (isOtherInstrument || isOtherMarkoutDuration)
                continue;
            string group = GetGroup(in markout, markoutGrouping);
            if (!_groupsByKey.TryGetValue((group, markout.MarkoutDuration), out WidgetMarkoutGroup? widgetMarkoutGroup))
            {
                _groupsByKey[(group, markout.MarkoutDuration)] = widgetMarkoutGroup = new WidgetMarkoutGroup(group, markout.MarkoutDuration);
                InsertRow(widgetMarkoutGroup);
            }
            widgetMarkoutGroup.Add(in markout, instrument.TickSize, instrument.UsdMultiplier);
        }

        // Step 2: the rows that changed redraw in place, and so do the bars
        foreach (WidgetMarkoutGroup widgetMarkoutGroup in Rows)
            widgetMarkoutGroup.Refresh();
        MarkoutHistogram.Show(Rows);
    }

    /// Puts a new row where it belongs: groups in order, each group's markout durations shortest first.
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

    /// The instrument of this id, looked up once; false when this context does not list it.
    private bool TryGetInstrument(int instrumentId, out Instrument instrument)
    {
        if (!_instrumentsByInstrumentId.TryGetValue(instrumentId, out Instrument? cachedInstrument))
            _instrumentsByInstrumentId[instrumentId] = cachedInstrument = Widgets.TryGetInstrument(_context, instrumentId, out Instrument listedInstrument) ? listedInstrument : null;
        instrument = cachedInstrument!;
        return cachedInstrument != null;
    }

    /// The row group this markout falls in under this grouping.
    public static string GetGroup(in Markout markout, MarkoutGrouping markoutGrouping)
    {
        DateTime chicagoDateTime = Session.CME.ConvertToLocal(markout.FillExchangeTimestamp);
        return markoutGrouping switch
        {
            MarkoutGrouping.Day => chicagoDateTime.ToString("yyyy-MM-dd"),
            MarkoutGrouping.TimeOfDay => chicagoDateTime.ToString("HH") + ":00",
            MarkoutGrouping.Side => ((Side)Math.Sign(markout.FillQuantity)).ToString(),
            MarkoutGrouping.FillType => markout.FillType.ToString(),
            MarkoutGrouping.Tag => markout.MarkoutTag.ToString(),
            _ => "All",
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

    /// The columns' layout, the four selections and the histogram's unit.
    public string? SaveStateJson() => Json.Serialize(new MarkoutsWidgetState
    {
        Columns = DataGridColumnState.NewDataGridColumnStates(MarkoutsGrid, DataGridColumnState.GetHeader),
        MarkoutView = (MarkoutView)ViewComboBox.SelectedItem!,
        MarkoutGrouping = (MarkoutGrouping)GroupingComboBox.SelectedItem!,
        ShortSymbol = InstrumentComboBox.SelectedItem as string,
        MarkoutSeconds = MarkoutDurationComboBox.SelectedItem as double?,
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Widget;

// One regex filter per column, keyed by the column's stable base name; rows must pass every active
// filter (AND). Shared by the grid widgets: owns the filter map, the header decoration
// ("Symbol (^es)"), the header context-menu section, and the prompt dialog. A widget keeps only its
// column-text map and an apply callback that re-filters its rows.
public sealed class ColumnRegexFilters<TRow>
{
    private sealed class ColumnFilter
    {
        public string? Pattern;
        public Regex? Regex;
        public bool IsEmpty => Regex == null;
    }

    private readonly DataGrid _dataGrid;
    private readonly Dictionary<string, Func<TRow, string>> _columnText;
    private readonly Action _applyFilter;
    private readonly Dictionary<string, ColumnFilter> _columnFilters = new Dictionary<string, ColumnFilter>();
    private readonly Dictionary<DataGridColumn, string> _columnBaseNames = new Dictionary<DataGridColumn, string>();

    public ColumnRegexFilters(DataGrid dataGrid, Dictionary<string, Func<TRow, string>> columnText, Action applyFilter)
    {
        _dataGrid = dataGrid;
        _columnText = columnText;
        _applyFilter = applyFilter;

        // Header text gets decorated with the active filter, so the stable identity of each column
        // is captured once here — persistence and the filter map key on it.
        foreach (DataGridColumn column in dataGrid.Columns)
            _columnBaseNames[column] = column.Header?.ToString() ?? "";
    }

    // Row must pass every active column regex (AND).
    public bool Matches(TRow row)
    {
        foreach (KeyValuePair<string, ColumnFilter> pair in _columnFilters)
        {
            if (!_columnText.TryGetValue(pair.Key, out Func<TRow, string>? text))
                continue;
            if (pair.Value.Regex != null && !pair.Value.Regex.IsMatch(text(row)))
                return false;
        }
        return true;
    }

    public bool IsFilterable(string column) => _columnText.ContainsKey(column);

    public string GetBaseName(DataGridColumn column) =>
        _columnBaseNames.TryGetValue(column, out string? name) ? name : column.Header?.ToString() ?? "";

    // Active (column, pattern) pairs, for persistence.
    public List<(string Column, string Pattern)> GetActivePatterns()
    {
        List<(string Column, string Pattern)> patterns = new List<(string, string)>(_columnFilters.Count);
        foreach (KeyValuePair<string, ColumnFilter> pair in _columnFilters)
        {
            if (pair.Value.Pattern is { Length: > 0 })
                patterns.Add((pair.Key, pair.Value.Pattern));
        }
        return patterns;
    }

    // Appends the regex-filter section for a right-clicked header; no-op for unfilterable columns.
    public void AddMenuItems(ContextMenu menu, string? clickedHeaderText)
    {
        string? clickedColumn = ResolveBaseName(clickedHeaderText);
        if (clickedColumn == null || !_columnText.ContainsKey(clickedColumn))
            return;

        string column = clickedColumn;
        _columnFilters.TryGetValue(column, out ColumnFilter? active);
        var regexItem = new MenuItem
        {
            Header = active?.Pattern is { Length: > 0 } pattern ? $"Filter {column}: /{pattern}/ …" : $"Filter {column} (regex)…"
        };
        regexItem.Click += async (_, _) => await PromptForRegexFilter(column);
        menu.Items.Add(regexItem);

        if (_columnFilters.ContainsKey(column))
        {
            var clearItem = new MenuItem { Header = $"Clear {column} filter" };
            clearItem.Click += (_, _) => ClearColumnFilter(column);
            menu.Items.Add(clearItem);
        }
        if (_columnFilters.Count > 0)
        {
            var clearAllItem = new MenuItem { Header = "Clear all filters" };
            clearAllItem.Click += (_, _) => ClearAllFilters();
            menu.Items.Add(clearAllItem);
        }
        menu.Items.Add(new Separator());
    }

    public void SetRegexFilter(string column, string pattern, Regex regex)
    {
        ColumnFilter filter = GetOrAddFilter(column);
        filter.Pattern = pattern;
        filter.Regex = regex;
        Apply();
    }

    public void ClearRegexFilter(string column)
    {
        if (_columnFilters.TryGetValue(column, out ColumnFilter? filter))
        {
            filter.Pattern = null;
            filter.Regex = null;
            PruneFilter(column);
        }
        Apply();
    }

    public void ClearColumnFilter(string column)
    {
        _columnFilters.Remove(column);
        Apply();
    }

    public void ClearAllFilters()
    {
        _columnFilters.Clear();
        Apply();
    }

    private void Apply()
    {
        _applyFilter();
        UpdateColumnHeaders();
    }

    // Headers show their active filter: "ShortSymbol (^es)", "InstrumentType (Future|Spread)".
    // Display-only — every lookup keys on the base name captured at construction.
    private void UpdateColumnHeaders()
    {
        foreach (DataGridColumn column in _dataGrid.Columns)
        {
            string baseName = GetBaseName(column);
            string decorated = baseName;
            if (_columnFilters.TryGetValue(baseName, out ColumnFilter? filter))
            {
                if (filter.Pattern is { Length: > 0 })
                    decorated = $"{baseName} ({filter.Pattern})";
            }
            if (!string.Equals(column.Header?.ToString(), decorated, StringComparison.Ordinal))
                column.Header = decorated;
        }
    }

    // A right-clicked header carries the decorated text; resolve it back to the column's identity.
    private string? ResolveBaseName(string? headerText)
    {
        if (headerText == null || _columnText.ContainsKey(headerText))
            return headerText;
        int suffix = headerText.IndexOf(" (", StringComparison.Ordinal);
        if (suffix > 0 && _columnText.ContainsKey(headerText[..suffix]))
            return headerText[..suffix];
        return headerText;
    }

    private ColumnFilter GetOrAddFilter(string column)
    {
        if (!_columnFilters.TryGetValue(column, out ColumnFilter? filter))
        {
            filter = new ColumnFilter();
            _columnFilters[column] = filter;
        }
        return filter;
    }

    private void PruneFilter(string column)
    {
        if (_columnFilters.TryGetValue(column, out ColumnFilter? filter) && filter.IsEmpty)
            _columnFilters.Remove(column);
    }

    // Code-built prompt: regex applies on commit only; an invalid pattern shows its parse error and
    // keeps the previous filter untouched.
    private async Task PromptForRegexFilter(string column)
    {
        if (TopLevel.GetTopLevel(_dataGrid) is not Window owner)
            return;

        _columnFilters.TryGetValue(column, out ColumnFilter? existing);

        TextBox input = new TextBox { Text = existing?.Pattern ?? "", PlaceholderText = "regex, e.g. ^SR1|ES  (case-insensitive)" };
        TextBlock error = new TextBlock { Foreground = Brushes.Red, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        Button apply = new Button { Content = "Apply", IsDefault = true };
        Button clear = new Button { Content = "Clear", IsEnabled = existing?.Regex != null };
        Button cancel = new Button { Content = "Cancel", IsCancel = true };

        Window dialog = new Window
        {
            Title = $"Filter {column}",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    input,
                    error,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, clear, cancel } }
                }
            }
        };

        apply.Click += (_, _) =>
        {
            string pattern = input.Text ?? "";
            if (pattern.Length == 0)
            {
                ClearRegexFilter(column);
                dialog.Close();
                return;
            }
            try
            {
                Regex regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                SetRegexFilter(column, pattern, regex);
                dialog.Close();
            }
            catch (ArgumentException ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        };
        clear.Click += (_, _) => { ClearRegexFilter(column); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => input.Focus();

        await dialog.ShowDialog(owner);
    }
}

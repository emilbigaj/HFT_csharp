using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tools;

namespace Widget;

/// One regex filter per column, keyed by the column's stable base name; rows must pass every active filter (AND). Shared by
/// the grid widgets: owns the filter map, the header decoration ("Symbol (^es)"), the header context-menu section, the prompt
/// dialog and the filters' saved state. A widget keeps only its column-text map and an apply callback that re-filters its rows.
/// UI thread, cold path.
public sealed class ColumnRegexFilters<TRow>
{
    private readonly DataGrid _dataGrid;
    private readonly Dictionary<string, Func<TRow, string>> _columnTextByColumn;
    private readonly Action _applyFilter;
    private readonly Dictionary<string, Regex> _regexesByColumn = new();   // a filter's pattern is its regex's ToString()
    private readonly Dictionary<DataGridColumn, string> _baseNamesByColumn = new();

    public ColumnRegexFilters(DataGrid dataGrid, Dictionary<string, Func<TRow, string>> columnTextByColumn, Action applyFilter)
    {
        _dataGrid = dataGrid;
        _columnTextByColumn = columnTextByColumn;
        _applyFilter = applyFilter;

        // Why captured once: the header text gets decorated with the active filter, so persistence and the filter map key on this
        foreach (DataGridColumn dataGridColumn in dataGrid.Columns)
            _baseNamesByColumn[dataGridColumn] = dataGridColumn.Header?.ToString() ?? "";
    }

    /// True when the row passes every active column regex.
    public bool Matches(TRow row)
    {
        foreach ((string column, Regex regex) in _regexesByColumn)
        {
            if (!regex.IsMatch(_columnTextByColumn[column](row)))
                return false;
        }
        return true;
    }

    /// The column's name before any filter decorated its header.
    public string GetBaseName(DataGridColumn dataGridColumn) =>
        _baseNamesByColumn.TryGetValue(dataGridColumn, out string? baseName) ? baseName : dataGridColumn.Header?.ToString() ?? "";

    /// The active filters, to save with the widget's layout.
    public List<ColumnRegexFilterState> NewColumnRegexFilterStates() =>
        _regexesByColumn.Select(regexByColumn => new ColumnRegexFilterState { Column = regexByColumn.Key, Pattern = regexByColumn.Value.ToString() }).ToList();

    /// Replaces every filter with the saved ones; a saved filter on a column that is no longer filterable, or whose pattern no longer parses, is dropped.
    public void ApplyColumnRegexFilterStates(IEnumerable<ColumnRegexFilterState> columnRegexFilterStates)
    {
        ClearAllFilters();
        foreach (ColumnRegexFilterState columnRegexFilterState in columnRegexFilterStates)
        {
            if (!string.IsNullOrEmpty(columnRegexFilterState.Pattern) && _columnTextByColumn.ContainsKey(columnRegexFilterState.Column))
                TrySetRegexFilter(columnRegexFilterState.Column, columnRegexFilterState.Pattern, out _);
        }
    }

    /// Appends the regex-filter section for a right-clicked header; nothing for an unfilterable column.
    public void AddMenuItems(ContextMenu contextMenu, string? clickedHeaderText)
    {
        // Step 1: the right-clicked header's column, if it is filterable
        string? column = GetBaseNameOfHeader(clickedHeaderText);
        if (column == null || !_columnTextByColumn.ContainsKey(column))
            return;

        // Step 2: set the filter, clear it, or clear every filter
        bool hasRegex = _regexesByColumn.TryGetValue(column, out Regex? regex);
        contextMenu.Items.Add(WidgetMenus.NewMenuItem(hasRegex ? $"Filter {column}: /{regex}/ …" : $"Filter {column} (regex)…", iconText: "", async () => await PromptForRegexFilter(column)));
        if (hasRegex)
            contextMenu.Items.Add(WidgetMenus.NewMenuItem($"Clear {column} filter", iconText: "", () => ClearColumnFilter(column)));
        if (_regexesByColumn.Count > 0)
            contextMenu.Items.Add(WidgetMenus.NewMenuItem("Clear all filters", iconText: "", ClearAllFilters));
        contextMenu.Items.Add(new Separator());
    }

    /// Filters the column by the pattern, case-insensitive, and re-filters the rows. Returns false with the parse error, keeping the previous filter, when the pattern is not a valid regex.
    private bool TrySetRegexFilter(string column, string pattern, out string? errorMessage)
    {
        try
        {
            _regexesByColumn[column] = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException argumentException)
        {
            errorMessage = argumentException.Message;
            return false;
        }
        errorMessage = null;
        Apply();
        return true;
    }

    private void ClearColumnFilter(string column)
    {
        _regexesByColumn.Remove(column);
        Apply();
    }

    private void ClearAllFilters()
    {
        _regexesByColumn.Clear();
        Apply();
    }

    private void Apply()
    {
        _applyFilter();
        UpdateColumnHeaders();
    }

    /// Shows each column's active filter in its header: "ShortSymbol (^es)", "InstrumentType (Future|Spread)". Display only: every lookup keys on the base name.
    private void UpdateColumnHeaders()
    {
        foreach (DataGridColumn dataGridColumn in _dataGrid.Columns)
        {
            string baseName = GetBaseName(dataGridColumn);
            string header = _regexesByColumn.TryGetValue(baseName, out Regex? regex) ? $"{baseName} ({regex})" : baseName;
            if (!string.Equals(dataGridColumn.Header?.ToString(), header, StringComparison.Ordinal))
                dataGridColumn.Header = header;
        }
    }

    /// The base name of the column whose header, decorated or not, reads headerText; null when none does.
    private string? GetBaseNameOfHeader(string? headerText)
    {
        DataGridColumn? dataGridColumn = headerText == null ? null : _dataGrid.Columns.FirstOrDefault(column => column.Header?.ToString() == headerText);
        return dataGridColumn == null ? null : GetBaseName(dataGridColumn);
    }

    /// Prompts for the column's pattern: it applies on commit only, and an invalid pattern shows its parse error and keeps the previous filter.
    private async Task PromptForRegexFilter(string column)
    {
        // Step 1: the dialog, opened on the current pattern
        if (TopLevel.GetTopLevel(_dataGrid) is not Window ownerWindow)
            return;
        _regexesByColumn.TryGetValue(column, out Regex? regex);
        TextBox patternTextBox = new() { Text = regex?.ToString() ?? "", PlaceholderText = "regex, e.g. ^SR1|ES  (case-insensitive)" };
        TextBlock errorTextBlock = new() { Foreground = Brushes.Red, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        Button applyButton = new() { Content = "Apply", IsDefault = true };
        Button clearButton = new() { Content = "Clear", IsEnabled = regex != null };
        Button cancelButton = new() { Content = "Cancel", IsCancel = true };
        Window dialogWindow = new()
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
                    patternTextBox,
                    errorTextBlock,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { applyButton, clearButton, cancelButton } }
                }
            }
        };

        // Step 2: apply sets the pattern (an empty one clears), clear clears, cancel leaves the filter as it was
        applyButton.Click += (_, _) =>
        {
            string pattern = patternTextBox.Text ?? "";
            if (pattern.Length == 0)
                ClearColumnFilter(column);
            else if (!TrySetRegexFilter(column, pattern, out string? errorMessage))
            {
                errorTextBlock.Text = errorMessage;
                errorTextBlock.IsVisible = true;
                return;
            }
            dialogWindow.Close();
        };
        clearButton.Click += (_, _) => { ClearColumnFilter(column); dialogWindow.Close(); };
        cancelButton.Click += (_, _) => dialogWindow.Close();
        dialogWindow.Opened += (_, _) => patternTextBox.Focus();

        await dialogWindow.ShowDialog(ownerWindow);
    }
}

/// One saved column filter: the column's base name and its regex pattern. Cold path: a workspace's save and load.
[RegisterJson]
public sealed class ColumnRegexFilterState
{
    public string Column { get; set; } = "";
    public string? Pattern { get; set; }
}

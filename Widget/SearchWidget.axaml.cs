using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Data;
using Provider;

namespace Widget;

public partial class SearchWidget : Window
{
    private readonly Context _context;
    
    // Static cache: Symbol -> InstrumentIndex (position in headers)
    // Using ConcurrentDictionary for thread safety across multiple widget instances
    private static readonly ConcurrentDictionary<string, int> _symbolCache = new();
    private static bool _isCacheLoaded = false;

    public SearchWidget()
    {
        InitializeComponent();
        _context = null!; // Designer support
    }

    public SearchWidget(Context context)
    {
        InitializeComponent();
        _context = context ?? throw new ArgumentNullException(nameof(context));
        
        // Ensure cache is populated when the widget opens
        EnsureCacheLoaded();
        
        // Focus the text box immediately
        Opened += (_, _) => SearchBox.Focus();
    }

    private void EnsureCacheLoaded()
    {
        if (_isCacheLoaded) return;

        // Populate cache from headers
        foreach (var header in _context.EnumerateInstrumentHeaders())
        {
            string symbol = header.Symbology.Symbol;
            if (!string.IsNullOrEmpty(symbol))
            {
                _symbolCache.TryAdd(symbol, header.AsInstrumentHeader().InstrumentHeaderId);
            }
        }

        _isCacheLoaded = true;
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(query))
        {
            ResultsList.ItemsSource = null;
            return;
        }

        // Perform case-insensitive search on the cached keys
        // Ordering by key for consistent list presentation
        var matches = _symbolCache.Keys
            .Where(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .Take(50) 
            .ToList();

        ResultsList.ItemsSource = matches;
    }

    private void OnResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is string symbol)
        {
            ResolveAndClose(symbol);
        }
    }

    private void ResolveAndClose(string symbol)
    {
        if (_symbolCache.TryGetValue(symbol, out int index))
        {
            // 1. Get Instrument ID using the index
            if (_context.TryGetInstrumentId(index, out int instrumentId))
            {
                // 2. Get the actual Instrument object
                Instrument instrument = _context.GetInstrument(instrumentId);
                
                // 3. Return result
                Close(instrument);
                return;
            }
        }

        // Fallback or cancel
        Close(null);
    }
}
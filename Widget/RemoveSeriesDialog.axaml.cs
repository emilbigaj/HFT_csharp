using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Chart;

namespace Widget;

public partial class RemoveSeriesDialog : Window
{
    public RemoveSeriesDialog()
    {
        InitializeComponent();
    }

    public RemoveSeriesDialog(IEnumerable<ISeries> series) : this()
    {
        SeriesList.ItemsSource = series.ToList();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        var selected = SeriesList.SelectedItems?.Cast<ISeries>().ToList();
        Close(selected);
    }
}
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Widget;

/// A list of names to tick and a button that confirms the ticked ones: which series to remove from a chart, which values
/// of a statistic's file to chart. Closes with the ticked names, or null when cancelled. UI thread; cold path.
public partial class SelectNamesDialog : Window
{
    public SelectNamesDialog()
    {
        InitializeComponent();
    }

    public SelectNamesDialog(string title, string confirmText, IEnumerable<string> names) : this()
    {
        Title = title;
        ConfirmButton.Content = confirmText;
        NamesList.ItemsSource = names.ToList();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(NamesList.SelectedItems?.Cast<string>().ToList());
}

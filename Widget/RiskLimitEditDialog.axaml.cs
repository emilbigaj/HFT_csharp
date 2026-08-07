using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Execution;
using Tools;

namespace Widget;

/// <summary>
/// Edits the quantity limits of a single RiskLimit. Seeded with a copy of the live limit and
/// returns that same copy with only the edited fields overwritten, so the rate limits and every
/// other field survive the round trip untouched — the server applies whatever it is handed.
/// Closes with the edited RiskLimit on confirm, or null on cancel.
/// </summary>
public partial class RiskLimitEditDialog : Window
{
    private RiskLimit _riskLimit;

    public RiskLimitEditDialog()
    {
        InitializeComponent();
    }

    public RiskLimitEditDialog(string symbol, RiskLimit riskLimit) : this()
    {
        _riskLimit = riskLimit;
        SymbolText.Text = symbol;
        MaxOrderQuantityInput.Text = riskLimit.MaxOrderQuantity.ToString();
        MaxPositionQuantityInput.Text = riskLimit.MaxPositionQuantity.ToString();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (!TryParseQuantity(MaxOrderQuantityInput.Text, out int maxOrderQuantity))
        {
            ShowError("Max Order Qty must be a whole number >= 0.");
            return;
        }

        if (!TryParseQuantity(MaxPositionQuantityInput.Text, out int maxPositionQuantity))
        {
            ShowError("Max Position Qty must be a whole number >= 0.");
            return;
        }

        _riskLimit.MaxOrderQuantity = maxOrderQuantity;
        _riskLimit.MaxPositionQuantity = maxPositionQuantity;
        _riskLimit.Timestamp = Clock.Now;

        Close(_riskLimit);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private static bool TryParseQuantity(string? text, out int quantity)
    {
        quantity = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Accept the grouped form the grid displays ("1,000") so a copied value pastes back in.
        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.CurrentCulture, out quantity))
            return false;

        return quantity >= 0;
    }
}

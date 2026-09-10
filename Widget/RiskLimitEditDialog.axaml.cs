using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Execution;
using Provider;
using Tools;

namespace Widget;

/// <summary>
/// Edits the quantity limits of a single RiskLimit. Seeded with the live limit for display and
/// closes with a ControlRiskLimit request (config fields only) on confirm, or null on cancel. The
/// server owns the row: it applies the request in place and stamps the timestamp.
/// </summary>
public partial class RiskLimitEditDialog : Window
{
    private ControlRiskLimit _controlRiskLimit;

    public RiskLimitEditDialog()
    {
        InitializeComponent();
    }

    public RiskLimitEditDialog(string symbol, RiskLimit riskLimit) : this()
    {
        _controlRiskLimit = new ControlRiskLimit
        {
            InstrumentId = riskLimit.InstrumentId,
            MaxOrderQuantity = riskLimit.MaxOrderQuantity,
            MaxPositionQuantity = riskLimit.MaxPositionQuantity,
        };
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

        _controlRiskLimit.MaxOrderQuantity = maxOrderQuantity;
        _controlRiskLimit.MaxPositionQuantity = maxPositionQuantity;

        Close(_controlRiskLimit);
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

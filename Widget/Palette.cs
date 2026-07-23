//BEGIN_FILE Widget/Palette.cs
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Data;
using System;
using System.Globalization;

namespace Widget;

public static class Palette
{
    // --- RAW SOLARIZED DEFINITIONS ---
    public static readonly Color SolBase03 = Color.Parse("#002b36");
    public static readonly Color SolBase02 = Color.Parse("#073642");
    public static readonly Color SolBase01 = Color.Parse("#586e75");
    public static readonly Color SolBase00 = Color.Parse("#657b83");
    public static readonly Color SolBase0 = Color.Parse("#839496");
    public static readonly Color SolBase1 = Color.Parse("#93a1a1");
    public static readonly Color SolBase2 = Color.Parse("#eee8d5");
    public static readonly Color SolBase3 = Color.Parse("#fdf6e3");
    public static readonly Color SolYellow = Color.Parse("#b58900");
    public static readonly Color SolOrange = Color.Parse("#cb4b16");
    public static readonly Color SolRed = Color.Parse("#dc322f");
    public static readonly Color SolMagenta = Color.Parse("#d33682");
    public static readonly Color SolViolet = Color.Parse("#6c71c4");
    public static readonly Color SolBlue = Color.Parse("#268bd2");
    public static readonly Color SolCyan = Color.Parse("#2aa198");
    public static readonly Color SolGreen = Color.Parse("#859900");

    // --- CUSTOM COLORS ---

    public static readonly Color CustomAskActive = Color.Parse("#AF47D2"); // Lighter Bright Purple
    public static readonly Color CustomAskEmpty = Color.Parse("#420D33");  // Warm Dark Plum
    public static readonly Color CustomBrightGreen = Color.Parse("#32CD32"); // Lime Green

    // --- MESSAGE EFFICIENCY STATUS (traffic-light; Lighten() them for cell backgrounds) ---
    public static readonly Color SafeGreen = Color.Parse("#27AE60");
    public static readonly Color WarningYellow = Color.Parse("#F1C40F");
    public static readonly Color CautionOrange = Color.Parse("#E67E22");
    public static readonly Color CriticalRed = Color.Parse("#E74C3C");

    // --- LADDER / ORDERS / FILLS ---

    public static readonly IBrush BidEmptyDarkBlue = new ImmutableSolidColorBrush(SolBase03);
    public static readonly IBrush BidActiveBlue = new ImmutableSolidColorBrush(SolBlue);
    public static readonly IBrush BidTextCream = new ImmutableSolidColorBrush(SolBase3);

    public static readonly IBrush AskEmptyDarkPurple = new ImmutableSolidColorBrush(CustomAskEmpty);
    public static readonly IBrush AskActivePurple = new ImmutableSolidColorBrush(CustomAskActive);
    public static readonly IBrush AskTextCream = new ImmutableSolidColorBrush(SolBase3);

    public static readonly ISolidColorBrush BuyOrder = GetRowBackground(BidActiveBlue, 0.2);
    public static readonly ISolidColorBrush SellOrder = GetRowBackground(AskActivePurple, 0.2);
    public static readonly IBrush Flat = new ImmutableSolidColorBrush(Colors.White);



    public static readonly IBrush WorkEmptyLightGray = new ImmutableSolidColorBrush(Color.FromRgb(211, 211, 211));
    public static readonly IBrush WorkActiveWhite = new ImmutableSolidColorBrush(Colors.White);
    public static readonly IBrush WorkTextBlack = new ImmutableSolidColorBrush(Colors.Black);

    public static readonly IBrush PriceBackgroundLightGray = new ImmutableSolidColorBrush(Color.FromRgb(240, 240, 240));
    public static readonly IBrush PriceTextBlack = new ImmutableSolidColorBrush(Colors.Black);

    public static readonly IPen GridLineGrayPen = new ImmutablePen(0xFFBEBEBE, 1.0);
    public static readonly IBrush HeaderBackgroundLightGray = new ImmutableSolidColorBrush(Color.FromRgb(220, 220, 220));
    public static readonly IBrush HeaderTextBlack = new ImmutableSolidColorBrush(Colors.Black);

    public static readonly IBrush ProfitGreen = new ImmutableSolidColorBrush(CustomBrightGreen);
    public static readonly IBrush LossRed = new ImmutableSolidColorBrush(SolRed);

    public static readonly ISolidColorBrush ProfitLightGreen = GetRowBackground(ProfitGreen, 0.2);
    public static readonly ISolidColorBrush LossLightRed = GetRowBackground(LossRed, 0.2);

    public static readonly IBrush WorkspaceRealtimeGreen = new ImmutableSolidColorBrush(Color.FromRgb(0x6A, 0xC3, 0x5B));
    public static readonly IBrush WorkspaceSimulationYellow = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0x99));

    public static readonly IBrush[] ChartSeriesColors = new IBrush[]
    {
        Brushes.Blue, Brushes.Red, Brushes.Green, Brushes.Orange,
        Brushes.Purple, Brushes.Cyan, Brushes.Magenta, Brushes.Black
    };

    public static ISolidColorBrush GetRowBackground(IBrush solidBrush, double opacity = 0.2)
    {
        if (solidBrush is ISolidColorBrush scb)
        {
            return new SolidColorBrush(scb.Color, opacity);
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    /// <summary>
    /// Lightens a color by a factor (0.0 to 1.0).
    /// </summary>
    public static Color Lighten(Color color, double factor)
    {
        return Color.FromRgb(
            (byte)Math.Min(255, color.R + (255 - color.R) * factor),
            (byte)Math.Min(255, color.G + (255 - color.G) * factor),
            (byte)Math.Min(255, color.B + (255 - color.B) * factor));
    }
}


public class SideToBrushConverter : IValueConverter
{
    

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Side side)
        {
            return side == Side.Buy ? Palette.BuyOrder : side == Side.Sell ? Palette.SellOrder : Palette.Flat;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}


public class PnLToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double pnl)
        {
            if (pnl > 0) return Palette.ProfitLightGreen;
            if (pnl < 0) return Palette.LossLightRed;
        }
        return Palette.Flat;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PnLToTextBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double number = 0;
        if (value is double d) number = d;
        else if (value is int i) number = i;
        else return Brushes.Black;

        if (number > 0) return Palette.ProfitGreen;
        if (number < 0) return Palette.LossRed;

        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
//END_FILE Widget/Palette.cs
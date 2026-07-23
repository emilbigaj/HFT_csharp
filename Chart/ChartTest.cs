//BEGIN_FILE HFT/Chart/ChartTest.cs
using System;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Dialogs;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Data;
using Tools;

namespace Chart;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public static class ChartTest
{
    public static void Run()
    {
        AppBuilder.Configure<ChartTestApp>()
                  .UsePlatformDetect()
                  .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } })
                  .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
                  .UseManagedSystemDialogs()
                  .LogToTrace();
    }
}

internal sealed class ChartTestApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ChartTestWindow();
    }
}

internal sealed class ChartTestWindow : Window
{
    private const int PanelCount = 6;
    private const int InitialPoints = 1_000_000;

    private static readonly long StepNs =
        (long)(TimeSpan.FromDays(365).TotalMilliseconds * 1_000_000)
        / InitialPoints;

    private static readonly long StartTimestampNs =
        (new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch)
        .Ticks * 100L;

    private readonly ChartControl _chartControl;
    private ChartStack? _stack;
    private readonly ISeries[] _seriesArray = new ISeries[PanelCount];
    private readonly FillSeries?[] _fillSeriesArray = new FillSeries?[PanelCount];
    private readonly double[] _lastValues = new double[PanelCount];
    private double _nextX;

    public ChartTestWindow()
    {
        Title = "Chart Test";
        Width = 1600;
        Height = 900;
        MinWidth = 800;
        MinHeight = 600;

        _chartControl = new ChartControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0)
        };

        Content = _chartControl;

        CreateData();
        StartTimer();
    }

    private void CreateData()
    {
        long domainEnd = StartTimestampNs + (InitialPoints - 1L) * StepNs;
        _stack = new ChartStack();
        var rng = new Random();

        for (int i = 0; i < PanelCount; i++)
        {
            var panel = new Chart($"Panel {i + 1}");
            _stack.AddPanel(panel);
            _lastValues[i] = 1000.0 + i * 100.0;

            if (i % 2 == 0)
            {
                // Point Series
                var color = Color.FromRgb((byte)(50 + i * 20), (byte)(80 + i * 40), (byte)(120 + i * 60));
                var series = new PointSeries($"Line {i + 1}", new SolidColorBrush(color), 1.0, YAxisSide.Right, InitialPoints);
                panel.AddSeries(series);
                _seriesArray[i] = series;
            }
            else
            {
                // Candle Series + Fill Series overlaid
                var series = new CandleSeries($"Candles {i + 1}", Brushes.Green, Brushes.Red, YAxisSide.Right, InitialPoints);
                panel.AddSeries(series);
                _seriesArray[i] = series;

                // Add random fills to this candle panel
                var fills = new FillSeries($"Fills {i + 1}", Brushes.Lime, Brushes.Green, Brushes.OrangeRed, Brushes.Red, YAxisSide.Right, 1024);
                panel.AddSeries(fills);
                _fillSeriesArray[i] = fills;
            }
        }

        // Seed data
        for (int pointIndex = 0; pointIndex < InitialPoints; pointIndex++)
        {
            long xNs = StartTimestampNs + pointIndex * StepNs;
            var ts = new Timestamp(xNs);

            for (int i = 0; i < PanelCount; i++)
            {
                if (_seriesArray[i] is PointSeries ps)
                {
                    _lastValues[i] += (rng.NextDouble() - 0.5) * 2.0;
                    ps.Append(ts, _lastValues[i]);
                }
                else if (_seriesArray[i] is CandleSeries cs)
                {
                    double open = _lastValues[i];
                    double change = (rng.NextDouble() - 0.5) * 5.0;
                    double close = open + change;
                    double high = Math.Max(open, close) + rng.NextDouble() * 2.0;
                    double low = Math.Min(open, close) - rng.NextDouble() * 2.0;
                    _lastValues[i] = close;

                    cs.Append(new Candle(ts, open) { High = high, Low = low, Close = close });

                    // Randomly add fills
                    if (rng.NextDouble() < 0.01 && _fillSeriesArray[i] != null)
                    {
                        double qty = (rng.NextDouble() - 0.5) * 10.0;
                        // Buy at low, Sell at high (visualizing executions)
                        double price = qty > 0 ? low : high;
                        _fillSeriesArray[i]!.Append(new Filld(ts, price, qty, FillType.Maker));
                    }
                }
            }
        }

        _stack.RecalculateDomainFromData();
        _nextX = domainEnd + StepNs;
        _chartControl.Stack = _stack;
    }

    private void StartTimer()
    {
        var rng = new Random();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            long xNs = (long)_nextX;
            _nextX += StepNs;
            var ts = new Timestamp(xNs);

            for (int i = 0; i < PanelCount; i++)
            {
                if (_seriesArray[i] is PointSeries ps)
                {
                    _lastValues[i] += (rng.NextDouble() - 0.5) * 2.0;
                    ps.Append(ts, _lastValues[i]);
                }
                else if (_seriesArray[i] is CandleSeries cs)
                {
                    double open = _lastValues[i];
                    double change = (rng.NextDouble() - 0.5) * 5.0;
                    double close = open + change;
                    double high = Math.Max(open, close) + rng.NextDouble() * 2.0;
                    double low = Math.Min(open, close) - rng.NextDouble() * 2.0;
                    _lastValues[i] = close;

                    cs.Append(new Candle(ts, open) { High = high, Low = low, Close = close });

                    if (rng.NextDouble() < 0.1 && _fillSeriesArray[i] != null)
                    {
                        double qty = (rng.NextDouble() - 0.5) * 10.0;
                        double price = qty > 0 ? low : high;
                        _fillSeriesArray[i]!.Append(new Filld(ts, price, qty, FillType.Maker));
                    }
                }
            }
        };
        timer.Start();
    }
}
//END_FILE HFT/Chart/ChartTest.cs
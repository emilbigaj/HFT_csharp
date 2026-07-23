using System;
using System.Collections.Generic;
using Avalonia;
using Tools;

namespace Chart;

public enum YAxisSide { Left, Right }

public struct TimeRange
{
    public double Start;
    public double End;
    public double Span => End - Start;

    public TimeRange(double start, double end)
    {
        if (end <= start) end = start + 1.0;
        Start = start; End = end;
    }

    public void Pan(double delta) { Start += delta; End += delta; }

    public void Zoom(double factor, double anchor)
    {
        double span = Span;
        double newSpan = span * factor;
        if (newSpan <= 0.0) return;
        double anchorRatio = (anchor - Start) / span;
        Start = anchor - (anchorRatio * newSpan);
        End = Start + newSpan;
    }

    public void Clamp(double minStart, double maxEnd, double minSpan)
    {
        if (minSpan <= 0.0) minSpan = 1.0;
        double domainSpan = maxEnd - minStart;
        double currentSpan = End - Start;

        // 1. Enforce Minimum Zoom
        if (currentSpan < minSpan) currentSpan = minSpan;

        // 2. Handle Empty/Invalid Domain
        if (domainSpan <= 0.0)
        {
            End = Start + currentSpan;
            return;
        }

        // 3. Max Zoom Cap
        if (currentSpan > domainSpan)
        {
            currentSpan = domainSpan;
            End = maxEnd;
            Start = maxEnd - currentSpan;
            return;
        }

        // 4. Edge Clamping
        if (End > maxEnd)
        {
            End = maxEnd;
            Start = End - currentSpan;
        }

        if (Start < minStart)
        {
            Start = minStart;
            End = Start + currentSpan;
        }
    }
}

public sealed class YAxis
{
    public double Minimum;
    public double Maximum;
    public double TickStep { get; set; } = 1.0;
    public double Span => Maximum - Minimum;

    public YAxis(double minimum = 0.0, double maximum = 0.0) => SetRange(minimum, maximum);

    public void SetRange(double min, double max)
    {
        if (max < min) max = min;
        Minimum = min; Maximum = max;
    }
}

public static class AxisAlgo
{
    public static double CalculateNiceStep(double range, double targetSteps)
    {
        if (range <= 0.0) return 1.0;
        double rawStep = range / targetSteps;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        double fraction = rawStep / mag;

        double niceFraction;
        if (fraction <= 1.0) niceFraction = 1.0;
        else if (fraction <= 1.25) niceFraction = 1.25;
        else if (fraction <= 2.0) niceFraction = 2.0;
        else if (fraction <= 2.5) niceFraction = 2.5;
        else if (fraction <= 4.0) niceFraction = 4.0;
        else if (fraction <= 5.0) niceFraction = 5.0;
        else niceFraction = 10.0;

        double step = niceFraction * mag;
        return step;
    }

    public static void FitAxisToData(YAxis axis, IEnumerable<ISeries> allSeries, double xMin, double xMax, double plotHeight)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        bool found = false;

        foreach (var s in allSeries)
        {
            if (!s.IsVisible) continue;
            if (s.TryGetRange(xMin, xMax, out double sMin, out double sMax))
            {
                if (sMin < min) min = sMin;
                if (sMax > max) max = sMax;
                found = true;
            }
        }

        if (!found) return;

        double rawRange = max - min;
        const double targetPixelSpacing = 50.0;
        double targetSteps = Math.Max(2.0, plotHeight / targetPixelSpacing);
        double step = CalculateNiceStep(rawRange, targetSteps);

        axis.TickStep = step;

        // 15px padding
        double unitsPerPx = rawRange / plotHeight;
        if (unitsPerPx <= 0) unitsPerPx = step / 40.0;
        double padding = 15.0 * unitsPerPx;

        axis.SetRange(min - padding, max + padding);
    }
}

public static class MathUtils
{
    public static int BinarySearchLeft(double[] xs, int length, double value)
    {
        int low = 0, high = length - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            if (xs[mid] < value) low = mid + 1; else high = mid - 1;
        }
        return low;
    }

    public static int BinarySearchRight(double[] xs, int length, double value)
    {
        int low = 0, high = length - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            if (xs[mid] <= value) { found = mid; low = mid + 1; } else high = mid - 1;
        }
        return found;
    }
}

public static class ChartTimeUtils
{
    public enum TickUnit { Year, Month, Week, Day, Hour, Minute, Second, Millisecond, Microsecond }
    public readonly struct TimeTicks
    {
        public TimeTicks(double[] values, TickUnit unit, int step)
        { Values = values; Unit = unit; Step = step; }
        public double[] Values { get; }
        public TickUnit Unit { get; }
        public int Step { get; }
    }

    private static readonly DateTime Epoch = DateTime.UnixEpoch;
    private static DateTime FromNs(long ns) => Epoch.AddTicks(ns / 100L);
    private static long ToNs(DateTime dt) => (dt - Epoch).Ticks * 100L;

    private const long Microsecond = 10;
    private const long Millisecond = 10_000;
    private const long Second = 10_000_000;
    private const long Minute = 60L * Second;
    private const long Hour = 60L * Minute;
    private const long Day = 24L * Hour;

    public static TimeTicks GenerateTimeTicks(double xMin, double xMax, double width, double labelWidth = 65.0)
    {
        if (xMax <= xMin || width <= 1.0) return new TimeTicks(Array.Empty<double>(), TickUnit.Day, 1);

        long range = (long)(xMax - xMin) / 100L;
        int budget = Math.Max(1, (int)(width / Math.Max(18.0, labelWidth)));
        long perStep = Math.Max(1, range / budget);

        TickUnit unit; int step;

        if (perStep < Millisecond) { unit = TickUnit.Millisecond; step = 1; }  // < 1s -> 100ms
        else if (perStep < 5 * Millisecond) { unit = TickUnit.Millisecond; step = 5; }    // < 100ms -> 10ms
        else if (perStep < 10 * Millisecond) { unit = TickUnit.Millisecond; step = 10; }  // < 1s -> 100ms
        else if (perStep < 50 * Millisecond) { unit = TickUnit.Millisecond; step = 50; }  // < 1s -> 100ms
        else if (perStep < 100*Millisecond) { unit = TickUnit.Millisecond; step = 100; }  // < 1s -> 100ms
        else if (perStep < 200 * Millisecond) { unit = TickUnit.Millisecond; step = 200; }  // < 1s -> 100ms
        else if (perStep < Second) { unit = TickUnit.Second; step = 1; }
        else if (perStep < 5* Second) { unit = TickUnit.Second; step = 5; }
        else if (perStep < 10 * Second) { unit = TickUnit.Second; step = 10; }
        else if (perStep < Minute) { unit = TickUnit.Minute; step = 1; }
        else if (perStep < 10 * Minute) { unit = TickUnit.Minute; step = 10; }
        else if (perStep < Hour) { unit = TickUnit.Hour; step = 1; }
        else if (perStep < 6 * Hour) { unit = TickUnit.Hour; step = 6; }
        else if (perStep < 12 * Hour) { unit = TickUnit.Hour; step = 12; }
        else if (perStep < Day) { unit = TickUnit.Day; step = 1; }
        else if (perStep < 7 * Day) { unit = TickUnit.Week; step = 1; }
        else if (perStep < 30 * Day) { unit = TickUnit.Month; step = 1; }
        else { unit = TickUnit.Year; step = 1; }

        var vals = new List<double>();
        DateTime t = AlignUp(FromNs((long)xMin), unit, step);
        DateTime end = FromNs((long)xMax);

        if (step <= 0) step = 1;

        int count = 0;
        while (t <= end)
        {
            vals.Add(ToNs(t));
            t = AddStep(t, unit, step);
            if (++count > 500) break;
        }
        vals.Add(ToNs(t));

        return new TimeTicks(vals.ToArray(), unit, step);
    }

    public static string FormatDateLabel(double ns, TickUnit unit, int step)
    {
        DateTime dt = FromNs((long)ns);
        return unit switch
        {
            TickUnit.Year => dt.ToString("yyyy"),
            TickUnit.Month => dt.Month == 1 ? dt.ToString("yyyy") : dt.ToString("MMM"),
            TickUnit.Day or TickUnit.Week => dt.ToString("dd MMM"),
            TickUnit.Hour or TickUnit.Minute => (dt.Hour == 0 && dt.Minute == 0) ? dt.ToString("dd MMM") : dt.ToString("HH:mm"),
            TickUnit.Second => dt.ToString("HH:mm:ss"),
            TickUnit.Millisecond => dt.ToString("mm:ss.fff"),
            TickUnit.Microsecond => dt.ToString("ss.fff_fff"),
            _ => (dt.Hour == 0 && dt.Minute == 0) ? dt.ToString("dd MMM") : dt.ToString("HH:mm:ss")
        };
    }

    public static string FormatTimestampFull(double ns) => FromNs((long)ns).ToString("yyyy-MM-dd HH:mm:ss.ffffff");

    private static DateTime AlignUp(DateTime dt, TickUnit unit, int step)
    {
        switch (unit)
        {
            case TickUnit.Year: return new DateTime(((dt.Year + step - 1) / step) * step, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            case TickUnit.Month:
                int mAbs = (dt.Year - 1970) * 12 + dt.Month - 1;
                int mAlign = ((mAbs + step - 1) / step) * step;
                return new DateTime(1970 + mAlign / 12, (mAlign % 12) + 1, 1, 0, 0, 0, DateTimeKind.Utc);
            case TickUnit.Day:
                return Epoch.AddDays(((int)(dt - Epoch).TotalDays + step - 1) / step * step);
            case TickUnit.Hour:
                return new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(((dt.Hour + step - 1) / step) * step);
            case TickUnit.Minute:
                return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, DateTimeKind.Utc).AddMinutes(((dt.Minute + step - 1) / step) * step);
            case TickUnit.Second:
                return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, DateTimeKind.Utc).AddSeconds(((dt.Second + step - 1) / step) * step);
            case TickUnit.Millisecond:
                // 1 ms = 10,000 ticks
                long msTicks = 10_000L * step;
                return new DateTime(((dt.Ticks + msTicks - 1) / msTicks) * msTicks, DateTimeKind.Utc);
            case TickUnit.Microsecond:
                // 1 us = 10 ticks
                long usTicks = 10L * step;
                return new DateTime(((dt.Ticks + usTicks - 1) / usTicks) * usTicks, DateTimeKind.Utc);
            default: return dt;
        }
    }

    private static DateTime AddStep(DateTime dt, TickUnit unit, int step) => unit switch
    {
        TickUnit.Year => dt.AddYears(step),
        TickUnit.Month => dt.AddMonths(step),
        TickUnit.Week => dt.AddDays(7 * step),
        TickUnit.Day => dt.AddDays(step),
        TickUnit.Hour => dt.AddHours(step),
        TickUnit.Minute => dt.AddMinutes(step),
        TickUnit.Second => dt.AddSeconds(step),
        TickUnit.Millisecond => dt.AddMilliseconds(step),
        TickUnit.Microsecond => dt.AddTicks(step * 10L),
        _ => dt.AddSeconds(step)
    };
}

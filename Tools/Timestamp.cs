using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Tools;

// =============================== Duration (ns-precision) ===============================

[StructLayout(LayoutKind.Sequential)]
public readonly struct Duration : IComparable<Duration>, IEquatable<Duration>
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Duration(long nanoseconds) => TotalNanoseconds = nanoseconds;

    // Constants
    public const long NanosecondsPerTick = 100;
    public const long NanosecondsPerMicrosecond = 1_000;
    public const long NanosecondsPerMillisecond = 1_000_000;
    public const long NanosecondsPerSecond = 1_000_000_000;
    public const long NanosecondsPerMinute = 60 * NanosecondsPerSecond;
    public const long NanosecondsPerHour = 60 * NanosecondsPerMinute;
    public const long NanosecondsPerDay = 24 * NanosecondsPerHour;

    public static readonly Duration Zero = new Duration(0);

    // Factories (prefer integer where possible to avoid FP rounding)
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromNanoseconds(long ns) => new Duration(ns);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromMicroseconds(long us) => new Duration(us * NanosecondsPerMicrosecond);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromMilliseconds(long ms) => new Duration(ms * NanosecondsPerMillisecond);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromSeconds(long s) => new Duration(s * NanosecondsPerSecond);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromMinutes(long m) => new Duration(m * NanosecondsPerMinute);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromHours(long h) => new Duration(h * NanosecondsPerHour);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromDays(long d) => new Duration(d * NanosecondsPerDay);

    // If you must use FP:
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromMilliseconds(double ms) => new Duration((long)(ms * NanosecondsPerMillisecond));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration FromSeconds(double s) => new Duration((long)(s * NanosecondsPerSecond));

    // Lossy interop with TimeSpan (100ns)
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public TimeSpan ToTimeSpanLossy() => TimeSpan.FromTicks(TotalNanoseconds / NanosecondsPerTick);

    // Totals

    public readonly long TotalNanoseconds;
    public double TotalMicroseconds => TotalNanoseconds / (double)NanosecondsPerMicrosecond;
    public double TotalMilliseconds => TotalNanoseconds / (double)NanosecondsPerMillisecond;
    public double TotalSeconds => TotalNanoseconds / (double)NanosecondsPerSecond;
    public double TotalMinutes => TotalNanoseconds / (double)NanosecondsPerMinute;
    public double TotalHours => TotalNanoseconds / (double)NanosecondsPerHour;
    public double TotalDays => TotalNanoseconds / (double)NanosecondsPerDay;

    // Component decomposition with TimeSpan-like sign semantics
    // (Days may be negative; Hours/Minutes/Seconds parts are in -23..23 / -59..59, etc.)
    public long Days
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TotalNanoseconds / NanosecondsPerDay; // truncates toward 0, matching TimeSpan
    }
    public int Hours
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long ns = TotalNanoseconds;
            long rem = ns % NanosecondsPerDay;
            return (int)(rem / NanosecondsPerHour);
        }
    }
    public int Minutes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long ns = TotalNanoseconds;
            long rem = ns % NanosecondsPerHour;
            return (int)(rem / NanosecondsPerMinute);
        }
    }
    public int Seconds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long ns = TotalNanoseconds;
            long rem = ns % NanosecondsPerMinute;
            return (int)(rem / NanosecondsPerSecond);
        }
    }
    public int Milliseconds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long ns = TotalNanoseconds;
            long rem = ns % NanosecondsPerSecond;
            return (int)(rem / NanosecondsPerMillisecond);
        }
    }
    public int Microseconds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long ns = TotalNanoseconds;
            long rem = ns % NanosecondsPerMillisecond;
            return (int)(rem / NanosecondsPerMicrosecond);
        }
    }

    // Math
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Duration Add(Duration other) => new Duration(TotalNanoseconds + other.TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Duration Subtract(Duration other) => new Duration(TotalNanoseconds - other.TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Duration Negate() => new Duration(-TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Duration Abs() => new Duration(TotalNanoseconds >= 0 ? TotalNanoseconds : -TotalNanoseconds);

    // Scale
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Duration Multiply(double factor) => new Duration((long)(TotalNanoseconds * factor));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Duration Divide(double divisor) => new Duration((long)(TotalNanoseconds / divisor));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public double Divide(Duration other) => TotalNanoseconds / (double)other.TotalNanoseconds;

    // Rounding to a quantum (ns)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Duration RoundUp(long quantumNanos)
    {
        if (quantumNanos <= 0) return this;
        long r = TotalNanoseconds % quantumNanos;
        if (r == 0) return this;
        if (r < 0) r += quantumNanos;
        return new Duration(TotalNanoseconds + (quantumNanos - r));
    }

    // Comparisons & equality
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public int CompareTo(Duration other) => TotalNanoseconds.CompareTo(other.TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public bool Equals(Duration other) => TotalNanoseconds == other.TotalNanoseconds;
    public override bool Equals(object? obj) => obj is Duration d && Equals(d);
    public override int GetHashCode() => TotalNanoseconds.GetHashCode();

    // Operators
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration operator +(Duration a, Duration b) => new Duration(a.TotalNanoseconds + b.TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration operator -(Duration a, Duration b) => new Duration(a.TotalNanoseconds - b.TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration operator -(Duration a) => new Duration(-a.TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration operator *(Duration a, double f) => a.Multiply(f);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration operator *(double f, Duration a) => a.Multiply(f);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration operator /(Duration a, double d) => a.Divide(d);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double operator /(Duration a, Duration b) => a.Divide(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator ==(Duration a, Duration b) => a.TotalNanoseconds == b.TotalNanoseconds;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator !=(Duration a, Duration b) => a.TotalNanoseconds != b.TotalNanoseconds;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator <(Duration a, Duration b) => a.TotalNanoseconds < b.TotalNanoseconds;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator <=(Duration a, Duration b) => a.TotalNanoseconds <= b.TotalNanoseconds;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator >(Duration a, Duration b) => a.TotalNanoseconds > b.TotalNanoseconds;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator >=(Duration a, Duration b) => a.TotalNanoseconds >= b.TotalNanoseconds;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Duration FromTimeSpan(TimeSpan ts)
    {
        checked
        {
            return new Duration(ts.Ticks * NanosecondsPerTick); // 1 tick = 100 ns
        }
    }

    public static Duration FromString(string input, string format = "-d.hh:mm:ss.fff_fff_fff")
    {
        if (input is null)
            throw new FormatException("Invalid duration string");

        // Strip underscores (nanosecond grouping separator in the canonical form).
        string cleanedInput = input.Replace("_", "").Trim();
        string cleanedFormat = format.Replace("_", "");

        if (cleanedInput.Length == 0)
            throw new FormatException("Invalid duration string");

        // Leading '-' in input is a literal sign; leading '-' in format is the optional-sign placeholder.
        bool isNegative = cleanedInput[0] == '-';
        if (isNegative) cleanedInput = cleanedInput[1..];
        if (cleanedFormat.Length > 0 && cleanedFormat[0] == '-') cleanedFormat = cleanedFormat[1..];

        // The fractional '.' is the one after seconds (after the last ':').
        // Any other '.' belongs to the days-hours separator.
        string timePart = SplitOffFraction(cleanedInput, out string fracPart);
        string timeFormat = SplitOffFraction(cleanedFormat, out _);

        // TimeSpan.ParseExact requires literal ':' and '.' to be backslash-escaped.
        string escapedFormat = timeFormat.Replace(":", @"\:").Replace(".", @"\.");

        if (!TimeSpan.TryParseExact(timePart, escapedFormat, CultureInfo.InvariantCulture, out TimeSpan ts))
            throw new FormatException($"Duration '{input}' does not match format '{format}'");

        long totalNanoseconds = ts.Ticks * NanosecondsPerTick;

        if (fracPart.Length > 0)
        {
            if (fracPart.Length > 9) fracPart = fracPart[..9];
            if (!long.TryParse(fracPart, NumberStyles.None, CultureInfo.InvariantCulture, out long nanos))
                throw new FormatException("Invalid fractional nanoseconds");
            for (int i = fracPart.Length; i < 9; i++) nanos *= 10;
            totalNanoseconds += nanos;
        }

        return new Duration(isNegative ? -totalNanoseconds : totalNanoseconds);
    }

    private static string SplitOffFraction(string s, out string frac)
    {
        int lastColon = s.LastIndexOf(':');
        int lastDot = s.LastIndexOf('.');
        if (lastDot > lastColon && lastDot != -1)
        {
            frac = s[(lastDot + 1)..];
            return s[..lastDot];
        }
        frac = string.Empty;
        return s;
    }

    // Logging only (not hot path)
    public override string ToString()
    {
        long ns = TotalNanoseconds;
        bool neg = ns < 0;
        if (neg) ns = -ns;

        long days = ns / NanosecondsPerDay; ns -= days * NanosecondsPerDay;
        long hrs = ns / NanosecondsPerHour; ns -= hrs * NanosecondsPerHour;
        long mins = ns / NanosecondsPerMinute; ns -= mins * NanosecondsPerMinute;
        long secs = ns / NanosecondsPerSecond; ns -= secs * NanosecondsPerSecond;

        string nanos9 = ((int)ns).ToString("D9", CultureInfo.InvariantCulture);
        string frac = $"{nanos9[..3]}_{nanos9.Substring(3, 3)}_{nanos9.Substring(6, 3)}";
        string sign = neg ? "-" : "";
        return $"{sign}{days}.{hrs:00}:{mins:00}:{secs:00}.{frac}";
    }
}


// =============================== Timestamp (with Duration ops) ===============================
[StructLayout(LayoutKind.Sequential)]
public readonly struct Timestamp : IComparable<Timestamp>, IEquatable<Timestamp>
{
    public readonly long NanosSinceEpoch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Timestamp(long nanosSinceEpoch) => NanosSinceEpoch = nanosSinceEpoch;

    // Constants
    private const long NanosecondsPerTick = 100;
    private const long NanosecondsPerSecond = 1_000_000_000;
    private const long NanosecondsPerMinute = 60 * NanosecondsPerSecond;
    private const long NanosecondsPerHour = 60 * NanosecondsPerMinute;
    private const long NanosecondsPerDay = 24 * NanosecondsPerHour;

    public static readonly Timestamp MaxValue = new Timestamp(long.MaxValue);
    public static readonly Timestamp MinValue = new Timestamp(0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PositiveMod(long value, long mod)
    {
        long r = value % mod;
        return r < 0 ? r + mod : r;
    }

    // UTC DateTime (interop / logging only)
    public DateTime ToDateTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => DateTime.UnixEpoch.AddTicks(NanosSinceEpoch / NanosecondsPerTick);
    }

    // Midnight UTC floor
    public Timestamp Date
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long r = PositiveMod(NanosSinceEpoch, NanosecondsPerDay);
            return new Timestamp(NanosSinceEpoch - r);
        }
    }

    public DayOfWeek DayOfWeek
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ToDateTime.DayOfWeek;
    }

    public int Day
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ToDateTime.Day;
    }
    public int Month
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ToDateTime.Month;
    }
    public int Year
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ToDateTime.Year;
    }

    public int Hour
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ToDateTime.Hour;
    }

    public int Minute
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ToDateTime.Minute;
    }

    public int Second
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ToDateTime.Second;
    }

    public Timestamp EndOfMonth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Timestamp endOfMonth = AddMonths(1);
            endOfMonth = new Timestamp(endOfMonth.Year, endOfMonth.Month, 1).AddNanoseconds(-1);
            return endOfMonth;
        }
    }

    public Timestamp(int year, int month, int day)
    {
        DateTime dt = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        NanosSinceEpoch = (dt - DateTime.UnixEpoch).Ticks * NanosecondsPerTick;
    }

    public Timestamp(int year, int month, int day, int hour, int minute, int second)
    {
        DateTime dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        NanosSinceEpoch = (dt - DateTime.UnixEpoch).Ticks * NanosecondsPerTick;
    }

    public Timestamp(int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond, int nanosecond)
    {
        DateTime dt = new DateTime(year, month, day, hour, minute, second,millisecond, microsecond, DateTimeKind.Utc);
        NanosSinceEpoch = (dt - DateTime.UnixEpoch).Ticks * NanosecondsPerTick + nanosecond;
    }


    // Min/Max
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp Min(Timestamp other) => NanosSinceEpoch <= other.NanosSinceEpoch ? this : other;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp Max(Timestamp other) => NanosSinceEpoch >= other.NanosSinceEpoch ? this : other;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Timestamp Min(Timestamp a, Timestamp b) => a.NanosSinceEpoch <= b.NanosSinceEpoch ? a : b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Timestamp Max(Timestamp a, Timestamp b) => a.NanosSinceEpoch >= b.NanosSinceEpoch ? a : b;

    // Arithmetic (prefer integer overloads for precision)
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddDuration(Duration duration) => new Timestamp(NanosSinceEpoch + duration.TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddTicks(long ticks) => new Timestamp(NanosSinceEpoch + ticks*NanosecondsPerTick);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddNanoseconds(long ns) => new Timestamp(NanosSinceEpoch + ns);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddMicroseconds(long us) => new Timestamp(NanosSinceEpoch + us * 1_000);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddMilliseconds(long ms) => new Timestamp(NanosSinceEpoch + ms * 1_000_000);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddSeconds(long s) => new Timestamp(NanosSinceEpoch + s * NanosecondsPerSecond);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddMinutes(long m) => new Timestamp(NanosSinceEpoch + m * NanosecondsPerMinute);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddHours(long h) => new Timestamp(NanosSinceEpoch + h * NanosecondsPerHour);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddDays(long d) => new Timestamp(NanosSinceEpoch + d * NanosecondsPerDay);

    // FP overloads if desired (not recommended on the hottest path)
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddMilliseconds(double ms) => new Timestamp(NanosSinceEpoch + (long)(ms * 1_000_000));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddSeconds(double s) => new Timestamp(NanosSinceEpoch + (long)(s * NanosecondsPerSecond));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddMinutes(double m) => new Timestamp(NanosSinceEpoch + (long)(m * NanosecondsPerMinute));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddHours(double h) => new Timestamp(NanosSinceEpoch + (long)(h * NanosecondsPerHour));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddDays(double d) => new Timestamp(NanosSinceEpoch + (long)(d * NanosecondsPerDay));

    // Calendar-sensitive
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddMonths(int months) => FromDateTime(ToDateTime.AddMonths(months));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp AddYears(int years) => FromDateTime(ToDateTime.AddYears(years));

    // Rounding
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Timestamp RoundUp(long quantumNanos)
    {
        if (quantumNanos <= 0) return this;
        long r = PositiveMod(NanosSinceEpoch, quantumNanos);
        return r == 0 ? this : new Timestamp(NanosSinceEpoch + (quantumNanos - r));
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp RoundUpMilliseconds(int milliseconds) => RoundUp(milliseconds * 1_000_000L);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp RoundUpMicroseconds(int microseconds) =>  RoundUp(microseconds * 1_000L);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp RoundUpSeconds(int seconds) => RoundUp(seconds * NanosecondsPerSecond);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp RoundUpMinutes(int minutes) => RoundUp(minutes * NanosecondsPerMinute);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public Timestamp RoundUpHours(int hours) => RoundUp(hours * NanosecondsPerHour);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Timestamp RoundUpDay()
    {
        var d = Date;
        return NanosSinceEpoch == d.NanosSinceEpoch ? this : new Timestamp(d.NanosSinceEpoch + NanosecondsPerDay);
    }

    // Operators with Duration (mirrors DateTime/TimeSpan)
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Duration operator -(Timestamp a, Timestamp b) => Duration.FromNanoseconds(a.NanosSinceEpoch - b.NanosSinceEpoch);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Timestamp operator +(Timestamp t, Duration d) => new Timestamp(t.NanosSinceEpoch + d.TotalNanoseconds);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Timestamp operator -(Timestamp t, Duration d) => new Timestamp(t.NanosSinceEpoch - d.TotalNanoseconds);

    // Comparisons & equality
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public int CompareTo(Timestamp other) => NanosSinceEpoch.CompareTo(other.NanosSinceEpoch);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public bool Equals(Timestamp other) => NanosSinceEpoch == other.NanosSinceEpoch;
    public override bool Equals(object? obj) => obj is Timestamp ts && Equals(ts);
    public override int GetHashCode() => NanosSinceEpoch.GetHashCode();

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator ==(Timestamp a, Timestamp b) => a.NanosSinceEpoch == b.NanosSinceEpoch;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator !=(Timestamp a, Timestamp b) => a.NanosSinceEpoch != b.NanosSinceEpoch;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator <(Timestamp a, Timestamp b) => a.NanosSinceEpoch < b.NanosSinceEpoch;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator <=(Timestamp a, Timestamp b) => a.NanosSinceEpoch <= b.NanosSinceEpoch;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator >(Timestamp a, Timestamp b) => a.NanosSinceEpoch > b.NanosSinceEpoch;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool operator >=(Timestamp a, Timestamp b) => a.NanosSinceEpoch >= b.NanosSinceEpoch;

    public string ToString(string format) => ToDateTime.ToString(format, CultureInfo.InvariantCulture);

    // yyyy-MM-dd HH:mm:ss.mmm_uuu_nnn   (mirrors C++ std::format output)
    public override string ToString()
    {
        DateTime dt = ToDateTime;
        long sub = NanosSinceEpoch % NanosecondsPerSecond;
        if (sub < 0) sub += NanosecondsPerSecond;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss}.{1:D3}_{2:D3}_{3:D3}",
            dt,
            sub / 1_000_000,
            sub / 1_000 % 1_000,
            sub % 1_000);
    }

    public string ToDateString() => ToDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Factories
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromDateTime(DateTime dateTime)
    {
        if (dateTime.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("DateTime must be UTC");
        return new Timestamp((dateTime - DateTime.UnixEpoch).Ticks * NanosecondsPerTick);
    }

    // Linux: CLOCK_REALTIME is the wall clock the kernel steers via adjtimex.
    // On the CME DC3 box sfptpd disciplines it to the Solarflare PHC, which is
    // itself disciplined to the GPS grandmaster on the CME roof — so a read of
    // CLOCK_REALTIME *is* ns-precision "roof time". The call rides the vDSO
    // (TSC -> realtime, no syscall) and returns a full timespec, so we keep
    // nanosecond resolution rather than DateTime's 100ns tick quantum.
    //
    // PRECONDITION — the kernel clocksource MUST be 'tsc' for this to be fast.
    // Verify before relying on this API in realtime:
    //     cat /sys/devices/system/clocksource/clocksource0/current_clocksource
    // If it reports 'hpet' (or 'acpi_pm') the vDSO falls back to a real syscall:
    // the call jumps from ~25ns to microseconds AND [SuppressGCTransition] on a
    // now-blocking call becomes unsafe (it can stall the GC). 'tsc' also requires
    // constant_tsc + nonstop_tsc in /proc/cpuinfo so the counter stays invariant.
    private const int CLOCK_REALTIME = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long tv_sec;   // whole seconds since Unix epoch (UTC)
        public long tv_nsec;  // nanosecond remainder, 0 .. 999_999_999
    }

    // [SuppressGCTransition] skips the cooperative<->preemptive GC handshake a
    // normal P/Invoke does on entry/exit (~10-25 ns of pure overhead). Safe here
    // only because the vDSO path is non-blocking and never triggers GC. This
    // collapses the managed call to roughly the bare vDSO cost (~20-35 ns).
    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = false)]
    [SuppressGCTransition]
    private static extern int clock_gettime(int clockId, out Timespec tp);

    public static Timestamp UtcNow
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // OperatingSystem.IsLinux() is a JIT/AOT intrinsic: for the Release
            // Linux AOT build it folds to a constant and the Windows branch is
            // stripped entirely, leaving just the clock_gettime read.
            if (OperatingSystem.IsLinux())
            {
                clock_gettime(CLOCK_REALTIME, out Timespec ts);
                return new Timestamp(ts.tv_sec * NanosecondsPerSecond + ts.tv_nsec);
            }

            // Windows is simulation-only; 100ns-quantised wall time is fine here.
            return FromDateTime(DateTime.UtcNow);
        }
    }


    public static Timestamp FromString(string input, string format = "yyyy-MM-dd HH:mm:ss.fff_fff_fff")
    {
        if (input is null)
            throw new FormatException("Invalid timestamp string");

        // Mirror C++ chrono::parse approach: strip underscores, split fractional part
        // (DateTime resolves only to 100ns ticks, so ns precision is handled separately).
        string cleanedInput = input.Replace("_", "").Trim();
        string cleanedFormat = format.Replace("_", "");

        int inputDot = cleanedInput.IndexOf('.');
        int formatDot = cleanedFormat.IndexOf('.');

        string datePart = inputDot < 0 ? cleanedInput : cleanedInput[..inputDot];
        string fracPart = inputDot < 0 ? string.Empty : cleanedInput[(inputDot + 1)..];
        string dateFormat = formatDot < 0 ? cleanedFormat : cleanedFormat[..formatDot];

        if (!DateTime.TryParseExact(
                datePart,
                dateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime dt))
        {
            throw new FormatException($"Timestamp '{input}' does not match format '{format}'");
        }

        long baseNanos = (dt - DateTime.UnixEpoch).Ticks * NanosecondsPerTick;

        if (fracPart.Length == 0)
            return new Timestamp(baseNanos);

        if (fracPart.Length > 9) fracPart = fracPart[..9];
        if (!long.TryParse(fracPart, NumberStyles.None, CultureInfo.InvariantCulture, out long ns))
            throw new FormatException("Invalid fractional nanoseconds");

        for (int i = fracPart.Length; i < 9; i++) ns *= 10;
        return new Timestamp(baseNanos + ns);
    }

}

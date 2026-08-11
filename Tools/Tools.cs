using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Tools
{
    public delegate void RefAction<T>(in T value);

    [RegisterJson]
    public enum Access : byte
    {
        Read,
        Write 
    }

    public static class Platform
    {
        public static readonly bool IsCME = System.Environment.MachineName == "arb-dc3-tns-bros";

        public static readonly bool IsLocal = System.Environment.MachineName == "THREADRIPPER";

        public static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        public static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static readonly string Name = System.Environment.MachineName;
    }



    public static class X86BaseWrapper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Pause()
        {
            System.Runtime.Intrinsics.X86.X86Base.Pause();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ExponentialPause()
        {
            int count = 1;
            for (int i = 0; i < count; ++i)
                Pause();
            if (count < 64)
                count <<= 1;
        }
    }

    public static class Milliseconds
    {
        public static readonly int Second = 1000;
        public static readonly int Minute = 60_000;
        public static readonly int Hour = 3_600_000;
        public static readonly int Day = 86_400_000;
    }
    public static class Seconds
    {
        public static readonly int Minute = 60;
        public static readonly int Hour = 3_600;
        public static readonly int Day = 86_400;
    }


    /// <summary>
    /// Exact division of a tick-aligned price mantissa by a fixed tick step, without a hardware
    /// integer divide. Every book, order, and trade price CME publishes is a whole number of ticks,
    /// so the mantissa is an exact multiple of the step, and exact division by an invariant divisor
    /// is a shift plus one multiply (Granlund and Montgomery, "Division by Invariant Integers using
    /// Multiplication"): factor the step into 2^k times an odd part, then
    ///   ticks = (mantissa >> k) * oddInverse   (mod 2^64),
    /// where oddInverse is the modular inverse of the odd part.
    ///
    /// Bit-exact across the signed 64-bit range (negative spread prices included) and - unlike a
    /// floating reciprocal - never loses precision on a large mantissa. Valid only on tick-aligned
    /// prices, so used only where CME guarantees alignment (never off-tick statistics entries).
    ///
    /// The double side (<see cref="FromPrice"/> / <see cref="ToPrice"/>) is the same conversion for
    /// prices that are already doubles: a multiply by the tick size or its reciprocal, no divide.
    /// The mantissa scale is a constructor argument, not an assumption - a billion for CME's
    /// nine-decimal wire prices, 1 for a feed whose prices are already whole price units.
    /// </summary>
    public struct TickDivision
    {
        private int _shift;                 // the step's power-of-two factor (its trailing zero bits)
        private ulong _oddInverse;          // modular inverse (mod 2^64) of the step's odd part
        private long _step;                 // the tick step itself, in mantissa units; 0 until set
        private long _scale;                // mantissa units to one price unit
        private double _tickSize;           // one tick as a price
        private double _inverseTickSize;    // its reciprocal, rounded through decimal so exact ticks stay exact

        /// <summary>The inverse of an odd number modulo 2^64 by Newton-Hensel: the seed is good to five bits and each of the five steps doubles the correct bits.</summary>
        private static ulong ModularInverse(ulong odd)
        {
            unchecked
            {
                ulong x = (3UL * odd) ^ 2UL;
                x *= 2 - odd * x;
                x *= 2 - odd * x;
                x *= 2 - odd * x;
                x *= 2 - odd * x;
                x *= 2 - odd * x;
                return x;
            }
        }

        /// <param name="step">The tick step, in mantissa units per tick.</param>
        /// <param name="scale">Mantissa units to one price unit - a billion for CME's nine-decimal wire prices.</param>
        public TickDivision(long step, long scale) : this() { Set(step, scale); }

        /// <summary>Builds the division from an instrument's display tick and factor: one tick is tickSize/displayFactor Globex units, times the mantissa scale.</summary>
        public static TickDivision FromDisplayTick(double tickSize, double displayFactor, long scale)
        {
            TickDivision division = default;
            division.SetDisplayTick(tickSize, displayFactor, scale);
            return division;
        }

        /// <summary>Sets the tick step (a nonzero mantissa count per tick) and precomputes the divide. The price side then works in mantissa/scale units.</summary>
        public void Set(long step, long scale) => Set(step, scale, scale == 0 ? 0.0 : step / (double)scale);

        /// <summary>Sets the step from the display tick and factor, as <see cref="FromDisplayTick"/>. The price side then works in display prices.</summary>
        public void SetDisplayTick(double tickSize, double displayFactor, long scale)
        {
            Set((long)Math.Round(tickSize / displayFactor, MidpointRounding.AwayFromZero) * scale, scale, tickSize);
        }

        private void Set(long step, long scale, double tickSize)
        {
            _step = step;
            _scale = scale;
            _tickSize = tickSize;
            _inverseTickSize = tickSize == 0.0 || !double.IsFinite(tickSize) ? 0.0 : (double)(1.0m / (decimal)tickSize);

            if (step == 0)
            {
                _shift = 0;
                _oddInverse = 1;
                return;
            }
            _shift = BitOperations.TrailingZeroCount((ulong)step);
            _oddInverse = ModularInverse((ulong)step >> _shift);
        }

        /// <summary>The tick step in mantissa units; 0 until set.</summary>
        public readonly long Step => _step;

        /// <summary>Mantissa units to one price unit, as given to the constructor.</summary>
        public readonly long Scale => _scale;

        /// <summary>One tick as a price - the unit <see cref="FromPrice"/> and <see cref="ToPrice"/> work in.</summary>
        public readonly double TickSize => _tickSize;

        public readonly double InverseTickSize => _inverseTickSize;

        public readonly bool IsSet => _step != 0;

        /// <summary>The tick count of a tick-aligned price mantissa - exact, no divide; zero when the step is unset.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly long ToTicks(long priceMantissa)
        {
            if (_step == 0)
                return 0;
            return unchecked((long)((ulong)(priceMantissa >> _shift) * _oddInverse));
        }

        /// <summary>The tick-aligned price mantissa of a tick count - the exact reverse of <see cref="ToTicks"/>, a single multiply.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly long ToPriceMantissa(int ticks) => ticks * _step;

        /// <summary>The tick count of a price - one multiply and a round, so an off-tick price snaps to the nearest tick; zero when the step is unset.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int FromPrice(double price) => (price * _inverseTickSize).RoundToInt();

        /// <summary>The price of a tick count - the exact reverse of <see cref="FromPrice"/> on tick-aligned input, a single multiply.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly double ToPrice(int ticks) => ticks * _tickSize;
    }

    public class ThisStateShouldNeverOccur : Exception
    {
        public ThisStateShouldNeverOccur(string message) : base(message) { }
    }

    public static class Tools
    {


        public static string Sanitize(this string input)
        {
            char[] result = input.ToCharArray();

            for (int i = 0; i < result.Length; i++)
            {
                char c = result[i];

                // ASCII-only [A-Za-z0-9] to match C++ isalnum (default "C" locale). char.IsLetterOrDigit is
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '-' || c == '.')
                {
                    continue;
                }

                result[i] = '_';
            }

            return new string(result);
        }






        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong SetBit(ulong bitmap, int index)           // set to 1
        {
            if ((uint)index >= 64u) throw new ArgumentOutOfRangeException(nameof(index));
            return bitmap | (1UL << index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ClearBit(ulong bitmap, int index)         // set to 0
        {
            if ((uint)index >= 64u) throw new ArgumentOutOfRangeException(nameof(index));
            return bitmap & ~(1UL << index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong WriteBit(ulong bitmap, int index, bool value) // set to 1 or 0
        {
            if ((uint)index >= 64u) throw new ArgumentOutOfRangeException(nameof(index));
            ulong mask = 1UL << index;
            // Branchless: clear then OR if value==true (0UL - (value?1:0) is 0x..00 or 0x..FF..FF)
            ulong onMask = (0UL - (value ? 1UL : 0UL)) & mask;
            return (bitmap & ~mask) | onMask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSet(ulong bitmap, int index)
        {
            if ((uint)index >= 64u) throw new ArgumentOutOfRangeException(nameof(index));
            return (bitmap & (1UL << index)) != 0UL;
        }

        // NOTE: there is intentionally no GetUtcNow() here. The single canonical
        // wall-clock source is Timestamp.UtcNow (PTP-disciplined CLOCK_REALTIME on
        // Linux). Routing everyone through one method avoids accidentally reading a
        // coarser/undisciplined clock on the hot path.

        public static unsafe ref T ReadStruct<T>(byte* source) where T : unmanaged
        {
            return ref Unsafe.AsRef<T>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SqrtEstimate(double x)
        {
            // Approximate sqrt using reciprocal-sqrt estimate: sqrt(x) ≈ x * (1/sqrt(x))
            double inv = Math.ReciprocalSqrtEstimate(x);
            double approx = x * inv;
            return double.IsFinite(approx) ? approx : Math.Sqrt(x);
        }

        /// <summary>
        /// Returns the smallest power of two greater than or equal to <paramref name="value"/>.
        /// Clamps at 0x80000000.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint NextPowerOfTwo(uint value)
        {
            if (value <= 1u)
                return 1u;

            int lzc = BitOperations.LeadingZeroCount(value);
            uint floor = 1u << (31 - lzc);
            if (floor == value)
                return value;
            if (floor == 0x80000000u)
                return 0x80000000u;
            return floor << 1;
        }

        /// <summary>
        /// Returns the smallest power of two greater than or equal to <paramref name="value"/>.
        /// Clamps at 0x40000000 to avoid negative results.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int NextPowerOfTwo(int value)
        {
            if (value <= 1)
                return 1;

            uint u = (uint)value;
            int lzc = BitOperations.LeadingZeroCount(u);
            uint floor = 1u << (31 - lzc);
            if (floor == u)
                return (int)floor;

            uint next = floor << 1;
            return next > 0x40000000u ? 0x40000000 : (int)next;
        }

        /// <summary>
        /// Returns the smallest power of two greater than or equal to <paramref name="value"/>.
        /// Clamps at 1UL &lt;&lt; 63.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong NextPowerOfTwo(ulong value)
        {
            if (value <= 1UL)
                return 1UL;

            int lzc = BitOperations.LeadingZeroCount(value);
            ulong floor = 1UL << (63 - lzc);
            if (floor == value)
                return value;
            if (floor == (1UL << 63))
                return 1UL << 63;
            return floor << 1;
        }

        /// <summary>
        /// Returns the smallest power of two greater than or equal to <paramref name="value"/>.
        /// Clamps at 1L &lt;&lt; 62 to stay positive.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long NextPowerOfTwo(long value)
        {
            if (value <= 1L)
                return 1L;

            ulong u = (ulong)value;
            int lzc = BitOperations.LeadingZeroCount(u);
            ulong floor = 1UL << (63 - lzc);
            if (floor == u)
                return (long)floor;

            ulong next = floor << 1;
            const ulong Max = 1UL << 62;
            return next > Max ? (long)Max : (long)next;
        }





        public static string ToDateString(this DateTime dateTime)
        {
            return dateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        public static string ToLongString(this DateTime dateTime)
        {
            return dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
        }
        public static DateTime Min(this DateTime dateTime1, DateTime dateTime2)
        {
            return dateTime1 <= dateTime2 ? dateTime1 : dateTime2;
        }
        public static DateTime Max(this DateTime dateTime1, DateTime dateTime2)
        {
            return dateTime1 >= dateTime2 ? dateTime1 : dateTime2;
        }
        public static string Capitalize(this string str)
        {
            return char.ToUpper(str[0]) + str.Substring(1).ToLower();
        }
        public static DateTime RoundUpTicks(this DateTime dateTime, long ticks)
        {
            if (ticks == 0)
            {
                return dateTime;
            }
            long ticksBelow = dateTime.Ticks % ticks;
            if (ticksBelow > 0)
            {
                long ticksToAdd = ticks - ticksBelow;
                return dateTime.AddTicks(ticksToAdd);
            }
            else
            {
                return dateTime;
            }
        }
        public static DateTime RoundUpMilliseconds(this DateTime dateTime, int milliseconds)
        {
            return dateTime.RoundUpTicks((long)10_000 * milliseconds);
        }
        public static DateTime RoundUpSeconds(this DateTime dateTime, int seconds)
        {
            return dateTime.RoundUpTicks((long)10_000_000 * seconds);
        }
        public static DateTime RoundUpMinutes(this DateTime dateTime, int minutes)
        {
            return dateTime.RoundUpTicks((long)10_000_000 * 60 * minutes);
        }
        public static DateTime RoundUpHours(this DateTime dateTime, int hours)
        {
            return dateTime.RoundUpTicks((long)10_000_000 * 60 * 60 * hours);
        }

        public static DateTime RoundUpDay(this DateTime dateTime)
        {
            if (dateTime.TimeOfDay == TimeSpan.Zero)
            {
                return dateTime;
            }
            return dateTime.Date.AddDays(1);
        }

        private const double s_epsilon = 1e-9;




        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int RoundToInt(this double value)
        {
            // The ternary operator here is faster than the overhead of Math.Round().
            return (int)(value + (value >= 0.0 ? 0.5 : -0.5));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FloorToInt(this double value)
        {
            // (int) cast simply truncates (rounds toward zero). 
            // Math.Floor handles the negative logic correctly (-2.1 -> -3).
            // In .NET Core/5+, this compiles to a single CPU instruction.
            return (int)Math.Floor(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CeilingToInt(this double value)
        {
            return (int)Math.Ceiling(value);
        }

        public static int GetNumberOfDecimalPlaces(double number)
        {
            // 1. Strip the sign and integer part
            number = Math.Abs(number);
            number -= double.Truncate(number);

            int decimalPlaces = 0;

            // 2. Loop until the fractional part disappears
            while (number > 0)
            {
                decimalPlaces++;
                number *= 10;
                number -= double.Truncate(number);
            }

            return decimalPlaces;
        }

        public static double FloorTo(this double number, double value)
        {
            return Math.Floor(number / value) * value;
        }
        public static double RoundTo(this double number, double value)
        {
            return Math.Round(number / value) * value;
        }
        public static double CeilingTo(this double number, double value)
        {
            return Math.Ceiling(number / value) * value;
        }

        private static readonly System.Collections.Generic.Dictionary<int, string> DateTimeFormats = new System.Collections.Generic.Dictionary<int, string>
        {
            {27, "yyyy-MM-dd HH:mm:ss.fffffff"},
            {26, "yyyy-MM-dd HH:mm:ss.ffffff"},
            {25, "yyyy-MM-dd HH:mm:ss.fffff"},
            {24, "yyyy-MM-dd HH:mm:ss.ffff"},
            {23, "yyyy-MM-dd HH:mm:ss.fff"},
            {19, "yyyy-MM-dd HH:mm:ss"},
            {10, "yyyy-MM-dd"},
        };
        public static DateTime ParseDateTime(this string dateTimeAsString, string? dateTimeFormat = null)
        {
            if (dateTimeFormat != null)
            {
                DateTime dateTime = DateTime.ParseExact(dateTimeAsString, dateTimeFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                if (dateTime.Kind != DateTimeKind.Unspecified)
                {
                    dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
                }
                return dateTime;
            }
            return dateTimeAsString.ParseDateTime(DateTimeFormats[dateTimeAsString.Length]);
        }

        public static double NaNIfZero(this double value) => value == 0 ? double.NaN : value;
        public static double ZeroIfNotFinite(this double value) => double.IsFinite(value) ? value : 0;

        public static string? ReadLastLine(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length == 0)
                {
                    return null;
                }

                long position = stream.Length - 1;

                // Check the last byte to see if the file ends with a newline.
                // If it does, we want to step back one more to find the start of the actual content.
                stream.Seek(position, SeekOrigin.Begin);
                int lastByte = stream.ReadByte();

                if (lastByte == '\n' && position > 0)
                {
                    position--;
                }
                else if (lastByte == '\n')
                {
                    // File is just a single newline
                    return string.Empty;
                }

                // Scan backwards for the next newline
                while (position >= 0)
                {
                    stream.Seek(position, SeekOrigin.Begin);
                    int currentByte = stream.ReadByte();

                    if (currentByte == '\n')
                    {
                        break;
                    }

                    position--;
                }

                // We are now at the newline character of the previous line (or -1 if start of file).
                // The last line starts at position + 1.
                stream.Seek(position + 1, SeekOrigin.Begin);

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string? result = reader.ReadLine();
                    return result;
                }
            }
        }
    }
}

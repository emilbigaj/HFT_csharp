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

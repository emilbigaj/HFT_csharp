using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Tools;

namespace Tools;

[RegisterJson]
public struct Session
{
    // -------------------- Public properties --------------------
    // Defaults apply during deserialization if field is absent.
    public bool DoesCloseForWeekend { get; init; }

    public string Name { get; init; } = string.Empty;

    [JsonConverter(typeof(TimeZoneInfoJsonConverter))]
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    [JsonConverter(typeof(TimeSpanJsonConverter))]
    public TimeSpan Open { get; init; } = TimeSpan.Zero;

    [JsonConverter(typeof(TimeSpanJsonConverter))]
    public TimeSpan Close { get; init; } = TimeSpan.Zero;

    public static Session Perpetual = new Session
    (
        "Perpetual",
        TimeZoneInfo.FindSystemTimeZoneById("UTC"),
        new TimeSpan(0, 0, 0),
        new TimeSpan(0, 23, 59, 59, 999),
        false 
    );
    public static Session Europe = new Session
    (
        "Europe",
        TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        false 
    );
    public static Session NewYork = new Session
    (
        "NewYork",
        TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"),
        new TimeSpan(9, 30, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session CME = new Session
    (
        "CME",
        TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"),
        new TimeSpan(17, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session Chicago = new Session
    (
        "Chicago",
        TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"),
        new TimeSpan(10, 30, 0),
        new TimeSpan(17, 0, 0),
        true
    );
    public static Session IdealPro = new Session
    (
        "IdealPro",
        TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"),
        new TimeSpan(17, 40, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session Australia = new Session
    (
        "Australia",
        TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session NewZealand = new Session
    (
        "NewZealand",
        TimeZoneInfo.FindSystemTimeZoneById("New Zealand Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session Singapore = new Session
    (
        "Singapore",
        TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session London = new Session
    (
        "London",
        TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session China = new Session
    (
        "China",
        TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session Japan = new Session
    (
        "Japan",
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session SouthAfrica = new Session
    (
        "SouthAfrica",
        TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public static Session Russia = new Session
    (
        "Russia",
        TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time"),
        new TimeSpan(10, 0, 0),
        new TimeSpan(16, 0, 0),
        true
    );
    public Session(string name, TimeZoneInfo timeZoneInfo, TimeSpan open, TimeSpan close, bool doesCloseForWeekend)
    {
        Name = name;
        TimeZone = timeZoneInfo;
        Open = open;
        Close = close;
        DoesCloseForWeekend = doesCloseForWeekend;
    }

    public override string ToString()
    {
        return $"{Name}: {Open}-{Close} {TimeZone}";
    }



    private DateTime GetNextOpen(DateTime localTime)
    {
        DateTime nextOpen = localTime.TimeOfDay <= Open ? localTime.Date + Open : localTime.Date.AddDays(1) + Open;
        if (DoesCloseForWeekend)
        {
            //DateTime closeOfNextOpen = nextOpen.TimeOfDay <= Close ? nextOpen.Date + Close : localTime.Date.AddDays(1) + Close;
            if (Open < Close)
            {
                if (nextOpen.DayOfWeek == DayOfWeek.Saturday)
                {
                    nextOpen = nextOpen.AddDays(2);
                }
                else if (nextOpen.DayOfWeek == DayOfWeek.Sunday)
                {
                    nextOpen = nextOpen.AddDays(1);
                }
            }
            else
            {
                if (nextOpen.DayOfWeek == DayOfWeek.Friday)
                {
                    nextOpen = nextOpen.AddDays(2);
                }
                else if (nextOpen.DayOfWeek == DayOfWeek.Saturday)
                {
                    nextOpen = nextOpen.AddDays(1);
                }
            }
            
        }
        return nextOpen;
    }



    private DateTime GetNextClose(DateTime localTime)
    {
        DateTime nextClose = localTime.TimeOfDay <= Close ? localTime.Date + Close : localTime.Date.AddDays(1) + Close;
        if (DoesCloseForWeekend)
        {
            if (nextClose.DayOfWeek == DayOfWeek.Saturday)
            {
                nextClose = nextClose.AddDays(2);
            }
            else if (nextClose.DayOfWeek == DayOfWeek.Sunday)
            {
                nextClose = nextClose.AddDays(1);
            }
        }
        return nextClose;
    }



    private DateTime GetLastClose(DateTime localTime)
    {
        // Adjust localTime to the previous day if it's before or exactly at the closing time
        DateTime previousClose = localTime.TimeOfDay <= Close ? localTime.Date.AddDays(-1) + Close : localTime.Date + Close;

        // Handle weekends
        if (DoesCloseForWeekend)
        {
            if (previousClose.DayOfWeek == DayOfWeek.Sunday)
            {
                previousClose = previousClose.AddDays(-2);
            }
            else if (previousClose.DayOfWeek == DayOfWeek.Saturday)
            {
                previousClose = previousClose.AddDays(-1);
            }
        }

        return previousClose;
    }



    private TimeSpan TimeTillOpen(DateTime localTime)
    {
        DateTime nextOpen = GetNextOpen(localTime);
        DateTime nextClose = GetNextClose(localTime);
        if (nextClose < nextOpen)
        {
            return new TimeSpan(0);
        }
        else
        {
            return nextOpen - localTime;
        }
    }

    private bool Contains(DateTime localTime)
    {
        DateTime nextClose = GetNextClose(localTime);
        DateTime nextOpen = GetNextOpen(localTime);
        return nextClose < nextOpen;
    }

    public TimeSpan TimeTillOpen(DateTime dateTime, TimeZoneInfo timeZone)
    {
        DateTime localTime = ConvertToLocal(dateTime, timeZone);
        TimeSpan timeTillOpen = TimeTillOpen(localTime);
        return timeTillOpen;
    }
    public bool Contains(DateTime dateTime, TimeZoneInfo timeZone)
    {
        return Contains(ConvertToLocal(dateTime, timeZone));
    }

    public DateTime ConvertToLocal(DateTime dateTime, TimeZoneInfo fromTimeZone)
    {
        return TimeZoneInfo.ConvertTime(dateTime, fromTimeZone, TimeZone);
    }

    public DateTime ConvertToLocal(Timestamp timestamp)
    {
        return TimeZoneInfo.ConvertTime(timestamp.ToDateTime, TimeZoneInfo.Utc, TimeZone);
    }

    public DateTime ConvertFromLocal(DateTime dateTime, TimeZoneInfo toTimeZone)
    {
        return TimeZoneInfo.ConvertTime(dateTime, TimeZone, toTimeZone);
    }

    public DateTime GetLastClose(DateTime dateTime, TimeZoneInfo timeZone)
    {
        DateTime localTime = ConvertToLocal(dateTime, timeZone);
        DateTime nextLocalClose = GetLastClose(localTime);
        DateTime nextClose = ConvertFromLocal(nextLocalClose, timeZone);
        return nextClose;
    }

    public DateTime GetNextClose(DateTime dateTime, TimeZoneInfo timeZone)
    {
        DateTime localTime = ConvertToLocal(dateTime, timeZone);
        DateTime nextLocalClose = GetNextClose(localTime);
        DateTime nextClose = ConvertFromLocal(nextLocalClose, timeZone);
        return nextClose;
    }

    public DateTime GetNextOpen(DateTime dateTime, TimeZoneInfo timeZone)
    {
        DateTime localTime = ConvertToLocal(dateTime, timeZone);
        DateTime nextLocalOpen = GetNextOpen(localTime);
        DateTime nextOpen = ConvertFromLocal(nextLocalOpen, timeZone);
        return nextOpen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Timestamp GetNextOpen(Timestamp timestamp)
    {
        DateTime dt = GetNextOpen(timestamp.ToDateTime, TimeZoneInfo.Utc);
        return Timestamp.FromDateTime(dt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Timestamp GetNextClose(Timestamp timestamp)
    {
        DateTime dt = GetNextClose(timestamp.ToDateTime, TimeZoneInfo.Utc);
        return Timestamp.FromDateTime(dt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Timestamp GetLastClose(Timestamp timestamp)
    {
        DateTime dt = GetLastClose(timestamp.ToDateTime, TimeZoneInfo.Utc);
        return Timestamp.FromDateTime(dt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(Timestamp timestamp)
    {
        return Contains(timestamp.ToDateTime, TimeZoneInfo.Utc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Duration TimeTillOpen(Timestamp timestamp)
    {
        TimeSpan ts = TimeTillOpen(timestamp.ToDateTime, TimeZoneInfo.Utc);
        return Duration.FromNanoseconds(ts.Ticks * 100); // 1 tick = 100 ns
    }
}


using System;

namespace Tools;

/// Watches the process's garbage collector. Once a second, on the Clock thread, it reads the collector's totals and
/// writes one console line for a second in which a collection happened. A realtime scenario starts it. Cold path:
/// the check allocates nothing; only the line of a second with a collection formats, allocates and writes
/// (see Spec.md "The GC monitor runs on the Clock thread").
public static class GCMonitor
{
    private const int s_checkPeriodMilliseconds = 1000;

    /// The collector's running totals since the process started, at one moment.
    private readonly record struct GcCounters(long AllocatedBytes, TimeSpan Pause, int Gen0Count, int Gen1Count, int Gen2Count)
    {
        public static GcCounters NewNow() =>
            new(GC.GetTotalAllocatedBytes(), GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private static GcCounters s_lastGcCounters;   // as of the last check; the Clock thread only, once started

    /// Takes the totals as they stand and schedules the check once a second on the Clock thread.
    public static void Start()
    {
        s_lastGcCounters = GcCounters.NewNow();
        new TickTocker("GCMonitor", s_checkPeriodMilliseconds, OnTickTock);
    }

    /// Writes a line if a collection happened since the last check, then keeps the totals for the next one.
    private static void OnTickTock(Timestamp timestamp)
    {
        // Step 1: the totals now, and the collections since the last check
        GcCounters gcCounters = GcCounters.NewNow();
        int gen0Count = gcCounters.Gen0Count - s_lastGcCounters.Gen0Count;
        int gen1Count = gcCounters.Gen1Count - s_lastGcCounters.Gen1Count;
        int gen2Count = gcCounters.Gen2Count - s_lastGcCounters.Gen2Count;

        // Step 2: a line only for a second with a collection, so a quiet second formats and writes nothing
        bool hasCollected = gen0Count > 0 || gen1Count > 0 || gen2Count > 0;
        if (hasCollected)
        {
            Console.WriteLine($"{DateTime.UtcNow.ToLongString()} allocRate={(gcCounters.AllocatedBytes - s_lastGcCounters.AllocatedBytes):n0} B/s, " +
                $"pause={(gcCounters.Pause - s_lastGcCounters.Pause).TotalMilliseconds:n3} ms/s, " +
                $"Gen0/s={gen0Count}, Gen1/s={gen1Count}, Gen2/s={gen2Count}");
        }

        // Step 3: these totals are the next check's "before"
        s_lastGcCounters = gcCounters;
    }
}


using System;

namespace Tools;

public static class GCMonitor
{
    private static System.Threading.Timer? Timer;

    public static void Stop()
    {
        Timer?.Dispose();
        Timer = null;
    }
    public static void Start()
    {
        // Snapshot baseline counters (since-process-start aggregates).
        long lastAlloc = GC.GetTotalAllocatedBytes();              // total bytes allocated so far (process-wide)
        int lastG0 = GC.CollectionCount(0),                        // total Gen0 collections so far
            lastG1 = GC.CollectionCount(1),                        // total Gen1 collections so far
            lastG2 = GC.CollectionCount(2);                        // total Gen2 collections so far
        var lastPause = GC.GetTotalPauseDuration();                // total time spent in GC so far (TimeSpan)

        // Create a timer that fires every 1000 ms to compute 1-second deltas.
        Timer = new System.Threading.Timer(_ =>
        {
            // Read current counters.
            long alloc = GC.GetTotalAllocatedBytes();              // new total allocated bytes (cumulative)
            var pause = GC.GetTotalPauseDuration();                // new total GC pause (cumulative)

            // Print *per-second* rates by subtracting the previous snapshot.

            int g0 = GC.CollectionCount(0);
            int g1 = GC.CollectionCount(1);
            int g2 = GC.CollectionCount(2);

            int g0Delta = g0 - lastG0;                // Gen0 collections in the last second
            int g1Delta = g1 - lastG1;                // Gen1 collections in the last second    
            int g2Delta = g2 - lastG2;                // Gen2 collections in the last second

            if (g0Delta > 0 || g1Delta > 0 || g2Delta > 0)
            {
                string line = $"{DateTime.UtcNow.ToLongString()} allocRate={(alloc - lastAlloc):n0} B/s, " +       // bytes allocated in the last second
                $"pause={(pause - lastPause).TotalMilliseconds:n3} ms/s, " + // GC pause ms in the last second
                $"Gen0/s={g0Delta}, " +      // Gen0 collections in the last second
                $"Gen1/s={g1Delta}, " +      // Gen1 collections in the last second
                $"Gen2/s={g2Delta}";        // Gen2 collections in the last second


                Console.WriteLine(line);

            }


            // Advance baselines to “now” so the next tick reports the next 1s window.
            lastAlloc = alloc;
            lastPause = pause;
            lastG0 = g0;
            lastG1 = g1;
            lastG2 = g2;

        }, null, 1000, 1000);                                      // dueTime = 1s, period = 1s
    }
}

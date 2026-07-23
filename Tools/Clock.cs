using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tools;

// ---------- Clock + Reminder ----------
public enum ClockMode
{
    Simulation = 0,
    Realtime = 1,
}

public readonly struct Reminder : IEquatable<Reminder>
{
    public string Name => Callback.Method.Name;
    public Action<Timestamp> Callback { get; }
    public Timestamp Timestamp { get; }

    public Reminder(Timestamp timestamp, Action<Timestamp> callback)
    {
        Callback = callback;
        Timestamp = timestamp;
    }

    public bool Equals(Reminder other) => Timestamp == other.Timestamp && ReferenceEquals(Callback, other.Callback);

    public override bool Equals(object? obj) => obj is Reminder r && Equals(r);

    public override int GetHashCode() => HashCode.Combine(Timestamp, Callback);

    public override string ToString() => $"Reminder {Name} {Timestamp.ToString()}";
}

public static class Clock
{

    private static readonly LockedPriorityQueue<Timestamp, Reminder> s_reminders =
        new LockedPriorityQueue<Timestamp, Reminder>();

    // Events
    public static event Action<Exception>? Exception;
    public static event Action<Timestamp>? TickTock;
    public static event Action<Timestamp>? Started;
    public static event Action<Timestamp>? Stopped;
    public static event Action<Timestamp>? Interject;

    public static bool IsRunning => s_isRunning;
    private static volatile bool s_isRunning = false;
    private static volatile bool s_isStopping = false;
    public static bool IsStopping => s_isStopping;

    private static ClockMode s_mode = ClockMode.Simulation;
    public static ClockMode Mode
    {
        get => s_mode;
        set
        {
            if (IsRunning)
                throw new InvalidOperationException("Can not set mode after Clock has been started.");
            else
                s_mode = value;
        }
    }

    private static Timestamp s_simulationNow;
    private static Timestamp s_interjection;
    private static Timestamp s_begin = Timestamp.MinValue;

    public static Timestamp Begin
    {
        get => s_begin;
        set
        {
            if (IsRunning && Mode == ClockMode.Simulation)
                throw new InvalidOperationException("Cannot set Begin while simulation is running.");
            s_begin = value;
            s_simulationNow = s_begin;
        }
    }

    public static Timestamp End { get; set; } = Timestamp.MaxValue;

    static Clock()
    {
        // Warm up Stopwatch frequency once
        _ = Stopwatch.GetTimestamp();
        s_simulationNow = s_begin;

        // We cannot access instance Application methods easily if they aren't static, 
        // but assuming Application.AddExitAction is static based on context:
        Application.AddExitAction("Stop Clock", int.MaxValue, () =>
        {
            Stop();
            while (IsRunning)
                Thread.Sleep(1);
        });
    }

    public static int RemindersQueued => s_reminders.Count;

    public static Timestamp Now => (Mode == ClockMode.Simulation) ? s_simulationNow : Timestamp.UtcNow;

    // --- Reminder plumbing ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddReminder(Reminder reminder) => s_reminders.Enqueue(reminder.Timestamp, reminder);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TryRemoveReminder(Reminder reminder) => s_reminders.TryRemove(reminder);

    private static int ConsumeReminders(Timestamp now)
    {
        int count = 0;
        while (s_reminders.TryPeek(out _, out Reminder reminder) && reminder.Timestamp <= now)
        {
            if (s_reminders.TryDequeue(out _, out reminder))
            {
                try
                {
                    count += 1;
                    reminder.Callback(reminder.Timestamp);
                }
                catch (Exception ex)
                {
                    Exception?.Invoke(ex);
                }
            }
        }
        return count;
    }

    // Allow external "interjection" of a time to jump to (simulation)
    public static void OnInterject(Timestamp timestamp)
    {
        if (timestamp >= s_simulationNow)
            s_interjection = s_interjection.Min(timestamp);
    }

    // --- Control ---

    private static object _lock = new object();
    public static void Start()
    {

        lock (_lock)
        {
            if (s_isRunning)
                throw new InvalidOperationException("Clock already running.");

            s_isRunning = true;
        }
        
        Console.WriteLine($"Clock::{Mode} started.");

        try
        {
            Started?.Invoke(Now);

            if (Mode == ClockMode.Simulation)
                RunSimulation();
            else
                RunRealtime();
        }
        catch (Exception ex)
        {
            Exception?.Invoke(ex);
        }
        finally
        {
            Stopped?.Invoke(Now);
        }
    }

    public static void Stop() => s_isStopping = true;

    // ---------------------------------------------------------
    // SPEED CONTROL STATE
    // ---------------------------------------------------------
    // Volatile to ensure the loop sees setter updates immediately
    private static double s_simulationSpeed = double.MaxValue;
    private static long s_anchorWallTicks;
    private static Timestamp s_anchorSimTime;

    public static double SimulationSpeed
    {
        get => s_simulationSpeed;
        set
        {
            AddReminder(new Reminder(Timestamp.MinValue, timestamp =>
            {
                // If value hasn't changed, do nothing
                if (Math.Abs(s_simulationSpeed - value) < 0.001) return;

                // 1. Capture the state of time *right now* before changing speed
                //    This effectively "resets" the drift calculation to 0 at this exact moment.
                if (IsRunning && Mode == ClockMode.Simulation)
                {
                    s_anchorWallTicks = Stopwatch.GetTimestamp();
                    s_anchorSimTime = s_simulationNow;
                }

                // 2. Apply the new speed
                s_simulationSpeed = value;
            }));
            
        }
    }
    private static void RunSimulation()
    {
        s_simulationNow = Begin;

        // Initial Anchor: Start of simulation
        s_anchorWallTicks = Stopwatch.GetTimestamp();
        s_anchorSimTime = s_simulationNow;

        while (!IsStopping && s_simulationNow < End)
        {
            try
            {
                ConsumeReminders(s_simulationNow);

                // Determine next stop
                s_interjection = End;
                if (s_reminders.TryPeek(out _, out Reminder next))
                    s_interjection = next.Timestamp;

                Interject?.Invoke(s_interjection);

                SimulateSpeed();

                s_simulationNow = s_interjection;
                TickTock?.Invoke(s_simulationNow);
            }
            catch (Exception ex)
            {
                Exception?.Invoke(ex);
            }
        }

        try { ConsumeReminders(s_simulationNow); }
        catch (Exception ex) { Exception?.Invoke(ex); }
        Interject?.Invoke(s_interjection);

        s_isRunning = false;
    }

    private static void SimulateSpeed()
    {
        if (s_simulationSpeed < double.MaxValue && s_interjection > s_simulationNow)
        {
            // 1. Calculate how much Simulation Time has passed since the LAST SPEED CHANGE (Anchor)
            Duration simElapsed = s_interjection - s_anchorSimTime;

            // 2. Calculate the Target Wall Time required
            //    Formula: WallDuration = SimDuration / Speed
            double targetWallSeconds = simElapsed.TotalSeconds / s_simulationSpeed;
            long targetWallTicks = s_anchorWallTicks + (long)(targetWallSeconds * Stopwatch.Frequency);

            // 3. Wait loop
            while (true)
            {
                if (IsStopping) break;

                long currentWallTicks = Stopwatch.GetTimestamp();
                long ticksToWait = targetWallTicks - currentWallTicks;

                if (ticksToWait <= 0) break;

                if (ticksToWait > Stopwatch.Frequency / 1000) // > 1ms
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(10);
            }
        }
    }

    private static void RunRealtime()
    {
        while (!IsStopping)
        {
            try
            {
                var now = Timestamp.UtcNow;
                int count = ConsumeReminders(now);
                TickTock?.Invoke(now);
                if (count == 0)
                    Thread.Sleep(1);
            }
            catch (Exception ex)
            {
                Exception?.Invoke(ex);
            }
        }

        s_isRunning = false;
    }
}
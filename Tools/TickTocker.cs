using System;
using System.Collections.Generic;

namespace Tools;



/// <summary>
/// Periodic scheduler that invokes a callback at a fixed frequency (milliseconds),
/// optionally constrained to one or more trading <see cref="Session"/> windows.
/// Uses a <see cref="Clock"/> to schedule a single-shot <see cref="Reminder"/> for each tick.
/// </summary>
/// <remarks>
/// <para>
/// Behavior:
/// <list type="bullet">
///   <item>
///     <description>
///       If <see cref="Sessions"/> is empty, ticks are emitted continuously (24/7) at the requested cadence.
///     </description>
///   </item>
///   <item>
///     <description>
///       If <see cref="Sessions"/> are provided, ticks are emitted only within the currently-active
///       session window(s). Outside session hours, the next reminder is scheduled at the earliest session open,
///       aligned via <see cref="GetNextTickTock(Timestamp)"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       Tick alignment: <see cref="GetNextTickTock(Timestamp)"/> snaps the scheduled time to the next
///       <c>Frequency</c>-spaced boundary (in ms) using <see cref="Timestamp.RoundUpMilliseconds(int)"/>,
///       then applies <see cref="Offset"/> (phase). See method summary for details.
///     </description>
///   </item>
///   <item>
///     <description>
///       Lazy mode (<see cref="IsLazy"/>): when true, each next tick is based on <c>Clock.Now</c> (best-effort cadence).
///       When false, the next tick is based on the scheduled timestamp passed to the callback (stable cadence).
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// Startup:
/// The ctor auto-subscribes to <see cref="Clock.Started"/> and <see cref="Clock.Stopped"/>.
/// If the clock is already running (<see cref="Clock.IsRunning"/>), it immediately calls <see cref="Start(Timestamp)"/>.
/// </para>
/// <para>
/// Thread-safety: this type is not thread-safe. Use on a single scheduler thread/event loop.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var t = new TickTocker(
///     name: "CME-1s",
///     clock: clock,
///     frequency: 1000,
///     onTickTock: ts =&gt; Console.WriteLine($"tick @ {ts}"),
///     offset: 250,
///     sessions: new List&lt;Session&gt; { Session.CME }
/// );
/// // Ticks at ...:00.250, :01.250, :02.250 within CME session windows.
/// </code>
/// </example>
public class TickTocker
{
    /// <summary>Logical name for diagnostics.</summary>
    public string Name { get; }

    /// <summary>Clock used to schedule one-shot reminders and to query current time.</summary>

    /// <summary>User callback invoked for each tick. Receives the scheduled tick timestamp.</summary>
    public Action<Timestamp> OnTickTock { get; set; }

    /// <summary>Tick period in milliseconds. Must be &gt; 0.</summary>
    public int Frequency { get; set; }

    private List<Session> sessions = new List<Session>();

    /// <summary>Last scheduled reminder (single-shot).</summary>
    private Reminder Reminder;

    /// <summary>
    /// Active trading sessions that gate tick emission. If empty, ticks run continuously.
    /// </summary>
    /// <remarks>
    /// Setter copies the provided list (if non-null) into an internal list to avoid external mutation.
    /// </remarks>
    public List<Session> Sessions
    {
        get => sessions;
        set
        {
            sessions.Clear();
            if (value != null)
            {
                foreach (Session session in value)
                {
                    sessions.Add(session);
                }
            }
        }
    }

    /// <summary>Current session (if any) that the ticker considers active.</summary>
    private Session Session { get; set; }

    /// <summary>Earliest upcoming session open across <see cref="Sessions"/> (from the last scheduling point).</summary>
    private Timestamp NextSessionOpen { get; set; } = Timestamp.MaxValue;

    /// <summary>Earliest upcoming session close across <see cref="Sessions"/> (from the last scheduling point).</summary>
    private Timestamp CurrentSessionClose { get; set; } = Timestamp.MinValue;

    /// <summary>
    /// Phase offset in milliseconds relative to the <see cref="Frequency"/> boundary.
    /// Normalized to <c>[0, Frequency)</c> in the constructor.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// If true, each next tick is scheduled based on <see cref="Clock.Now"/> (best-effort cadence).
    /// If false, each next tick is scheduled based on the scheduled timestamp passed to the callback (stable cadence).
    /// </summary>
    public bool IsLazy { get; set; } = false;


    // Cache the delegate to prevent per-tick heap allocations
    private readonly Action<Timestamp> _onTickTockThenSetNextTickTockDelegate;

    /// <summary>
    /// Constructs a new <see cref="TickTocker"/>, subscribes to clock events, and immediately starts
    /// if the clock is already running.
    /// </summary>
    /// <param name="name">Logical name for diagnostics.</param>
    /// <param name="clock">Clock used to schedule reminders.</param>
    /// <param name="frequency">Tick period in milliseconds (&gt; 0).</param>
    /// <param name="onTickTock">Callback invoked on each tick with the scheduled timestamp.</param>
    /// <param name="offset">
    /// Phase in milliseconds relative to frequency (e.g., 250 means <c>..:00.250, :01.250, ...</c>).
    /// Any value (negative or &gt; frequency) is normalized to <c>[0, frequency)</c>.
    /// </param>
    /// <param name="sessions">Optional list of sessions limiting when ticks are emitted.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="clock"/> or <paramref name="onTickTock"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="frequency"/> &lt;= 0.</exception>
    public TickTocker(string name, int frequency, Action<Timestamp> onTickTock, int offset = 0, List<Session>? sessions = null)
    {
        if (onTickTock is null) throw new ArgumentNullException(nameof(onTickTock));
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency), "Frequency must be > 0 ms.");

        Name = name;
        Frequency = frequency;
        OnTickTock = onTickTock;
        Sessions = sessions!;
        // normalize into [0, Frequency)
        Offset = ((offset % frequency) + frequency) % frequency;

        // Initialize the delegate cache once
        _onTickTockThenSetNextTickTockDelegate = OnTickTockThenSetNextTickTock;

        Clock.Started += Start;
        Clock.Stopped += Stop;
        if (Clock.IsRunning)
        {
            Start(Clock.Now);
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"TickTocker(OnTickTock: {OnTickTock}, Frequency: {Frequency})";
    }

    /// <summary>
    /// Handles <see cref="Clock.Stopped"/>: attempts to remove the currently scheduled <see cref="Reminder"/>.
    /// </summary>
    /// <param name="timestamp">Clock-provided stop time (unused for scheduling).</param>
    private void Stop(Timestamp timestamp)
    {
        Clock.TryRemoveReminder(Reminder);
    }

    /// <summary>
    /// Handles <see cref="Clock.Started"/> (or immediate start in ctor):
    /// computes the next aligned tick and schedules it.
    /// </summary>
    /// <param name="timestamp">Clock-provided start time (UTC).</param>
    private void Start(Timestamp timestamp)
    {
        Timestamp nextTickTock = GetNextTickTock(timestamp);
        SetTickTock(nextTickTock);
    }

    /// <summary>
    /// Computes the next tick boundary at or after <paramref name="timestamp"/>,
    /// aligned to <see cref="Frequency"/> ms and phase-shifted by <see cref="Offset"/>.
    /// </summary>
    /// <remarks>
    /// Algorithm:
    /// <list type="number">
    ///   <item><description>Round up to the next multiple of <c>Frequency</c> ms (no offset yet): <c>t' = RoundUpMilliseconds(Frequency)</c>.</description></item>
    ///   <item><description>Shift by <c>-Frequency + Offset</c> so that the returned value is the first boundary with the desired phase.</description></item>
    ///   <item><description>If that result is &lt; the input timestamp, add one <c>Frequency</c> step.</description></item>
    /// </list>
    /// Example (<c>Frequency=1000, Offset=250</c>): boundaries are <c>..:00.250, :01.250, :02.250</c>.
    /// </remarks>
    private Timestamp GetNextTickTock(Timestamp timestamp)
    {
        Timestamp nextTickTock = timestamp.RoundUpMilliseconds(Frequency).AddMilliseconds(-Frequency + Offset);
        if (nextTickTock < timestamp)
        {
            nextTickTock = nextTickTock.AddMilliseconds(Frequency);
        }
        return nextTickTock;
    }

    /// <summary>
    /// Wrapper callback for the scheduled <see cref="Reminder"/>:
    /// calls user <see cref="OnTickTock"/> and then schedules the next tick
    /// based on either <see cref="Clock.Now"/> (<see cref="IsLazy"/> = true) or the scheduled timestamp (false).
    /// </summary>
    /// <param name="timestamp">The scheduled tick timestamp passed by the clock.</param>
    private void OnTickTockThenSetNextTickTock(Timestamp timestamp)
    {
        OnTickTock(timestamp);
        if (IsLazy)
        {
            SetNextTickTock(Clock.Now);
        }
        else
        {
            SetNextTickTock(timestamp);
        }
    }

    /// <summary>
    /// Schedules the next <see cref="Reminder"/> according to sessions and phase.
    /// If there are no sessions, or if the target time lies within the current session window,
    /// schedules directly; otherwise, schedules at the earliest upcoming session open.
    /// </summary>
    /// <param name="timestamp">Target time to schedule from (already aligned by caller).</param>
    private void SetTickTock(Timestamp timestamp)
    {
        if (Sessions.Count == 0)
        {
            Reminder = new Reminder(timestamp, _onTickTockThenSetNextTickTockDelegate);
        }
        else if (timestamp <= CurrentSessionClose)
        {
            // Still inside the currently active session window.
            Reminder = new Reminder(timestamp, _onTickTockThenSetNextTickTockDelegate);
        }
        else
        {
            // Compute next open and close across all sessions.
            Timestamp nextSessionOpen = Timestamp.MaxValue;
            Session nextSessionOpening = Session;
            foreach (Session session in Sessions)
            {
                Timestamp nextOpen = session.GetNextOpen(timestamp);
                if (nextOpen < nextSessionOpen)
                {
                    nextSessionOpen = nextOpen;
                    nextSessionOpening = session;
                }
            }
            Timestamp nextSessionClose = Timestamp.MaxValue;
            Session nextSessionClosing = Session;
            foreach (Session session in Sessions)
            {
                Timestamp nextClose = session.GetNextClose(timestamp);
                if (nextClose < nextSessionClose)
                {
                    nextSessionClose = nextClose;
                    nextSessionClosing = session;
                }
            }
            NextSessionOpen = nextSessionOpen;
            CurrentSessionClose = nextSessionClose;

            // If the next event is an OPEN (<= handles equality boundary), schedule from the open boundary;
            // otherwise, we're inside a session: schedule immediately at the requested timestamp.
            if (NextSessionOpen <= CurrentSessionClose)
            {
                Session = nextSessionOpening;
                Reminder = new Reminder(GetNextTickTock(NextSessionOpen), _onTickTockThenSetNextTickTockDelegate);
            }
            else if (NextSessionOpen > CurrentSessionClose)
            {
                Session = nextSessionClosing;
                Reminder = new Reminder(timestamp, _onTickTockThenSetNextTickTockDelegate);
            }
            else
            {
                throw new ThisStateShouldNeverOccur($"TickTocker.SetTickTock({timestamp.ToString()}) - if statement logic is incorrect.");
            }
        }
        Clock.AddReminder(Reminder);
    }

    /// <summary>
    /// Computes and schedules the next tick after <paramref name="timestamp"/>.
    /// In non-lazy mode this preserves exact cadence (<c>timestamp + Frequency</c>);
    /// in lazy mode it also uses <c>+Frequency</c> but the anchor passed in is <c>Clock.Now</c>.
    /// </summary>
    /// <param name="timestamp">Anchor time for the next tick computation.</param>
    private void SetNextTickTock(Timestamp timestamp)
    {
        timestamp = timestamp.AddMilliseconds(Frequency);
        SetTickTock(timestamp);
    }
}

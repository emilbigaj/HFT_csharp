//BEGIN_FILE HFT/Data/InstrumentManager.cs
using System;
using Tools;

namespace Data;


public sealed class SessionManager
{
    public event Action<Timestamp>? Changed;
    public event Action<Timestamp>? Closed;
    public event Action<Timestamp>? Opened;

    public Session Session { get; }
    public SessionManager(Session session)
    {
        Session = session;
        Clock.Started += Init;
        if (Clock.IsRunning)
        {
            Init(Clock.Now);
        }
    }
    public bool IsInSession { get; private set; }
    public bool IsClosedForWeekend { get; private set; }


    private void Init(Timestamp timestamp)
    {
        if (Session.Contains(timestamp))
        {
            OnSessionOpen(timestamp);
        }
        else
        {
            OnSessionClose(timestamp);
        }
    }


    public void OnSessionOpen(Timestamp timestamp)
    {
        IsInSession = true;
        Opened?.Invoke(timestamp);
        Changed?.Invoke(timestamp);
        Timestamp close = Session.GetNextClose(timestamp);
        Clock.AddReminder(new Reminder(close, OnSessionClose));
    }

    private void OnSessionClose(Timestamp timestamp)
    {
        IsInSession = false;
        Timestamp open = Session.GetNextOpen(timestamp);
        if (Session.DoesCloseForWeekend)
        {
            IsClosedForWeekend = Session.Open > Session.Close && open.DayOfWeek == DayOfWeek.Sunday || Session.Open < Session.Close && open.DayOfWeek == DayOfWeek.Monday;
        }
        Closed?.Invoke(timestamp);
        Changed?.Invoke(timestamp);
        Clock.AddReminder(new Reminder(open, OnSessionOpen));
    }


}
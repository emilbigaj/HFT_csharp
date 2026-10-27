using Avalonia.Media;
using Provider;
using Socket;
using Tools;

namespace Widget;

/// What every head of the GUI (a desk Workspace window, the phone's view) builds the same way: a widget from its type key, and the
/// latency text beside the clock. UI thread; cold path.
public static class Widgets
{
    /// A new widget of this type key (its class name, as a saved layout and the Widgets menu name it), on this context; null for a
    /// key no widget has.
    public static IWidget? NewWidget(string typeKey, WorkspaceContext workspaceContext) => typeKey switch
    {
        nameof(ChartWidget) => new ChartWidget(workspaceContext.Primary),
        nameof(FillsWidget) => new FillsWidget(workspaceContext.Primary),
        nameof(PositionsWidget) => new PositionsWidget(workspaceContext),
        nameof(OrdersWidget) => new OrdersWidget(workspaceContext),
        nameof(LadderWidget) => new LadderWidget(workspaceContext),
        nameof(AuditTrailWidget) => new AuditTrailWidget(workspaceContext),
        nameof(SendOrderWidget) => new SendOrderWidget(workspaceContext),
        nameof(InstrumentHeadersWidget) => new InstrumentHeadersWidget(workspaceContext),
        nameof(RiskLimitsWidget) => new RiskLimitsWidget(workspaceContext),
        nameof(MessageEfficiencyWidget) => new MessageEfficiencyWidget(workspaceContext),
        nameof(RateLimitWidget) => new RateLimitWidget(workspaceContext),
        _ => null
    };
}

/// The latency a remote GUI shows beside its clock: Latency[0] exactly as the Remote Mirror writes it (below 0 while its link is down);
/// offline once that Mirror's process has exited, not responding once it has stopped rewriting the row for the silence timeout (a hung
/// Remote Mirror). One per window or view, since it remembers when the row last changed. UI thread; cold path.
public sealed class LatencyText
{
    private ulong _latencySeq;                                      // the Latency[0] row's seq when last seen to change
    private Timestamp _latencyTimestamp = Timestamp.StopwatchNow;   // when it last changed; unchanged for the silence timeout is a hung Remote Mirror

    public string GetText(Context context, RemoteClient remoteClient)
    {
        // Step 1: the Remote Mirror itself: alive, and still rewriting the row every pass
        if (!remoteClient.IsRemoteMirrorAlive)
            return "Remote Mirror offline";
        ref SharedArrayEntry<Duration> latencyEntry = ref context.GetLatency(0);
        if (latencyEntry.GetSeq() != _latencySeq)
            (_latencySeq, _latencyTimestamp) = (latencyEntry.GetSeq(), Timestamp.StopwatchNow);
        if (Timestamp.StopwatchNow - _latencyTimestamp >= RemoteClient.s_silenceTimeout)
            return "Remote Mirror not responding";

        // Step 2: its number, to the microsecond so a steady link visibly ticks. Why a plain read: one aligned 8-byte Duration cannot tear, and Read would spin forever on a row a killed Remote Mirror left mid-write
        Duration latency = latencyEntry.GetReadonlyRef();
        return latency < Duration.Zero ? "Connection broken – reconnecting…" : $"{latency.TotalMilliseconds:0.000} ms";
    }
}

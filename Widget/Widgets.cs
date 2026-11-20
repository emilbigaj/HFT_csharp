using Avalonia.Controls;
using Avalonia.Media;
using Data;
using Provider;
using Socket;
using Tools;

namespace Widget;

/// What every head of the GUI (a desk Workspace window, the phone's view) builds or shows the same way: a widget from its type key,
/// a profit figure on a text block, and the latency text beside the clock. UI thread; cold path.
public static class Widgets
{
    /// Writes this profit on a widget's text block: the figure to two decimals, green above zero and red below.
    public static void ShowProfit(TextBlock textBlock, double profit)
    {
        textBlock.Text = $"{profit:N2}";
        textBlock.Foreground = Palette.GetProfitBrush(profit);
    }

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
        nameof(MarkoutsWidget) => new MarkoutsWidget(workspaceContext.Primary),
        _ => null
    };

    /// The instrument a file record names, or false when this context no longer lists it (an expired contract).
    public static bool TryGetInstrument(Context context, int instrumentId, out Instrument instrument)
    {
        try { instrument = context.GetInstrument(instrumentId); return true; }
        catch { instrument = null!; return false; }
    }
}

/// The latency a remote GUI shows beside its clock: Latency[0] exactly as the Remote Mirror writes it. The number is always the
/// link's own round trip, whether the first snapshot is still coming down (shown as initializing) or the copy is current; offline
/// once that Mirror's process has exited, not responding once it has stopped rewriting the row for the silence timeout (a hung
/// Remote Mirror). One per window or view, since it remembers when the row last changed. UI thread; cold path.
public sealed class LatencyText
{
    private ulong _latencySeq;                                      // the Latency[0] row's seq when last seen to change
    private Timestamp _latencyTimestamp = Timestamp.StopwatchNow;   // when it last changed; unchanged for the silence timeout is a hung Remote Mirror

    public string GetText(Context context, RemoteClient? remoteClient)
    {
        // Step 1: the Remote Mirror itself: alive, and still rewriting the row every pass. Why nullable: a GUI still taking its
        // first snapshot has no client yet, and on the phone the Mirror is a thread of this process
        if (remoteClient is { IsRemoteMirrorAlive: false })
            return "Remote Mirror offline";
        ref SharedArrayEntry<Duration> latencyEntry = ref context.GetLatency(0);
        if (latencyEntry.GetSeq() != _latencySeq)
            (_latencySeq, _latencyTimestamp) = (latencyEntry.GetSeq(), Timestamp.StopwatchNow);
        if (Timestamp.StopwatchNow - _latencyTimestamp >= RemoteClient.s_silenceTimeout)
            return "Remote Mirror not responding";

        // Step 2: the two faults the Remote Mirror names: no link at all, and a link whose Local end has stopped publishing
        // Why a plain read: one aligned 8-byte Duration cannot tear, and Read would spin forever on a row a killed Remote Mirror left mid-write
        Duration latency = latencyEntry.GetReadonlyRef();
        if (latency == RemoteClient.s_noLinkLatency)
            return "Connecting…";
        if (latency == RemoteClient.s_brokenLinkLatency)
            return "Connection broken – reconnecting…";

        // Step 3: its number, to the microsecond so a steady link visibly ticks; negative while this link's first snapshot is
        // still coming down, and the number is the same round trip either way
        // Why the number first either way: it then sits in the same place on the bar whichever phase the link is in
        return latency < Duration.Zero
            ? $"{-latency.TotalMilliseconds:0.000} ms Initializing…"
            : $"{latency.TotalMilliseconds:0.000} ms";
    }
}

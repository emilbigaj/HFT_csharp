using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tools;

public enum CallId : int
{
    // --- Client Layer (0 - 99) ---
    ClientReadExecution = 0,
    ClientReadData = 1,
    ClientOnSocketMessage = 2,
    ClientOnMarketByPriceDelta = 3,
    ClientOnTrade = 4,
    ClientOnFill = 5,
    ClientOnOrderState = 6,
    ClientOnOrderRejected = 7,
    ClientOnOrderTarget = 8,
    ClientCreate = 9,
    ClientAmend = 10,
    ClientValidate = 11,
    ClientSend = 12,
    ClientWrite = 13,

    // --- Risk Layer (100 - 199) ---
    RiskLayerValidateOrder = 100,

    // --- Instrument Layer (200 - 299) ---
    InstrumentOnMarketByPrice = 200,
    InstrumentOnTrade = 201,
    InstrumentTryGetQuote = 202,
    InstrumentMarketByPriceCopy = 203,
    InstrumentGetProfit = 204,
    InstrumentGetSeq = 205,
    InstrumentIfMarketByPriceChanged = 206,



    // --- Position Layer (300 - 399) ---
    PositionTryGetQuote = 300,
    PositionGetActiveTargets = 301,
    PositionOnFill = 302,
    PositionOnOrderActive = 303,
    PositionOnOrderDone = 304,
    PositionCalculateProfit = 305,

    // --- Algo / Strategy Layer (400 - 499) ---
    AlgoExecute = 400,
    AlgoTarget = 401,
    AlgoAggregateTargets = 402,
    AlgoZipperMerge = 403,
    AlgoNewOrder = 404,
    AlgoNewAmend = 405,
    AlgoSend = 406,

    // --- Market Data / OrderBook (500 - 599) ---
    MarketByPriceTrySetBid = 500,
    MarketByPriceTrySetAsk = 501,
    MarketByPriceGetBid = 502,
    MarketByPriceGetAsk = 503,
    MarketByPriceCopyToSnapshot = 504,
    SideByPriceTrySetQuantity = 505,
    SideByPriceGetQuantity = 506,

    // --- Socket / IPC Memory (600 - 699) ---
    SocketTryRead = 600,
    SocketWrite = 601,
    MemoryTryRead = 602,
    MemoryWriteToRing = 603,
    SharedArrayRead = 604,
    SharedArrayWrite = 605,
    SeqLockBeginRead = 606,
    SeqLockEndWrite = 607,

    MaxCallId = 1024
}

public struct LatencyRecord
{
    public int CallId;
    public long StartTicks;
    public long ChildrenTicks;
    public long TotalTicks;

    public Duration ExclusiveDuration => Duration.FromNanoseconds((long)(Math.Max(0, TotalTicks - ChildrenTicks) * Latency.NanosPerTick));
    public Duration TotalDuration => Duration.FromNanoseconds((long)(TotalTicks * Latency.NanosPerTick));
    public Duration ChildrenDuration => Duration.FromNanoseconds((long)(ChildrenTicks * Latency.NanosPerTick));
}

public delegate void LatencyFlushHandler(ReadOnlySpan<LatencyRecord> records);

/// <summary>
/// A zero-allocation, stack-only disposable timer.
/// Auto-flushes to subscribers when the root call finishes.
/// Tracks both inclusive and exclusive execution times.
/// </summary>
public ref struct Latency
{
    public static bool Enabled = true;

    private const int MaxRecords = 1024;

    [ThreadStatic]
    private static LatencyRecord[]? s_records;

    [ThreadStatic]
    private static int s_count;

    [ThreadStatic]
    private static int s_stackDepth;

    [ThreadStatic]
    private static int s_currentParentIndex = -1;

    public static event LatencyFlushHandler? OnFlush;
    public static readonly double NanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly int _recordIndex;
    private readonly int _parentIndex;
    private readonly long _startTicks;
    private bool _isCanceled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Latency(CallId callId) : this((int)callId)
    {

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Latency(int callId)
    {
        _isCanceled = false;
        _parentIndex = s_currentParentIndex;

        if (!Enabled)
        {
            _recordIndex = -1;
            _startTicks = 0;
            return;
        }

        if (s_records == null)
            Initialize();

        _startTicks = Stopwatch.GetTimestamp();

        _recordIndex = s_count++;
        s_stackDepth++;
        s_currentParentIndex = _recordIndex;

        if (_recordIndex < MaxRecords)
        {
            // Elide bounds checks by taking a reference to the array slot once
            ref LatencyRecord record = ref s_records![_recordIndex];
            record.CallId = callId;
            record.StartTicks = _startTicks;
            record.ChildrenTicks = 0;
            record.TotalTicks = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Cancel()
    {
        _isCanceled = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (!Enabled)
            return;

        s_stackDepth--;

        // Restore the context back to the parent frame
        if (_recordIndex == s_currentParentIndex)
            s_currentParentIndex = _parentIndex;

        if (_isCanceled)
        {
            if (_recordIndex == s_count - 1)
                s_count--;

            if (s_stackDepth == 0)
                FlushAndClear();

            return;
        }

        long endTicks = Stopwatch.GetTimestamp();
        long totalTicks = endTicks - _startTicks;

        if (_recordIndex < MaxRecords)
        {
            s_records![_recordIndex].TotalTicks = totalTicks;

            // Bubble up this call's total time to the parent's ChildrenTicks accumulator
            if (_parentIndex >= 0 && _parentIndex < MaxRecords)
                s_records[_parentIndex].ChildrenTicks += totalTicks;
        }

        if (s_stackDepth == 0)
            FlushAndClear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Initialize()
    {
        s_records = new LatencyRecord[MaxRecords];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FlushAndClear()
    {
        if (OnFlush != null && s_count > 0)
            OnFlush(new ReadOnlySpan<LatencyRecord>(s_records, 0, s_count));

        s_count = 0;
        s_currentParentIndex = -1;
    }
}
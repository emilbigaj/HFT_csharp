using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Data;
using Tools;

namespace Execution;

// CME's message weights. Sealed and passed by its own type, not an interface: dispatch measured +1.4 ns per send.
public sealed class CMEGetWeightedMessage
{
    public SessionManager SessionManager = new SessionManager(new Session("CME Message Efficiency Regular Trading Hours", Session.CME.TimeZone, new TimeSpan(7,0,0), new TimeSpan(16,0,0), true));
    public bool IsRegularTradingHours => SessionManager.IsInSession;

    // The MEP's weight, and in emtWeightedMessage the EMT's (Excessive Messaging Threshold's): a new order 0, a cancel 3, an
    // amend 1, the MEP's ×0.1 outside regular trading hours.
    public double GetWeightedMessage(OrderTargetAction orderTargetAction, out double emtWeightedMessage)
    {
        emtWeightedMessage = orderTargetAction == OrderTargetAction.Create ? 0 : orderTargetAction == OrderTargetAction.Cancel ? 3 : 1;
        return emtWeightedMessage * (IsRegularTradingHours ? 1 : 0.1);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct MessageEfficiency()
{
    public Header<OrderType> Header = new(OrderType.MessageEfficiency);   // Why: the server posts the row itself on its audit channel (see Spec.md)
    public bool Reset(DateTime dateTime)
    {
        DateTime tradeDateTime = TradeDate.Add(TradeTime);
        if (dateTime >= tradeDateTime)
        {
            TradeDate = dateTime.Date.AddDays(1);
            RawMessages = 0;
            WeightedMessages = 0;
            EMTWeightedMessages = 0;
            QuantityTraded = 0;
            InverseQuantityTraded = 1;
            return true;
        }
        return false;
    }
    public DateTime TradeDate;
    public TimeSpan TradeTime;
    public MessageEfficiencyTier TierEMT;   // CME's iLink session Excessive Messaging Threshold, judged on EMTWeightedMessages (see Spec.md)
    public MessageEfficiencyTier Tier1;
    public MessageEfficiencyTier Tier2;
    public MessageEfficiencyTier Tier3;
    public String4 ProductGroupRoot;
    public int ProductGroupId = -1;
    public int RawMessages = 0;
    public double WeightedMessages = 0;
    public double Efficiency => WeightedMessages * InverseQuantityTraded;
    public double InverseQuantityTraded = 1;
    public int QuantityTraded = 0;
    public double EMTWeightedMessages = 0;   // the EMT's score: as WeightedMessages, without the overnight discount
    public double EMTEfficiency => EMTWeightedMessages * InverseQuantityTraded;

    public void OnFill(int quantity)
    {
        QuantityTraded += Math.Abs(quantity);
        InverseQuantityTraded = 1.0 / QuantityTraded;
    }

    // Permissive default (simulation): tiers so high CanSend always falls through to "allowed".
    public static MessageEfficiency GetMaxLimits(String4 productGroupRoot) => new MessageEfficiency()
    {
        ProductGroupRoot = productGroupRoot,
        TierEMT = new MessageEfficiencyTier { DailyRawMessages = int.MaxValue, Benchmark = int.MaxValue },
        Tier1 = new MessageEfficiencyTier { DailyRawMessages = int.MaxValue, Benchmark = int.MaxValue },
        Tier2 = new MessageEfficiencyTier { DailyRawMessages = int.MaxValue, Benchmark = int.MaxValue },
        Tier3 = new MessageEfficiencyTier { DailyRawMessages = int.MaxValue, Benchmark = int.MaxValue },
    };

    // Realtime default for a group with no file: no MEP program (tiers of 0), and CME's EMT, which applies to every group.
    public static MessageEfficiency GetMinLimits(String4 productGroupRoot) => new MessageEfficiency()
    {
        ProductGroupRoot = productGroupRoot,
        TierEMT = new MessageEfficiencyTier { DailyRawMessages = 1_000_000, Benchmark = 500 },
    };

    // Whether one more message keeps the day within the EMT (TierEMT, EMT score) and the MEP tier it is in (Tier1-3, MEP score); a cancel always may.
    public bool CanSend(OrderTargetAction orderTargetAction, CMEGetWeightedMessage messageWeighter, out int rawMessages, out double weightedMessages, out double emtWeightedMessages)
    {
        rawMessages = RawMessages + 1;
        weightedMessages = WeightedMessages + messageWeighter.GetWeightedMessage(orderTargetAction, out double emtWeightedMessage);
        emtWeightedMessages = EMTWeightedMessages + emtWeightedMessage;
        bool isWithinEMT = TierEMT.IsWithin(rawMessages, emtWeightedMessages * InverseQuantityTraded);
        bool isWithinMEP = GetMEPTier(rawMessages).IsWithin(rawMessages, weightedMessages * InverseQuantityTraded);
        return isWithinEMT && isWithinMEP || orderTargetAction == OrderTargetAction.Cancel;
    }

    // The MEP tier the day's raw messages fall in: the highest threshold passed (Tier1 has the highest), Tier3 below all.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MessageEfficiencyTier GetMEPTier(int rawMessages) => rawMessages > Tier1.DailyRawMessages ? Tier1 : rawMessages > Tier2.DailyRawMessages ? Tier2 : Tier3;

    // Always sends, regardless of efficiency.
    public bool Send(OrderTargetAction orderTargetAction, CMEGetWeightedMessage messageWeighter)
    {
        bool canSend = CanSend(orderTargetAction, messageWeighter, out int rawMessages, out double weightedMessages, out double emtWeightedMessages);
        RawMessages = rawMessages;
        WeightedMessages = weightedMessages;
        EMTWeightedMessages = emtWeightedMessages;
        return canSend;
    }

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct MessageEfficiencyTier()
{
    public int DailyRawMessages;
    public int Benchmark;

    // Applies once the day's raw messages pass its threshold; a tier of 0 is one the group does not have.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsApplied(int rawMessages) => DailyRawMessages > 0 && rawMessages > DailyRawMessages;

    // Within this tier: not applied, or the score per lot below its benchmark.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsWithin(int rawMessages, double efficiency) => !IsApplied(rawMessages) || efficiency < Benchmark;

    // The score per lot as a share of the benchmark, for display; 0 while not applied.
    public readonly double GetBenchmarkRatio(int rawMessages, double efficiency) => !IsApplied(rawMessages) ? 0 : Benchmark <= 0 ? double.PositiveInfinity : efficiency / Benchmark;

    public override string ToString()
    {
        return Json.Serialize(this);
    }

}







[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public record struct RateLimit(Duration Duration, int Limit)
{
    public int RateLimitId = -1;

    // Unlimited in a 1 second window: only the 255-per-bucket burst cap remains (see Spec.md).
    public static RateLimit GetMaxLimits(int rateLimitId) => new RateLimit(Duration.FromSeconds(1), int.MaxValue)
    {
        RateLimitId = rateLimitId,
    };
    public static RateLimit GetMinLimits(int rateLimitId) => new RateLimit(Duration.FromSeconds(1), 0)
    {
        RateLimitId = rateLimitId,
    };

    public override string ToString()
    {
        return Json.Serialize(this);
    }

}





// A rolling-window throttle in 64 bytes, so it can live in a shared array and the GUI can read it
// from another process. Exact send timestamps are replaced by BucketCount coarse buckets; they span
// one bucket MORE than Duration, so the count covers a superset of the window and can only
// over-state it, never under-state it. A bucket refuses at 255, which is the burst limit. Total is
// the running sum of the buckets, so a send never loops over them (see Spec.md).
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct RollingRateLimit
{
    public const int BucketCount = 32;

    public RateLimit RateLimit;             //  0, 16
    public Timestamp BucketTimestamp;       // 16, 8   start of the newest bucket
    public int BucketIndex;                 // 24, 4   newest bucket
    public int Total;                       // 28, 4   sum of Counts, kept as they change
    public Array32<byte> Counts;            // 32, 32
                                            // = 64

    public RollingRateLimit(RateLimit rateLimit)
    {
        RateLimit = rateLimit;
        BucketTimestamp = default;   // nanos 0: the first send rolls the whole ring forward
        BucketIndex = 0;
        Total = 0;
        Counts = default;
    }

    // BucketCount-1, not BucketCount: the buckets then span Duration + one bucket, which is what keeps the count conservative.
    private readonly long BucketNanoseconds => RateLimit.Duration.TotalNanoseconds / (BucketCount - 1);

    // Messages sent in the window ending at timestamp, rounded UP to a bucket boundary. Safe from a
    // reader in another process: it mutates nothing and decays at the reader's own clock.
    public readonly int GetCount(Timestamp timestamp)
    {
        // Step 1: how many whole buckets have passed since the writer last rolled
        long elapsedNanoseconds = timestamp.NanosSinceEpoch - BucketTimestamp.NanosSinceEpoch;
        long expiredBuckets = elapsedNanoseconds <= 0 ? 0 : elapsedNanoseconds / BucketNanoseconds;

        // Step 2: the whole ring has aged out, nothing is in the window
        if (expiredBuckets >= BucketCount)
            return 0;

        // Step 3: start from the running total, which covers every bucket as of that last roll
        int count = Total;

        // Step 4: take off the oldest buckets that have expired since, usually none
        for (long offset = BucketCount - expiredBuckets; offset < BucketCount; offset++)
        {
            int bucketIndex = BucketIndex - (int)offset;
            if (bucketIndex < 0)
                bucketIndex += BucketCount;
            count -= Counts[bucketIndex];
        }
        return count;
    }

    public readonly bool CanSendOrder(Timestamp timestamp)
    {
        // Step 1: the window is full
        if (GetCount(timestamp) >= RateLimit.Limit)
            return false;

        // Step 2: the send would land in the newest bucket and that bucket is at its burst cap
        long elapsedNanoseconds = timestamp.NanosSinceEpoch - BucketTimestamp.NanosSinceEpoch;
        bool isNewestBucketCurrent = elapsedNanoseconds >= 0 && elapsedNanoseconds < BucketNanoseconds;
        return !isNewestBucketCurrent || Counts[BucketIndex] < byte.MaxValue;
    }

    public bool TrySendOrder(Timestamp timestamp)
    {
        // Step 1: roll the ring up to now, so nothing in it is expired and Total is the count
        Advance(timestamp);

        // Step 2: the window is full
        if (Total >= RateLimit.Limit)
            return false;

        // Step 3: the newest bucket is at its burst cap; refuse rather than wrap
        ref byte count = ref Counts[BucketIndex];
        if (count == byte.MaxValue)
            return false;

        // Step 4: record the send in the newest bucket and in the running total
        count++;
        Total++;
        return true;
    }

    // Counts a send that goes out regardless of the limit, a cancel; the bucket still stops at 255 rather than wrapping.
    public void SendOrder(Timestamp timestamp)
    {
        // Step 1: roll the ring up to now, so nothing in it is expired and Total is the count
        Advance(timestamp);

        // Step 2: the newest bucket is at its burst cap; leave the count rather than wrap
        ref byte count = ref Counts[BucketIndex];
        if (count == byte.MaxValue)
            return;

        // Step 3: record the send in the newest bucket and in the running total
        count++;
        Total++;
    }

    // Rolls the newest bucket forward to timestamp, zeroing every bucket it passes.
    private void Advance(Timestamp timestamp)
    {
        // Step 1: still inside the newest bucket, nothing to roll; this is the common case
        long bucketNanoseconds = BucketNanoseconds;
        long elapsedNanoseconds = timestamp.NanosSinceEpoch - BucketTimestamp.NanosSinceEpoch;
        if (elapsedNanoseconds < bucketNanoseconds)
            return;

        // Step 2: a whole ring's worth has passed, start again from empty at this timestamp
        long steps = elapsedNanoseconds / bucketNanoseconds;
        if (steps >= BucketCount)
        {
            Counts = default;
            Total = 0;
            BucketIndex = 0;
            BucketTimestamp = timestamp;
            return;
        }

        // Step 3: step the newest bucket forward, zeroing each bucket it lands on and taking it off Total
        for (long step = 0; step < steps; step++)
        {
            BucketIndex = BucketIndex + 1 < BucketCount ? BucketIndex + 1 : 0;
            Total -= Counts[BucketIndex];
            Counts[BucketIndex] = 0;
        }

        // Step 4: the newest bucket starts on the boundary the steps carried it to, not at timestamp, so buckets stay aligned
        BucketTimestamp += Duration.FromNanoseconds(steps * bucketNanoseconds);
    }

    public override readonly string ToString() => Json.Serialize(this);
}

public sealed class SessionRateLimit
{
    private readonly int _limit;

    // Messages sent since the last Reset, which the session roll calls.
    public int Count { get; private set; }

    public SessionRateLimit(int limit)
    {
        _limit = limit;
        Count = 0;
    }

    public bool CanSendOrder(Timestamp timestamp)
    {
        return Count < _limit;
    }

    public bool TrySendOrder(Timestamp timestamp)
    {
        if (!CanSendOrder(timestamp))
        {
            return false;
        }
        Count++;
        return true;
    }

    public void Reset()
    {
        Count = 0;
    }
}


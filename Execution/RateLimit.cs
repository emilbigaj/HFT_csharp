using System;
using System.Runtime.InteropServices;
using Data;
using Tools;

namespace Execution;

public class CMEGetWeightedMessage : IGetWeightedMessage
{
    public SessionManager SessionManager = new SessionManager(new Session("CME Message Efficiency Regular Trading Hours", Session.CME.TimeZone, new TimeSpan(7,0,0), new TimeSpan(16,0,0), true));
    public bool IsRegularTradingHours => SessionManager.IsInSession;
    public double GetWeightedMessage(OrderTargetAction orderTargetAction)
    {
        double weight = orderTargetAction == OrderTargetAction.Create ? 0 : orderTargetAction == OrderTargetAction.Cancel ? 3 : 1;
        weight *= IsRegularTradingHours ? 1 : 0.1;
        return weight;
    }
}

public interface IGetWeightedMessage
{
    public double GetWeightedMessage(OrderTargetAction orderTargetAction);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct MessageEfficiency(String4 productGroup)
{
    public bool Reset(DateTime dateTime)
    {
        DateTime tradeDateTime = TradeDate.Add(TradeTime);
        if (dateTime >= tradeDateTime)
        {
            TradeDate = dateTime.Date.AddDays(1);
            RawMessages = 0;
            WeightedMessages = 0;
            QuantityTraded = 0;
            InverseQuantityTraded = 1;
            return true;
        }
        return false;
    }
    public DateTime TradeDate;
    public TimeSpan TradeTime;
    public MessageEfficiencyTier Tier0;
    public MessageEfficiencyTier Tier1;
    public MessageEfficiencyTier Tier2;
    public MessageEfficiencyTier Tier3;
    public String4 ProductGroup = productGroup;
    public int ProductGroupId = -1;
    public int RawMessages = 0;
    public double WeightedMessages = 0;
    public double Efficiency => WeightedMessages * InverseQuantityTraded;
    public double InverseQuantityTraded = 1;
    public int QuantityTraded = 0;

    public void OnFill(int quantity)
    {
        QuantityTraded += Math.Abs(quantity);
        InverseQuantityTraded = 1.0 / QuantityTraded;
    }
    
    

    // Permissive default (simulation): tiers so high TrySend always falls through to "allowed".
    public static MessageEfficiency GetMaxLimits(String4 productGroup) => new MessageEfficiency(productGroup)
    {
        Tier0 = new MessageEfficiencyTier { DailyRawMessages = int.MaxValue, Benchmark = int.MaxValue },
        Tier1 = new MessageEfficiencyTier { DailyRawMessages = int.MaxValue, Benchmark = int.MaxValue },
        Tier2 = new MessageEfficiencyTier { DailyRawMessages = int.MaxValue, Benchmark = int.MaxValue },
        Tier3 = new MessageEfficiencyTier { DailyRawMessages = int.MaxValue, Benchmark = int.MaxValue },
    };

    // Restrictive default (live until configured): zero tiers => TrySend blocks every message.
    public static MessageEfficiency GetMinLimits(String4 productGroup) => new MessageEfficiency(productGroup)
    {
        Tier0 = new MessageEfficiencyTier { DailyRawMessages = 0, Benchmark = 0 },
        Tier1 = new MessageEfficiencyTier { DailyRawMessages = 0, Benchmark = 0 },
        Tier2 = new MessageEfficiencyTier { DailyRawMessages = 0, Benchmark = 0 },
        Tier3 = new MessageEfficiencyTier { DailyRawMessages = 0, Benchmark = 0 },
    };

    public bool CanSend(OrderTargetAction orderTargetAction, IGetWeightedMessage messageWeighter, out int rawMessages, out double weightedMessages)
    {
        rawMessages = RawMessages + 1;
        double weightedMessage = messageWeighter.GetWeightedMessage(orderTargetAction);
        weightedMessages = WeightedMessages + weightedMessage;
        if (rawMessages > Tier1.DailyRawMessages)
        {
            double efficiency = weightedMessages * InverseQuantityTraded;
            return efficiency < Tier1.Benchmark || orderTargetAction == OrderTargetAction.Cancel;
        }
        else if (rawMessages > Tier2.DailyRawMessages)
        {
            double efficiency = weightedMessages * InverseQuantityTraded;
            return efficiency < Tier2.Benchmark || orderTargetAction == OrderTargetAction.Cancel;
        }
        else if (rawMessages > Tier3.DailyRawMessages)
        {
            double efficiency = weightedMessages * InverseQuantityTraded;
            return efficiency < Tier3.Benchmark || orderTargetAction == OrderTargetAction.Cancel;
        }
        else
        {
            return true;
        }
    }

    // Always sends, regardless of efficiency.
    public bool Send<T>(OrderTargetAction orderTargetAction, T messageWeighter) where T : IGetWeightedMessage
    {
        bool canSend = CanSend(orderTargetAction, messageWeighter, out int rawMessages, out double weightedMessages);
        RawMessages = rawMessages;
        WeightedMessages = weightedMessages;
        return canSend;
    }

    // Only sends if the efficiency is below the benchmark, otherwise returns false and does not send.
    public bool TrySend<T>(OrderTargetAction orderTargetAction, T messageWeighter) where T : IGetWeightedMessage
    {
        bool canSend = CanSend(orderTargetAction, messageWeighter, out int rawMessages, out double weightedMessages);
        if (canSend)
        {
            RawMessages = rawMessages;
            WeightedMessages = weightedMessages;
        }
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

    // CME Globex order entry, on the stricter reading of its window and under the reject line (see Spec.md).
    public static readonly RateLimit CMEOrderEntry = new RateLimit(Duration.FromSeconds(3), 500);

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


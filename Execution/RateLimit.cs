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
        DateTime tradeDate = dateTime.Date.AddDays(1);
        TimeSpan tradeTime = dateTime - tradeDate;
        if (tradeDate > TradeDate && tradeTime >= TradeTime)
        {
            TradeDate = tradeDate;
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
    public override string ToString()
    {
        return Json.Serialize(this);
    }

}





public sealed class SessionRateLimit
{
    private readonly int _limit;
    private int _ordersSentToday;

    public SessionRateLimit(int limit)
    {
        _limit = limit;
        _ordersSentToday = 0;
    }

    public bool CanSendOrder(Timestamp timestamp)
    {
        return _ordersSentToday < _limit;
    }

    public bool TrySendOrder(Timestamp timestamp)
    {
        if (!CanSendOrder(timestamp))
        {
            return false;
        }
        _ordersSentToday++;
        return true;
    }

    public void Reset()
    {
        _ordersSentToday = 0;
    }
}

public sealed class RollingRateLimit
{
    private readonly RateLimit _rateLimit;
    private readonly Timestamp[] _timestamps;
    private int _current;

    public RollingRateLimit(RateLimit rateLimit)
    {
        _rateLimit = rateLimit;
        _timestamps = new Timestamp[_rateLimit.Limit];
        _current = 0;
    }

    public bool CanSendOrder(Timestamp timestamp)
    {
        Timestamp oldestTimestamp = _timestamps[_current];

        if (timestamp - oldestTimestamp >= _rateLimit.Duration)
            return true;

        return false;
    }

    public bool TrySendOrder(Timestamp timestamp)
    {
        if (!CanSendOrder(timestamp))
        {
            return false;
        }

        _timestamps[_current] = timestamp;

        int next = _current + 1;
        _current = next < _rateLimit.Limit ? next : 0;

        return true;
    }
}
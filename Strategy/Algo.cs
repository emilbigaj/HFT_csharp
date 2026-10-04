using Data;
using Execution;
using Provider;
using Socket;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tools;

namespace Strategy;

[SkipLocalsInit]
public unsafe abstract class Algo
{
    public Instrument Instrument { get; }
    public Client Client { get; }
    public Position Position { get; }

    public ulong Count { get; private set; }

    /// <summary>
    /// Hold at most one working order per (side, price).
    ///
    /// When a target is matched against a resting order at the same price and quantity is left over,
    /// the remainder is discarded rather than becoming a second order at that price. A second order
    /// rests behind our own size, so it only fills once the level is swept -- i.e. exactly when we
    /// did not want it. The size is collected instead on the next reprice, where a price change has
    /// already forfeited queue priority and the amend-up is therefore free, clipped to the risk limits.
    ///
    /// Cost: we quote underweight for as long as the price is stable.
    /// Set false to restore the split-order behaviour.
    /// </summary>
    public bool IsOneOrderPerPrice { get; set; } = true;

    private const int s_maxOrders = 64;

    public Algo(Client client, Position position)
    {
        Position = position;
        Instrument = Position.Instrument;
        Client = client;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SortKey
    {
        public ulong PackedScore;
        public int Index;
    }

    private readonly ArrayList<ActiveTarget> _activeTargets = new ArrayList<ActiveTarget>(s_maxOrders);
    private bool _hasSnapshot;

    // Take at the top of the tick, BEFORE reading position — the era rule (see Spec.md).
    public void SnapshotActives()
    {
        _hasSnapshot = true;
        _activeTargets.Clear();
        Position.ActiveTargetsEnumerator activeTargets = Position.ActiveTargets.GetEnumerator();
        while (activeTargets.MoveNext())
        {
            _activeTargets.Add(activeTargets.Current);
        }
    }

    protected int GetPositionQuantity()
    {
        while(true)
        {
            ref readonly SharedArrayEntry<PositionHeader> positionHeaderEntry = ref Position.PositionHeader;
            ulong seq0 = positionHeaderEntry.GetSeq();
            // Odd = fill transaction in progress: without this, a pass that runs entirely inside the
            // write window sees seq0 == seq1 (both odd) and validates the mid-transaction pair.
            if (Protocol.IsWriteInProgress(seq0)) { X86BaseWrapper.Pause(); continue; }
            SnapshotActives();
            int quantity = positionHeaderEntry.GetReadonlyRef().Quantity;
            ulong seq1 = positionHeaderEntry.GetSeq();
            if (seq0 == seq1)
                return quantity;
        }
    }

    private struct SortKeyComparer : IComparer<SortKey>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(SortKey x, SortKey y) => x.PackedScore.CompareTo(y.PackedScore);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetPackedActiveKey(in ActiveTarget active)
    {
        int sign = active.Target.Sign;
        ulong signPart = (ulong)((~(sign >> 31)) & 1);
        ulong ticksPart = (ulong)((long)active.Target.Ticks - int.MinValue);
        ticksPart ^= (ulong)(~(sign >> 31));
        uint queuePart = (uint)active.QuantityAhead & 0x7FFFFFFF;
        return (signPart << 63) | (ticksPart << 31) | queuePart;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetPackedTargetKey(in Target target)
    {
        int sign = target.Sign;
        ulong signPart = (ulong)((~(sign >> 31)) & 1);
        ulong ticksPart = (ulong)((long)target.Ticks - int.MinValue);
        ticksPart ^= (ulong)(~(sign >> 31));
        return (signPart << 63) | (ticksPart << 31);
    }

    // Sends the targets exactly as asked: anything refused comes back as a reject and pauses the algo, so a strategy bug shows (see Spec.md).
    public void Target(ref StackList<Target> targets)
    {
        Target(ref targets, isBestEffort: false);
    }

    // Best effort: clipped to the risk limits, dropped when it would be refused, never a reject; false when a target was not sent as asked (see Spec.md).
    public bool TryTarget(ref StackList<Target> targets)
    {
        return Target(ref targets, isBestEffort: true);
    }

    // Set per Target call: Send and CancelAllOrders follow the mode of the call that runs them.
    private bool _isBestEffort;
    private bool _isTargetAchieved;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool Target(ref StackList<Target> targets, bool isBestEffort)
    {
        //using Latency latency = new Latency(CallId.AlgoTarget);
        _isBestEffort = isBestEffort;
        _isTargetAchieved = true;
        // Consumed before any early return or throw, so a later call never works from this tick's snapshot.
        bool hasSnapshot = _hasSnapshot;
        _hasSnapshot = false;

        if (Position.AlgoStatus == AlgoStatus.Paused)
        {
            CancelAllOrders();
            return false;
        }

        // ---------------------------------------------------------
        // 0. Validate strategy logic
        // ---------------------------------------------------------

        if (targets.Count > s_maxOrders)
            ThrowOrderCountExceeded(targets.Count);

        // One pass: drop zero quantities (a target with no side is not a target), take IOCs out (they never rest, so they
        // never take part in the zipper; Phase 8 sends them), and bound buys and sells across both for the self-cross guard.
        Target* immediateOrCancelPtr = stackalloc Target[targets.Count];
        UnsafeStackList<Target> immediateOrCancelTargets = new UnsafeStackList<Target>(immediateOrCancelPtr);
        int maxBuyTicks = int.MinValue;
        int minSellTicks = int.MaxValue;
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < targets.Count; readIndex++)
        {
            Target target = targets[readIndex];
            if (target.WorkingQuantity == 0)
                continue;
            int mask = target.WorkingQuantity >> 31;   // -1 sell, 0 buy
            maxBuyTicks = Math.Max(maxBuyTicks, (target.Ticks & ~mask) | (int.MinValue & mask));
            minSellTicks = Math.Min(minSellTicks, (target.Ticks & mask) | (int.MaxValue & ~mask));
            if (target.TimeInForce == TimeInForce.ImmediateOrCancel)
                immediateOrCancelTargets.Add() = target;
            else if (target.TimeInForce == TimeInForce.Day)
                targets[writeIndex++] = target;
            else
                throw new NotImplementedException($"TimeInForce {target.TimeInForce}: an algo target is Day or ImmediateOrCancel");
        }
        while (targets.Count > writeIndex)
            targets.SwapRemoveAt(targets.Count - 1);
        if (maxBuyTicks >= minSellTicks)
            ThrowSelfCrossingTargets(maxBuyTicks, minSellTicks);


        // ---------------------------------------------------------
        // 1. Setup & Aggregate + Sort Targets
        // ---------------------------------------------------------

        AggregateTargets(ref targets);

        int targetCount = targets.Count;


        Target* sortedTargetPtr = stackalloc Target[targetCount + 1]; // add 1 for sentinel
        UnsafeStackList<Target> sortedTargets = new(sortedTargetPtr, targetCount);

        fixed(Target* targetsPtr = targets.AsSpan())
        {
            Unsafe.CopyBlock(sortedTargetPtr, targetsPtr, (uint)(targetCount * sizeof(Target)));
        }
        // add loop break sentinel
        sortedTargets[targetCount] = new Target(int.MinValue, 1);

        // ---------------------------------------------------------
        // 2. Setup & Sort Actives
        // ---------------------------------------------------------
        // Un-migrated caller: snapshot now — era-unsafe but identical to pre-snapshot behaviour; never zipper a previous tick's actives.
        if (!hasSnapshot)
            SnapshotActives();
        _hasSnapshot = false; // consumed; next tick must re-take it

        SortKey* activeKeysPtr = stackalloc SortKey[_activeTargets.Count + 1]; // add 1 for sentinel
        UnsafeStackList<SortKey> activeKeys = new(activeKeysPtr);

        int minActiveSellPrice = int.MaxValue;
        int maxActiveBuyPrice = int.MinValue;

        foreach (ActiveTarget active in _activeTargets)
        {
            activeKeys.Add() = new SortKey
            {
                PackedScore = GetPackedActiveKey(in active),
                Index = activeKeys.Count-1
            };

            int ticks = active.Target.Ticks;
            int sign = active.Target.Sign;
            int mask = sign >> 31;
            int buyInput = (ticks & ~mask) | (int.MinValue & mask);
            int sellInput = (ticks & mask) | (int.MaxValue & ~mask);
            maxActiveBuyPrice = Math.Max(maxActiveBuyPrice, buyInput);
            minActiveSellPrice = Math.Min(minActiveSellPrice, sellInput);
        }


        InsertionSortKeysInplace(activeKeysPtr, activeKeys.Count);
        activeKeys.Add(new SortKey { PackedScore = ulong.MaxValue });


        // ---------------------------------------------------------
        // Phase 1: Pointer Zipper (Output -> Bitset64)
        // ---------------------------------------------------------
        // GOAL: Minimize churn by matching existing Active Orders to new Targets.
        // STRATEGY: "Zipper Merge" on two sorted lists (O(N) complexity).
        //
        // SORT ORDER (Critical):
        // 1. Aggressive Sells (Lowest Price)
        // 2. Passive Sells (Highest Price)
        // 3. Aggressive Buys (Highest Price)
        // 4. Passive Buys (Lowest Price)
        //
        // TERTIARY SORT (Active Orders Only):
        // If Price & Side match, orders are sorted by 'QuantityAhead' (Ascending).
        // This ensures we match/keep orders at the FRONT of the queue first,
        // preserving our most valuable queue position.

        Bitset64 unmatchedTargets = new Bitset64();
        Bitset64 unmatchedActiveKeys = new Bitset64();

        // Targets a same-price order was matched against that still have quantity left over. Under
        // IsOneOrderPerPrice that remainder is dropped rather than handed to Phase 7 as a second
        // order at the same price. With the flag off nothing is ever set here, so every target
        // reaches Phase 7 exactly as before.
        Bitset64 partiallyMatchedTargets = new Bitset64();

        {
            Target* target = sortedTargets.Ptr;
            SortKey* activeKey = activeKeys.Ptr;

            while (true)
            {
                ulong targetKeyScore = GetPackedTargetKey(in *target) >> 31; // zero queue priority
                ulong activeKeyScore = activeKey->PackedScore >> 31;  // zero queue priority

                if (targetKeyScore == activeKeyScore) // => imples sign is equal
                {
                    if (targetKeyScore == (ulong.MaxValue >> 31))
                        break;

                    int activeIndex = activeKey->Index;
                    ref ActiveTarget active = ref _activeTargets[activeIndex];

                    int absTargetQty = (target->WorkingQuantity < 0) ? -target->WorkingQuantity : target->WorkingQuantity;
                    int absActiveQty = (active.Target.WorkingQuantity < 0) ? -active.Target.WorkingQuantity : active.Target.WorkingQuantity;

                    if (absActiveQty <= absTargetQty)
                    {
                        target->SetQuantity(target->WorkingQuantity - active.Target.WorkingQuantity);
                        activeKey++;
                        if (target->WorkingQuantity == 0)
                            target++;
                        else if (IsOneOrderPerPrice)
                            partiallyMatchedTargets.Set((int)(target - sortedTargets.Ptr));
                    }
                    else
                    {
                        // Best effort: a reduce cut further misses its target, and one nothing workable fits becomes a cancel,
                        // so a level never stays above its target (see Spec.md).
                        OrderTarget reduceOrder = NewAmend(in active, in *target);
                        int orderQuantity = reduceOrder.OrderProfile.Quantity;
                        if (_isBestEffort && !Client.RiskLayer.TryClipToRiskLimit(ref reduceOrder))
                            reduceOrder = NewAmend(in active, active.Target.Ticks, 0);
                        _isTargetAchieved &= reduceOrder.OrderProfile.Quantity == orderQuantity;
                        Send(ref reduceOrder);
                        target++;
                        activeKey++;
                    }
                }
                else if (targetKeyScore < activeKeyScore)
                {
                    int targetIndex = (int)(target - sortedTargets.Ptr);
                    if (!partiallyMatchedTargets[targetIndex])
                        unmatchedTargets.Set(targetIndex);
                    target++;
                }
                else
                {
                    unmatchedActiveKeys.Set((int)(activeKey - activeKeys.Ptr));
                    activeKey++;
                }
            }
        }

        // ---------------------------------------------------------
        // Phase 2: Reprice
        // ---------------------------------------------------------
        // Each unmatched target takes unmatched same-side actives in price order, largest first among actives at one price. Best effort clips
        // every amend to the risk limits, so when the whole move fits the first active takes the full target and the rest are
        // cancelled in Phase 5; when it doesn't, actives are repriced at the sizes that fit until the target is covered.
        // Strict sends the whole move as asked. IsOneOrderPerPrice drops what is left of a target after its first active.
        // Capacity: every target and every active yields at most one delayed order.
        OrderTarget* delayedPtr = stackalloc OrderTarget[unmatchedTargets.Count + unmatchedActiveKeys.Count];
        UnsafeStackList<OrderTarget> delayed = new UnsafeStackList<OrderTarget>(delayedPtr);

        // ITERATORS: Create local copies of the Bitsets.
        // We modify these copies to drive the loop iteration.
        Bitset64 unmatchedTargetsCopy = unmatchedTargets;
        Bitset64 unmatchedActiveKeysCopy = unmatchedActiveKeys;

        while (!unmatchedTargetsCopy.IsEmpty && !unmatchedActiveKeysCopy.IsEmpty)
        {
            int targetIndex = unmatchedTargetsCopy.LowestSet;
            // A reprice gives up the old queue, so among our actives at one price the largest goes first: it carries the most of the target within its own reservation (see Spec.md).
            int activeKeyIndex = GetLargestActiveKeyIndexAtPrice(activeKeysPtr, unmatchedActiveKeysCopy, unmatchedActiveKeysCopy.LowestSet);

            ref Target target = ref sortedTargets[targetIndex];
            ref ActiveTarget active = ref _activeTargets[activeKeys[activeKeyIndex].Index];

            if (target.Sign == active.Target.Sign)
            {
                // SAME SIDE: Reprice. The active is settled either way: amended here, or cancelled in Phase 5 when nothing workable fits.
                unmatchedActiveKeysCopy.Clear(activeKeyIndex);

                OrderTarget repriceOrder = NewAmend(in active, in target);
                if (_isBestEffort && !Client.RiskLayer.TryClipToRiskLimit(ref repriceOrder))
                    continue;
                unmatchedActiveKeys.Clear(activeKeyIndex);

                if (target.IsThisMoreAggressive(active.Target.Ticks))
                    delayed.Add() = repriceOrder;
                else
                    Send(ref repriceOrder);

                target.SetQuantity(target.WorkingQuantity - (repriceOrder.OrderProfile.Quantity - active.QuantityFilled));
                if (target.WorkingQuantity == 0 || IsOneOrderPerPrice)
                {
                    // Under IsOneOrderPerPrice a reprice may grow, so a remainder here exists only because the risk clip cut it: a miss, not policy.
                    _isTargetAchieved &= target.WorkingQuantity == 0;
                    unmatchedTargetsCopy.Clear(targetIndex);
                    unmatchedTargets.Clear(targetIndex);
                }
            }
            else
            {
                // DIFFERENT SIDE: Advance the "earlier" sort key
                if (target.Sign < active.Target.Sign)
                    unmatchedTargetsCopy.Clear(targetIndex); // incremnets targetIndex
                else
                    unmatchedActiveKeysCopy.Clear(activeKeyIndex); // increments activeKeyIndex
            }
        }


        // ---------------------------------------------------------
        // Phase 5: Cancel Unused
        // ---------------------------------------------------------
        // A cancel frees nothing until its Done; the risk check counts it until then (see Spec.md).
        while (unmatchedActiveKeys.TryPopLowest(out int activeKeyIndex))
        {
            ref ActiveTarget active = ref _activeTargets[activeKeys[activeKeyIndex].Index];
            OrderTarget cancelOrder = NewAmend(in active, active.Target.Ticks, 0);
            Send(ref cancelOrder);
        }

        // ---------------------------------------------------------
        // Phase 7: New Orders
        // ---------------------------------------------------------
        while (unmatchedTargets.TryPopLowest(out int targetIndex))
        {
            ref Target target = ref sortedTargets[targetIndex];

            // BUY SIDE LOGIC
            if (target.WorkingQuantity > 0)
            {
                if (target.IsThisCrossing(minActiveSellPrice))
                    delayed.Add() = NewOrder(in target); // Delay if aggressive
                else
                {
                    OrderTarget buyOrder = NewOrder(in target);
                    Send(ref buyOrder);
                }
            }
            // SELL SIDE LOGIC
            else if (target.WorkingQuantity < 0)
            {
                if (target.IsThisCrossing(maxActiveBuyPrice))
                    delayed.Add() = NewOrder(in target); // Delay if aggressive
                else
                {
                    OrderTarget sellOrder = NewOrder(in target);
                    Send(ref sellOrder);
                }
            }
        }

        // ---------------------------------------------------------
        // Phase 6: Flush (Send Delayed Orders Last)
        // ---------------------------------------------------------
        // Now using the explicit list pointer for maximum speed
        for (int i = 0; i < delayed.Count; i++)
        {
            Send(ref delayed[i]);
        }

        // ---------------------------------------------------------
        // Phase 8: IOCs
        // ---------------------------------------------------------
        // Last, so this tick's cancels and amends reach the exchange first. Free for all: no match-off against IOCs in flight.
        for (int i = 0; i < immediateOrCancelTargets.Count; i++)
        {
            OrderTarget immediateOrCancelOrder = NewOrder(in immediateOrCancelTargets[i]);
            Send(ref immediateOrCancelOrder);
        }

        return _isTargetAchieved;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertionSortKeysInplace(SortKey* ptr, int count)
    {
        for (int i = 1; i < count; i++)
        {
            SortKey key = ptr[i];
            int j = i - 1;
            while (j >= 0 && ptr[j].PackedScore > key.PackedScore)
            {
                ptr[j + 1] = ptr[j];
                j--;
            }
            ptr[j + 1] = key;
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void AggregateTargets(ref StackList<Target> targets)
    {
        int count = targets.Count;
        if (count == 0)
            return;

        SortKey* keysPtr = stackalloc SortKey[count];

        for (int i = 0; i < count; i++)
        {
            keysPtr[i] = new SortKey
            {
                Index = i,
                PackedScore = GetPackedTargetKey(targets[i])
            };
        }

        InsertionSortKeysInplace(keysPtr, count);

        Target* pTempSorted = stackalloc Target[count];
        for (int i = 0; i < count; i++)
            pTempSorted[i] = targets[keysPtr[i].Index];

        int writeIndex = 0;
        Target currentAggregatedTarget = pTempSorted[0];

        for (int readIndex = 1; readIndex < count; readIndex++)
        {
            Target nextTarget = pTempSorted[readIndex];
            if (currentAggregatedTarget.Ticks == nextTarget.Ticks && currentAggregatedTarget.Sign == nextTarget.Sign)
            {
                currentAggregatedTarget.SetQuantity(currentAggregatedTarget.WorkingQuantity + nextTarget.WorkingQuantity);
            }
            else
            {
                if (currentAggregatedTarget.WorkingQuantity != 0)
                    targets[writeIndex++] = currentAggregatedTarget;
                currentAggregatedTarget = nextTarget;
            }
        }
        if (currentAggregatedTarget.WorkingQuantity != 0)
            targets[writeIndex++] = currentAggregatedTarget;

        while (targets.Count > writeIndex)
            targets.SwapRemoveAt(targets.Count - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal OrderTarget NewAmend(in ActiveTarget active, int newTicks, int newWorkingQuantity)
    {
        int maxOrderQuantity = OrderRisk.MaxOrderQuantity;
        bool isCancel = newWorkingQuantity == 0;
        int orderQuantity = Math.Clamp((isCancel ? active.Target.WorkingQuantity : newWorkingQuantity) + active.QuantityFilled, -maxOrderQuantity, maxOrderQuantity);
        OrderProfile orderProfile = new OrderProfile(newTicks, orderQuantity);
        OrderProfile activeProfile = new OrderProfile(active.Target.Ticks, active.Target.WorkingQuantity + active.QuantityFilled);
        return new OrderTarget
        {
            OrderHeader = new OrderHeader
            {
                OrderId = active.ClientOrderId,
                Seq = active.Seq + 1
            },
            OrderProfile = orderProfile,
            TimeInForce = active.Target.TimeInForce,
            OrderTargetAction = isCancel ? OrderTargetAction.Cancel : orderProfile.IsReduceOf(in activeProfile) ? OrderTargetAction.Reduce : OrderTargetAction.Replace
        };
    }

    // The largest unmatched active among those sharing activeKeyIndex's side and price (keys at one price are contiguous).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetLargestActiveKeyIndexAtPrice(SortKey* activeKeys, Bitset64 unmatchedActiveKeys, int activeKeyIndex)
    {
        ulong priceScore = activeKeys[activeKeyIndex].PackedScore >> 31;
        int largestActiveKeyIndex = activeKeyIndex;
        int largestQuantity = Math.Abs(_activeTargets[activeKeys[activeKeyIndex].Index].Target.WorkingQuantity);
        for (int i = activeKeyIndex + 1; activeKeys[i].PackedScore >> 31 == priceScore; i++)
        {
            int quantity = Math.Abs(_activeTargets[activeKeys[i].Index].Target.WorkingQuantity);
            if (unmatchedActiveKeys[i] && quantity > largestQuantity)
            {
                largestActiveKeyIndex = i;
                largestQuantity = quantity;
            }
        }
        return largestActiveKeyIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal OrderTarget NewAmend(in ActiveTarget active, in Target target)
    {
        return NewAmend(in active, target.Ticks, target.WorkingQuantity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal OrderTarget NewOrder(in Target target)
    {
        return new OrderTarget
        {
            OrderHeader = new OrderHeader
            {
                // template: instrument only — Client.Create stamps ClientId/StrategyId
                OrderId = new OrderId { InstrumentId = Instrument.InstrumentId },
                Seq = 1
            },
            OrderProfile = new OrderProfile
            {
                Ticks = target.Ticks,
                Quantity = target.WorkingQuantity
            },
            TimeInForce = target.TimeInForce,
            OrderTargetAction = OrderTargetAction.Create
        };
    }

    // Best effort drops what would be refused (a closed session, no free order slot) and clips what would not fit, noting the miss; strict sends as asked.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Send(ref OrderTarget orderTarget)
    {
        if (_isBestEffort)
        {
            int quantity = orderTarget.OrderProfile.Quantity;
            bool isSendable = Instrument.Header.TradingStatus == TradingStatus.Open
                && (orderTarget.OrderTargetAction != OrderTargetAction.Create || Client.HasFreeOrderSlot)
                && (orderTarget.OrderTargetAction == OrderTargetAction.Cancel || Client.RiskLayer.TryClipToRiskLimit(ref orderTarget));
            _isTargetAchieved &= isSendable && orderTarget.OrderProfile.Quantity == quantity;
            if (!isSendable)
                return;
        }
        Count += Client.OnOrderTarget(ref orderTarget) ? 1UL : 0UL;
    }

    // Sends the same cancel as Phase 5 (working + filled) for every active, in the mode of the Target call that runs it.
    private void CancelAllOrders()
    {
        foreach (ActiveTarget activeTarget in Position.ActiveTargets)
        {
            OrderTarget cancelOrder = NewAmend(in activeTarget, activeTarget.Target.Ticks, 0);
            Send(ref cancelOrder);
        }
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    protected static void ThrowOrderCountExceeded(int count)
    {
        throw new InvalidOperationException($"Order count {count} exceeds limit {s_maxOrders}");
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    protected static void ThrowSelfCrossingTargets(int maxBuyTicks, int minSellTicks)
    {
        throw new InvalidOperationException($"Self-crossing targets: max buy ticks {maxBuyTicks} >= min sell ticks {minSellTicks}");    
    }
}
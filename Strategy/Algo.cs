using Data;
using Execution;
using Provider;
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
    /// already forfeited queue priority and the amend-up is therefore free.
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
        foreach (ActiveTarget active in Position.ActiveTargets)
        {
            _activeTargets.Add(active);
        }
    }

    protected int GetPositionQuantity()
    {
        SnapshotActives();
        int quantity = Position.Header.Quantity;
        return quantity;
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Target(ref StackList<Target> targets)
    {
        //using Latency latency = new Latency(CallId.AlgoTarget);


        if (Position.AlgoStatus == AlgoStatus.Paused)
        {
            CancelAllOrders();
            return;
        }

        // ---------------------------------------------------------
        // 0. Validate strategy logic
        // ---------------------------------------------------------

        if (targets.Count > s_maxOrders)
            ThrowOrderCountExceeded(targets.Count);
        ThrowIfSelfCrossingTargets(ref targets);


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
        if (!_hasSnapshot)
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
                        OrderTarget reduceOrder = NewAmend(in active, in *target);
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
        // Capacity: exact upper bound (max 1 delayed order per unmatched target)
        OrderTarget* delayedPtr = stackalloc OrderTarget[unmatchedTargets.Count];
        UnsafeStackList<OrderTarget> delayed = new UnsafeStackList<OrderTarget>(delayedPtr);

        // ITERATORS: Create local copies of the Bitsets.
        // We modify these copies to drive the loop iteration.
        Bitset64 unmatchedTargetsCopy = unmatchedTargets;
        Bitset64 unmatchedActiveKeysCopy = unmatchedActiveKeys;

        while (!unmatchedTargetsCopy.IsEmpty && !unmatchedActiveKeysCopy.IsEmpty)
        {
            int targetIndex = unmatchedTargetsCopy.LowestSet;
            int activeKeyIndex = unmatchedActiveKeysCopy.LowestSet;

            ref Target target = ref sortedTargets[targetIndex];
            ref ActiveTarget active = ref _activeTargets[activeKeys[activeKeyIndex].Index];

            if (target.Sign == active.Target.Sign)
            {
                // SAME SIDE: Reprice
                bool isAggressiveMove = target.IsThisMoreAggressive(active.Target.Ticks);

                if (isAggressiveMove)
                    delayed.Add() = NewAmend(in active, in target);
                else
                {
                    OrderTarget repriceOrder = NewAmend(in active, in target);
                    Send(ref repriceOrder);
                }

                // Advance Iterators (Loop Logic)
                unmatchedTargetsCopy.Clear(targetIndex);
                unmatchedActiveKeysCopy.Clear(activeKeyIndex);

                // Clear from Original Sets (Handled)
                // These will NOT be processed in Phase 5/7
                unmatchedTargets.Clear(targetIndex);
                unmatchedActiveKeys.Clear(activeKeyIndex);
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
        // TRACKING: If we cancel anything, we lock that side for New Orders this tick.
        bool isPendingSellCancel = false;
        bool isPendingBuyCancel = false;

        while (unmatchedActiveKeys.TryPopLowest(out int activeKeyIndex))
        {
            ref ActiveTarget active = ref _activeTargets[activeKeys[activeKeyIndex].Index];
            // We use |= to flag if ANY order on this side is being cancelled.
            isPendingSellCancel |= (active.Target.Sign < 0);
            isPendingBuyCancel |= (active.Target.Sign > 0);

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
            // Condition: Positive Quantity AND No pending buy cancels (Safety Lock)
            if (target.WorkingQuantity > 0 && !isPendingBuyCancel)
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
            // Condition: Negative Quantity AND No pending sell cancels (Safety Lock)
            else if (target.WorkingQuantity < 0 && !isPendingSellCancel)
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
    protected void ThrowIfSelfCrossingTargets(ref StackList<Target> targets)
    {
        int maxBuyTicks = int.MinValue;
        int minSellTicks = int.MaxValue;

        foreach (Target target in targets)
        {
            if (target.Sign > 0)
                maxBuyTicks = Math.Max(maxBuyTicks, target.Ticks);
            else
                minSellTicks = Math.Min(minSellTicks, target.Ticks);
        }

        if (maxBuyTicks >= minSellTicks)
            ThrowSelfCrossingTargets(maxBuyTicks, minSellTicks);
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
        return new OrderTarget
        {
            OrderHeader = new OrderHeader
            {
                OrderId = active.ClientOrderId,
                Seq = active.Seq + 1
            },
            OrderProfile = new OrderProfile
            {
                Ticks = newTicks,
                Quantity = orderQuantity
            },
            OrderTargetAction = isCancel ? OrderTargetAction.Cancel : OrderTargetAction.Amend
        };
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
            OrderTargetAction = OrderTargetAction.Create
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(ref OrderTarget orderTarget)
    {
        Count += Client.OnOrderTarget(ref orderTarget) ? 1UL : 0UL;
    }

    public void CancelAllOrders()
    {
        foreach (ActiveTarget activeTarget in Position.ActiveTargets)
        {
            OrderTarget delete = new OrderTarget()
            {
                OrderHeader = new()
                {
                    OrderId = activeTarget.ClientOrderId,
                    Seq = activeTarget.Seq + 1,
                },
                OrderProfile = new OrderProfile()
                {
                    Ticks = activeTarget.Target.Ticks,
                    Quantity = activeTarget.QuantityFilled,
                },
                OrderTargetAction = OrderTargetAction.Cancel,
            };
            Count += Client.OnOrderTarget(ref delete) ? 1UL : 0UL;
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
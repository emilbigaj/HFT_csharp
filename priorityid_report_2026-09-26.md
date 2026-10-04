# PriorityId continuity in the Databento MBO tick history — issue report, 2026-09-26

Audience: the Databento converter (`c:\Home\csharp\HFT\Databento`). Written from the simulator side
(`c:\Home\csharp\HFT\HFT`), where the problem surfaced. Nothing in the converter or in the data on `Z:`
has been changed. The simulator has a workaround, described in section 4.

---

## 1. Symptom

Backtests on 6E Sep26 showed queue position ("quantity ahead") collapsing to 0 on thick books. A resting
order 10+ ticks deep, with ~100 lots at its price, would show 0 ahead within minutes. That is not
realistic. Replayed against the real book (section 3), an order parked 3 ticks deep still has ~80% of
the queue ahead after 5 minutes, and being unfilled with 0 ahead happens 0-5% of the time.

## 2. How the simulator uses PriorityId (the contract it assumed)

`Simulator/OrderManager.cs`, `QueueManager`:

- A price level's queue is a list of chunks of market quantity, with user orders between them. Each
  chunk is stamped with the highest `PriorityId` it holds.
- A user order joining the queue is stamped with `OrderManager.PriorityId`, the highest id seen so far.
- A `Reduce`/`Cancel` of id `p` is applied to the **first chunk whose id is >= p**
  (`QueueManager.ReduceMarketBy(priorityId, quantity)`).

This is exact if and only if **every order that joins a queue after the user order carries a higher id
than the user's stamp**. In other words, `PriorityId` must be monotonic in arrival order across the whole
time the simulator runs, not just within a day.

## 3. What the data actually contains

Measured on `Z:\TickHistory\Databento\XCME 6E\Future XCME 6E 2026-09-14.MarketByOrder.Tick.zstd`
with scratch tools (see section 6).

**Within a day the contract holds.** On 2026-08-25, 100% of update `Add`s had id = previous `Add` id + 1,
and none landed below an id already resting at its level.

**Across days it does not.** Add id ranges per day:

| Day | Add ids during the day |
|---|---|
| 2026-08-21 | 125,849,563,614,862 … 125,849,564,393,244 |
| 2026-08-23 (Sun) | 264,293,534,724,959 … 264,293,534,740,785 |
| 2026-08-24 | 172,069,317,774,679 … 172,069,318,474,168 |
| 2026-08-25 | 118,345,186,088,606 … 118,345,186,707,003 |
| 2026-08-26 | 118,345,186,709,526 … 118,345,187,301,669 |

The base goes **down** from 08-23 to 08-24 and again to 08-25. 08-25 to 08-26 is continuous (same run).

**Every midnight carries two MBO snapshots**, both stamped 00:00:00:

| Midnight | Snapshot 1 ids | Snapshot 2 ids |
|---|---|---|
| 08-23 | 125,849,563,612,326 … 125,849,564,393,244 (799 orders) | empty (0 orders) |
| 08-24 | 264,293,534,724,959 … 264,293,534,740,785 | 172,069,317,772,100 … 172,069,317,774,678 |
| 08-25 | 172,069,317,772,174 … 172,069,318,474,168 | 118,345,186,086,159 … 118,345,186,088,605 |
| 08-26 | 118,345,186,086,165 … 118,345,186,707,002 | 118,345,186,707,002 … 118,345,186,709,525 |

Snapshot 1 is the book carried from the previous day with its original ids. Snapshot 2 is the same book
renumbered, and the day's updates reference snapshot 2's ids.

## 4. Root causes (as far as I can tell from the code)

**4a. Snapshot 1 comes from the writer, snapshot 2 from the parser.**
`Data/TickHistory.cs`, `TickHistoryWriter.WriteTomorrowHeader` calls `WriteSnapshot(date)` at every day
boundary. That writes the writer's own `MarketByOrderBook` with preserved ids, so each day frame can be
read on its own. The parser then writes its own snapshot from the Databento `R` + snapshot rows. This
looks intentional; it is recorded here only because consumers see two snapshots per midnight.

**4b. The parser renumbers the whole book at every midnight.**
`Databento/TickHistoryParser.cs`, `OnLine`: on `R` it does `OrderMap.Clear()`, so every snapshot-flagged
`A` row misses the map and gets `++PriorityId`. Every resting order that survives midnight gets a new,
higher id. Queue order within a level is preserved, but the numbers change. A consumer holding ids from
before midnight can no longer match them to the orders after it.

**4c. The counter is seeded from the wrong value when a run starts.**
`Databento/TickHistory.cs`, `EnsureTickHistory`: `tickHistoryParser.PriorityId = Footer.PriorityId`.
The footer's `PriorityId` is the delta-encoding base (`TickHistoryHeader.PriorityId`). In
`Data/TickHistory.cs`, `DeltaPriorityId` sets that base to the id of **each order as it is written**,
so the footer holds the id of the **last order written**, not the highest id issued. The last order of a
day is often a `Cancel`/`Reduce` of an older order, or a `Trade`, which is written with `PriorityId` 0.
Seeding from it can start the new run's counter below ids already used. That fits the bases going down
between separately converted days. I have not traced exactly which run produced which base, so the
exact history is unconfirmed; the mechanism is not in doubt.

## 5. The workaround in the simulator (a hack, not a fix)

Committed state: uncommitted in the HFT working tree as of 2026-09-26.

- `Simulator/OrderManager.cs`:
  - `OrderManager.OnMarketByOrderSnapshot(orders, snapshotPriorityId)`: on every MBO snapshot, **reset**
    `OrderManager.PriorityId` to the snapshot's highest id instead of keeping the running maximum.
  - `QueueManager.RestampPriorityIds(levelOrders, snapshotPriorityId)`: re-stamp every live queue
    against the snapshot. The level's snapshot orders, ascending by id, are the queue head to tail. Each
    market chunk takes the id of the snapshot order where its cumulative quantity lands; each user order
    takes the id of the chunks in front of it.
- `Simulator/ServerSimulator.cs`: the MBO snapshot branch of `InstrumentSimulator.OnMarketByOrder`
  computes the highest id across both sides and calls the above for bids and asks.

It relies on three things that hold today: ids are +1 per `Add` within a day, the snapshot lists each
level head to tail, and a new id sequence never starts without a snapshot first.

Verified with a replay of the real 6E book (virtual 1-lot orders at the touch, 3 and 10 ticks deep,
horizons 10 s to 1 h, including orders resting across midnight). Share left unfilled with 0 ahead:

| Case | Real queue | Old simulator | Simulator with workaround |
|---|---|---|---|
| 3 ticks deep, 5 min, day after Sunday | 0–1% | 28–30% | 0–1% |
| At the touch, 10 s | 4–5% | 33–37% | 4–5% |
| Resting across midnight, 10 ticks deep, 1 h | 0–10% | 21% | 0–10% |

With the workaround, every measured row matches the real queue exactly.

## 6. Evidence and tools

Scratch projects in the HFT session scratchpad
(`C:\Users\Emil\AppData\Local\Temp\claude\c--Home-csharp-HFT-HFT\a6fdebb5-6850-4ae6-80d9-2f5b4774a0a0\scratchpad\`):

- `IdRanges`: prints each snapshot's id range and each day's update `Add` id range.
- `QueueAheadTruth`: replays the book with a true arrival-order queue and the simulator's rules side by
  side. Options: `--from yyyy-MM-dd --days N --span --noreset`.
- `TradeVsRemoval`: shows a trade's fill-removals mostly arrive in the packet after the trade (83% of
  volume), 9% in the same packet.

## 7. Options for the converter (for you to decide)

1. **Seed from the true maximum.** Have the writer carry the highest `PriorityId` written as each new
   day header's base (in `WriteTomorrowHeader`), instead of the last order's id. The reader decodes each
   day from its header, so it does not change, and the footer becomes a correct seed. Alternatively keep
   the maximum per contract in `TickHistoryProgress.json`.
2. **Stop renumbering at midnight.** At `R`, reconcile the snapshot rows against the existing `OrderMap`
   by Databento `order_id`: keep the `PriorityId` of orders still resting, route changes through the
   existing `M` branch, give new orders new ids, cancel orders missing from the snapshot. Snapshot 2 would
   then match snapshot 1.
3. **Rebuild existing files from the raw downloads.** The current files are append-only and already
   contain the broken ids; (1) and (2) only help data written afterwards.

Either way the simulator's workaround can stay: with continuous, preserved ids it re-stamps queues to the
same ids and changes nothing.

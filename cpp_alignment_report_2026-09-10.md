# C++ alignment report — 2026-09-10 (amends the 2026-09-08 report)

Covers every C# change landed after the 2026-09-08 report's baseline (`2e1ddfa`) up to and including
the commit that adds this file: the maturity-token parser commits (already folded into the
2026-09-08 report's amendments), `dd33156` (`OrderRisk`), and this working tree. Read the
2026-09-08 report first; this one wins where they disagree. C# is the source of truth for every
item. `cpp_alignment_report_2026-09-10_orderrisk.md` is the detailed port note for A4 below.

**Deploy discipline:** Part A is shared-memory/wire bytes. `OrderTarget` and `RiskLimit` change
size, so every process mapping the `OrderTargets` or `RiskLimits` arrays or reading an execution
channel must move together. JSON `.risklimit` lines written before the change still parse.

## Why these changes exist

Three drivers. (1) The 2026-09-08 live run on CME_NewRelease: after a cancel-all, RTY, NQ and NKD
each got `PositionExceedsRiskLimit` ~400 µs later on the first quote back, because the strategy
re-quoted a side whose cancel the exchange had not confirmed and the server counted both; and every
first cancel of an acked order was refused as `TargetIsActive` (69 of 69). (2) Operator edits of
risk limits and algo status were applied on the admin thread against rows the CoreGroup thread was
writing — a genuine two-writer race with no lock behind it. (3) `OrderRisk` capped order quantity
at 55 for no risk reason. Everything below is in service of those three.

---

# Part A — wire / shared-memory shapes

## A1. `OrderTarget` — 52 bytes (was 44): `TriggerTimestamp` inserted after `OrderHeader`

```cpp
#pragma pack(push, 1)
struct OrderTarget
{
    Data::Header<OrderType> Header = Data::Header<OrderType>(OrderType::OrderTarget); //  0, 4
    OrderHeader      OrderHeader;                                                      //  4, 28  (Seq 4 | OrderId 8 | ExchangeTimestamp 16 | NicTimestamp 24)
    Tools::Timestamp TriggerTimestamp;                                                 // 32, 8   NEW
    OrderProfile     OrderProfile;                                                     // 40, 8   (Ticks 40 | Quantity 44)
    TimeInForce      TimeInForce;                                                      // 48, 1
    OrderTargetAction OrderTargetAction;                                               // 49, 1
    OrderStateStatus OrderTargetStatus = OrderStateStatus::Active;                     // 50, 1
    uint8_t          _reserved[1];                                                     // 51, 1
};
#pragma pack(pop)
static_assert(sizeof(OrderTarget) == 52);
static_assert(offsetof(OrderTarget, TriggerTimestamp) == 32);
static_assert(offsetof(OrderTarget, OrderProfile) == 40);
```

Semantics (client side, `Client.OnOrderTarget`):
- `TriggerTimestamp` = NIC arrival timestamp of the message this target reacted to (the client's
  current `NicTimestamp` when it decided).
- `OrderHeader.NicTimestamp` = the client's **send** time (`Clock.Now` at send) — it used to carry the
  trigger's NIC time. `OrderHeader.ExchangeTimestamp` = the client's current exchange timestamp, as before.

It crosses the wire client → server on the CoreGroup execution channel and lives in the
`OrderTargets` shared array (client-written, server-read). A C++ client must set both stamps the
same way; a C++ server reads the new offsets.

## A2. `RiskLimit` — 32 bytes (was 36): `StrategyId` removed

Risk limits are **server-wide**: one row per instrument, applied to every strategy. `StrategyId`
was never enforced or restored per strategy; it only chose where an echo went. Removed from the
row and from the request.

```cpp
#pragma pack(push, 1)
struct RiskLimit
{
    Data::Header<OrderType> Header = Data::Header<OrderType>(OrderType::RiskLimit); //  0, 4
    int32_t          InstrumentId = 0;                                                //  4
    Tools::Timestamp Timestamp = Tools::Timestamp::MinValue();                       //  8   stamped by the server on apply
    int32_t          MaxOrderQuantity = 0;                                            // 16
    int32_t          MaxPositionQuantity = 0;                                         // 20
    int32_t          WorstLongWorkingQuantity = 0;                                    // 24   aggregate, >= 0
    int32_t          WorstShortWorkingQuantity = 0;                                   // 28   aggregate, <= 0
};
#pragma pack(pop)
static_assert(sizeof(RiskLimit) == 32);
```

`.risklimit` lines on disk that still carry `"StrategyId"` parse fine (unknown property, skipped).

## A3. `ControlRiskLimit` — NEW request, 20 bytes; `ControlType::RiskLimit = 201`

```cpp
enum class ControlType : uint8_t { AlgoStatus = 200, RiskLimit = 201 };

#pragma pack(push, 1)
struct ControlRiskLimit
{
    Data::Header<ControlType> Header = Data::Header<ControlType>(ControlType::RiskLimit); //  0, 4
    int32_t ClientId = -1;                                                                //  4
    int32_t InstrumentId = -1;                                                            //  8
    int32_t MaxOrderQuantity = 0;                                                         // 12
    int32_t MaxPositionQuantity = 0;                                                      // 16
};
#pragma pack(pop)
static_assert(sizeof(ControlRiskLimit) == 20);
```

A client sends this on the instrument's **CoreGroup execution channel** (not admin) and never sends
a `RiskLimit` row. `OrderType::RiskLimit` from a client is no longer accepted on any channel.
`ControlAlgoStatus` (unchanged, 17 bytes) now travels on the same execution channel.

## A4. `OrderRisk` — still 64 bytes, new layout (commit `dd33156`)

`uint16 ActiveTargetsCount` @0, `uint16 WorstOrderQuantity` @2, `uint16 AbsOrderQuantities[30]` @4.
Quantity ceiling 65535, 30 in-flight targets per order slot, same API and multiset semantics.
Full port note, reference implementation and differential test:
`cpp_alignment_report_2026-09-10_orderrisk.md`. Do not port it with SIMD.

## A5. Alert record (AlertManager → logging server `.alert` socket) — only if C++ emits alerts

`Header<AlertType> | [OrderRejected | String64 Symbol] | ASCII message`. The `String64 Symbol` after
`OrderRejected` is new; the exception text for `AlertType::Exception` is rendered on the alert
thread, not the caller's.

## A6. Enums

`ControlType` gains `RiskLimit = 201`. `OrderType`, `OrderRejectedReason`, `AllocateType` unchanged
(numeric parity per A3/A4 of the 2026-09-08 report still applies).

---

# Part B — server behavior

## B1. Threading model (corrects the "RX thread" wording in the 2026-09-08 report and old comments)

There is **no separate RX thread**. In production one thread per CoreGroup runs

```
while (true) { ReadFromIlink(); ReadFromClients(); }
```

so exchange fills, states and rejects, and every client's targets and controls for that segment,
apply on the same thread. Every row a segment owns — order rows, risk-limit rows, server and local
position rows, the book — has exactly one writer. The admin thread does allocation only. The
listen thread's client-close path enqueues cancels through the per-CoreGroup injection queue and
clears bitsets with atomics; it is the queue's only producer besides the hub.

Consequences the C++ must mirror:
- The per-entry `AcquireLock`/`ReleaseLock` are sequence bumps, not mutual exclusion. They are
  correct **only** because each row has one writer. Never add a second writer and "fix" it with a
  bump.
- The return-channel spinlock (`_recvFromExchangeLocks`) is uncontended insurance; keep it.
- Do not route controls through the injection queue (the 2026-09-08 C3 said "injection-queue
  pattern"; the C# does not do that — see B2).

## B2. `ReadExecution` handles the controls; `ReadAdmin` handles allocation only

`ReadExecution(cg)` switch, per client channel record:
- `OrderType::OrderTarget` → `OnOrderTarget` (as before).
- `OrderType::OrderRejected` (client-side reject) → pause the algo (as before).
- `ControlType::RiskLimit` → `OnControlRiskLimit(request)`:
  1. `row = GetRiskLimit(request.InstrumentId)`; `AcquireLock` (seq odd).
  2. `row.MaxOrderQuantity = request.MaxOrderQuantity; row.MaxPositionQuantity =
     request.MaxPositionQuantity; row.Timestamp = now;` — **the two working quantities are never
     touched**, so an edit cannot rewind a reservation and the old copy-the-live-aggregates
     workaround is gone.
  3. `ReleaseLock` (seq even).
  4. `WriteToAudit(cg, row)` on the **server's own audit socket**. No echo to any client (nothing
     consumed it). Do **not** audit the request: the logging server taps every client socket in
     both directions, so the request is already logged under the client.
- `ControlType::AlgoStatus` → `OnControlAlgoStatus(strategyId, instrumentId, status)` (body
  unchanged: stamps both header timestamps, sets the status, writes the local position row, echoes
  it to the strategy). No audit write for the same reason.

`ReadAdmin` handles `AllocateType::Instrument` only. Delete `OnRiskLimit` and `SaveRiskLimit`: **the
server never writes `.risklimit` files** — the logging server's audit writer appends the posted row
to `<Server>/RiskLimits/<symbol>.risklimit` through the same path helper the server reads at
allocation. A C++ server that also appends gives the file two writers.

## B3. Allocation is idempotent at both levels

`Context::AllocateInstrument(clientId, instrumentId)` returns early when the client's bit is already
set (the instrument-level half already did). It used to re-read the client's position file,
rewrite the live local position row (forcing Paused in realtime) and read-modify-write two bitsets
on every call from the admin thread — and the strategy-0 union rule re-ran it on every other
client's allocation of the same instrument, against a row the CoreGroup thread may be filling.
Rows are now initialised exactly once; the realtime Paused default applies at first allocation.

GUI consequence (C# widgets already do this): the server polls a client's execution channel only
for CoreGroups that client has allocated in, so a GUI allocates the instrument to its manual client
before sending a control for it on that channel.

## B4. `NicTimestamp` stamping — one clock for the audit

The logging server orders execution records by `OrderHeader.NicTimestamp` (`RiskLimit` by its own
`Timestamp`; control requests carry none and sort at the high-water mark, arrival order kept). Every
writer of these records must stamp identically:

| record | who stamps `NicTimestamp` | value |
|---|---|---|
| `OrderTarget` | client, at send | `Clock.Now` (send time); `TriggerTimestamp` = NIC of the trigger |
| `OrderState` (ack/done/reject-state) | server, `OnOrderState` on entry | `now`; `WriteOrderState` copies it into the row |
| `OrderState` + its `Fill`s (one fill event) | server, `OnFill` | one `now` shared by the state and every fill |
| `OrderRejected` from the server risk layer | server, `OnOrderTarget` reject path | `now` (its own stamp, so it sorts after the target it copies) |
| `PositionHeader` echoes | server | as before |

The audit widget uses the same key, stable-sorted, so a target and its trigger, or a fill and its
position, keep file order.

## B5. `RiskLayer` (server side): `TargetIsActive` is Amend-only

A Cancel is built as `working + filled` at the acked price, i.e. it always equals the acked profile,
so with the no-op check applied to cancels every first cancel of an acked order was refused and
only the client's Seq+2 retry got through — one round trip later. The check is now
`isAmend && state.Seq + 1 == target.Seq && state.Profile == target.Profile`. The client side was
already Amend-only.

---

# Part C — strategy / client behavior

## C1. A side with an unconfirmed cancel takes no new orders (Spec.md "Cancel-pending orders")

`Position.ActiveTargets` hides an order whose cancel is sent (a Cancel target, or an amend down to at
most the filled quantity), but the exchange still holds it and the server keeps its worst-case
quantity reserved until `Done`. The enumerator now reports **why** through
`IsPendingBuyCancel` / `IsPendingSellCancel` (side from the acked profile, never zero for an acked
order). `Algo.SnapshotActives()` captures the flags in the same pass as the actives, under the era
rule, and Phase 5's per-tick "no new orders on this side" lock is seeded from them instead of
starting false. The lock releases on the same `Done` that releases the server's reservation, so
client and server agree on capacity by construction. A rejected cancel flips `targetRejected`, the
order reappears as active, and Phase 5 re-cancels it.

Known limit (deliberately open): an amend-up of a live same-side order while another order's cancel
is pending is still double-counted at the server; `Make` cannot produce it.

## C2. Controls go on the execution channel

`ManualClient.OnControlAlgoStatus` / `OnControlRiskLimit` write on
`Context.GetInstrument(InstrumentId).Header.CoreGroupId`, not `SocketChannel.Admin`; the remote
workspace proxy forwards `ControlType::RiskLimit` to the manual client like `AlgoStatus`. See B3
for the allocate-first rule.

## C3. C#-only, no port needed

`Scenario.GetFuture` registers a product search only in simulation; Testing scenario/strategy
changes are harness config; the audit-trail widget and logging-server sort key are C# tooling.

---

# Part D — verification checklist

1. `static_assert`: `sizeof(OrderTarget)==52`, `offsetof(OrderTarget,TriggerTimestamp)==32`,
   `offsetof(OrderTarget,OrderProfile)==40`; `sizeof(RiskLimit)==32`; `sizeof(ControlRiskLimit)==20`;
   `sizeof(ControlAlgoStatus)==17`; `sizeof(OrderRisk)==64` with offsets 0/2/4 (orderrisk note §4);
   `ControlType::RiskLimit == 201`. Everything else in the 2026-09-08 Part D still holds except
   `sizeof(RiskLimit)==36`, which is superseded.
2. Risk-limit round trip: send `ControlRiskLimit` on the CoreGroup channel → the row's two maxima
   and `Timestamp` change in place, the two working quantities do not, the row appears once in the
   server audit, `<Server>/RiskLimits/<symbol>.risklimit` gains one line, a server restart
   restores it at allocation.
3. Audit ordering: for every target, `OrderHeader.NicTimestamp >= TriggerTimestamp`; a server
   reject sorts after the target it copies; a fill event's state and fills share one stamp.
4. Cancel-pending: replay the 2026-09-08 sequence (cancel-all, then the first quote back after a
   book clear) — no `PositionExceedsRiskLimit`, no replacement order on a side whose cancel is
   unconfirmed; and a first Cancel of an acked order is never refused as `TargetIsActive`.
5. Allocation: allocating the same (client, instrument) twice leaves the local position row and
   its `AlgoStatus` untouched the second time.
6. Threading: confirm no code path other than the CoreGroup thread writes a `RiskLimit`, local
   position or order row after allocation; the admin thread's writes stop at first allocation.

---

# Amendment (later on 2026-09-10) — `TradingStatus`: the lib primitive the CME side asked for

Answers the CME-side note ("CME gives it three ways, we consume two and throw the status away, the
lib has the field but nothing writes it"). The C# lib now has the runtime path; wire secdef tag
1682, the snapshot's `MDSecurityTradingStatus` and `SecurityStatus` (template 30, incl. the
group-level fan-out over every subscribed instrument in the `SecurityGroup`) to this one primitive.

## T1. Wire shapes

```cpp
enum class TickType : uint8_t { /* Trade=0 … MarketByOrderDelta=12 unchanged, */ TradingStatus = 20 };
// 20, not 13: the audit's first-byte switch spans OrderType 10..16, so a tick that is ever audited
// must not collide. Nothing audits it yet.

enum class TradingStatus : uint8_t   // the fold; now declared in Data/Tick.cs (moved from Instrument.cs, values unchanged)
{
    Unknown = 0,   // uninitialized, CME UnknownorInvalid(20) / NoValue(255)
    Open,          // ReadyToTrade(17)
    Closed,        // Close(4), NotAvailableForTrading(18), PostClose(26)
    Auction,       // PreOpen(21), NewPriceIndication(15), PreCross(24), Cross(25)
    Halted,        // TradingHalt(2)
};

#pragma pack(push, 1)
struct TradingStatusUpdate                 // 64 bytes (padded like Trade)
{
    TickHeader    TickHeader;              //  0, 32: TickType | 3 reserved | InstrumentId | Exchange, Sending, Nic timestamps
    TradingStatus TradingStatus;           // 32, 1
    uint8_t       _pad[31];                // 33..63
};
#pragma pack(pop)
static_assert(sizeof(TradingStatusUpdate) == 64);
static_assert(offsetof(TradingStatusUpdate, TradingStatus) == 32);
```

`InstrumentHeader.TradingStatus` stays where it was: the byte at offset 7 of the 64-byte header
(after `Header` 4, `InstrumentType` 5, `CoreGroupId` 6). No header size change. `HaltReason` /
`SecurityTradingEvent` are NOT carried yet — when they are, the reserved byte at offset 8 is where
`HaltReason` goes, again with no size change.

## T2. Server primitive — `Server::OnTradingStatusUpdate(const TradingStatusUpdate&)`

```
headerId = GetInstrumentHeaderIdByInstrumentId(tick.TickHeader.InstrumentId)
GetInstrumentHeader(headerId).AsInstrumentHeader().TradingStatus = tick.TradingStatus   // plain byte store, no seq bump
WriteToInstrumentData(tick)                                                             // the event: the instrument's data ring
```

- The byte store is the only write to that field after load, so it needs no lock and is safe from
  the thread that delivers it. The ring write is NOT: call this on the thread that already writes
  the instrument's ring (the one running `OnMarketByPrice` for it), never from a second thread.
- Emit on transitions only; the client filters duplicates too (T3), but the ring is not free.
- Fill all three `TickHeader` timestamps from the `SecurityStatus` message (exchange, sending, NIC).
  The C# constructor `TradingStatusUpdate(instrumentId, timestamp, status)` stamps one value into
  all three — that is the simulator's convenience, not the contract.
- Start-of-day photo (secdef 1682, snapshot status at seeding): same primitive, or a direct header
  store before any client is attached. Before the first status the byte is `Unknown`.

## T3. Client side (C# reference: `Client.OnInstrumentData` → `Instrument.OnTradingStatusUpdate`)

`case TickType::TradingStatus` on the instrument data ring → `Instrument::OnTradingStatusUpdate`,
which keeps a private `_tradingStatus` mirror and raises `TradingStatusUpdateEvent(in tick)` only
when the value changed. Compare against the private mirror, NOT against the header row: the server
writes the row before the tick reaches the ring, so the row already equals the tick by the time a
client reads it and a row-based guard never fires. `Instrument.Header.TradingStatus` (the row) is
the any-time read for strategies and the GUI.

## T4. Not yet — do not assume these on the C++ side

- `Instrument.IsInSession` is still the clock-based `SessionManager`; it has NOT been switched to
  `Header.TradingStatus == Open`. The local create/amend gate (`NotInSession`) therefore does not
  yet follow the feed. When it flips, `Auction` will count as not-in-session and cancels will
  never be gated locally.
- No audit record, no `HaltReason`/event, no simulator emission from `SessionManager` yet.
- `Tick.SizeOf()` has no `TradingStatus` case (only book updates call it today).

## T5. Verification

`sizeof(TradingStatusUpdate) == 64`, `offsetof(TradingStatus) == 32`, `TickType::TradingStatus == 20`,
`TradingStatus` enum parity 0..4, `offsetof(InstrumentHeader, TradingStatus) == 7`. Round trip: a
`SecurityStatus` for ES at the 15:15 CT pause updates the header byte of every ES instrument to
`Halted`, one tick per instrument appears on each ring, each strategy sees exactly one
`TradingStatusUpdateEvent`, and the GUI's instrument headers show `Halted`.

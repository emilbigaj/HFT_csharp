# C++ alignment list

Handoff for the C++ implementation (github.com/emilbigaj/HFT_cpp). Compared `origin/main`
(`aeac53c`, 2026-07-23) against C# `main` (`bc94437` + working tree, 2026-08-11). The C# side is
the source of truth for every item below; file references name the C# implementation to copy.

**Ordering matters:** §0 first (it may already exist on a local branch), then §1 wire structs
(everything else depends on them), then the rest in any order.

---

## 0. Land the persist-client-sockets work

`origin/main` predates the persistence protocol entirely — no `ServerHeader::Persistance`, no
`Detached`, no `Recover`/`SkipRing`. This was implemented on a local C++ branch
(`persist-client-sockets`) and mirrored into C# from there, so it likely just needs merging and
pushing. Verify against the C# mirror after merge:

- `ServerHeader.Persistance` — one byte, offset 172, `sizeof(ServerHeader) == 173` (C# `Socket/Socket.cs`)
- `ClientStatus` ladder `Disposed=0, Detached=1, Open=2, Closing=3, Closed=4` (process-local, but
  keep both sides identical); write gate accepts `Open || Detached || (Persistance && Closing)`,
  reads stay `Open`-only
- `Tools::AtomicEnum` ({state, epoch} in one word; C++ tree may still name it AtomicTransition —
  rename to match): readers request `Open → Closing` via snapshot-CAS, the listen thread performs
  every transition; `OpenClient` recovers (persist) or resets (non-persist) the reused server-side
  Socket — see Spec.md "Socket close protocol".
  Landed in C++ first (2026-08); mirrored into C# `Tools/AtomicEnum.cs` + `Socket/Socket.cs`
- `Protocol::SkipRing` + `Recover()` on both socket halves; **nothing calls `Reset()`** on shared memory
- Read-status probes check `Magic` and resolve cursors the same way the read path does
- Shared-memory region names built with `std::filesystem::path::operator/` (C# mirrors this via
  `FileSystemPath operator/`); `.server`/`.audit`/`.alert` suffixes stay concatenated

## 1. Wire structs — byte-for-byte (blocking)

### 1.1 `RiskLimit` (Execution/Order.hpp ~144)

> **Status 2026-08-11: already done in the local C++ tree** (36-byte struct with static_assert,
> matching glaze schema — confirmed by inspection). GitHub `origin/main` still shows the old
> 40-byte version; push the local work. Verify against the layout below rather than reimplementing.

C++ still has the 40-byte struct with `RateLimit` members on origin/main. C# (`Execution/Order.cs`) is now:

```
Header<OrderType> Header          (4 B, Type = OrderType::RiskLimit)
int32   InstrumentId
Timestamp Timestamp               (8 B, default Timestamp::MinValue)
int32   StrategyId                (default -1; -1 = server-wide)
int32   MaxOrderQuantity
int32   MaxPositionQuantity
int32   WorstLongWorkingQuantity  (signed aggregate, >= 0)
int32   WorstShortWorkingQuantity (signed aggregate, <= 0)
```

- **Remove** `MaxOrdersPerSession` / `MaxOrdersPerSecond` (rate limits deferred on both sides).
- Add helpers: `GetLongQuantityAllowance(position)  = max(0,  MaxPositionQuantity - position - WorstLongWorkingQuantity)`
  and `GetShortQuantityAllowance(position) = min(0, -MaxPositionQuantity - position - WorstShortWorkingQuantity)`.
- Update `GetMaxLimits`/`GetMinLimits` accordingly.
- **Update the glaze schema** — `.risklimit` files are shared between the two processes; C# writes
  `{"Header":{"Type":"RiskLimit"},"InstrumentId":..,"Timestamp":..,"StrategyId":..,"MaxOrderQuantity":..,"MaxPositionQuantity":..,"WorstLongWorkingQuantity":..,"WorstShortWorkingQuantity":..}`.

### 1.2 `OrderState` (Execution/Order.hpp ~363)

Field order and content diverge. C# layout:

```
Header<OrderType> Header
OrderHeader       OrderHeader        (Seq, OrderId, ExchangeTimestamp, NicTimestamp — already aligned)
uint64            ExchangeOrderId    (stays on the state, both sides agree)
OrderProfile      OrderProfile
TimeInForce       TimeInForce        (uint8)
OrderStateStatus  OrderStateStatus   (uint8: Done=0, Active=1 — already aligned)
OrderStateReason  OrderStateReason   (uint8, NEW — see below)
uint8             _reserved[1]
int32             QuantityFilled     (signed: negative for sells)
int32             QuantityAhead
```

C++ currently has `OrderStateStatus + Reserved[3]` *before* `OrderProfile`, no `TimeInForce`, no
reason. Reorder to match.

### 1.3 `OrderStateReason` replaces `OrderStateDoneReason`

```cpp
enum class OrderStateReason : uint8_t
{
    Unknown = 0,
    PendingNew = 1,
    Acked = 2,
    Fill = 3,        // partial vs complete lives in OrderStateStatus: Fill+Active / Fill+Done
    Canceled = 4,    // here onwards -> Done unconditionally
    Rejected = 5,    // create rejected, not amend/cancel rejected
    Eliminated = 6,
};
```

Semantics (FIX): `OrderStateReason` = ExecType — *why this state was published*;
`OrderStateStatus` = OrdStatus — *what the order is*. A cancel preserves `OrderProfile.Quantity`
(OrderQty survives; CumQty reports fills; LeavesQty goes to zero via status, never by rewriting
the order). Delete every use of `OrderStateDoneReason`.

**Renumbered 2026-09 (C# done): PartialFill/Filled merged into `Fill`** — FIX itself deprecated the
partial/complete ExecTypes; OrdStatus carries that. Canceled/Rejected/Eliminated shifted down one.
WIRE CHANGE on a shared-memory byte: both sides must deploy in lockstep, and JSON logs written
before the rename ("PartialFill"/"Filled" strings) no longer deserialize.

### 1.4 `AllocateInstrument` (Provider/Allocate.hpp ~34)

Insert `int32_t ExchangeInstrumentId = -1;` between `InstrumentId` and `Symbol`. C# order:
`Header, ClientId, InstrumentHeaderId, InstrumentId, ExchangeInstrumentId, Symbol(String64)`.

### 1.5 `OrderRejectedReason`

Diff value-for-value against C# `Execution/Order.cs`. C# has added entries the snapshot predates
(e.g. `QuantityNotValid`, `TooManyActiveTargets`, `ExceptionThrownByRiskLayer`,
`PositionExceedsRiskLimit`). Numeric values must match — they cross the wire in `OrderRejected`
bitsets.

### 1.6 `OrderId` packing

Already aligned (6/6/6/14/32) — no change. Listed so nobody "fixes" it.

## 2. Renames — layout-neutral, JSON-visible

- `enum class ExpiryType : char` → **`MaturityType`** (Data/Symbology.hpp). Same underlying values
  `D W M Q Y`.
- `FutureHeader`: `ExpiryDate` → `MaturityDate`, `ExpiryType` → `MaturityType`
  (Data/Instrument.hpp ~121).
- `SpreadHeader`: `Long/ShortExpiryDate` → `Long/ShortMaturityDate`, `Long/ShortExpiryType` →
  `Long/ShortMaturityType` (~150).
- **Update glaze keys to the new names** — header JSON must round-trip against C#.
- If any C++ code reads the `Z:\InstrumentDetails` catalog: on-disk keys are now
  `FirstTradeTimestamp` (was `ListingDate`), `MaturityType` (was `ExpiryType`), `MaturityDate`
  (was `ExpiryDate`). `Units`/`DeliveryMethod` unchanged. `Legs` (Weight/Symbol/
  ExchangeInstrumentId) describes spreads; `SpreadHeader` carries only the first positive-weight /
  first negative-weight pair.

## 3. RiskLayer — port the worst-case working-quantity accounting

C++ `RiskLayer.hpp` has header/seq validation and the `MaxOrderQuantity` check but no reservation
accounting. Port from C# `Provider/RiskLayer.cs` + `OrderRisk` in `Execution/Order.cs`:

- **`OrderRisk`** — exactly 64 bytes: `uint16 ActiveTargetsCount`, `uint16 WorstOrderQuantity`,
  then 30 × uint16 abs quantities (the first `ActiveTargetsCount` are live, the rest are 0). A
  compact array of in-flight (unacked) order quantities with a cached max — a multiset: the same
  quantity twice is two entries. `TryAdd` rejects `abs(q) > 65535` or `q == 0` (`QuantityNotValid`)
  and a 31st in-flight target (`TooManyActiveTargets`); otherwise appends and raises the cached max.
  `Ack`/`Reject` linear-scan for ONE matching entry (absent → no-op), swap-remove it (last entry
  into the hole, last slot zeroed), and rescan for the max only when the removed quantity equalled
  it. `GetAbsWorstOrderQuantity(acked) = max(abs(acked), WorstOrderQuantity)`. One `OrderRisk` per
  order slot in the server context. *(Amended 2026-09-09: replaces the `Bitset64` + 56 × uint8
  bucket layout of the 2026-09-08 report — same size, same API and semantics, quantity ceiling
  55 → 65535. Rationale in Spec.md; port note with offsets, reference implementation and the
  differential test in `cpp_alignment_report_2026-09-10_orderrisk.md`.)*
- **Sign convention** — everything signed: buys/longs positive, sells/shorts negative.
  `WorstLongWorkingQuantity >= 0`, `WorstShortWorkingQuantity <= 0`. `GetAbsWorstOrderQuantity`
  returns a **magnitude**; the sign is applied **exactly once** per update (multiplying both
  endpoints *and* the delta by sign cancels for sells — that bug drove the short aggregate
  positive in C#).
- **Reserve** (ValidateOrder): before/after magnitude around `TryAdd`, `delta * sign`, tentatively
  add to the side's aggregate; check `position + worstLong > Max` / `position + worstShort < -Max`;
  on breach `Reject()` the just-added quantity and refuse, else commit both aggregates.
- **Hooks** (wired in `Server::OnOrderState` / `OnFill` / `OnOrderRejected`):
  - *Acked*: magnitude delta around `Ack()`, applied `* (Buy ? 1 : 0)` / `* (Sell ? -1 : 0)`.
  - *Done* (any terminal status): `released = worstMagnitude - abs(QuantityFilled)`; subtract
    `released * (Buy ? 1 : 0)` / `released * (Sell ? -1 : 0)`; zero the `OrderRisk`.
  - *Fill*: subtract the **signed** per-fill quantity from the fill's side (multiplier 1 — the
    quantity already carries direction).
  - *Rejected* (exchange rejects only — server's own rejects never touched the aggregates):
    magnitude delta around `Reject()`, `* (Buy ? 1 : 0)` / `* (Sell ? -1 : 0)`.
- Known-open items **not** to port (deliberately unfixed in C# too): acked amend-down releases at
  Done rather than at ack; rate limits enforced nowhere.

## 4. Strategy 0 — the house book (see Spec.md in the C# repo)

- Reserve client id 0 **before any client can connect**: set `ClientIds[0]` when the server header
  is first stored, so `LowestClear` can never hand it out (C# `Context.cs`, server-header connect).
- **Union rule** in `Server::OnAllocateInstrument(clientId, …)`: after allocating to the client,
  also `AllocateInstrument(ServerStrategyId, instrumentId)` when `clientId != 0`. No core-group
  poll bit for id 0, no admin echo for it.
- `Context::ServerStrategyName` = the client-directory path built from the **server's leaf name**
  (`ClientContext::GetDirectoryPath(ServerName)` equivalent). `Context::AllocateInstrument` with
  `clientId == 0` resolves its position-file path from that instead of `GetSocketHeader(0)` (which
  is a zeroed header → junk path).
- `AllocateClientId`: throw if `socketHeader.ClientName == ServerStrategyName` — the server's leaf
  name is reserved for the house directory.
- `ValidateInstrument` stays strategy-keyed — the union rule is what makes StrategyId-0 orders
  pass it; do not special-case the validator.

## 5. Smaller behavior fixes (each bit C# in production/backtest)

- **Type-guard before `AsFuture()`** everywhere instrument headers are enumerated
  (Strategy/Scenario.hpp:40 is blind today). A realtime context contains spreads and empty slots;
  the blind cast is a startup crash. Pattern: `if (header128.InstrumentType != Future) continue;`.
- **Risk limits are SERVER-WIDE; `StrategyId` is REMOVED from `RiskLimit` (2026-09-10).** The
  struct is now 32 bytes: `Header` 0, `InstrumentId` 4, `Timestamp` 8, `MaxOrderQuantity` 16,
  `MaxPositionQuantity` 20, `WorstLongWorkingQuantity` 24, `WorstShortWorkingQuantity` 28 —
  `static_assert(sizeof(RiskLimit) == 32)` (A5 of the 2026-09-08 report said 36 with `StrategyId`
  at 16; superseded). One row per instrument, applied to every strategy; a per-strategy limit is a
  future feature. Old `.risklimit` lines that still carry `"StrategyId"` parse fine (unknown
  property, skipped).
- **Risk-limit edits are requests on the execution channel (amended 2026-09-10; replaces the
  earlier "copy the live `Worst*` across" workaround).** A client sends `ControlRiskLimit`
  (`ControlType::RiskLimit = 201`; `int32 ClientId, InstrumentId, MaxOrderQuantity,
  MaxPositionQuantity` after the 4-byte header — 20 bytes, pack 1) on the instrument's CoreGroup
  channel, never a `RiskLimit` row, and `ReadExecution` applies it on the CoreGroup thread:
  `MaxOrderQuantity`, `MaxPositionQuantity`, `Timestamp = now` written in place under the row's seq
  bump, the working quantities untouched; then post the row to the server's own audit socket
  (CoreGroup channel). No echo to any client (nothing consumed it) and do NOT audit the request:
  the logging server taps every client socket in both directions, so the request is already logged
  under the client — only the server's audit socket feeds the server-level file.
  `OrderType::RiskLimit` is no longer accepted from a client on any channel. The server does NOT
  write `.risklimit` files any more — the logging server appends the posted row (same
  `GetRiskLimitsFilePath`); a C++ server that appends too gives the file two writers.
- **`ControlAlgoStatus` moves the same way (2026-09-10):** the client sends it on the instrument's
  CoreGroup channel and `ReadExecution` calls `OnControlAlgoStatus` (no audit write — the client
  tap logs it); `ReadAdmin` no longer accepts it. With fills, states and targets already on that
  thread, the local position row has exactly one writer — no queue, no CAS.
- **New shared array `RateLimits` (2026-09-22):** region `<server>/RateLimits`, `CoreGroupIds.Length`
  (64) rows of `RollingRateLimit`, 64 bytes each: `RateLimit` 16 @0 (`Duration` int64 nanos @0,
  `Limit` int32 @8, `RateLimitId` int32 @12), `BucketTimestamp` int64 nanos @16, `BucketIndex` int32
  @24, `Total` int32 @28, `uint8 Counts[32]` @32. Index == CoreGroupId, server-written, created in
  `Context` directly after `MessageEfficiency` (keep that array-id order for the mirror). The server
  writes the CME default (3 s, 500, id = CoreGroupId) into every set CoreGroup at construction;
  `RiskLayer` throttles order entry per CoreGroup with it, `TrySendOrder(Clock.Now)` on a plain ref
  with no seq bump, rejecting `TooManyOrdersPerSecond`. Bucket semantics and the conservative
  Duration/31 rule are in Spec.md "Order rate limit". A C++ server must create the same region and
  own its writes, or a C# GUI attached to it shows an empty Rate Limits widget. `CoreGroupId` enum
  (OS 0, Reserved 1, SandP500 2, Equity 3, Forex 4, Crypto 5) moved from Strategy to Data.
- **Unknown message types**: `default: break` in ReadAdmin/ReadExecution swallowed a real bug in
  C# (a zeroed `Header::Type` made risk-limit edits silently no-op). At minimum count and expose
  them.

## 6. Verification (add as static_asserts / tests)

- `static_assert` on `sizeof` and every field offset for `RiskLimit`, `OrderState`,
  `AllocateInstrument`, `ServerHeader` (Persistance at 172, sizeof 173), `Header<T>` (4 bytes),
  `OrderRisk` (64 bytes).
- Cross-process file round-trip: a `.risklimit` / `.position` line written by C# parses in C++ and
  vice versa (glaze ↔ System.Text.Json).
- Numeric parity: `OrderStateReason`, `OrderStateStatus`, `OrderRejectedReason`,
  `OrderType`/`AllocateType`/`ControlType` header bytes.
- The C# `Simulator` is C#-only; nothing in it needs porting.

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
- `Tools::AtomicTransition` ({state, epoch} in one word): readers request `Open → Closing` via
  snapshot-CAS, the listen thread performs every transition; `OpenClient` recovers (persist) or
  resets (non-persist) the reused server-side Socket — see Spec.md "Socket close protocol".
  Landed in C++ first (2026-08); mirrored into C# `Tools/AtomicTransition.cs` + `Socket/Socket.cs`
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
    PartialFill = 3,
    Filled = 4,      // here onwards -> Done
    Canceled = 5,
    Rejected = 6,    // create rejected, not amend/cancel rejected
    Eliminated = 7,
};
```

Semantics (FIX): `OrderStateReason` = ExecType — *why this state was published*;
`OrderStateStatus` = OrdStatus — *what the order is*. A cancel preserves `OrderProfile.Quantity`
(OrderQty survives; CumQty reports fills; LeavesQty goes to zero via status, never by rewriting
the order). Delete every use of `OrderStateDoneReason`.

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

- **`OrderRisk`** — exactly 64 bytes: `Bitset64` + 56 × uint8 counts. A magnitude-bucketed multiset
  of in-flight (unacked) order quantities. `TryAdd` rejects `abs(q) > 55` (`QuantityNotValid`) or a
  saturated bucket (`TooManyActiveTargets`). `GetAbsWorstOrderQuantity(acked) =
  max(abs(acked), highest set bucket)`. One `OrderRisk` per order slot in the server context.
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
- **`Server::OnRiskLimit`**: an operator edit sends the whole struct — copy the *live*
  `WorstLong/ShortWorkingQuantity` from the existing row over the incoming one before storing, or
  an edit zeroes the reservations.
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

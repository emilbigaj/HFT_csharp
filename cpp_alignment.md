# C++ alignment list

Handoff for the C++ implementation (github.com/emilbigaj/HFT_cpp). Compared `origin/main`
(`aeac53c`, 2026-07-23) against C# `main` (`bc94437` + working tree, 2026-08-11). The C# side is
the source of truth for every item below; file references name the C# implementation to copy.

**Ordering matters:** §0 first (it may already exist on a local branch), then §1 wire structs
(everything else depends on them), then the rest in any order.

**Latest batch: 2026-10-04, the risk-layer / order-flow rework.** The full port note is
`cpp_alignment_report_2026-10-04.md`: rationale, code excerpts and the C++ checklist. This list
carries the binding shapes and rules. The 2026-10-04 items are §1.1 and §1.7–§1.10 (wire), §3
(rewritten), the bullets dated 2026-10-04 in §5, and §6. Four WIRE CHANGES deploy in lockstep with
the C# build:
- `RiskLimit` is 24 bytes.
- New `WorkingRisk` row and array. This shifts `ServerPositionHeaders` from array id 15 to 16.
- `OrderRisk` has the acked field and 29 slots.
- `OrderTargetAction` adds `Reduce = 3`.

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

### 1.1 `RiskLimit` (Execution/Order.hpp ~144) — config only, 24 bytes (WIRE CHANGE 2026-10-04)

History: the 40-byte version (with `RateLimit` members) went to 36 bytes in 2026-08, with a
`StrategyId`. It went to 32 bytes on 2026-09-10, when `StrategyId` was removed (see §5). On
2026-10-04 it went to 24 bytes: the live `Worst*` aggregates moved to `WorkingRisk` (§1.7). The
current C# layout (`Execution/Order.cs`, `[StructLayout(Sequential, Pack = 1)]`):

```
@0   Header<OrderType> Header     (4 B, Type = OrderType::RiskLimit = 16)
@4   int32     InstrumentId
@8   Timestamp Timestamp          (8 B, default Timestamp::MinValue; stamped on every edit)
@16  int32     MaxOrderQuantity
@20  int32     MaxPositionQuantity
     sizeof == 24
```

- `RiskLimit` holds operator config only, written on edits (`ControlRiskLimit`, §5). It is still
  one server-wide row per instrument (`<server>/RiskLimits`, server-written). It is posted to the
  logging server as a 24-byte record.
- The helpers take the working row and are `const`:
  - `GetLongQuantityAllowance(const WorkingRisk& w) = max(0, MaxPositionQuantity - w.Position - w.WorstLongWorkingQuantity)`
  - `GetShortQuantityAllowance(const WorkingRisk& w) = min(0, -MaxPositionQuantity - w.Position - w.WorstShortWorkingQuantity)`
- `GetMaxLimits` sets both maxima to int32 max. `GetMinLimits` sets both to 0. Both stamp
  `Timestamp = now`. These are unchanged.
- **Each `.risklimit` line** is
  `{"Symbol":"<symbol>","Header":{"Type":"RiskLimit"},"InstrumentId":..,"Timestamp":..,"MaxOrderQuantity":..,"MaxPositionQuantity":..}`.
  The logging server inserts `Symbol` as the first key of every line (`LoggingServer.cs:1058-1060`).
  A reader must skip `Symbol`, and older lines that still carry `StrategyId`,
  `WorstLongWorkingQuantity` or `WorstShortWorkingQuantity` must still parse: skip unknown keys, as
  System.Text.Json does.
- Any audit or logging reader keyed on `sizeof(RiskLimit)` must use 24.

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
int32             QuantityAhead      (@56, 8-aligned)
int32             QuantityBehind     (@60, added 2026-09-26, see §5; sizeof == 64)
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

**`OrderDiscarded` (2026-10-04, binding).** A reject is discarded when its reason set is non-empty
and a subset of exactly these 8 reasons:

| reason | value | reason | value |
|---|---|---|---|
| `StateIsDone` | 40 | `TooManyActiveTargets` | 45 |
| `CreateIsActive` | 41 | `AlgoIsPaused` | 46 |
| `CancelIsActive` | 42 | `TooManyOrdersPerSecond` | 56 |
| `TargetIsActive` | 43 | | |
| `TargetIsStale` | 44 | | |

- The C++ enum has no `TooManyActiveTargets` today, and the C++ `OrderDiscarded` set holds only the
  other 7. Add `TooManyActiveTargets = 45` to the enum and to `OrderDiscarded`.
- Without it, a literal port pauses the algo on the 30th in-flight amend. §3 and §5 rely on it being
  discarded.
- §6 asserts the set.

### 1.6 `OrderId` packing

Already aligned (6/6/6/14/32) — no change. Listed so nobody "fixes" it.

### 1.7 `WorkingRisk` — new row, 16 bytes (WIRE CHANGE 2026-10-04)

This row holds what `RiskLayer` has applied for one instrument: its fills and its worst-case
working reservations. It is never persisted.

```
@0   Header<OrderType> Header             (4 B, Type = OrderType::WorkingRisk = 17)
@4   int32 Position                       (moved by RiskLayer::OnFill, same call as the release)
@8   int32 WorstLongWorkingQuantity       (>= 0)
@12  int32 WorstShortWorkingQuantity      (<= 0)
     sizeof == 16, pack 1
```

- **Storage.** A new shared array `WorkingRisks`. The region is `<contextDir>/WorkingRisks`
  (path-joined), with `InstrumentIds.Length` rows. It is dense and indexed by `instrumentId`.
  - The slot stride is 128 B: the 64 B seq header plus the row, padded to a cache line.
  - There is **one array per context directory**: the server's lives under `<server>`, and each
    client's under its own client directory.
  - The server opens its own with server access. A client opens its own with its own (writable)
    client access.
  - It is created in the base `Context` constructor **immediately after `LocalPositionHeaders`**,
    as the last of the base arrays. See §1.10 for the array-id shift.
- **Writers.** Every write is seq-bumped (`Write`, or `AcquireLock` … `ReleaseLock`), so the TCP
  mirror and readers see untorn rows. Each context has a single writer.
  - **Server allocation** (`ServerContext::AllocateInstrument`, first allocation only) writes
    `WorkingRisk{Position = server-wide position row Quantity}` with zero reservations, right after
    the server position row is restored.
  - **Client onboarding** (`Client::OnInstrumentAllocated`, on every process start) writes
    `WorkingRisk{Position = this strategy's own LocalPositionHeaders row Quantity}` with zero
    reservations (see §5 2026-10-04).
  - **`RiskLayer`** moves `Position` in `OnFill` and the two `Worst*` fields in
    `ApplyWorstWorkingQuantityDelta`.
- **Why it has its own `Position`.** The client must see position and reservations change
  atomically with the order event that caused them. With the position coming from the position row
  and the reservations from here, the two would be out of step between a Fill and its OrderState.
- **Accessor.** `Context::GetWorkingRisk(instrumentId)` returns this context's own row. On a
  client, the range check passes only instruments this client has allocated.
- **C++ GUI interop.** A C# Risk Limits widget attached to a C++ server reads the server's
  `WorkingRisks` row. Without it, the widget does not list the instrument at all: `Read()` of an
  Empty (seq 0) row throws, and the widget skips the instrument and retries every tick. The C++
  server must write and seq-bump the rows, starting with the allocation seed.

### 1.8 `OrderRisk` — acked quantity in the row, 29 in-flight slots (WIRE CHANGE 2026-10-04)

The struct is still 64 bytes, pack 1, with no `Header` (it is not `RegisterJson`).

```
@0   uint16 ActiveTargetsCount         (0..29)
@2   uint16 WorstOrderQuantity          (max over in-flight entries, 0 when none)
@4   uint16 AbsAckedOrderQuantity       (NEW: last acked |quantity|, 0 until the first ack)
@6   uint16 AbsOrderQuantities[29]      (live at [0, count), zeros after)
     sizeof == 64;  MaxActiveTargets = 29 (was 30);  MaxOrderQuantity = 65535
```

- `GetAbsWorstOrderQuantity()` **takes no argument** and returns
  `max(AbsAckedOrderQuantity, WorstOrderQuantity)`. A zeroed row returns 0.
- `IsFull()` is `count == 29`. `TryAdd` refuses the **30th** in-flight target with
  `TooManyActiveTargets (45)`.
- `Ack(q)` calls `Remove(q)`, then sets `AbsAckedOrderQuantity = abs(q)`. The value is in range
  because every acked quantity passed `TryAdd`.
- `Reject(q)` is `Remove(q)` only. It does not touch the acked field.
- `TryAdd` and `Remove` are unchanged: same scan, swap-remove, and max rescan.
- The row is reset to zero on Create (in `ValidateOrder`) and on Done (in `OnOrderState`).
- **Storage changed:** the region is now `<contextDir>/OrderRisks`, one array per context
  directory, at the same creation position (array id 12).
  - The server's covers every order and is opened with server access. Its region name is unchanged.
  - A client's covers only its own slots and is opened with client access (writable).
  - A client can no longer see the server's rows through its client context.
  - `OrderRisk` rows are written by plain ref, with no seq bump. This is unchanged.
- This supersedes the layout, reference implementation and differential test in
  `cpp_alignment_report_2026-09-10_orderrisk.md`. The reworked test is in §6.

### 1.9 `OrderTargetAction` — `Amend` → `Replace`, new `Reduce` (WIRE CHANGE 2026-10-04)

```cpp
enum class OrderTargetAction : uint8_t { Create = 0, Replace = 1 /* was Amend */, Cancel = 2, Reduce = 3 };
```

- The byte travels at `OrderTarget` offset 49 (`TimeInForce` @48, sizeof 52) and at
  `OrderRejected` offset 32 (after the 4 B header and the 28 B `OrderHeader`).
- The JSON names change: `"Amend"` becomes `"Replace"`, and `"Reduce"` is new.
- `Reduce` means less quantity at the same price and side. It keeps queue priority and never adds
  risk. `Replace` is everything else: a new price, more quantity, or the same profile.
- `OrderProfile::IsReduceOf(const OrderProfile& other)` decides it:
  `Ticks == other.Ticks && Sign == other.Sign && abs(Quantity) < abs(other.Quantity)`. The
  comparison is strict, so a zero-quantity profile is never a reduce of a live order.
- **Every C++ branch that tested `== Amend` must accept `Replace || Reduce`.** Both take the amend
  path everywhere except two places:
  - the server rate limit (§3);
  - the exchange adapter, which maps `Reduce` to the venue's priority-keeping quantity-down modify.
    Action 3 must be encoded exactly like `Replace` (1): on iLink, `OrderCancelReplaceRequest` at
    the same price with the smaller `OrderQty`. Never route it to a default or throw branch: by
    then the server has already reserved and counted it. §6 has the test.
- The C# server and simulator compare only against `Create` and `Cancel`.
- **C++ senders.** `Strategy.hpp` `Amend` sets `OrderTargetAction::Amend`, an identifier that goes
  away with the rename. It should set `Reduce` when `newProfile.IsReduceOf(currentProfile)` and
  `Replace` otherwise, as the C# senders do (`Strategy/Algo.cs`, `Widget/SendOrderWidget.axaml.cs`).
  A plain rename to `Replace` is wire-correct, but a size-down then loses the cancel-like rate-limit
  treatment (§3). Labelling everything `Reduce` gains nothing: an unverified `Reduce` is throttled
  like a `Replace`.
- The OrderTarget's `TimeInForce` now carries `ImmediateOrCancel (2)` on algo IOC creates and is
  copied onto amends. The C++ server must honour it on a `Create`.

### 1.10 `OrderType::WorkingRisk = 17` and shared-array ids (WIRE CHANGE 2026-10-04)

- Add `WorkingRisk = 17` to `enum class OrderType : uint8_t`. Values 10–16 are unchanged. Any
  dispatcher that switches on a row's first byte (TCP mirror, audit) must accept 17. The C#
  `TCPServer.ReadTCP` sends it to the `default:` mirror path.
- **Array ids are the creation order and must match C# exactly.** The TCP mirror packet is
  `int32 arrayId, int32 index, row bytes`, and `Mirror()` writes blindly by id. The order is:

  | id | array | id | array |
  |---|---|---|---|
  | 0 | ServerHeader LetterBox mirror | 9 | RateLimits |
  | 1 | ClientHeaders | 10 | CoreGroups |
  | 2 | InstrumentHeaders | 11 | OrderStates |
  | 3 | InstrumentHeaderIdByInstrumentId | 12 | OrderRisks (now `<contextDir>`) |
  | 4 | InstrumentIdsByClientId | 13 | OrderTargets |
  | 5 | ClientIdsByInstrumentId | 14 | LocalPositionHeaders |
  | 6 | MarketsByPrice (`<contextDir>`) | **15** | **WorkingRisks (NEW, `<contextDir>`)** |
  | 7 | RiskLimits | **16** | **ServerPositionHeaders (server context only; was 15)** |
  | 8 | MessageEfficiency | | |

  A client context has ids 0–15, and a server context has 0–16.
- Only the server context's arrays are mirrored. Client-side risk rows stay local to the client.
- A peer that still uses the old order breaks the mirror, in a different way in each direction:
  - Old → new is silent. The old peer's 57-byte `PositionHeader` rows (pack 1) under id 15 land,
    truncated to their first 16 bytes, in the new peer's 16-byte `WorkingRisks`.
  - New → old throws on a C# receiver. A 16-byte `WorkingRisk` row under id 15 is too short for the
    old peer's 57-byte `PositionHeader` array (`SharedArray.Write` length check), and id 16 does not
    exist there (`Mirror` indexes a dictionary by id). A C++ receiver without those checks corrupts
    memory instead.

  Both ends must be upgraded together.
- Pre-existing, latent: `OrderRisk` has no header. Its rows are written by plain ref with no seq
  bump (§1.8), so they stay at seq 0, which the mirror reads as Empty and skips, snapshots included.
  No `OrderRisk` row is shipped today. If one were ever seq-bumped, it would be dispatched on the low
  byte of its count (C# `TCPServer.ReadTCP`):
  - Counts 10–14 would hit the `OrderState`/`OrderTarget`/`OrderRejected`/`Fill`/`Position`
    branches. 12 and 13 would then be forwarded to a client instead of mirrored, and 11 mirrored
    only conditionally.
  - Counts 15–17 would reach the `default:` mirror path, which is correct.

  Keep C++ `OrderRisk` writes plain (no seq bump), and do not copy this into new code.

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

*(Rewritten 2026-10-04. The previous text described a server-only RiskLayer that reserved on
`RiskLimit` with a tentative-add/back-out. That is superseded.)* C++ `RiskLayer.hpp` has
header/seq validation and the `MaxOrderQuantity` check but no reservation accounting. Port from C#
`Provider/RiskLayer.cs`, plus `OrderRisk`/`WorkingRisk` in `Execution/Order.cs`. Excerpts are in
`cpp_alignment_report_2026-10-04.md`.

- **One class, two sides.** The constructor is `RiskLayer(Context&, OrderRejectedSource)`: the
  base context, not the server context.
  - The server runs it on its `ServerContext` with source `Server`.
  - Each client runs the **same code** on its own `ClientContext` with source `Client`, against
    its own `OrderRisks` and `WorkingRisks` (§1.7, §1.8).
  - The server-only early returns in `OnOrderState`, `OnFill` and `OnOrderRejected` are gone.
  - The C++ server needs the server path. The existing C++ `Client.hpp` needs the client path and
    the client hooks in §5, because `Strategy.hpp` already sends algo Creates and Amends through it.
  - The server never relies on a client having checked anything.
  - By timing, the client copy is at least as tight as the server for this strategy's own orders:
    it counts its own sends before the server reads them, and it learns of acks, Dones and rejects
    after the server. It cannot see other strategies, manual orders booked to the strategy, the
    house book or the server rate limit, so the server can still refuse (and pause) what the client
    passed. That is accepted (report §7: B6, rate limit, B16; Spec.md "The client copy is never
    looser than the server — and where it can be").
- **`OrderRisk`** is described in §1.8. It is a multiset of in-flight `|quantity|` values plus the
  last acked one: worst = `max(acked, max in flight)`.
- **Sign convention** (unchanged). Everything is signed: buys and longs positive, sells and shorts
  negative. `WorstLong >= 0` and `WorstShort <= 0`. `GetAbsWorstOrderQuantity()` returns a
  magnitude.
  - `ApplyWorstWorkingQuantityDelta(orderId, orderSideSign, magnitudeDelta)` does nothing when the
    delta is 0. Otherwise, for each leg of the order's instrument (an outright is its own leg, with
    weight +1):
    - `legSide = orderSideSign * sign(weight)` and `legDelta = magnitudeDelta * |weight|`;
    - `WorstLong += legSide > 0 ? legDelta : 0`, and `WorstShort -= legSide < 0 ? legDelta : 0`;
    - the update runs under that leg's `WorkingRisk` seq lock.
  - **The sign is applied exactly once, here.**
- **Room** is `GetAbsAllowedOrderQuantity(target, isWithinLimit = false)`, using 64-bit arithmetic:
  ```
  worst  = (action == Create) ? 0 : OrderRisk(orderId).GetAbsWorstOrderQuantity()   // a Create's row is still the previous order's
  allowed = INT32_MAX
  for each leg: legSide = target.Sign * sign(w);  wr = this context's WorkingRisk(leg); rl = RiskLimit(leg)
      room = legSide > 0 ? rl.MaxPositionQuantity - wr.Position - wr.WorstLong
                         : rl.MaxPositionQuantity + wr.Position + wr.WorstShort
      roomOrders = room >= 0 ? room / |w| : (isWithinLimit ? floorDiv(room, |w|) : 0)
      allowed = min(allowed, worst + roomOrders)
  return (int)allowed     // may be negative only with isWithinLimit
  ```
  Example: an order whose worst is 5 sits on a leg 3 past its limit (room −3), with `|w|` = 1.
  - It may stay at 5 (`isWithinLimit = false`). `ValidateOrder` uses this form, so a cut always
    passes.
  - The best-effort clip cuts it to 2 (`isWithinLimit = true`).
- **Reserve** (`ValidateOrder`, any non-Cancel) is check-then-commit, with nothing to back out:
  1. Per leg, `|workingQuantity * w| > RiskLimit(leg).MaxOrderQuantity` refuses with
     `QuantityExceedsRiskLimit`. Here `workingQuantity = q - filled`, and `filled` counts only
     when the state row is this order.
  2. `|q| > GetAbsAllowedOrderQuantity(target)` refuses with `PositionExceedsRiskLimit`
     (`IsWithinRiskLimit`, which writes nothing).
  3. On a Create, zero the `OrderRisk` row.
  4. `before = worst`, then `TryAdd(q)`. On failure, refuse with `QuantityNotValid` or
     `TooManyActiveTargets`.
  5. Apply `(worst_after - before)` with `target.Sign`.

  Keep this reason order. `PositionExceedsRiskLimit` (not discarded, so it pauses) is now reported
  before `TooManyActiveTargets` (discarded).
- **Full `ValidateOrder` order** (match it exactly, so discard and pause classification is
  identical):
  1. Create: instrument checks (`NotInSession` unless `TradingStatus == Open`), then client checks,
     then create checks. Not Create: the server or client header, seq and side checks, with
     `isReduceOrReplace` wherever `isAmend` used to be.
  2. `AlgoIsPaused` for a non-cancel algo order whose strategy row is Paused.
  3. Client side, non-algo order: return here, with no risk and no reservation.
  4. Return false if any reason is already set.
  5. Server side only: the rate limit (below).
  6. The reserve block above.
  7. Any exception sets `ExceptionThrownByRiskLayer`.
- **Rate limit** (server only, 2026-10-04):
  - `isReduce = action == Reduce && target.Profile.IsReduceOf(orderState.Profile)`. The server's
    state row is the last exchange-reported state, so a Reduce queued behind an unacked Replace
    may fail this test (for example after a price change), and is then throttled like a Replace.
  - `if (isCancel || isReduce) SendOrder(now)`: counted, never refused.
  - Otherwise, `if (!TrySendOrder(now))` refuses with `TooManyOrdersPerSecond`, which is discarded.
- **Hooks**:
  - **`OnOrderState(state)`**: no second argument, and the server no longer passes
    `beforeAckedOrderQuantity`.
    - `if (Reason == Acked)`: `before = worst; Ack(state.Profile.Quantity);` apply `worst - before`.
    - `else if (Status == Done)`: `released = worst - |QuantityFilled|`; zero the row; apply
      `-released`.
    - The side is taken from `state.Profile.Side` (`Buy ? 1 : -1`).
    - Server: called from `WriteOrderState` after the seq-bumped write, only when the overwrite was
      applied, with the merged row.
  - **`OnFill(fill, isReserved = true)`**:
    1. Always add `fill.Quantity` (signed) to `WorkingRisk(fill instrument).Position`, under the
       seq lock.
    2. Return if `!isReserved || instrument.IsLegged`.
    3. Otherwise apply `-|fill.Quantity|` with `fill.Sign`.

    The server now calls it for **every** fill, including the spread's own fill row, so that row's
    `Position` moves. The `IsLegged` check moved from `Server::OnFill` into `RiskLayer`.
  - **`OnOrderRejected(rej)`**:
    - Return early if `rej.Source == this side's source` (it never reserved here) or
      `rej.Action == Cancel` (a cancel never reserves, and its profile could match a live entry).
    - Otherwise `before = worst; Reject(rej.Profile.Quantity);` apply `worst - before`.
    - It no longer reads the `OrderState` row.
    - Server: called for exchange rejects only. A client: called for server and exchange rejects
      of its own algo orders (§5).
- **Best-effort clip** (`TryClipToRiskLimit(target&)`) is client/Algo only. Port it only if C++
  ports `Algo::TryTarget`; the server does not need it.
  1. A non-Create with `OrderRisk.IsFull()` returns false.
  2. `absFilled` = `|state.QuantityFilled|` for a non-Create whose state row is this order, else 0.
  3. `allowed = min(GetAbsAllowedOrderQuantity(t, true), 65535)`, then per leg
     `min(allowed, absFilled + RiskLimit(leg).MaxOrderQuantity / |w|)`.
  4. Return false if `allowed <= absFilled`.
  5. If `|q| > allowed`, set `q = sign * allowed` (it only ever lowers). Return true.

  It is idempotent.
- **Invariant per order.** The deltas applied sum to zero over the order's life whenever the fills
  this RiskLayer saw add up to the Done's `QuantityFilled`. The deltas are: reserve, ack, reject,
  per-fill release and Done release. This holds whether or not every amend was acked. On the
  server, the duplicate-fill drop (§5) keeps it true.
- The old "known-open" bullet is **deleted**. Both items are implemented: an acked amend-down
  releases at the ack (because `OrderRisk` records the acked quantity), and order entry is
  rate-limited per CoreGroup (§5 `RateLimits`).
- `GetPositionHeader(clientId, instrumentId)` moved from the server context to the base `Context`,
  with an identical body. It is read-only on a client. This is an API move only.

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
- **Risk limits are SERVER-WIDE; `StrategyId` is REMOVED from `RiskLimit` (2026-09-10).** One row
  per instrument, applied to every strategy. A per-strategy limit is a future feature. Old
  `.risklimit` lines that still carry `"StrategyId"` parse fine (an unknown property is skipped).
  *Amended 2026-10-04:* the 32-byte layout given here originally, with `Worst*` @24/@28, is
  superseded. `RiskLimit` is now 24 bytes and config only (§1.1). The working quantities live on
  `WorkingRisk` (§1.7). `static_assert(sizeof(RiskLimit) == 24)`.
- **Risk-limit edits are requests on the execution channel (amended 2026-09-10; replaces the
  earlier "copy the live `Worst*` across" workaround).** A client sends `ControlRiskLimit`
  (`ControlType::RiskLimit = 201`; `int32 ClientId, InstrumentId, MaxOrderQuantity,
  MaxPositionQuantity` after the 4-byte header — 20 bytes, pack 1) on the instrument's CoreGroup
  channel, never a `RiskLimit` row, and `ReadExecution` applies it on the CoreGroup thread:
  `MaxOrderQuantity`, `MaxPositionQuantity`, `Timestamp = now` written in place under the row's seq
  bump (since 2026-10-04 the working quantities are not on this row at all: they live on
  `WorkingRisk`, so an edit cannot rewind a reservation); then post the row to the server's own audit socket
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
- **In-Flight Mitigation is mandatory (2026-09-22):** every iLink 3 session logs on with IFM,
  tag 9768 = 1. `OrderState.QuantityFilled` is cumulative across every cancel/replace and the risk
  layer's `Done` release is `worst - QuantityFilled`; a non-IFM session restarts CumQty at zero on
  each modify and would over-release by everything filled before the replace. If a session ever
  cannot get IFM, the adapter must normalise CumQty to cumulative before the state reaches the
  server. It must never pass a reset through. Spec.md "In-Flight Mitigation is always on".
- **Acceptance before trade is a contract the risk layer depends on (2026-09-22; amended
  2026-10-04):** `RiskLayer::OnOrderState(state)` stays the two-branch form:
  - `Acked` retires the target. `Ack` also records the acked quantity in `OrderRisk`. The branch
    releases the change in worst.
  - `else if Done` releases `GetAbsWorstOrderQuantity() - |QuantityFilled|` from the row, not
    from the message.

  Since 2026-10-04 a fill carrying a quantity the server never saw acknowledged no longer leaks.
  Its target stays in flight, so it over-reserves and holds one of the 29 slots until Done, which
  then releases exactly what the order holds. The contract still matters, because reservations
  shrink at the ack. CME delivers ExecutionReportNew/Modify before ExecutionReportTrade (whether
  CME ever acknowledges an amend only inside a fill is pending CME confirmation, report §7 item 3;
  a violation over-reserves until Done instead of leaking); the adapter must preserve that order and must never coalesce an acceptance into a fill, cancel or
  elimination, and on recovery must replay acceptances before fills. If a venue ever coalesces, the
  adapter synthesises the acceptance; do not "fix" it in RiskLayer. (A reconcile-on-any-quantity-
  change variant shipped briefly in 1eef81a and was reverted.) Spec.md "Acceptance before trade".
- **New shared array `RateLimits` (2026-09-22):** region `<server>/RateLimits`, `CoreGroupIds.Length`
  (64) rows of `RollingRateLimit`, 64 bytes each: `RateLimit` 16 @0 (`Duration` int64 nanos @0,
  `Limit` int32 @8, `RateLimitId` int32 @12), `BucketTimestamp` int64 nanos @16, `BucketIndex` int32
  @24, `Total` int32 @28, `uint8 Counts[32]` @32. Index == CoreGroupId, server-written, created in
  `Context` directly after `MessageEfficiency` (keep that array-id order for the mirror). The server
  writes a CoreGroup's row when it loads the CoreGroup's file (2026-09-23, see next bullet): the
  whole of `<server>/RateLimits/<CoreGroupName>.ratelimit` (static pretty JSON `RateLimit`, `Duration`
  in the Duration converter's string form, e.g. "0.00:00:03.000_000_000"), else
  `RateLimit::GetMaxLimits` (1 s, int32 max) in simulation and `GetMinLimits` (1 s, 0) in realtime,
  the same defaults as `.risklimit`. The hard-coded CME default is
  gone: New Release, Certification and Production publish different limits, so the number is per
  server directory. `RiskLayer` throttles order entry per CoreGroup with it, `TrySendOrder(Clock.Now)`
  on a plain ref with no seq bump, rejecting `TooManyOrdersPerSecond`. A `Cancel` goes through
  `SendOrder` instead (2026-09-23): counted in the window, never refused, bucket byte still capped at
  255 — a cancel is the message that reduces risk and must never be held back. Since 2026-10-04 a
  verified `Reduce` also goes through `SendOrder`. Verified means
  `target.OrderProfile.IsReduceOf(orderState.OrderProfile)` holds against the server's state row.
  An unverifiable `Reduce` (for example one behind an unacked `Replace` that changed the price)
  goes through `TrySendOrder` like any `Replace`. Bucket semantics and
  the conservative Duration/31 rule are in Spec.md "Order rate limit". A C++ server must create the
  same region and own its writes, or a C# GUI attached to it shows an empty Rate Limits widget.
- **New shared array `CoreGroups` (2026-09-23):** region `<server>/CoreGroups`, `CoreGroupIds.Length`
  (64) rows of `CoreGroup`, 36 bytes each: `String16 CoreGroupName` @0, `int32 CoreGroupId` @16,
  `int32 ServerCoreId` @20, `int32 MarketDataCoreId` @24, `int32 StrategyCoreId` @28,
  `int32 ReservedCoreId` @32 (all ids -1 when unset). Index == CoreGroupId, server-written, created in
  `Context` directly after `RateLimits` (array-id order for the mirror). The `CoreGroupId` enum in Data
  is DELETED: the server names its groups in files. A server context opened for write loads every
  `<server>/CoreGroups/*.coregroup` (one static whole-file pretty JSON `CoreGroup` per file, NOT the
  appended-line form of `.risklimit`: neither a CoreGroup nor a rate limit is amended at runtime) into the row at
  its `CoreGroupId`, throwing for an id not in `ServerHeader.CoreGroupIds`, then loads that group's
  `.ratelimit` by name as above. The C++ CME server must do the same when it opens its context. Keep
  a CME-side enum (OS 0, Reserved 1, SandP500 2, Equity 3, Forex 4, Crypto 5) for the names, and put
  the four core ids for the box in each file. Clients resolve a group by name
  (`Context::GetCoreGroupId(String16)` scans the set bits and THROWS if absent) and pin their
  strategy thread to the row's `StrategyCoreId`; the C# Testing scenario prompts for one of those four
  names, so the strings in the C++ server's files must match them exactly. The Rate Limits widget
  shows `CoreGroupName` from this row, so a group with no file shows an empty name.
- **`OrderState` is 64 bytes: `QuantityBehind` (2026-09-26, WIRE CHANGE):** C# `OrderState` gained
  `int32 QuantityBehind` at offset 60, directly after `QuantityAhead` @56 (`QuantityFilled` @52), growing
  it from 60 to 64 bytes; there is no reserved room left. It is the quantity queued behind the order at
  its price, maintained only by the C# simulator; a live C++ server has no queue model and must write 0
  (every `OrderState` it constructs, including the PendingNew state in the `Create` branch). The C++
  struct, its static_assert, the `OrderStates` shared array, TCP mirror and audit decoding must move to
  64 bytes in lockstep with the C# deploy, or every field after offset 60 in the array is misread.
  `AheadOfOrder` also gained `QuantityBehind` (20 bytes), but it only travels inside the C# simulator.
- **Session state is the header's `TradingStatus` (2026-09-24):** the C# instrument no longer has a
  `SessionManager` or `IsInSession`. `RiskLayer::ValidateInstrument` rejects a create with
  `NotInSession` unless `InstrumentHeader.TradingStatus == Open`, and quotes (`TryGetQuote`, the
  position quote) are empty unless it is `Open`. Unknown counts as not open, so the C++ CME server
  must publish each instrument's status from the snapshot / SecurityStatus at startup
  (`OnTradingStatusUpdate`, per the 2026-09-10 report T1–T5), or nothing trades and no client has a
  quote. Port the same gate into the C++ `RiskLayer`. No wire-shape change. Spec.md "Session state
  is the exchange's TradingStatus".
- **PendingNew `QuantityAhead` is provisional, not 0 (2026-09-23):** in the `Create` branch of the
  order-target path the C# server writes the PendingNew `OrderState` with `QuantityAhead` = its own
  `MarketByPrice64` quantity at the order's price on the order's side (`Bids.GetQuantity(ticks)` for
  a buy, `Asks.GetQuantity(ticks)` for a sell), read by `ref readonly` before the order row lock is
  taken. The C++ server must seed the same way: a 0 there shows every fresh order at the front of
  the queue for the whole round trip to the venue. The acceptance overwrites it with the real
  position and `OnQuantityAhead` keeps it current afterwards. No wire-shape change; addendum in
  cpp_alignment_report_2026-09-22.md.
- **SIGHUP runs the exit actions (2026-10-02):** C# `Tools.Application` now registers a SIGHUP
  handler on non-Windows platforms (`PosixSignalRegistration`, held in a static field so it is never
  collected). It runs `OnExit` (every exit action, in priority order), then lets the default action
  terminate the process. Before this, a closed terminal or dropped ssh session killed the process
  without any cleanup: no audit flush, no final position lines. The C++ server and tools must treat
  SIGHUP the same as SIGINT/SIGTERM. Use the usual async-signal-safe pattern: the handler only sets the
  exit flag, and the main loop runs the cleanup. Windows is unchanged (`SetConsoleCtrlHandler`).
- **`SideByPrice64.Quantity`, total depth per side (2026-10-02, layout unchanged):** `int32 Quantity`
  at offset 17 of each `SideByPrice64`, carved from `_reserved` (47 → 43 bytes), so `MarketByPrice64`
  keeps its size. It is the sum of the quantity at every active level on that side, maintained inside
  `TrySetQuantity`:
  - On a reset to a single level, it is set to that level's quantity.
  - On an update, it changes by the new quantity minus the slot's old active quantity.
  - When levels fall outside the 64-level window, their quantities are subtracted.
  - `Clear` sets it to 0.
  Only the C# simulator reads it today (`QueueManager.AddGhost`, `OrderManager.DecayCrossMask`), and the
  simulator maintains its own book, so nothing breaks yet. But the field sits in the `MarketsByPrice`
  shared array, so the C++ `SideByPrice64::TrySetQuantity` must maintain it the same way, with a
  static_assert on the offset. Otherwise any future C# reader attached to the C++ server sees 0.
- **Struct and array changes (2026-10-04, WIRE CHANGE):**
  - `RiskLimit` is 24 bytes (§1.1).
  - New `WorkingRisk`, 16 bytes, `OrderType::WorkingRisk = 17` (§1.7, §1.10).
  - `OrderRisk` has an acked field @4 and 29 slots (§1.8).
  - `OrderTargetAction` is `Replace = 1` / `Reduce = 3` (§1.9).
  - The array ids change: `WorkingRisks` is 15 and `ServerPositionHeaders` goes from 15 to 16.
  - `OrderRisks` and `WorkingRisks` are per context directory (§1.10).

  The C++ structs, static_asserts, shared arrays, TCP mirror and audit decoding all move in one
  deploy with the C# build.
- **Only an algo order's reject pauses the algo, at both pause sites (2026-10-04, behaviour):**
  `IsAlgoOrder()` means `ClientId == StrategyId`.
  - `Server::Reject`, in this order: forward the reject to the client (always). Return if it is
    discarded. **`if (orderId.IsAlgoOrder())`** set the strategy to `Paused`. Raise the
    `OrderRejected` event, which still fires for manual orders.
  - `ReadExecution`, in the `OrderType::OrderRejected` case (a reject a client writes to the
    server): set `Paused` only `if (orderId.IsAlgoOrder())`. The server does not re-check the
    discard set there, because the client sends only non-discarded rejects.

  A manual (GUI) order booked to the algo's strategy may still be refused. It may use room the
  algo's client copy cannot see, which is accepted. Its refusal is reported, but it never pauses
  the algo.
- **Duplicate fill drop (2026-10-04, behaviour):** `Server::OnFill(state, fills)` checks the order
  first: the row must be this order, or it throws "unknown clientOrderId". **Then, before any
  stamping, locking, position, risk, forwarding or audit:**
  `if (abs(state.QuantityFilled) <= abs(row.QuantityFilled)) return;`. The row is the ORDER's
  state row, which for a spread is the spread row. Magnitudes are compared because the field is
  signed.
  - The whole event is dropped as an iLink resend (`PossRetransFlag`). The drop is neither logged
    nor counted. C++ may count it.
  - Example: the row holds `QuantityFilled = -3` and a resend arrives with `-3`. It is dropped, so
    neither position row moves and no risk is released a second time.
  - This depends on the adapter putting the cumulative `CumQty` (IFM, tag 14) in the event's
    `OrderState.QuantityFilled` on every trade report, retransmissions included. It also assumes
    fills arrive in order per session. Both are pending CME confirmation. If either fails, both
    sides switch to a recent-set keyed by ExecID (`FillId`).
- **`ReadAdmin` / `ReadExecution` must never kill the process (2026-10-04, contract):** both may
  throw. Known throwers, per entry point:
  - `ReadAdmin` → `OnAllocateInstrument` (instrument header id or client id out of range) and
    `CreateInstrument` (spread legs missing, unknown instrument type).
  - `ReadExecution` → whatever `OnOrderTarget`, `OnControlRiskLimit` and `OnControlAlgoStatus`
    throw.
  - `ReadFromIlink` (the exchange read on the same CoreGroup thread; in C#, the simulator's release
    loop) → `Server::OnFill` ("unknown clientOrderId", "fill does not belong to the order").

  Realtime: the caller wraps each iteration in try/catch, reports to the alert path (C#:
  `AlertManager.OnException`, as in Scenario's `ReadSocket` loop) and keeps polling. This applies
  to every CoreGroup thread loop (ReadFromIlink + ReadExecution) and to the admin loop. C# found
  this when a throw in `Context.CreateInstrument` on the admin thread took the whole server down.
- **A client refuses to start while a previous process's order is still Active (2026-10-04,
  behaviour):** `Client::OnInstrumentAllocated` runs `ThrowIfPreviousOrdersActive(instrumentId)`.
  It runs before the `WorkingRisk` seed and before the data socket opens.
  - For `localIndex` 0..63 it reads the server-wide `OrderState` row of `OrderId{ClientId = own,
    LocalIndex}`, and skips rows on another instrument.
  - `Active`: throw `InvalidOperationException` ("…still Active on instrument N: start again once
    the server has cancelled it").
  - `Done`: **zero this client's `OrderRisk` row for that slot**, so the new process inherits
    nothing.
  - Then `Context.GetWorkingRisk(id).Write(WorkingRisk{Position = own LocalPositionHeaders row
    Quantity})`, with zero reservations. With no Active orders this is exact.
  - A C++ client mirrors all of it.
  - Accepted residual: a fill landing between the socket connect and the check can be counted
    twice.
  - Known gap: a previous process's Create that the server has not yet read is not detected,
    because only the state row is read.
- **The spread shape is refused on the CLIENT; the server does not check (2026-10-04,
  behaviour):** `Client::GetInstrument` throws `NotImplementedException("Spread N: only a two-leg
  +1/-1 calendar is supported")` for any `InstrumentType::Spread` unless
  `LegCount == 2 && |Legs[0].Weight| == 1 && Legs[0].Weight == -Legs[1].Weight`.
  - The check runs before any leg onboarding and before `AllocateInstrument` is sent.
  - A C++ client **must** mirror it. A butterfly would otherwise be mis-risked, because `Spread`
    builds its risk legs as +1/−1.
  - The C++ server must **not** throw for it in `CreateInstrument` or anywhere on the admin path.
- **The client hooks that feed the client `RiskLayer` copy (2026-10-04, behaviour; required for
  the existing C++ `Client.hpp`, whose `Strategy.hpp` sends algo Creates and Amends):**
  - **Sites to change in C++.**
    - `Client.hpp:109` builds `_riskLayer(ServerName, Client)`. It must be built over the client's
      own context (C# `Client.cs`: `new RiskLayer(Context, OrderRejectedSource.Client)`).
    - `OnInstrumentAllocated` (:161), `OnOrderRejected` (:357), `OnOrderState` (:366) and
      `OnFill` (:388) need the hooks below.
    - `GetInstrument` (:138) needs the spread-shape check above.
  - **Land the `RiskLayer` port and the client-context switch together.** With the server-only
    early returns gone, and client `ValidateOrder` now reserving for algo orders, a client
    `RiskLayer` still built over `ServerName` would write the server's `OrderRisks` and
    `WorkingRisks` rows. On a client those rows are read-only, or they would be corrupted.
  - **Construction.** `RiskLayer(own ClientContext, Client)`. Before this change it was built over
    the server's rows.
  - **`OnOrderState`.** Inside the existing "state is this slot's current order" branch (full
    ClientOrderId, generation included), keep `int32 ackedSeqs[64]`. If `IsAlgoOrder() &&
    slotActive && (Status == Done || (Reason == Acked && Seq > ackedSeqs[slot]))`, call
    `RiskLayer::OnOrderState`, and on an Acked set `ackedSeqs[slot] = Seq`. Do this **before** the
    Done handling frees the slot, so an echoed Done fails the `slotActive` test. Reset
    `ackedSeqs[slot] = 0` in `Create`, after the slot is allocated.
  - **`OnFill`.** `RiskLayer::OnFill(fill, fill.ClientId == own ClientId)`, before the position
    and event handling. A manual order booked to this strategy moves `Position` but releases
    nothing.
  - **`OnOrderRejected`.** If `rej.OrderId == slot's OrderTarget OrderId && IsAlgoOrder()`, call
    `RiskLayer::OnOrderRejected(rej)` **before** the discard check. Discarded rejects
    (`TooManyActiveTargets`, `TooManyOrdersPerSecond`, …) must still release what the client
    reserved at send time.
  - **`ValidateOrder`.** On the client, risk checks and the reservation run for algo orders only.
    There is no rate limit on the client.
  - `HasFreeOrderSlot` (`!isOrderActive.IsFull()`) matters only if C++ ports `TryTarget`.
- **What a refused target means (2026-10-04, semantics):**
  - **Server-refused.** It reserves nothing on the server. The pure check runs before `TryAdd`,
    so there is no back-out. A non-cancel that passed the rate check still used its rate slot
    (unchanged).
    - A refused Create still writes the Done/`Rejected` PendingNew state row.
    - The reject goes back with source `Server`, and the client's copy releases what it reserved
      when it sent.
    - `TooManyActiveTargets` and `TooManyOrdersPerSecond` stay discarded: no pause.
  - **Exchange-refused.** A refused amend or cancel leaves the order's profile as it was, with no
    delete. CME behaves this way, and the C# simulator was fixed to match: a cancel, or an amend
    refused for `SeqOutOfOrder`, `NotInSession`, `StrategyIdNotValid` or `InstrumentIdNotValid`,
    leaves the simulator's order untouched, seq included. An amend refused for `TargetIsActive` or
    `SideNotValid` also leaves the order unchanged, but the simulator's stored seq has already
    advanced to the refused target's seq (Simulator/spec.txt "Refused Targets Change Nothing").
    The server passes the reject to `RiskLayer::OnOrderRejected` only while the state row is
    still that order. For a refused amend (`Replace`/`Reduce`) that removes one matching in-flight
    entry. A refused cancel changes no risk row, because `OnOrderRejected` returns early for
    `Cancel`.
  - **Client-refused** (source `Client`). It never reserved anything: it is refused before or at
    `TryAdd`. It is written to the server only when it is not discarded, and then pauses only an
    algo order.
- **A refused Create: the server publishes Done first, then the reject (2026-10-05, behaviour):**
  C# `Server.OnOrderRejected` (the adapter's entry point for a refused target), inside the
  same-order check and after the `OrderNotFound` → `StateIsDone` mapping: if
  `OrderTargetAction == Create`, build `OrderState { OrderHeader, OrderProfile from the reject,
  Done, Rejected }` and call `OnOrderState` with it, then run the existing reject path. Mirror it in
  C++ `Server::OnOrderRejected`, and remove the post-reject Done/Rejected state from
  `InstrumentRouter.hpp` (:338-344 and :293-298): the router sends only the reject. If the router
  kept sending it, nothing is released twice (`WriteOrderState` ignores a second Done) but the
  duplicate reaches the client. A refused Replace/Cancel still gets no state. Spec.md "A refused
  Create: Done first, then the reject"; closes harness case G5d.
- **GUI cancels of an algo order jump the seq by 1,000,000 (2026-10-04, behaviour):** C#
  `ManualClient::Amend` owns all manual numbering.
  - A cancel of an algo order uses `max(existing target seq + 1,000,000, caller seq)`, the same
    jump `Server::CancelAllOrders` uses.
  - A manual order's amend uses `existing + 1`.
  - Any non-Cancel on an algo order throws `InvalidOperationException`: an algo's orders are
    cancel-only from the GUI.
  - All widget-side seq offsets are gone.
  - The server refuses a target only when the existing target row's seq is greater than the new
    one's (`TargetIsStale`, 44, discarded). An equal seq must pass, because the owning client
    wrote that row before sending. Do not require `prev + 1`.
  - The GUI writes no algo target row. So a GUI cancel (seq + 1,000,000) and a racing algo amend
    (seq + 1) both pass the server, and the venue's arrival order decides (report §2.9, F3).
    Nothing comes back `SeqOutOfOrder`, because the GUI cancel and the algo amend never share a
    seq. `SeqOutOfOrder` (31, not discarded, so it pauses an algo order) is raised by the client's
    own check (existing target seq >= new), by `ValidateCreate` for a Create whose seq != 1, and by
    the venue/simulator when the order's stored seq >= the target's. A GUI cancel numbered + 1 could
    have hit that last one, which is why it jumps.
  - Any C++ manual or GUI client numbers the same way.
- **Cancel shape from the C# Algo (2026-10-04, wire consequence):** every Algo cancel has
  `Quantity = working + filled` (signed by side, non-zero) at the active's price, with
  `Seq = active.Seq + 1`. This includes the paused-branch `CancelAllOrders`, which used to send
  `Quantity = filled` and so no side when nothing was filled. The C++ server must accept it.
- **C#-only in this batch; nothing to port** unless C++ ports `Algo`/`Position`:
  - the `Target` (strict) vs `TryTarget` (best effort) split;
  - Phase 2 largest-first;
  - the reduce-or-cancel fallback;
  - IOC targets sent last (Phase 8);
  - the removed `IsPendingBuyCancel`/`IsPendingSellCancel` flags and their Phase 7 lock. Part C1 of
    `cpp_alignment_report_2026-09-10.md` is superseded.
  - `ActiveTarget.QuantityBehind`, the IOC skip, hiding a cancel-pending order early, a
    Reduce-only order keeping its queue position, and the one 64-bit ahead/behind read;
  - `Target.TimeInForce`;
  - `Clock.OnException`.

  Detail is in `cpp_alignment_report_2026-10-04.md`.
- **Unknown message types**: `default: break` in ReadAdmin/ReadExecution swallowed a real bug in
  C# (a zeroed `Header::Type` made risk-limit edits silently no-op). At minimum count and expose
  them.

## 6. Verification (add as static_asserts / tests)

- `static_assert` on `sizeof` and every field offset for `RiskLimit`, `OrderState`,
  `AllocateInstrument`, `ServerHeader` (Persistance at 172, sizeof 173), `Header<T>` (4 bytes),
  `OrderRisk` (64 bytes).
- **2026-10-04 layout asserts.** The sizes and field offsets below are asserted by the C#
  RiskHarness layout check (a scratchpad harness, not in the repo), all passing on 2026-10-04. The
  enum values, the `OrderRisk` constants, `OrderTarget` @49 and `OrderRejected` @32 are read from
  `Execution/Order.cs`.
  ```cpp
  static_assert(sizeof(RiskLimit) == 24);   // InstrumentId 4, Timestamp 8, MaxOrderQuantity 16, MaxPositionQuantity 20
  static_assert(sizeof(WorkingRisk) == 16); // Position 4, WorstLongWorkingQuantity 8, WorstShortWorkingQuantity 12
  static_assert(sizeof(OrderRisk) == 64);   // count 0, worst 2, AbsAckedOrderQuantity 4, AbsOrderQuantities 6
  static_assert(OrderRisk::MaxActiveTargets == 29 && OrderRisk::MaxOrderQuantity == 65535);
  static_assert(sizeof(OrderState) == 64);  // OrderHeader 4, ExchangeOrderId 32, OrderProfile 40, QuantityFilled 52, QuantityAhead 56, QuantityBehind 60
  static_assert(sizeof(OrderTarget) == 52); // OrderHeader 4, TriggerTimestamp 32, OrderProfile 40, TimeInForce 48, OrderTargetAction 49
  static_assert(offsetof(OrderRejected, OrderTargetAction) == 32);
  static_assert((uint8_t)OrderType::WorkingRisk == 17 && (uint8_t)OrderType::RiskLimit == 16);
  static_assert((uint8_t)OrderTargetAction::Create == 0 && (uint8_t)OrderTargetAction::Replace == 1
             && (uint8_t)OrderTargetAction::Cancel == 2 && (uint8_t)OrderTargetAction::Reduce == 3);
  ```
- **`OrderDiscarded`** (§1.5) equals exactly the 8-reason bitset {40, 41, 42, 43, 44, 45, 46, 56}.
  Assert it at startup or in a test.
- **OrderRisk raw-byte check** (from the scratchpad harness). Run `TryAdd(7)`, `TryAdd(-9)`, `Ack(7)`. The
  uint16 words are then `[0] = 1` (count), `[1] = 9` (worst in flight), `[2] = 7` (acked) and
  `[3] = 9` (first entry), and `GetAbsWorstOrderQuantity() == 9`.
- **OrderRisk differential test.** This reworks §6 of the 2026-09-10 orderrisk report. The
  reference model is an in-flight list capped at 29, plus an `acked` scalar:
  - `Ack(q)` removes one entry and sets `acked = |q|`. `Reject` only removes.
  - After every op, assert `GetAbsWorstOrderQuantity() == max(acked, max(list))` and
    `IsFull() == (list.size() == 29)`.
  - The 30th `TryAdd` fails with `TooManyActiveTargets`.
  - `TryAdd` of `{0, 65536, INT_MAX, INT_MIN}` fails with `QuantityNotValid`.
  - A zeroed row returns 0.
  - The 2026-09-10 op counts are superseded.
- **Array ids.** A server context creates ids 0–16 in the §1.10 order (`WorkingRisks` == 15,
  `ServerPositionHeaders` == 16). A client context creates 0–15. Assert the count and the names
  at startup.
- **`IsReduceOf` truth table.** The other profile is (ticks 100, +10):
  - (100, +9) → true;
  - (100, +10) → false;
  - (101, +5) → false;
  - (100, −5) → false;
  - (100, 0) → false.
- **Risk invariants.** Assert these in tests or in a debug build:
  - after every hook, `WorstLong >= 0` and `WorstShort <= 0` on every `WorkingRisk` row;
  - once an order is Done with fills that add up to its `QuantityFilled`, the sum of its applied
    deltas is 0;
  - the server's `WorkingRisk.Position` equals the server-wide position row `Quantity` after
    every fill event;
  - a resent fill event (same cumulative `QuantityFilled`) changes no row;
  - a reject sourced from the RiskLayer's own side, or with `Action == Cancel`, changes no row;
  - `GetAbsAllowedOrderQuantity`: with room −3, `|w|` = 1 and worst 5, it returns 5 when
    `isWithinLimit` is false and 2 when it is true. For `|w|` = 2 and room −3 the within-limit
    form gives `floorDiv(−3, 2) = −2`, so it returns 3.
- **Pause gate.** A non-discarded reject of a manual order (`ClientId != StrategyId`) leaves
  `AlgoStatus` unchanged on both paths (`Reject` and `ReadExecution`). C# harness case G2c (a
  scratchpad case, not shipped in the repo) covers the `Server::Reject` path: the server refuses a
  manual create past `MaxPositionQuantity` and the algo stays Live. The `ReadExecution` path is
  covered by inspection only.
- **Reduce to the venue (C++-only, no C# harness counterpart).** A `Reduce` (action 3) from a C#
  client reaches the venue as a modify, encoded exactly like a `Replace` (on iLink,
  `OrderCancelReplaceRequest` at the same price with the smaller `OrderQty`), and never hits a
  default or throw branch (§1.9).
- **Spread refusal.** A client asking for a butterfly (or a weight-2 calendar) throws before
  anything reaches the server, and the server keeps trading. C# harness case G9 (scratchpad, not
  in the repo) covers this.
- Cross-process file round-trip: a `.risklimit` / `.position` line written by C# parses in C++ and
  vice versa (glaze ↔ System.Text.Json).
- Numeric parity: `OrderStateReason`, `OrderStateStatus`, `OrderRejectedReason`,
  `OrderType`/`AllocateType`/`ControlType` header bytes.
- The C# `Simulator` is C#-only; nothing in it needs porting. This includes the 2026-10-02 changes:
  per-instrument exchange-to-client queues (market data and execution reports kept apart), the
  `CrossMask` decay driven by same-side book activity, `MaskCrossed` now gating `CrossMask`, and
  decay remainders zeroed once Ghost or `CrossMask` reaches 0. See Simulator/spec.txt.
  Also C#-only (2026-10-04):
  - IOC and marketable orders publish queue 0/0 through `OnQueuePosition`; an IOC's remainder is
    `Eliminated` right after its fills.
  - The simulator's Create state copies `TimeInForce`.
  - A refused target changes nothing: the reasons are evaluated before the cancel path, and the
    seq is overwritten only after the `SeqOutOfOrder`, `NotInSession`, `StrategyIdNotValid` and
    `InstrumentIdNotValid` checks. An amend refused later for `TargetIsActive`
    or `SideNotValid` leaves the order unchanged but its stored seq advanced.
  - The Init thread's pre-clock `ReadAdmin` loop catches exceptions and reports them through
    `Clock.OnException`.
- `SideByPrice64.Quantity` equals the sum of the active levels after every `TrySetQuantity` (C# and C++).

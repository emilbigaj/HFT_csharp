# C++ alignment report — 2026-10-04: client-side risk copy, RiskLimit/WorkingRisk split, Reduce, duplicate-fill drop

This handoff is for the engineer or agent aligning the C++ server (and the C++ `Client`) with the C#
change set finished on 2026-10-04. It is self-contained: you should not need the C# history. C# is
the source of truth. Every size, offset, enum value and line reference below was checked against
the C# working tree on 2026-10-04. The scratchpad harness `Layout.cs` asserts the struct sizes and
offsets independently (all PASS).

Read it in section order: §1 is the wire, and everything else depends on it.

---

## 0. Scope, and deploy in lockstep

**What changed, in one paragraph.** `RiskLayer` now runs the same code on the server and on every
client. Each side keeps its own copy of the risk rows (`OrderRisk`, and a new `WorkingRisk`), so a
client can clip its own targets to what the server will accept. `RiskLimit` became config only. The
live numbers (position plus worst-case working reservations) moved to the new 16-byte
`WorkingRisk` row. `OrderRisk` now stores the acked quantity itself. `OrderTargetAction.Amend` was
renamed `Replace`, and a new `Reduce = 3` was added. The server drops resent fills, and it no longer
pauses an algo when a manual order is refused.

**WIRE CHANGE. Deploy C# and C++ together.** These change in this set:

| What | Before | After |
|---|---|---|
| `sizeof(RiskLimit)` | 32 | 24 |
| `OrderRisk` layout (still 64 B) | count, worst, 30 × u16 @4 | count, worst, acked @4, 29 × u16 @6 |
| `OrderType` | ..16 | + `WorkingRisk = 17` |
| `OrderTargetAction` | Create 0, Amend 1, Cancel 2 | Create 0, Replace 1, Cancel 2, Reduce 3 |
| Shared array ids | `ServerPositionHeaders` = 15 | `WorkingRisks` = 15, `ServerPositionHeaders` = 16 |
| `OrderRisks` region | `<server>/OrderRisks` only | `<contextDir>/OrderRisks`, one per context |

A mixed build breaks in a different way in each direction. The TCP mirror writes rows by array id:
- Old → new is silent. The old peer's `ServerPositionHeaders` rows under id 15 land, truncated, in
  `WorkingRisks`.
- New → old throws on a C# receiver. A 16-byte `WorkingRisk` row under id 15 is too short for the
  old `PositionHeader` array (`SharedArray.Write` length check), and id 16 does not exist there
  (`Mirror` indexes a dictionary by id). A C++ receiver without those checks corrupts memory instead.
- Old readers of shared memory are silent. An old reader of `OrderRisk` (still 64 B) would take the
  acked quantity for the first in-flight entry. An old reader of `RiskLimit` would misread the
  24-byte audit record.

**Baseline.** "Before" means the C# tree at commit 9155763. That is the target state of
`cpp_alignment.md` and of the 2026-09-08, 09-10, 09-14 and 09-22 reports. Apply those first, or
alongside this one. The C++ tree as of 2026-10-04 has not reached that baseline: `RiskLimit` (no
`Timestamp`, still the `RateLimit` fields), `OrderTarget` (no `TimeInForce` byte),
`OrderTargetAction` (still `Amend`) and `TimeInForce` (no `Day = 0`) still differ. So lines such as "`sizeof(RiskLimit)` 32 → 24" or "Unchanged structs: `OrderTarget` (52)"
describe the C# history, not today's C++ tree. The static asserts in §6.1 are the acceptance check
for the combined result.

**Not in scope.** These are already handed off. Reference them, but do not re-port them:
- `OrderState.QuantityBehind` @60 (64-byte `OrderState`) and the single 64-bit
  `QuantityAhead`/`QuantityBehind` store in `OnQuantityAhead`: `cpp_alignment.md` §5 and `patch_log.md`
  2026-09-26.
- Simulator `MaskMade`/`PriorityId`/`CrossMask`/per-instrument queue channels, TickHistory
  `PriorityId`, SIGHUP, `SideByPrice64.Quantity`, `PositionsWidget`: `patch_log.md`,
  `Simulator/spec.txt`, `cpp_alignment.md` §5.

**Earlier C++ notes this report supersedes where they conflict:**
- `cpp_alignment.md` was updated in step with this report (§1.1, §1.7–1.10, §3, §5). Where this
  report and it differ, this report wins.
- `cpp_alignment_report_2026-09-10_orderrisk.md`: its layout, API and differential test.
- `cpp_alignment_report_2026-09-22.md` Contract 1: the code block and its "leaks permanently" text.
- `cpp_alignment_report_2026-09-10.md` Part C1 (the per-side pending-cancel lock, now removed).

---

## 1. Wire and shared-memory changes

### 1.1 `OrderType` (uint8): add `WorkingRisk = 17`

`Execution/Order.cs:11-21`:

| Name | Value |
|---|---|
| OrderState | 10 |
| OrderTarget | 11 |
| OrderRejected | 12 |
| Fill | 13 |
| Position | 14 |
| AheadOfOrder | 15 |
| RiskLimit | 16 |
| **WorkingRisk** | **17 (new)** |

`Header<T>` is 4 bytes: a 1-byte type at offset 0 and 3 reserved bytes. Any first-byte dispatcher
over shared rows or mirror packets must accept 17. No C# switch handles 17 explicitly: the mirror
receiver sends it to the `default:` path (see §1.8).

### 1.2 `RiskLimit`: 24 bytes, config only

`Execution/Order.cs:182-218`, pack 1:

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | `Header<OrderType> Header` | Type = `RiskLimit` (16) |
| 4 | 4 | `int32 InstrumentId` | |
| 8 | 8 | `Timestamp Timestamp` | default `Timestamp::MinValue` |
| 16 | 4 | `int32 MaxOrderQuantity` | |
| 20 | 4 | `int32 MaxPositionQuantity` | |

```cpp
static_assert(sizeof(RiskLimit) == 24);
static_assert(offsetof(RiskLimit, InstrumentId) == 4);
static_assert(offsetof(RiskLimit, Timestamp) == 8);
static_assert(offsetof(RiskLimit, MaxOrderQuantity) == 16);
static_assert(offsetof(RiskLimit, MaxPositionQuantity) == 20);
```

- **Removed:** `WorstLongWorkingQuantity` (was @24) and `WorstShortWorkingQuantity` (was @28).
- **Helpers** now take the `WorkingRisk` row and are `const`:
  - `GetLongQuantityAllowance(const WorkingRisk& w) = max(0, MaxPositionQuantity - w.Position - w.WorstLongWorkingQuantity)`
  - `GetShortQuantityAllowance(const WorkingRisk& w) = min(0, -MaxPositionQuantity - w.Position - w.WorstShortWorkingQuantity)`
- **Unchanged:** `GetMaxLimits`/`GetMinLimits`.
- **Unchanged:** the row is still server-wide and single-writer: region `<server>/RiskLimits`,
  `InstrumentIds.Length` rows, edited only through `ControlRiskLimit` on the CoreGroup thread
  (`Server.cs:274-287`).
- **Unchanged:** the row is posted to the server's audit socket after an edit, and the logging server
  appends it to `.risklimit`. Any logging or audit decoder keyed on `sizeof(RiskLimit)` must use 24.
- **`.risklimit` JSON** is now `{"Symbol":"<symbol>","Header":{"Type":"RiskLimit"},"InstrumentId":..,"Timestamp":..,"MaxOrderQuantity":..,"MaxPositionQuantity":..}`.
  The logging server inserts `Symbol` as the first key of every line (`LoggingServer.cs:1058-1060`).
  Readers ignore `Symbol` and any other unknown keys, so older lines that still carry
  `WorstLong/ShortWorkingQuantity` or `StrategyId` must still parse, as System.Text.Json does by
  default.

### 1.3 `WorkingRisk`: new, 16 bytes

`Execution/Order.cs:220-234`, pack 1. It holds what `RiskLayer` has applied for an instrument: the
fills it has seen and its worst-case working reservations. **Never persisted** to any file.

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | `Header<OrderType> Header` | Type = `WorkingRisk` (17) |
| 4 | 4 | `int32 Position` | moved by `RiskLayer::OnFill` |
| 8 | 4 | `int32 WorstLongWorkingQuantity` | always >= 0 |
| 12 | 4 | `int32 WorstShortWorkingQuantity` | always <= 0 |

```cpp
static_assert(sizeof(WorkingRisk) == 16);
static_assert(offsetof(WorkingRisk, Position) == 4);
static_assert(offsetof(WorkingRisk, WorstLongWorkingQuantity) == 8);
static_assert(offsetof(WorkingRisk, WorstShortWorkingQuantity) == 12);
```

**Why it has its own `Position`.** The client must see position and reservations change atomically,
together with the order event that caused them. If position came from the position row and
reservations from this row, the two would be out of step between a `Fill` and its `OrderState`.

**Writes are always seq-bumped**: `AcquireLock()`, then the field updates, then `ReleaseLock()`, or a
whole-row `Write()`. That way the TCP mirror ships the change and readers see a row that is not
torn. Each row has a single writer (one thread per context), so the lock costs two volatile stores.

### 1.4 `OrderRisk`: still 64 bytes, now holds the acked quantity

`Execution/Order.cs:236-329`, pack 1, no `Header`.

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 2 | `uint16 ActiveTargetsCount` | 0..29 |
| 2 | 2 | `uint16 WorstOrderQuantity` | max over in-flight entries, 0 when none |
| 4 | 2 | `uint16 AbsAckedOrderQuantity` | **new**: last acked quantity, 0 until the first ack |
| 6 | 58 | `uint16 AbsOrderQuantities[29]` | live at `[0, count)`, zeros after |

```cpp
static constexpr int MaxOrderQuantity = 65535;   // unchanged
static constexpr int MaxActiveTargets = 29;      // was 30
static_assert(sizeof(OrderRisk) == 64);
static_assert(offsetof(OrderRisk, WorstOrderQuantity) == 2);
static_assert(offsetof(OrderRisk, AbsAckedOrderQuantity) == 4);
static_assert(offsetof(OrderRisk, AbsOrderQuantities) == 6);
```

**API.** Port it exactly:

```cpp
int  GetAbsWorstOrderQuantity() const { return std::max<int>(AbsAckedOrderQuantity, WorstOrderQuantity); } // NO argument now
bool IsFull() const { return ActiveTargetsCount == MaxActiveTargets; }   // 29 in flight

bool TryAdd(int q, OrderRejectedReason& reason)        // logic unchanged; the limit is now 29
{
    uint32_t a = (uint32_t)abs_branchless(q);          // INT_MIN stays huge, so the unsigned compare rejects it
    if (a > MaxOrderQuantity || a == 0) { reason = QuantityNotValid; return false; }    // 20
    if (ActiveTargetsCount == MaxActiveTargets) { reason = TooManyActiveTargets; return false; } // 45: refuses the 30th
    AbsOrderQuantities[ActiveTargetsCount++] = (uint16_t)a;
    WorstOrderQuantity = std::max<int>(WorstOrderQuantity, a);
    return true;
}
void Ack(int q)    { Remove(q); AbsAckedOrderQuantity = (uint16_t)abs(q); }  // in range: every acked quantity passed TryAdd
void Reject(int q) { Remove(q); }                                            // does NOT touch the acked quantity
// Remove: unchanged. Scan forward for ONE entry equal to |q| (none -> no-op), swap the last entry into the hole,
// zero the last slot, decrement the count, rescan for the max only if the removed value equalled it.
```

**Write discipline.** `OrderRisk` rows are written by plain reference, with no seq bump (a single
writer per context). Keep it that way in C++: the rows then stay at seq 0, which the mirror reads as
Empty and never ships (§1.8).

**Resets.** On `Create`, `ValidateOrder` resets the row to zero just before `TryAdd`
(`RiskLayer.cs:453-454`). On `Done`, `OnOrderState` sets the row to zero (`RiskLayer.cs:194`).

**Why the acked quantity moved into the row.** Each `RiskLayer` copy, server or client, must be
self-contained. Its worst case must come from the acks *this* `RiskLayer` applied. It must not
come from the shared `OrderState` row, because a client sees that row at a different time, and the
row may still hold the slot's previous order. One in-flight slot was given up to stay at 64 bytes.

**Consequence: an ack that never arrives no longer leaks.** Take an amend whose ack rides inside a
`Fill`, with no separate `Acked`. Its in-flight entry stays in the row, so its worst stays reserved
until `Done`. `Done` then releases exactly what the row holds. Before this change, the old
two-argument form measured the release from the message and leaked the difference permanently. See
§3.4.

### 1.5 `OrderTargetAction` (uint8): `Amend` becomes `Replace`, add `Reduce`

`Execution/Order.cs:331-338`:

| Name | Value | Meaning |
|---|---|---|
| Create | 0 | |
| **Replace** | 1 | was `Amend`, same wire value. A new price or more quantity: loses queue priority |
| Cancel | 2 | |
| **Reduce** | 3 | **new.** Less quantity at the same price: keeps queue priority, never adds risk |

- **Where the byte sits:** `OrderTarget.OrderTargetAction` @49 (`OrderTarget` is 52 B: `OrderProfile`
  @40, `TimeInForce` @48, `OrderTargetStatus` @50) and `OrderRejected.OrderTargetAction` @32.
- **JSON / glaze enum names:** `"Amend"` becomes `"Replace"`; add `"Reduce"`. Audit lines written by C#
  now carry these names.
- **Every C++ branch that tested `== Amend`** must accept `Replace || Reduce` (the amend path),
  with two exceptions: the server rate limit (§3.3 step 7) and the exchange adapter (below).
- **The C# simulator compares only against `Create` and `Cancel`.** It decides priority from the
  price and quantity change, as CME does, not from the action byte. The C# server's `RiskLayer`
  also treats a `Reduce` that `IsReduceOf` the state row as a cancel for the rate limit: counted,
  never refused (§3.3 step 7, `RiskLayer.cs:415-424`).
- **Exchange adapter.** Action 3 (`Reduce`) must be encoded exactly like `Replace` (1). On iLink that
  is `OrderCancelReplaceRequest` with the same price and the smaller `OrderQty`, which CME treats as
  priority-preserving. Never route it to a default or throw branch: by then the server has already
  reserved and counted it, and a throw would reach the CoreGroup loop. Add a test that a `Reduce`
  from a C# client reaches the venue as a modify.
- **C++ sites to change:**
  - `Provider/RiskLayer.hpp:186`, `:198` (`isAmend`, also used at `:204` and `:213`) and `:219`:
    `== Amend` becomes `isReduceOrReplace` (`Replace || Reduce`).
  - `Strategy/Strategy.hpp:143` (`Amend()` hard-codes `OrderTargetAction::Amend`): send
    `orderProfile.IsReduceOf(state.OrderProfile) ? Reduce : Replace`, or plain `Replace`. Choosing
    `Reduce` gives a size-down the rate-limit exemption. This sender goes through the C++ `Client`
    with algo orders, so the §4 client hooks apply to it.

### 1.6 `OrderProfile::IsReduceOf` (new)

`Execution/Order.cs:389-394`:

```cpp
bool IsReduceOf(const OrderProfile& o) const
{ return Ticks == o.Ticks && Sign() == o.Sign() && std::abs(Quantity) < std::abs(o.Quantity); }
```

The quantity must be strictly smaller. The same quantity at the same price is a `Replace`. A
zero-quantity profile (sign 0) is never a reduce of a live order. `OrderProfile` itself is
unchanged: `int32 Ticks` @0, `int32 Quantity` @4, 8 bytes.

### 1.7 Shared arrays: new `WorkingRisks`, `OrderRisks` per context, ids shifted

The array id is the order of creation (`Context.cs:276-284`). The C++ `Context` must create its
arrays in exactly this order:

| Id | Region | Rows | Keyed by | Server opens | Client opens |
|---|---|---|---|---|---|
| 0 | `<server>/ServerHeader` + `LetterBox` (mirror only) | 1 | server | | |
| 1 | `<server>/ClientHeaders` | ClientIds.Length | server | | |
| 2 | `<server>/InstrumentHeaders` | InstrumentsCapacity | server | | |
| 3 | `<server>/InstrumentHeaderIdByInstrumentId` | InstrumentIds.Length | server | | |
| 4 | `<server>/InstrumentIdsByClientId` | ClientIds.Length | server | | |
| 5 | `<server>/ClientIdsByInstrumentId` | InstrumentIds.Length | server | | |
| 6 | `<dir>/MarketsByPrice` | InstrumentIds.Length | **context dir** | serverAccess | clientAccess |
| 7 | `<server>/RiskLimits` | InstrumentIds.Length | server | | |
| 8 | `<server>/MessageEfficiency` | InstrumentIds.Length | server | | |
| 9 | `<server>/RateLimits` | CoreGroupIds.Length | server | | |
| 10 | `<server>/CoreGroups` | CoreGroupIds.Length | server | | |
| 11 | `<server>/OrderStates` | OrdersCapacity (sparse) | server | | |
| 12 | `<dir>/OrderRisks` | OrdersCapacity (sparse) | **context dir (CHANGED)** | serverAccess | clientAccess |
| 13 | `<server>/OrderTargets` | OrdersCapacity (sparse) | server | | |
| 14 | `<server>/LocalPositionHeaders` | LocalPositionsCapacity (sparse) | server | | |
| **15** | **`<dir>/WorkingRisks`** | **InstrumentIds.Length (dense)** | **context dir (NEW)** | serverAccess | clientAccess |
| **16** | `<server>/ServerPositionHeaders` (ServerContext only) | InstrumentIds.Length | server | | |

- **Directories.** `<dir>` is the server name for a server context. For a client context it is the
  client's own directory (`S:\Strategies\{ClockMode}\{name}`). Join with
  `std::filesystem::path::operator/`, never by concatenation (the region name is sanitised, and a
  concatenated name opens a different, empty region).
- **Where the new arrays sit.** `WorkingRisks` is created right after `LocalPositionHeaders`, as the
  **last base-class array**, so ids 0..14 keep their mirror order. The only id that moves is the
  server-only `ServerPositionHeaders`, from 15 to 16. `OrderRisks` keeps id 12; only its region key
  and access changed.
- **Array counts.** A client context has ids 0..15 (16 arrays) and a server context 0..16 (17).
- **Slot stride** is `(64 + sizeof(T) + 63) & ~63`. That is 128 bytes for both `WorkingRisk` (16) and
  `OrderRisk` (64). For `WorkingRisks` with 64 instruments, the region is 8,192 bytes.
- **Index.** `WorkingRisks` is indexed by instrument id; `OrderRisks` by `OrderId.GlobalIndex`.
- **A client can only reach its own rows.** A client context has no view of the server's
  `WorkingRisks` or `OrderRisks`. The C# GUI reads the server's through its `ServerContext`.
- **Accessor.** `Context::GetWorkingRisk(instrumentId)` lives on the base context
  (`Context.cs:367-373`). It range-checks the instrument the same way `GetRiskLimit` does: on a
  client, only instruments allocated to that client pass.
- **Moved accessor.** `GetPositionHeader(clientId, instrumentId)` moved from `ServerContext` to the
  base `Context` (`Context.cs:347-352`), with the same body. It is read-only on a client. This is an
  API-only move.

### 1.8 TCP mirror and audit

- **Packet:** `int32 arrayId LE, int32 index LE, row bytes` (unchanged, `TCPServer.cs` `ContextProxy`).
  Only the `ServerContext`'s arrays are mirrored, and only rows whose seq reads `New` (or any
  non-Empty row in a snapshot, `Context.cs:1254-1263`). The server's `OrderRisks` rows are never
  seq-bumped (§1.4), so they stay Empty (seq 0) to the mirror and are never shipped, not even in a
  snapshot. Only `WorkingRisks` (id 15) carries live risk numbers over the mirror. Client-side risk
  rows are local to the client.
- **A C++ server that feeds a C# mirror or GUI** must emit `ServerPositionHeaders` rows as array id
  **16**, and must emit `WorkingRisk` rows (first byte 17) as array id 15. A `WorkingRisk` row falls
  to the receiver's `default:` and is written blindly by id. Without these rows (seq 0, Empty),
  `Read()` throws and the C# Risk Limits widget never lists that instrument at all; it retries every
  tick. Zero working quantities appear only if a row was written (for example the allocation seed)
  but never maintained afterwards.
- **`WorkingRisk` is never audited.** Nothing writes it to the audit socket or to any file.
- **The `RiskLimit` audit record is 24 bytes** (the logging server sorts it on `Timestamp` @8).
- **Latent, pre-existing, not introduced here:** `OrderRisk` has no `Header`, so a mirrored
  `OrderRisk` row would be dispatched on the low byte of its count. A count of 10..14 collides with
  the `OrderType` values the C# receiver dispatches explicitly (`TCPServer.cs:277-353`): OrderState
  10, OrderTarget 11, OrderRejected 12, Fill 13, Position 14. 12 and 13 are forwarded to a client
  socket instead of mirrored. 15..17 fall to `default:` and are mirrored correctly. This applies only
  if someone starts seq-bumping `OrderRisk` rows; today none is shipped. See §7.

### 1.9 What did NOT change on the wire

- **`OrderRejectedReason`.** Values used in this report: `QuantityNotValid 20`, `SideNotValid 22`,
  `SeqOutOfOrder 31`, `OrderNotFound 35`, `StateIsDone 40`, `CancelIsActive 42`,
  `TargetIsActive 43`, `TargetIsStale 44`, `TooManyActiveTargets 45`, `AlgoIsPaused 46`,
  `NotInSession 50`, `QuantityExceedsRiskLimit 52`, `PositionExceedsRiskLimit 54`,
  `TooManyOrdersPerSecond 56`, `ExceptionThrownByRiskLayer 63`. No `RiskLimitChanged` reason was
  added.
- **`OrderRejected::OrderDiscarded`** = {40, 41, 42, 43, 44, 45, 46, 56}. Verify that the C++ set
  matches; `TooManyActiveTargets` (45) and `TooManyOrdersPerSecond` (56) must be in it.
- **`TimeInForce`:** Day 0, GoodTillCancel 1, ImmediateOrCancel 2, FillOrKill 3, OpeningAuction 4,
  ClosingAuction 5.
- **Unchanged structs:** `OrderState` (64), `OrderTarget` (52), `OrderHeader` (28), `Fill` (64).
- **Senders now set `TimeInForce` on every target.** The C# Algo copies `TimeInForce` onto every
  `Create` and amend. C# clients now send `ImmediateOrCancel` creates, which the C++ server must
  honour and pass through to the venue. C# copies `OrderTarget.TimeInForce` into the PendingNew
  state (`Server.cs:434`). The C++ tree does not yet: its `OrderTarget` has no `TimeInForce` byte,
  its `OrderState` has none either, its PendingNew initialiser in `Server::OnOrderTarget`
  (`Server.hpp:413-424`) sets none, and its `TimeInForce` enum lacks `Day = 0`, so its numbering
  is off by one. All of this comes from `cpp_alignment_report_2026-09-08.md` A2 and
  `cpp_alignment.md` (`OrderTarget` @48 `TimeInForce`, sizeof 52), and must land before IOC
  creates from C# clients can be honoured.

---

## 2. Server behaviour changes

Each item gives the rule, the C# reference and C++ pseudo-code.

### 2.1 Drop a resent fill whole (`Server.cs:551-553`)

**Rule.** If a fill event's cumulative `QuantityFilled` is not larger in magnitude than the order
row's, it is a retransmission (iLink `PossRetransFlag`). Drop the entire event. This check runs
after the unknown-order check and **before** anything else. Do not stamp, lock, write state, move
any position, call the risk layer, forward or audit. The C# side neither logs nor counts the drop.

```cpp
void Server::OnFill(OrderState& state, std::span<Fill> fills)
{
    const OrderState& row = orderStates[state.OrderHeader.OrderId.GlobalIndex()];
    if (row.OrderHeader.OrderId != state.OrderHeader.OrderId) throw ...;            // unchanged
    if (std::abs(state.QuantityFilled) <= std::abs(row.QuantityFilled)) return;      // NEW: duplicate
    ...
}
```

The comparison uses magnitudes because `QuantityFilled` is signed (negative for sells). For a spread
order, `row` is the spread's own row. Its fills carry the leg instrument ids.

**Example.** A buy has filled 3 (`row.QuantityFilled = 3`). An event with `QuantityFilled = 5` and one
2-lot fill is applied. The same event is then resent (`QuantityFilled = 5` again): 5 <= 5, so it is
dropped. Before this change, the resend moved both position rows and released risk a second time.

**This depends on two assumptions, not yet confirmed with CME (§7):**
- (a) the session delivers one order's fills in order;
- (b) the adapter always sets `OrderState.QuantityFilled` to the cumulative quantity (`CumQty`, tag
  14, In-Flight Mitigation on), retransmissions included.

`WriteOrderState` keeps the larger-magnitude `QuantityFilled` from **any** state. So if a non-fill
state (for example a terminal `Canceled`) ever carried a `CumQty` that included a fill not yet
delivered, that later fill would be dropped. Assumption (a) covers this. The fallback, if CME says
otherwise, is a recent-set keyed by `FillId` (ExecID) on both sides. Consider counting drops in C++.

### 2.2 A manual order's reject never pauses the algo (`Server.cs:406-415`, `:219-226`)

**Rule.** Both server pause sites now pause only for an algo order (`OrderId.IsAlgoOrder()`, which is
`ClientId == StrategyId`).
- **`Server::Reject`:** the order of steps is unchanged. Always forward, then return if discarded,
  then pause only if it is an algo order, then always raise the reject event or alert.
- **The `OrderRejected` case in `ReadExecution`** (a reject a client writes to the server) is gated
  the same way.

```cpp
void Server::Reject(const OrderRejected& r, ...)
{
    WriteToExecution(r.OrderHeader, r);                 // always forwarded
    if (IsDiscarded(r)) return;
    if (r.OrderHeader.OrderId.IsAlgoOrder())            // NEW gate
        OnControlAlgoStatus(r.OrderHeader.OrderId.StrategyId, r.OrderHeader.OrderId.InstrumentId, AlgoStatus::Paused);
    RaiseOrderRejected(r, msg);                         // still raised for manual orders
}
// ReadExecution, case OrderType::OrderRejected:
if (r.OrderHeader.OrderId.IsAlgoOrder()) OnControlAlgoStatus(..., AlgoStatus::Paused);
```

**Why.** A manual (GUI) order is booked to the algo's strategy, but it is not the algo's order. Spec.md
already says that manual orders get no pause gate. The user accepted that a manual order may use
room the algo cannot see, so the algo's own order may then be refused and pause (B6). Only the
manual order's own refusal is kept from pausing.

### 2.3 `RiskLayer::OnFill` for every fill, legs and the spread row alike (`Server.cs:585-592`)

**Rule.** Call `_riskLayer.OnFill(fill)` for every fill in the event, with no `IsLegged` guard at the
call site. `RiskLayer::OnFill` always moves `WorkingRisk.Position`. It releases only for
non-legged instruments (§3.5). The position on the spread's own row is accounting only.

```cpp
for (const Fill& f : fills) {
    serverPos(f).OnFill(f, mult); strategyPos(f).OnFill(f, mult);
    _riskLayer.OnFill(f);           // was: if (!instrument.IsLegged) _riskLayer.OnFill(f);
}
```

The call order inside `OnFill` is unchanged: lock the position rows in fills order, then
`WriteOrderState` (which may run the `Done` release, §2.4), then per fill: server position, then
strategy position, then `RiskLayer::OnFill`. Then release the locks and forward. Per-fill releases
and the `Done` remainder add up to exactly the reserved worst, in either order.

### 2.4 `WriteOrderState` calls `OnOrderState(row)`, with no captured acked quantity (`Server.cs:357-384`)

**Rule.** Delete the `beforeAckedOrderQuantity` capture. After the seq-bumped write, and only when
the overwrite was applied (`isSafeToOverwrite`), call `_riskLayer.OnOrderState(mergedRow)`.

```cpp
if (isSafeToOverwrite) {
    entry.AcquireLock(); /* copy seq, ExchangeOrderId, profile, status, reason, max-magnitude QuantityFilled, timestamps */ entry.ReleaseLock();
    _riskLayer.OnOrderState(row);   // was OnOrderState(row, beforeAcked)
}
```

### 2.5 Exchange rejects: the call site is unchanged, `RiskLayer` gained a guard (`Server.cs:387-404`)

`Server::OnOrderRejected` keeps the existing rule: a lone `OrderNotFound` on a `Done` row is relabelled
`StateIsDone`, and the reject is processed only when the row is this order. It still calls
`_riskLayer.OnOrderRejected(r)`, which now also returns early for a rejected `Cancel` (§3.6).
Rejects that the server's own `ValidateOrder` produced never reach `RiskLayer::OnOrderRejected`. If
one did, it would return early, because the source equals its own side.

### 2.6 Instrument allocation seeds `WorkingRisk` (`Context.cs:1053-1066`)

**Rule.** On first allocation in `ServerContext::AllocateInstrument`:
1. Restore `RiskLimit` from `.risklimit`, or Max (simulation) / Min (realtime). The two lines that
   zeroed `Worst*` on `RiskLimit` are gone, because the fields no longer exist.
2. Restore the server-wide position row.
3. Write the server's `WorkingRisk` row: `{Position = that row's Quantity, 0, 0}`, as a seq-bumped
   `Write`.

An instrument that is already allocated returns early and does not rewrite the row. Allocating
for a client (`AllocateInstrument(clientId, instrumentId)`) writes no `WorkingRisk`. After this
point the server's `WorkingRisk.Position` moves only in `RiskLayer::OnFill`, separately from the
server position row. The two must stay equal (§6 invariant I4a).

### 2.7 Read loops never die (`Server.cs:186`, `:246`)

**Rule.** Every iteration of a CoreGroup loop (`ReadFromIlink` and `ReadExecution`) and of the admin
loop (`ReadAdmin`) must be wrapped in try/catch. Report to the alert path and keep polling. An
exception must never end the process. These paths are known to throw:
- `OnAllocateInstrument`: `clientId` or `instrumentHeaderId` out of range;
- `CreateInstrument`: missing spread legs;
- `Server::OnFill`: an unknown `clientOrderId`, or a fill that does not belong to the order.

```cpp
for (;;) { try { server.ReadAdmin(); } catch (const std::exception& e) { alerts.OnException(e); } cpu_pause(); }
```

Why: a butterfly check that was briefly placed in `Context.CreateInstrument` threw on the admin thread
and killed the whole C# server process.

### 2.8 The server does not check spread shape

**Do not throw** in the C++ server's `CreateInstrument` or admin path for an unsupported spread. The
two-leg +1/−1 check lives on the client (§4.6). `CreateInstrument` still assigns the long leg to
any positive weight and the short leg to any negative weight, so on a butterfly the last leg of each
sign would silently win. That is why the client must refuse such a spread before it asks for it.

### 2.9 Sequence numbers: lower is stale; equal or greater passes

C# GUIs now number a cancel of an **algo** order as existing target seq + 1,000,000, as
`Server::CancelAllOrders` already does. A manual order's amend is numbered existing seq + 1. The
C++ server must keep its rule that a target is refused for seq only when the owner's existing
target row has a **higher** seq (`existingTarget.Seq > target.Seq` → `TargetIsStale`, 44,
discarded; `RiskLayer.cs:350-351`). An equal seq must pass, because the owning client writes its
target row with the same seq before it sends. Gaps pass too: do not require `prev + 1`. The `>=`
test (`SeqOutOfOrder`, 31) belongs to the client's own check (`RiskLayer.cs:373-374`) and to the
venue/simulator, which refuses a seq that is not above the order's stored seq.

**Example (harness F3).** An algo order has target seq 7. The GUI cancels it with seq 1,000,007,
and in the same instant the algo amends it with seq 8. The GUI writes no algo target row, so the
server checks each one against the algo's own row, and both pass. At the venue, the order of
arrival decides:
- Cancel first: the order is gone when the amend arrives. The venue answers `OrderNotFound` on a
  `Done` row, which the server relabels `StateIsDone` (discarded).
- Amend first: the cancel's seq is still higher, so it applies.

Either way nothing comes back `SeqOutOfOrder` (31, which is not discarded). Before this change each
widget applied its own ad-hoc offset to the **state** seq (cancels at +1,000,000 on the Ladder,
+10,000 + age in seconds on the Orders grid and +1000 on SendOrder; SendOrder's amend at +1), and
`ManualClient.Amend` numbered `max(existing target seq + 1, caller seq)`. So only a direct
`ManualClient` caller without an offset (such as the harness) sent the cancel at seq 8, colliding
with the algo's amend: one of them got `SeqOutOfOrder`, and the algo paused. Now all numbering is in
`ManualClient.Amend`.

### 2.10 Cancel quantity from C# algos

A `Cancel` `OrderTarget` from a C# algo, including its paused-branch `CancelAllOrders`, now carries
`Quantity = working + filled` (non-zero, signed by side) at the active target's price (`Algo.cs`
`NewAmend(active, active.Target.Ticks, 0)`). That is the acked profile once the state is current;
otherwise it is the latest in-flight target's profile (`Position.cs:345`). Before, it carried
`Quantity = filled`, which was often 0 and so had no side. The C++ server must accept this. It
reserves nothing for a cancel and only counts it against the rate limit.

---

## 3. `RiskLayer`: the algorithm to port exactly

The class is constructed over a **context** (`RiskLayer.cs:24-28`): the server's context with source
`Server`, or a client's own context with source `Client`. All reads and writes of `OrderRisk` and
`WorkingRisk` go to *that* context's rows. `OrderStates`, `OrderTargets`, `RiskLimits`,
`RateLimits` and `LocalPositionHeaders` are server-wide and shared. The server-only early returns
at the top of `OnOrderState`, `OnFill` and `OnOrderRejected` were **removed**. The same code runs on
both sides.

**Sign convention (unchanged).** All quantities are signed: buys and longs positive, sells and shorts
negative. `GetAbsWorstOrderQuantity` returns a magnitude. The sign is applied **once**, in
`ApplyWorstWorkingQuantityDelta`. `side == Buy ? 1 : -1`, so a zero-quantity profile falls on the
short side.

### 3.1 `ApplyWorstWorkingQuantityDelta` (`RiskLayer.cs:146-164`)

```cpp
void ApplyWorstWorkingQuantityDelta(OrderId id, int orderSideSign, int magnitudeDelta)
{
    if (magnitudeDelta == 0) return;
    for (const InstrumentLeg& leg : ctx.GetInstrument(id.InstrumentId).Legs())   // outright: one leg {itself, +1}
    {
        int legSide = orderSideSign * sign(leg.Weight);
        int legDelta = magnitudeDelta * std::abs(leg.Weight);
        auto& e = ctx.GetWorkingRisk(leg.InstrumentId);
        e.AcquireLock();                                   // NEW: seq-bumped (was a plain write into RiskLimit)
        e.Ref().WorstLongWorkingQuantity  += legSide > 0 ? legDelta : 0;
        e.Ref().WorstShortWorkingQuantity -= legSide < 0 ? legDelta : 0;
        e.ReleaseLock();
    }
}
```

A spread instrument's `Legs` are always `{long, +1}, {short, −1}`. A buy of the calendar reserves the
front leg long and the back leg short.

### 3.2 `GetAbsAllowedOrderQuantity` (new, `RiskLayer.cs:238-264`)

This returns the largest |order quantity|, filled included, that the order may carry and still pass
the position check: its current worst plus the room left on every leg, in order units.

```cpp
int GetAbsAllowedOrderQuantity(const OrderTarget& t, bool isWithinLimit = false)
{
    int worst = t.OrderTargetAction == Create ? 0                       // a Create's row still holds the slot's previous order
              : ctx.GetOrderRisk(t.OrderHeader.OrderId).GetAbsWorstOrderQuantity();
    int sign = t.OrderProfile.Sign();
    int64_t allowed = INT32_MAX;
    for (const InstrumentLeg& leg : ctx.GetInstrument(t.OrderHeader.OrderId.InstrumentId).Legs())
    {
        int legSide = sign * sign(leg.Weight);
        const RiskLimit&   rl = ctx.GetRiskLimit(leg.InstrumentId).ReadonlyRef();
        const WorkingRisk& wr = ctx.GetWorkingRisk(leg.InstrumentId).ReadonlyRef();
        int64_t room = legSide > 0 ? (int64_t)rl.MaxPositionQuantity - wr.Position - wr.WorstLongWorkingQuantity
                                   : (int64_t)rl.MaxPositionQuantity + wr.Position + wr.WorstShortWorkingQuantity; // legSide 0 lands here
        int64_t w = std::abs(leg.Weight);
        int64_t roomOrders = room >= 0 ? room / w
                           : isWithinLimit ? -((w - 1 - room) / w)     // = floor(room / w), i.e. -ceil(overshoot / w)
                           : 0;                                        // past the limit: may keep its worst, never grow
        allowed = std::min(allowed, (int64_t)worst + roomOrders);
    }
    return (int)allowed;   // can be negative only when isWithinLimit is set
}
```

- **The arithmetic must be 64-bit.** `GetMaxLimits` sets `MaxPositionQuantity = INT32_MAX`. The
  `INT32_MAX` seed keeps the result in `int` range.
- **Floor rounding for negative room.** C++ and C# integer division both truncate toward zero, so do
  not write `room / w` for a negative room. Use the expression above. Example with a weight of 2
  (no current spread has one, but port the general form): room −3 gives −((2−1+3)/2) = −2 =
  floor(−1.5). Plain `−3/2` would give −1, which is wrong.
- **The room already counts this order's own worst.** So `|q| <= worst + floor(room/w)` is the same
  test as "TryAdd's leg delta `(|q| − worst)·w` fits in room".

**Example.** `MaxPositionQuantity = 10`, `Position = 6`, and this order is a buy acked at 7, so
`WorstLong = 7` and its worst is 7. Room = 10 − 6 − 7 = −3. ValidateOrder (`isWithinLimit = false`)
gets allowed = 7 + 0 = 7: keeping 7 or cutting passes, and growing to 8 is refused. The best-effort
clip (`isWithinLimit = true`) gets allowed = 7 + (−3) = 4. After its ack, worst = 4, so
Position + WorstLong = 6 + 4 = 10, exactly the limit.

**Position source.**
- On the server, this is the server-wide instrument position.
- On a client, it is the client's own strategy position, and the client sees only its own
  reservations. Other strategies' orders and manual orders are invisible to the client (B6,
  accepted).

### 3.3 `IsWithinRiskLimit` and the order of checks in `ValidateOrder` (`RiskLayer.cs:266-271`, `:297-476`)

```cpp
bool IsWithinRiskLimit(const OrderTarget& t) { return std::abs(t.OrderProfile.Quantity) <= GetAbsAllowedOrderQuantity(t); }
```

`ValidateOrder` must report reasons in this exact order, so that the `IsDiscarded` classification and
the pause behaviour match:

1. Load the instrument, then the target row and state row (both by `GlobalIndex`).
2. **Create:**
   - `ValidateInstrument`: `InstrumentIdNotValid` (return at once), `InstrumentNotAllocated`,
     `NotInSession` if `TradingStatus != Open`. Return if any.
   - `ValidateClient`: `ClientIdNotValid`, `ClientIdNotAllocated`, `StrategyIdNotValid`,
     `StrategyIdNotAllocated`. Return if any.
   - `ValidateCreate`: `SeqOutOfOrder` if seq != 1; on the client only, `ClientOrderIdOutOfOrder`;
     `OrderIndexIsBusy` if the state row is Active. Return if any.
3. **Not Create**, with `isReduceOrReplace = (action == Replace || action == Reduce)`:
   - **Server branch:**
     - `ValidateOrderHeader(state header, target header)`; return on any error.
     - State Done → `StateIsDone`.
     - `isReduceOrReplace && state.Seq + 1 == target.Seq && state.Profile == target.Profile` →
       `TargetIsActive`.
     - `existingTarget.Seq > target.Seq` → `TargetIsStale`.
     - `isReduceOrReplace && state.Side != target.Side` → `SideNotValid`.
   - **Client branch:** see §4.2.
4. A non-cancel algo order whose strategy's local position row has `AlgoStatus == Paused` →
   `AlgoIsPaused`, return false.
5. On a client, if the order is not an algo order: `return reasons.IsEmpty()`. This step makes no
   reservation and does no rate limiting.
6. If any reason is set, return false.
7. **Server only: rate limit.**
   ```cpp
   bool isReduce = action == Reduce && target.OrderProfile.IsReduceOf(state.OrderProfile);   // verified against the STATE row
   if (isCancel || isReduce) rate.SendOrder(now);                  // counted, never refused
   else if (!rate.TrySendOrder(now)) { set(TooManyOrdersPerSecond); return false; }
   ```
   The server's state row holds the last state the exchange reported. Amends do not overwrite it;
   only a `Create` initialises it.

   **Example.** The state row is acked at (100, 10). The algo sends a `Replace` to (101, 12), which
   is still unacked. Then it sends a `Reduce` to (101, 8). Against the state (100, 10) the ticks
   differ, so `IsReduceOf` fails and the Reduce is throttled like any Replace. A mislabelled Reduce
   can therefore never skip the throttle.
8. **Non-cancel: risk block.** These are the steps in order. Nothing has to be backed out.
   ```cpp
   int filled  = state.OrderId == target.OrderId ? state.QuantityFilled : 0;
   int working = target.Quantity - filled;
   for (leg : instrument.Legs())                                                 // (a) per-leg order size, LEG units
       if (std::abs(working * leg.Weight) > RiskLimit(leg).MaxOrderQuantity) { set(QuantityExceedsRiskLimit); return false; }
   if (!IsWithinRiskLimit(target)) { set(PositionExceedsRiskLimit); return false; }   // (b) pure, writes nothing
   OrderRisk& r = ctx.GetOrderRisk(id);
   if (action == Create) r = OrderRisk{};                                        // (c)
   int before = r.GetAbsWorstOrderQuantity();
   if (!r.TryAdd(target.Quantity, reason)) { set(reason); return false; }        // (d) QuantityNotValid / TooManyActiveTargets
   ApplyWorstWorkingQuantityDelta(id, target.Sign(), r.GetAbsWorstOrderQuantity() - before);   // (e)
   ```
9. On any exception, set `ExceptionThrownByRiskLayer` and log it. Return `reasons.IsEmpty()`. A
   `Cancel` never makes a reservation.

**What changed against the old C++ notes:**
- The position check used to be a tentative add per leg, followed by `Reject()` to back it out on a
  breach. It is now check first (b), then `TryAdd` and commit (d, e).
- Past the limit, an order that does not grow its worst now **passes**. Before, an over-limit
  long-side row refused even a zero-delta cut, for example after an operator lowered the limit.
- `PositionExceedsRiskLimit` (not discarded, so it pauses) is now reported before
  `TooManyActiveTargets` (discarded). A 30th amend that also breaches the limit is therefore
  reported as `PositionExceedsRiskLimit`.
- A non-cancel that the risk block later refuses has still used a rate slot. This is unchanged.

### 3.4 `OnOrderState(state)`: no second argument (`RiskLayer.cs:166-198`)

```cpp
void OnOrderState(const OrderState& s)
{
    int side = s.OrderProfile.Side() == Side::Buy ? 1 : -1;
    if (s.OrderStateReason == OrderStateReason::Acked)            // tested FIRST
    {
        OrderRisk& r = ctx.GetOrderRisk(s.OrderHeader.OrderId);
        int before = r.GetAbsWorstOrderQuantity();
        r.Ack(s.OrderProfile.Quantity);                           // retires one in-flight entry AND records the acked quantity
        ApplyWorstWorkingQuantityDelta(s.OrderHeader.OrderId, side, r.GetAbsWorstOrderQuantity() - before);
    }
    else if (s.OrderStateStatus == OrderStateStatus::Done)
    {
        OrderRisk& r = ctx.GetOrderRisk(s.OrderHeader.OrderId);
        int released = r.GetAbsWorstOrderQuantity() - std::abs(s.QuantityFilled);
        r = OrderRisk{};
        ApplyWorstWorkingQuantityDelta(s.OrderHeader.OrderId, side, -released);
    }
}
```

**Invariant.** For each order, the sum of everything applied is:
reserved worst − per-fill releases − (worst at Done − |QuantityFilled|).
That is zero whenever the fills this `RiskLayer` saw add up to the `Done`'s `QuantityFilled`, whether
or not every amend was acked. On the server, the duplicate-fill drop (§2.1) keeps this true.

**Acceptance before trade (2026-09-22 Contract 1) is still the contract.** CME and the simulator send
`Acked` before the trade. If the contract is violated, the reservation is now **too large until
`Done`**, and the stale entry occupies one of the 29 in-flight slots. It no longer leaks. The adapter
must still preserve the order, so that reservations shrink at the ack. Do not add tolerance to
`RiskLayer`. A state that is both `Done` and `Acked` would take only the `Acked` branch.

An acked amend down now releases the difference **at the ack**. The old "known-open: releases at
Done" note in `cpp_alignment.md` §3 was deleted.

### 3.5 `OnFill(fill, isReserved = true)` (`RiskLayer.cs:201-217`)

```cpp
void OnFill(const Fill& f, bool isReserved = true)
{
    int iid = f.OrderHeader.OrderId.InstrumentId;
    auto& e = ctx.GetWorkingRisk(iid);
    e.AcquireLock(); e.Ref().Position += f.Quantity; e.ReleaseLock();       // EVERY fill, signed
    if (!isReserved || ctx.GetInstrument(iid).IsLegged()) return;          // the spread row: accounting only
    ApplyWorstWorkingQuantityDelta(f.OrderHeader.OrderId, f.Sign(), -std::abs(f.Quantity));
}
```

Leg fills carry the leg's instrument id and the leg quantity (spread quantity × weight), so they
release the leg rows directly. The server always passes `isReserved = true`.

### 3.6 `OnOrderRejected(r)` (`RiskLayer.cs:219-235`)

```cpp
void OnOrderRejected(const OrderRejected& r)
{
    if (r.OrderRejectedSource == ownSource || r.OrderTargetAction == OrderTargetAction::Cancel) return;  // Cancel guard is NEW
    OrderRisk& o = ctx.GetOrderRisk(r.OrderHeader.OrderId);
    int before = o.GetAbsWorstOrderQuantity();
    o.Reject(r.OrderProfile.Quantity);                                     // removes one matching entry, or nothing
    ApplyWorstWorkingQuantityDelta(r.OrderHeader.OrderId, r.OrderProfile.Side() == Side::Buy ? 1 : -1,
                                   o.GetAbsWorstOrderQuantity() - before);
}
```

- **A reject from this `RiskLayer`'s own side reserved nothing here.** It was refused before or at its
  own `TryAdd`, without a commit.
- **A cancel never reserves.** Its profile is working + filled at the active target's price (§2.10).
  While a `Replace` or `Reduce` is in flight and unacked, that is the in-flight target's own profile,
  so the cancel's quantity can equal an in-flight entry exactly. Without the guard, a rejected cancel
  would remove that entry wrongly. That is why the `Cancel` guard is needed.
- **The state row is no longer read here.**
- **A late reject after `Done` finds a zeroed row**, so the delta is 0.

### 3.7 `TryClipToRiskLimit(target&)` (new, `RiskLayer.cs:273-295`)

This is used only by the C# best-effort `Algo::TryTarget`. It is not needed on the C++ server. Port
it only alongside a C++ Algo (§5). It lowers a create or amend to the largest quantity the client's
own `ValidateOrder` accepts. The server accepts it whenever the client's room is no larger than the
server's. By timing that holds for this strategy's own orders, but not when other strategies,
manual orders or the house book use room on the instrument, nor against the server rate limit or a
limit lowered in flight (§7).

```cpp
bool TryClipToRiskLimit(OrderTarget& t)
{
    OrderId id = t.OrderHeader.OrderId;
    if (t.OrderTargetAction != Create && ctx.GetOrderRisk(id).IsFull()) return false;   // 29 in flight: an amend would be TooManyActiveTargets
    const OrderState& s = ctx.GetOrderState(id);
    int absFilled = t.OrderTargetAction != Create && s.OrderHeader.OrderId == id ? std::abs(s.QuantityFilled) : 0;
    int64_t allowed = std::min<int64_t>(GetAbsAllowedOrderQuantity(t, /*isWithinLimit*/ true), OrderRisk::MaxOrderQuantity);
    for (leg : ctx.GetInstrument(id.InstrumentId).Legs())
        allowed = std::min<int64_t>(allowed, absFilled + (int64_t)ctx.GetRiskLimit(leg.InstrumentId).MaxOrderQuantity / std::abs(leg.Weight));
    if (allowed <= absFilled) return false;                       // an amend down to the filled quantity would be a cancel
    if (std::abs(t.OrderProfile.Quantity) > allowed) t.OrderProfile.Quantity = t.OrderProfile.Sign() * (int)allowed;   // only ever lowers
    return true;                                                   // OrderTargetAction is NOT re-derived
}
```

- **It is idempotent.** The allowed size does not depend on the target's quantity.
- **It does not check** session, paused status, slots, seq or the rate limit.
- **Accepted cost.** Several orders cut in one call are each cut by the whole overshoot. A cut leaves
  worst = max(acked, in flight) unchanged until its ack, so the room stays negative for the next
  order.

---

## 4. Client-side rules (the C++ `Client` exists: `Provider/Client.hpp`)

The C++ `Client` today builds `_riskLayer(ServerName, OrderRejectedSource::Client)` over the
**server's** rows (`Client.hpp:109`). `OnOrderState` (`:366`), `OnFill` (`:388`) and `OnOrderRejected`
(`:357`) make no risk calls. Port the following. The server never depends on a client doing any of
this: it validates independently.

**Design rule: the client is never looser than the server for its own algo orders.** It counts its
own sends before the server reads them, and it learns of reductions (acks and Dones) after the
server does. It is blind to other strategies, manual orders booked to its strategy, the house book
and the server rate limit, so "never looser" holds exactly when this strategy's own orders are all
that is on the instrument (§3.2 "Position source").

### 4.1 Build `RiskLayer` over the client's own context (`Client.cs:252`)

Construct it with source `Client` over the `ClientContext`. Its `OrderRisks` and `WorkingRisks` are then
the client directory's regions (§1.7), opened writable.

### 4.2 `ValidateOrder` on the client: risk for algo orders only, no rate limit (`RiskLayer.cs:357-404`)

**Client branch for non-Create,** in this order:
1. `ValidateOrderHeader(existingTarget header, target header)`; return on any error.
2. If the state row is this order:
   - Done → `StateIsDone`;
   - `isReduceOrReplace && existingTarget is Done && state.Profile == target.Profile` → `TargetIsActive`.
3. `existingTarget.Seq >= target.Seq` → `SeqOutOfOrder`.
4. If `existingTarget` is Active:
   - `isReduceOrReplace` and the same profile → `TargetIsActive`;
   - the existing target is a Cancel, or will cancel (it is this order and
     `sign·(qty − state.filled) <= 0`) → `CancelIsActive`.
5. `isReduceOrReplace && existingTarget.Side != target.Side` → `SideNotValid`.

**Then:**
- The `AlgoIsPaused` check runs on both sides.
- A **non-algo (manual) order returns here** with no reservation, because its position sits on
  another strategy's row.
- An **algo order** goes through the full risk block of §3.3 step 8, against the client's own rows.
- The rate limit (step 7) runs only on the server.

### 4.3 `OnOrderState`: the echo gate, applied once per real event (`Client.cs:593-627`, `:633`, `:684`)

```cpp
int32_t ackedSeqs[64] = {};           // per local slot: last seq whose ack RiskLayer applied

// Client::Create, after TryAllocate succeeds and before Send:
ackedSeqs[slot] = 0;                  // Create seq is 1, so the first ack always passes

// Client::OnOrderState, inside the existing `state.OrderId == targetRow.OrderId` branch (full id, generation included),
// BEFORE the Done handling (target Done, Position.OnOrderDone, Free):
bool isRiskEvent = state.OrderId.IsAlgoOrder() && isOrderActive[slot]
    && (state.OrderStateStatus == Done
        || (state.OrderStateReason == Acked && state.OrderHeader.Seq > ackedSeqs[slot]));
if (isRiskEvent) {
    _riskLayer.OnOrderState(state);
    if (state.OrderStateReason == Acked) ackedSeqs[slot] = state.OrderHeader.Seq;
}
```

**Why there is a gate.** An echo repeats a `Done` or an ack that the client has already applied.
Applying it twice would release twice, and the client would become looser than the server.
- **A repeated `Done`** fails `isOrderActive`, because the first `Done` freed the slot after the risk
  call.
- **A repeated `Acked` at the same seq** fails the seq test.

**Example.** The order has `Acked` at seq 3, and `Acked` seq 3 arrives a second time. The client skips
it. The server's `WriteOrderState` (`seq >= row seq`) re-applies it. That is harmless, since `Ack`
finds no entry and the acked value is unchanged, so the delta is 0. Harness case G5c.

### 4.4 `OnFill`: `isReserved` (`Client.cs:571-572`)

Call `_riskLayer.OnFill(fill, fill.OrderHeader.OrderId.ClientId == ownClientId)` before the
position update and the event.

The server routes every fill to the **strategy's** client. A manual (GUI) order booked to the
strategy therefore reaches the algo client too. That fill moves the client's `WorkingRisk.Position`
but releases nothing, because this client never reserved it.

### 4.5 `OnOrderRejected`: release before the discard check (`Client.cs:728-747`)

```cpp
// after the existing isTargetDone update, BEFORE `if (IsDiscarded(r)) return;`
if (r.OrderHeader.OrderId == targetRow.OrderHeader.OrderId && r.OrderHeader.OrderId.IsAlgoOrder())
    _riskLayer.OnOrderRejected(r);   // any seq, not only the latest target
```

Discarded rejects (`TooManyActiveTargets`, `TooManyOrdersPerSecond`, ...) must still release what the
client reserved when it sent the target. Otherwise the client copy stays tighter than the server
for good. Client-sourced rejects return early inside `RiskLayer` (§3.6).

### 4.6 Instrument onboarding: spread-shape refusal, startup refusal, `OrderRisk` clear, `WorkingRisk` seed

**Spread shape** (`Client.cs:281-286`, `GetInstrument`). Before onboarding any leg and before sending
`AllocateInstrument`, refuse any spread that is not a two-leg +1/−1 calendar:

```cpp
if (hdr.InstrumentType == Spread &&
    !(legged.LegCount == 2 && std::abs(legged.Legs[0].Weight) == 1 && legged.Legs[0].Weight == -legged.Legs[1].Weight))
    throw NotImplemented("Spread <id>: only a two-leg +1/-1 calendar is supported");
```

Both (+1, −1) and (−1, +1) are accepted. The server does not check (§2.8), so a C++ client **must**
mirror this check. Harness case G9: a +1/−2/+1 butterfly is refused on the client, and the server
never sees it and keeps trading.

**`OnInstrumentAllocated`** (`Client.cs:308-341`). On every allocation, in this order:

```cpp
// 1. ThrowIfPreviousOrdersActive(instrumentId)
for (int slot = 0; slot < 64; ++slot) {
    OrderId id{ .ClientId = ownClientId, .LocalIndex = slot };
    const OrderState& s = ctx.GetOrderState(id);              // server-wide row, plain read
    if (s.OrderHeader.OrderId.InstrumentId != instrumentId) continue;
    if (s.OrderStateStatus == Active)
        throw InvalidOperation("Order <id> from a previous process is still Active on instrument <iid>: start again once the server has cancelled it.");
    ctx.GetOrderRisk(id) = OrderRisk{};                       // Done: inherit nothing from the previous process
}
// 2. Seed WorkingRisk from THIS strategy's own position row (seq-bumped Write), zero reservations:
ctx.GetWorkingRisk(instrumentId).Write(WorkingRisk{ .Position = ctx.GetPositionHeader(instrumentId).Quantity });
// 3. Then open the instrument data socket, etc. (unchanged)
```

**Why the startup refusal.** A previous process's Active orders would hold room, slots and fills that
this process never made. That happens on a restart faster than the server's cancel-on-close round
trip, during an iLink outage, or in a no-cancel phase. With no Active orders on the instrument,
zero reservations is exact.

**Why the seed is rewritten every start.** The region can outlive a process, so it is rewritten
rather than trusted. A spread's legs pass through this path first, before the spread itself.

**Accepted residuals:**
- The check reads only `OrderState`. A previous process's `Create` that the server has not yet read
  is not detected (`Server::CancelAllOrders` also treats an Active `OrderTarget` as live).
- A fill landing between the socket connecting and the check could be counted twice.

### 4.7 Manual (GUI) client rules: C#-only today

The C++ tree has no manual client. If one is ever written:
- **Refuse any non-Cancel on an algo order.** An algo's orders are cancel-only from a GUI, because the
  algo's next tick would undo the amend and the algo's `RiskLayer` copy cannot see it.
- **Number a cancel of an algo order** `max(existing target seq + 1,000,000, caller seq)`.
- **Number a manual order's amend** `max(existing + 1, caller seq)`.
- **Choose Reduce or Replace** with `IsReduceOf` against the order's **state** profile.

---

## 5. C#-only: do NOT port

The C++ tree has no Algo, ActiveTarget or Target layer (`cpp_alignment_report_2026-09.md` N2), so
none of the following has a C++ counterpart. `Strategy/Strategy.hpp` does send creates and amends
through the C++ `Client`; it is covered by §1.5 and §4. Each line says why it stays in C#.

- **`Algo.Target` (strict) / `Algo.TryTarget` (best effort, returns bool):** client strategy
  convenience layer. Strict sends exactly what was asked, so a refusal for a non-discarded reason
  pauses the algo and a strategy bug shows. Discarded refusals (for example the 29-amend cap's
  `TooManyActiveTargets`, or `TooManyOrdersPerSecond`) are dropped silently and never pause. Best
  effort never sends what it can see would be refused.
- **`Algo.Send` best-effort checks** (TradingStatus Open, `HasFreeOrderSlot`, `TryClipToRiskLimit`):
  Algo-only; the server validates on its own.
- **Phase 1 reduce becomes a cancel when nothing fits; Phase 2 largest-first reprice
  (`GetLargestActiveKeyIndexAtPrice`); Phase 8 IOCs sent last:** zipper internals.
- **`NewAmend` choosing Reduce vs Replace; `NewOrder`/`NewAmend` copying `TimeInForce`:** sender-side.
  The server sees only the result: §1.5, §1.9, §3.3 step 7.
- **`CancelAllOrders` made private and sending the Phase 5 shape:** the server-visible effect is
  §2.10 only.
- **`IsPendingBuyCancel`/`IsPendingSellCancel` and the Phase 7 per-side lock removed:** the client's
  `RiskLayer` copy now keeps a cancel-pending order reserved until `Done`. This supersedes
  `cpp_alignment_report_2026-09-10.md` C1, so do not add those flags to any C++ enumerator.
- **`ActiveTargetsEnumerator`** (IOCs never active, a cancel hidden before the server reads its
  Create, `reduceOnly` only for an explicit Reduce, book-quantity `QuantityAhead` for unconfirmed
  orders, one 64-bit ahead/behind load): client view only. The server side of the 64-bit pair is
  already handed off (2026-09-26).
- **`Target.TimeInForce`, `ActiveTarget.QuantityBehind`:** C# record structs, not wire.
- **`Client.HasFreeOrderSlot`:** Algo-only predicate.
- **`TimeInForce` restriction** (Algo throws `NotImplementedException` for anything but Day or IOC):
  Algo-only. The server still accepts every `TimeInForce` value.
- **Simulator changes** (IOC/marketable orders publish queue 0/0 and are taken or eliminated at once;
  a refused target changes nothing; the `Init` admin drain wrapped in try/catch;
  `Clock.OnException`): the C# simulator is C#-only. A real CME session eliminates IOC remainders
  itself, and the `Eliminated` state goes through the normal `Done` path.
- **GUI widgets** (Amend hidden for algo orders, widget-side seq offsets removed, Risk Limits widget
  reading `WorkingRisk`): C# GUI. The C++ server need only publish the rows (§1.8).
- **`Array29<T>`:** C# inline array; use `uint16_t[29]`.
- **`Exposure` struct deleted:** it was dead code. Delete any C++ mirror of it.
- **`ServerContext.PrintDebug`:** diagnostic. If C++ has a dump, add `WorkingRisks` and read worst as
  max(acked, in flight).

---

## 6. Verification checklist

### 6.1 Static asserts

- `sizeof(RiskLimit) == 24`, with offsets 4/8/16/20.
- `sizeof(WorkingRisk) == 16`, with offsets 4/8/12.
- `sizeof(OrderRisk) == 64`, with offsets 2/4/6 and `MaxActiveTargets == 29`.
- Unchanged and still asserted:
  - `OrderState` 64 (`OrderProfile` @40, `QuantityFilled` @52, `QuantityAhead` @56, `QuantityBehind` @60);
  - `OrderTarget` 52 (`OrderProfile` @40, `TimeInForce` @48, `OrderTargetAction` @49);
  - `OrderHeader` 28;
  - `Fill` 64 (`Price` @40);
  - `AheadOfOrder` 20.
- Numeric parity for `OrderType` (17) and `OrderTargetAction` (0..3).
- `OrderDiscarded` = {40..46, 56}.

### 6.2 `OrderRisk` unit test

This is the differential test from the 2026-09-10 report, reworked:
- The reference model keeps an in-flight multiset capped at 29, plus an `acked` scalar. `Ack(q)`
  removes one entry and sets `acked = |q|`. `Reject` only removes.
- After every random op, assert that `GetAbsWorstOrderQuantity() == max(acked, max(reference))`.
- A 30th `TryAdd` fails with `TooManyActiveTargets`.
- `TryAdd` of {0, 65536, INT_MAX, INT_MIN} fails with `QuantityNotValid`.
- A zeroed struct returns 0.
- Fixed vector, as the C# harness checks it: `TryAdd 7`, `TryAdd −9`, `Ack 7` gives the u16 words
  [0]=1, [1]=9, [2]=7, [3]=9, and a worst of 9.

The op counts quoted in the 2026-09-10 report are superseded.

### 6.3 Invariants

The C# harness checks these at every drain point. A C++ replay of the `.audit` can check the
server-side ones.

- **I1 signs.** On every `WorkingRisk` row, `WorstLong >= 0` and `WorstShort <= 0`. Per order,
  worst >= |filled|.
- **I3.** The spread's own `WorkingRisk` row never holds a reservation, only a position view.
- **I4a.** The server's `WorkingRisk.Position` == the server-wide position row.
- **I4b.** A client's `WorkingRisk.Position` == its own strategy position row.
- **I4c.** The strategy rows sum to the server-wide row.
- **I5.** The client never under-counts the server for its own algo orders. Per order: client worst
  >= server worst, and client in-flight count >= server count. Per leg it holds only when this
  strategy's own algo orders are the only orders on the instrument: the server's per-leg aggregates
  also hold other strategies', manual and house orders.
- **I6 at rest.** Once every target is resolved, the order has nothing in flight and worst == |acked
  quantity|. This does not apply when an ack rode inside a fill.
- **I8.** A `Done` order holds nothing on the server, and a free slot holds nothing on the client.
- **I17.** An order live at the exchange is covered by both ledgers: worst >= the exchange's live
  quantity.
- **Ledger replay** (`cpp_alignment_report_2026-09-22.md`, updated for this change). Per order,
  reserve − per-fill releases − (worst at Done − |filled|) = 0. Every day must end with zero
  reservations on instruments with nothing working.

### 6.4 Scenarios to mirror

These C# harness cases all pass on 2026-10-04: a 137-job sweep, 40 of 40 scripted cases, and all fuzz
presets, including two calendar-spread runs. Mirror the server-relevant ones in C++.

| Case | Scenario | What must hold |
|---|---|---|
| F3 | A GUI cancel races an algo amend | The cancel carries seq + 1,000,000, so it never collides with the amend's seq. Whichever reaches the venue second is applied or discarded (`StateIsDone`), never `SeqOutOfOrder`. The algo is never paused (§2.9). |
| F6 | A paused algo's `CancelAllOrders` cancel races the order's last fill | The cancel carries working + filled at the active target's price (§2.10), so it has a side. Nothing comes back `QuantityNotValid`/`SideNotValid`, and nothing re-pauses. |
| F11 | Past the limit, an order is "kept" at a size its own reduce had already taken away (acked at the server, not yet read by the client) | Best effort cuts back within the limit (§3.2, `isWithinLimit`), so the server accepts. |
| G2c | The server refuses a manual (GUI) order booked to the algo's strategy | The reject is forwarded and raised. The algo is NOT paused (§2.2). |
| G5a / G5b | A replace is acked only inside its first fill (G5b: 29 times) | It stays reserved at the new size until `Done`, then is released exactly. The 30th move cancels and re-creates (best effort). |
| G5c | The same `Acked` is delivered twice | The client skips it (echo gate). The server re-applies it with zero effect. Client worst >= server worst >= live. |
| G5e | A fill event is delivered twice | Position and reservation count it once (§2.1). |
| G7a / G7b | `MaxOrderQuantity = 0` (kill switch) | A level above its target is reduced or cancelled, never left. A reprice whose clip fails is cancelled, never sent as a Replace. |
| G9 | A +1/−2/+1 butterfly is requested | The client refuses it before the allocate request. The server never sees it and keeps trading (§2.7, §2.8, §4.6). |
| R1a | The client restarts with orders live | The new process refuses to start until they are `Done`. It then starts with `WorkingRisk` equal to the server's, and its create fits. |
| R1b | The same restart | The first create never finds a busy slot (`OrderIndexIsBusy`). |
| R1c | The client restarts while a marketable order is in flight | It refuses until `Done`, so it never releases what it never reserved. |
| C26 | 29 amends in flight on one order | Best effort sees `IsFull`, cancels the order and places the target as a new order. Nothing leaks. In strict mode the 30th amend gets `TooManyActiveTargets`, which is discarded and never pauses. |
| G6a | A carried position of +7 against `MaxPositionQuantity` 5 | Buys are skipped, sells go up to 12, nothing pauses. This exercises the negative-room and keep-worst rule. |
| G14 | A spread algo and an outright algo on its first leg, in one client | Both share that leg's room. The outright create is clipped by the spread's reservation, and the other side is independent. |

C++-only test to add (no C# harness counterpart): a `Reduce` (action 3) from a C# client reaches the venue as a modify, encoded exactly like a
`Replace` (on iLink, `OrderCancelReplaceRequest` at the same price with the smaller `OrderQty`), and
never hits a default or throw branch (§1.5).

These residuals are accepted and not expected to pass "clean":
- G1b/G8: the session closes with orders in flight, so `NotInSession` pauses.
- G2b/G6b: manual or house-book room that the algo client cannot see.
- G5d is parked for CME (§7).

---

## 7. Open items

**CME confirmations (the user is calling CME):**
1. **Can a rejected new order arrive with no terminal state?** The C# simulator synthesises a
   `Done/Rejected` `OrderState` before routing an exchange reject of a `Create`
   (`ServerSimulator.cs:1665-1681`). The proposal on hold is to move that synthesis into
   `Server::OnOrderRejected`, on both sides together. Until CME answers, the C++ adapter must make
   sure a rejected `Create` reaches the server as a `Done` state. Otherwise the slot and its
   reservation are held for good. This is harness case G5d, parked.
2. **The duplicate-fill drop's assumptions** (§2.1): fills for one order arrive in order, and
   `QuantityFilled` is always the cumulative `CumQty`, retransmissions included. If either is false,
   both sides switch to an ExecID recent-set.
3. **Does an amend ever get acked only inside a fill?** CME and the simulator send `Acked`, then the
   fill. The risk layer retires amends only on `Acked`. Since this change, a violation
   over-reserves until `Done` instead of leaking (§3.4).

**Declined or accepted, so do not "fix" them in C++ alone:**
- **B16.** A risk limit lowered between the client's check and the server's read pauses the algo. A
  discarded `RiskLimitChanged` reason was declined.
- **B6.** Manual orders may use room the algo's client cannot see, and the algo may then be refused
  and paused.
- **Rate limit.** The server rate limit (`TooManyOrdersPerSecond`, discarded) is invisible to best
  effort.
- **C25.** Phase 7 creates take room before delayed aggressive reprices, and several orders are each
  cut by the whole overshoot.

**Noted, not implemented:**
- **A `Done` with |QuantityFilled| > worst** would apply a negative release silently. Spec.md once
  described an alarm for this. None exists in C# or the harness.
- **The `OrderRisk` mirror dispatch collision** (§1.8, pre-existing, latent). `OrderRisk` rows are
  never seq-bumped, so none is mirrored today. If someone starts seq-bumping them, a mirrored
  `OrderRisk` whose count is 10..14 collides with the `OrderType` values the C# receiver dispatches
  explicitly (OrderState 10, OrderTarget 11, OrderRejected 12, Fill 13, Position 14): 12 and 13 are
  forwarded to a client socket instead of mirrored. 15..17 fall to `default:` and are mirrored
  correctly. Keep `OrderRisk` writes plain in C++ (§1.4), or decide with the C# owner first.
- **Dropped duplicate fills are not counted.** The C# side does not count them. C++ may add a counter.

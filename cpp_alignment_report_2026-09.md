# C++ alignment report — 2026-09-03

Companion to `cpp_alignment.md`. That doc compared C++ `origin/main` (`aeac53c`) against C# main as of
2026-08-11 and still stands. **This report adds the changes made to C# after that date** (this
session's fill-atomicity / order-lifecycle work), which the C++ tree does not yet have.

## Read this first — which tree are you on?

- **GitHub `emilbigaj/HFT_cpp` is a single commit, `aeac53c` (2026-07-23).** None of the local C++
  branch work (`persist-client-sockets`, the RiskLimit/OrderState layout changes, the socket
  Closing protocol, RAIISpinLock, `_maxClientOrderId`) was ever pushed. If you are on GitHub, you
  are missing **everything** in both `cpp_alignment.md` §0–§6 and this report — merge the local
  branch first.
- **If you are on the local C++ working tree**, several items below may already be present (it is
  ahead of GitHub). Every item is written **C#-as-source-of-truth with a "verify" note** — check
  your actual tree before implementing; don't assume from GitHub.
- C# is the source of truth for behavior. C#-only projects (Simulator, Widget, Workspace, Logging,
  Testing, Debug) do **not** get ported; where a change lives partly in the Simulator, the
  C++-facing contract is called out explicitly.

---

## NEW items (C#-first, made after `cpp_alignment.md`)

### N1. `Server::OnFill` — atomic fill transaction (HIGH priority, core correctness)

**The bug this fixes** is the one that caused the live `PositionExceedsRiskLimit` incident: a fill's
two effects — **position** and the order's **QuantityFilled** — were written by two separate handlers
(`OnFill` then `OnOrderState`) milliseconds apart. A strategy tick that read a fresh position but a
stale filled count sized an amend to the wrong total, cancelled its own order, and cascaded.

**C# now (`Provider/Server.cs` `OnFill`):**
- Signature changed to `OnFill(ref OrderState orderState, ref Fill fill)` — the fill arrives paired
  with the order state it produced.
- Inside **one** lock envelope over **both** position rows (server-wide and local), in a fixed
  acquire order (server row, then local row): write the order state (row + risk ledger only), apply
  `serverPosition.OnFill`, apply `localPosition.OnFill`, apply `_riskLayer.OnFill`. Release local,
  then server.
- The state **forward and callback happen after release** — a new private `WriteOrderState` does the
  row-write-plus-ledger half; the public `OnOrderState` (row + `WriteToExecution` + callback) is NOT
  called inside the envelope, or the state would be sent twice and sent from inside the lock.

**C++ needs:** the same two-position envelope in `Server::OnFill`, splitting the current
`OnOrderState` into a lock-safe `WriteOrderState` (row + `_riskLayer.OnOrderState`) called inside,
and the forward/callback after `ReleaseLock`. Take `orderState` as a parameter alongside `fill`.

**Live-integration note:** in C# the Simulator delivers the fill and its state as one queue entry so
`OnFill` gets the pair. On the live path, your vendor/iLink3 session must hand `Server::OnFill` the
resulting `OrderState` together with the `Fill` (one ExecutionReport already carries both) rather
than routing them through two independent calls.

**Depends on:** single-writer discipline (N7). Verify: C++ `Server.hpp` `OnFill` currently locks
server and local positions **separately** and does not touch the state row — so this is genuinely
absent, not partial.

### N2. Era-rule reader: `Algo::GetPositionQuantity` seqlock loop (HIGH, pairs with N1)

**C# (`Strategy/Algo.cs`):** `GetPositionQuantity` now reads under a seqlock-validated loop against
the position row so it can never straddle N1's transaction:

```
loop:
  seq0 = positionHeaderEntry.GetSeq();
  if (Protocol.IsWriteInProgress(seq0)) { pause; continue; }   // odd = fill txn in progress
  SnapshotActives();                                            // actives FIRST (era order)
  quantity = positionHeaderEntry.GetReadonlyRef().Quantity;     // then position
  seq1 = positionHeaderEntry.GetSeq();
  if (seq0 == seq1) return quantity;
```

Order matters: actives are snapshotted **before** position so any escape degrades to
position-fresher (under-quote), the era-rule safe direction.

**C++ needs:** the same loop wherever the C++ Algo reads position for target sizing. **Caveat:** the
whole actives/era-rule/Algo layer (`SnapshotActives`, the `ActiveTarget` enumerator, the zipper)
appears **absent** from the C++ tree — no `ActiveTarget`, `SnapshotActives`, or `Algo` class found.
If that layer isn't ported yet, N2 and N3 come as part of porting it, not as tweaks. Confirm with the
owner whether the C++ Algo layer exists on a branch.

### N3. Actives enumerator: implicit-cancel detection (`Provider/Position.cs`)

An order whose in-flight amend total is **at or below** its reported fills will be **cancelled** by
the venue (CME leaves-0 semantics), not rejected. The actives enumerator must treat such an order as
done so the zipper doesn't quote against a phantom zero-working order.

**C# now:**
```csharp
bool targetIsCancel = !targetRejected &&
    (target.OrderTargetAction == OrderTargetAction.Cancel
     || target.OrderProfile.Sign * (target.OrderProfile.Quantity - state.QuantityFilled) <= 0);
bool isOrderDone = sameOrder && (state.OrderStateStatus == OrderStateStatus.Done || targetIsCancel);
```
Note the **`<= 0`** (was `< 0`, which is the bug: an amend to exactly CumQty leaves zero and is
cancelled) and the **`!targetRejected`** gate (a rejected cancel un-hides the still-live order).

**C++ needs:** the same in its actives enumerator (part of the N2 layer). Verify: GitHub `Position.hpp`
has no enumerator at all.

### N4. `RiskLayer::ValidateOrder` — implicit-cancel guard on `CancelIsActive` (`Provider/RiskLayer.cs`)

Follow-up amends/cancels to a de-facto-cancelled order must be rejected client-side instead of sent
to the venue. C# extended the existing `CancelIsActive` check:

```csharp
bool existingWillCancel = existingTarget.OrderHeader.OrderId == orderState.OrderHeader.OrderId
    && existingTarget.OrderProfile.Sign * (existingTarget.OrderProfile.Quantity - orderState.QuantityFilled) <= 0;
if (existingTarget.OrderTargetAction == OrderTargetAction.Cancel || existingWillCancel)
    orderRejectedReasons.Set((int)OrderRejectedReason.CancelIsActive);
```

The **generation guard** (`existingTarget.OrderId == orderState.OrderId`) is essential: on a recycled
slot with a create in flight, the state row holds the *previous* order's fills, which must not
condemn the new order.

**C++ needs:** the `existingWillCancel` term added to its `CancelIsActive` set. Verify: C++
`RiskLayer.hpp` currently sets `CancelIsActive` on `OrderTargetAction::Cancel` **only** (GitHub
line ~219) — the implicit `<= filled` case is missing.

### N5. `OrderStateReason` — merge `PartialFill`+`Filled` into `Fill` (WIRE CHANGE)

Already written into `cpp_alignment.md` §1.3, restated here as it is new and is a shared-memory byte
renumber. New enum:

```
Unknown=0, PendingNew=1, Acked=2, Fill=3, Canceled=4, Rejected=5, Eliminated=6
```

Partial-vs-complete now lives in `OrderStateStatus` (Fill+Active = partial, Fill+Done = complete).
The simulator's promotion logic (`PartialFill → Filled`) is deleted; reason is set once as the event
tag. **Both sides deploy in lockstep** (byte crosses shared memory); pre-rename JSON logs
(`"PartialFill"`/`"Filled"`) will no longer deserialize. Verify: GitHub still has the older
`OrderStateDoneReason {None,Filled,Canceled,Rejected}` — so this stacks on top of the §1.3
`OrderStateDoneReason → OrderStateReason` rework, which itself isn't on GitHub.

### N6. `Tools::AtomicTransition` → rename to `AtomicEnum`

Pure rename (the `{state, epoch}` packed-word type). C# file is `Tools/AtomicEnum.cs`, type
`AtomicEnum<TState>`. Verify: C++ likely still names it `AtomicTransition` in
`Tools/AtomicTransition.hpp` + its Socket.hpp uses.

### N7. Design decision — single-writer for positions & order states (no CAS)

The seqlocks on the `OrderState` and `PositionHeader` entries are **single-writer** by discipline,
not CAS-protected. N1's envelope and N2's reader both rely on this. Enforcement requirement: every
writer of those rows must be the one owner thread. Two paths that must route to the owner rather than
write directly:
- `OnQuantityAhead` (MDP3/book thread in live) — must enqueue to the order-state owner thread, not
  write the row from the RX thread. This is the August "slot 64" torn-seq bug's root.
- `OnControlAlgoStatus` (admin/hub thread) writes `AlgoStatus` into position rows — same requirement.

C# gets this for free in sim (one release thread). C++ live has genuinely separate MDP3/iLink3
threads, so this routing is mandatory there. Do **not** add CAS as an alternative — the decision is
single-writer.

---

## Pre-existing items — see `cpp_alignment.md` (still all needed on GitHub)

Not duplicated here. §0 (persist-client-sockets, ClientStatus **Closing** ladder, `AtomicEnum`,
RAIISpinLock), §1 wire structs (RiskLimit 36B, OrderState reorder, AllocateInstrument
ExchangeInstrumentId, OrderRejectedReason values), §2 Maturity renames, §3 RiskLayer worst-case
accounting + sign convention, §4 Strategy-0 house book + union rule, §5 behavior fixes (AsFuture
type-guard, OnRiskLimit live-working-qty copy, unknown-message counting), §6 verification asserts.

## Suggested order

1. Merge the local C++ branch to GitHub (or confirm you are on it) — closes §0–§6.
2. N7 (single-writer routing) — prerequisite for N1/N2.
3. N1 (`OnFill` envelope) + N5 (`OrderStateReason` renumber) — coordinated, both touch the fill path.
4. N4 (`existingWillCancel`) — small, self-contained.
5. N2/N3 — with the Algo/actives layer, if/when it is ported to C++.
6. N6 rename — cosmetic, any time.

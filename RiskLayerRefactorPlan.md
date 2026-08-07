# Implementation Plan — Server-Authoritative Reserved Exposure Ledger (RXL)

## 0. Decision summary

**Base:** Design 2 (RXL — reserve-on-send / release-on-confirm, per-slot ring retaining *all* unacked targets, exact-Seq removal on exchange reject). It is the only candidate that survives the four adversarial interleavings (Create admission, reject-with-lost-ack, overflow, Fill/OrderState reordering).

**Grafted in:** Design 3's entire evidence layer (`RiskDecision` on every decision, `OrderHeader` at offset 4, admin/audit-channel routing for headerless records, `DecisionSeq`, `ReduceExemption` flag, fail-closed Reconcile). Design 1's `CreditedFillQty` framing, restart seeding, server-private forwarded-Seq, and `PeakExcess` instrumentation.

**Rejected outright:** Design 1's per-order high-water scalar (fail-open on every Create — `SlotWorstWorking` short-circuits on `OrderStateStatus == Done`, and `ValidateCreate` at `Provider/RiskLayer.cs:127-130` only admits a Create when the slot's OrderState is *not* Active, i.e. Done; both C# and `Downloads/Server.hpp:443-470` write the Active OrderState *after* `ValidateOrder` returns). Design 3's monotonic deque (prefix-pop on `OrderRejected` discards entries the exchange *applied*).

**Three corrections to what the judges agreed on:**

1. The fill invariant is **not** "MaxPotentialLong and MaxPotentialShort are invariant under a fill." A buy fill of `f` moves `Position += f` and `Reserved.Buy -= f`, so `MaxPotentialLong` is invariant but `MaxPotentialShort` *increases by exactly f*. The correct assertion, and the one the harness checks: **the potential on the filling order's own side is invariant; the opposite side moves by exactly the fill quantity.**
2. `Reserved` must be `long`, not `int`. `RiskLimit.GetMaxLimits` sets `MaxOrderQuantity = MaxPositionQuantity = int.MaxValue` (`Execution/Order.cs:189-196`) and simulation defaults to it (`Provider/Context.cs:917`). A sum of `E(g)` over 4096 slots in `int` overflows to negative and silently fails open. All aggregate arithmetic and all limit comparisons are `long`. (§5 Step 2 additionally deletes `GetMaxLimits` — an unlimited default is not a defensible harness baseline.)
3. **Loss of a client is not loss of exposure.** No candidate said what happens to `E(g)` when the owning client dies. `CancelAllOrders` (`Downloads/Server.hpp:305-329`) only *enqueues* cancels; nothing is confirmed. Because `MaxPositionQuantity` is firm-wide (`RiskLayer.cs:279-280` reads `_serverPositionHeaders[i]`), releasing a dead client's reservations hands its headroom to a surviving client while its orders may still be resting. RXL therefore **never releases on disconnect** — see §1.5.

**Scope decision taken up front:** spread instruments are **blocked**, not modelled. `SpreadHeader.LongInstrumentId`/`ShortInstrumentId` (`Data/Instrument.cs:153-154`) have zero consumers anywhere in `Provider/`, `Simulator/`, `Strategy/`; `Server::OnFill` (`Server.hpp:558-570`) and `ServerSimulator.cs:1495-1506` move `PositionHeader` for `fill.OrderHeader.OrderId.InstrumentId()` only, so a CME calendar spread that legs into outrights at clearing creates real leg exposure this system's `MaxPositionQuantity` never sees. Leg fan-out is §7 phase 2; the enforced statement in this phase is a hard reject (§1.4 line 6a).

---

## 1. The algorithm

### 1.1 Name and shape

**RXL — Reserved Exposure Ledger.** Two-phase credit reservation (reserve before the irreversible send, release only on exchange confirmation), evaluated as the pair of one-sided worst cases the FIA/CME pre-trade risk recommendations call **Maximum Potential Long / Maximum Potential Short**:

```
MaxPotentialLong (i)  = Position(i) + Reserved(i).Buy
MaxPotentialShort(i)  = Position(i) − Reserved(i).Sell
```

`Position(i)` is the filled net position (`_serverPositionHeaders[i].Quantity`). `Reserved(i).Side` is the sum, over every live order slot on side `Side`, of that slot's worst-case unfilled quantity.

The pipelined-amend sub-problem is a **maximum over a window that is retired by two different rules**: a prefix rule (an ack at Seq S resolves everything ≤ S) and a point rule (an `OrderRejected` carries an exact Seq — `Execution/Order.cs:120-128`). Because retirement is not purely prefix-ordered, a monotonic deque is unsound here: pop-on-push discards a dominated-but-still-resting entry, and a later point-retirement then leaves nothing behind it. RXL therefore keeps **every** unacked target in a bounded FIFO ring and pays a ≤16-entry rescan on the two retire paths. That trade is the single design decision this whole plan turns on, and it should be written into the certification document as "why not the textbook algorithm."

### 1.2 Safety invariant

Notation, per global order slot `g` (`OrderId.GlobalIndex` = `clientId << 6 | localIndex`, `Execution/OrderIdAllocator.cs:17-37`):

- `Rest(g)` — the true unfilled quantity the exchange currently holds for the order owning `g`. Never directly observable.
- `Fwd(g)` — the set of targets the server forwarded for that order and the exchange has neither acked-through nor rejected.
- `Ack(g)` — `|OrderState.OrderProfile.Quantity|` at the highest ack Seq observed.
- `Filled(g)` — `|cumulative fill|` **that has already been applied to `_serverPositionHeaders[i].Quantity`**.
- `M(g)`, `E(g)` — ledger state.

> **(I-slot)** `M(g) ≥ max( Ack(g), max{ |q(t)| : t ∈ Fwd(g) }, OverflowQty(g) )`
> and `E(g) = max(0, M(g) − Filled(g)) ≥ Rest(g)` at every instant between any two machine instructions of the enforcer.
>
> **(I-agg)** `Reserved(i).Buy = Σ { E(g) : Sign(g)=+1, Instr(g)=i }` **exactly** (maintained by delta, verified by periodic full recompute; on mismatch the ledger adopts `max(maintained, recomputed)` — fail-closed).
>
> **(I-limit)** Every target the server forwards satisfies, at the instant of forwarding, for the side it moves:
> `sign>0 ⇒ Position(i) + Reserved(i).Buy ≤ MaxPositionQuantity`
> `sign<0 ⇒ Position(i) − Reserved(i).Sell ≥ −MaxPositionQuantity`
> A target that does not increase `E(g)` is not tested at all (see §1.4).
>
> **(I-order)** `|OrderProfile.Quantity| ≤ MaxOrderQuantity` for every forwarded target — the total OrderQty CME sees, not the remainder.
>
> **(I-life)** `E(g)` is released only by an **exchange-originated** event (`OrderState.Done`, exchange `OrderRejected` of a Create) — never by a local event (client disconnect, session boundary, GUI action, server restart). Local events may only *raise* `E(g)` or mark the slot.

**Certification claim that follows:** the maximum long position reachable *without any further accepted target* is exactly `MaxPotentialLong(i)`, and it never exceeds `MaxPositionQuantity`, on the instruments RXL admits (outrights; spreads are refused at admission). That is the sentence Wedbush certifies.

**Why (I-slot) holds — one case per transition:**

| Transition | Argument |
|---|---|
| Forward | The ring append and `M = max(M, |q|)` happen **before** `WriteToExecution`/`OrderTarget(...)` forwards. From the first instant the exchange could know about `t`, `M ≥ |q(t)|`. |
| Prefix retire (ack at Seq S) | `S > Watermark` ⇒ the exchange applied something at least as new as every popped entry, and `Ack := |st.OrderProfile.Quantity|` is set in the *same* critical section. `M := max(Ack, RingMax, OverflowQty)` still dominates. |
| Point retire (exchange reject of Seq S) | The exchange refused exactly `t(S)`; `q(t(S))` is not and can never become `Rest`. Every other entry is untouched. |
| Fill | `Filled` advances only in `OnFill`, only after `PositionHeader::OnFill` has moved `Position` (`Downloads/Server.hpp:560-563`). `Position += f` and `E −= f` in one critical section. |
| Ack regression | `Watermark := max(Watermark, st.Seq)`, monotone. `Simulator/ServerSimulator.cs:603` assigns `orderState.OrderHeader.Seq = orderTarget.OrderHeader.Seq` unconditionally *after* flagging `SeqOutOfOrder` at :593-600 and *before* the early return at :678-679 — the ledger is immune, and the simulator is fixed anyway (Step 3). |
| Slot reuse (ABA) | Every mutation is gated on the full 64-bit `OwnerOrderId` including the 32-bit generation. A late ack for generation N cannot touch generation N+1. |
| Ring overflow | The evicted (oldest) entry is **folded** into a sticky `(OverflowQty, OverflowSeq)` pair, not dropped. `OverflowQty` participates in every `M` recompute and is cleared only when `Watermark ≥ OverflowSeq`. Over-estimation, never under. |
| Message loss (lapping ring; `Socket/Protocol.cs:9-15` has no `Lapped` status, `WriteToRing` never reads a consumer cursor) | Any lost retire event leaves an entry live ⇒ `M` stays high ⇒ over-reservation. Every transport failure degrades in the safe direction. |
| Owner disconnect | `Flags \|= OwnerLost`; `E` untouched. Retirement still arrives — exchange events land on the *server*, not the dead client's socket. |
| Session boundary | No hook. The exchange cancels at close and emits `Done` per order (`ServerSimulator.cs:51-58` → `:334` → `:479-480`, `:539-555`); the ordinary retire path handles it. |
| Terminal (`Done`) | `Rest = 0`; slot released, `E = 0`. |

### 1.3 The reduce exemption is structural, not a carve-out

`Provider/RiskLayer.cs:284-292` today does `isIncreasing = |projected| > |current|`, which lets `+25 → −25` through under a limit of 20 (because `25 > 25` is false), and it tests `|projected|` against both sides at once. Replace with:

```
if (delta > 0)                                              // delta is >= 0 on the send path, always
{
    if (sign > 0 && maxLong  >   (long)limit.MaxPositionQuantity) reasons.Set(PositionTooLarge);
    if (sign < 0 && maxShort <  -(long)limit.MaxPositionQuantity) reasons.Set(PositionTooLarge);
}
```

`sign` is fixed for an order's life (`SideNotValid` enforces it), so `delta` moves exactly one side. Consequences, all correct and all testable:

- `delta == 0` (amend-down, cancel, duplicate quantity) is never refused on position grounds — it cannot create or worsen a breach.
- A **sell is never refused because the long side is over the limit.** Position `+25`, Max `20`: sell 5 → `maxShort = 20`, accept; sell 45 → `maxShort = −20`, accept (exactly to the limit); sell 50 → `maxShort = −25 < −20`, **reject**; buy 1 → `maxLong = 26 > 20`, **reject**. The `±25` ping-pong at 50 lots a turn is dead and genuine reduction always gets through.
- No `Sign()` comparison, no magnitude comparison, two branches.

**Boundary is inclusive** (`|potential| == Max` passes), preserving today's strict `>`. State this explicitly in the limit definition handed to the auditor — "maximum position 20" reads as 20-disallowed to an auditor.

**The same reduce-first principle governs every blocking state introduced by this plan.** `Blocked` (§7 case (b), §5 Step 9), `Recovering` (§1.4 line 49) and `LimitsNotProvisioned` (§5 Step 2) all refuse increases and always admit `OrderTargetAction.Cancel`. A risk control that can prevent flattening is a worse control than none.

### 1.4 Send path (replaces `Provider/RiskLayer.cs:158-329` in full)

```
ValidateOrder(in OrderTarget t, int socketClientId, out Bitset64 reasons, out RiskDecision d) -> bool

 0  // socketClientId is the id of the channel the bytes arrived on. Server::ReadExecution
 1  // (Server.hpp:262-289) and ServerSimulator.cs:1317-1334 already iterate `for (clientId : clientIds)`
 2  // and then DROP it; globalOrderIndex is derived purely from the message (Server.hpp:445). Without
 3  // this parameter client A can write into client B's slots and every OwnerOrderId/LastSentSeq gate
 4  // below is anchored on an attacker-chosen field. Server-injected cancels enter through the separate
 5  // ValidateInjectedCancel() entry point and bypass this check only.
 6  if (t.OrderHeader.OrderId.ClientId != socketClientId) { reasons.Set(ClientIdIsWrong); goto EMIT }
 7  i = t.OrderHeader.OrderId.InstrumentId; s = ...StrategyId; c = ...ClientId; g = ...GlobalIndex
 8  // explicit bounds; the try/catch at :162 and :321-325 is DELETED (interpolated string + blocking
 9  // console I/O on the CoreGroup poll thread, inside the risk check)
10  if ((uint)i >= InstrumentIds.Length) { reasons.Set(InstrumentIdNotValid); goto EMIT }
11  if ((uint)g >= OrdersCapacity)       { reasons.Set(ClientIdNotValid);     goto EMIT }
12  ValidateInstrument(s, i) / ValidateClient(c, s)          // RiskLayer.cs:51-107, plus:
13  if (instrument.Header.InstrumentType == Spread) reasons.Set(OrderTypeNotSupported)   // §0 scope
14  if (_blocked[i] && t.Action != Cancel)          reasons.Set(PositionIsSuspended)     // §7 case (b),
15                                                                                       // Σ-position
16                                                                                       // mismatch,
17                                                                                       // unprovisioned
18                                                                                       // limits
19
20  p = &_reservations[g];  owner = AcquireLoad(p->OwnerOrderId)
21  limit = _riskLimits[i].GetReadonlyRef()                  // ServerAccess, written ~never, raw ref safe
22
23  // ---- identity & sequencing, SERVER-AUTHORITATIVE.
24  // _orderTargets is ClientAccess (Context.cs:235) => attacker-writable. RiskLayer.cs:171/:209 reads it
25  // today. The server branch NEVER reads it after this change. p->LastSentSeq replaces it.
26  if (t.OrderTargetAction == Create)
27        if (owner != 0)                    reasons.Set(OrderIndexIsBusy)   // incl. OwnerLost orphans;
28                                                                           // see §3.4 for disposition
29        if (t.OrderHeader.Seq != 1)        reasons.Set(SeqOutOfOrder)
30        sign = Sign(t.OrderProfile.Quantity); if (sign == 0) reasons.Set(QuantityNotValid)
31  else if (owner != t.OrderHeader.OrderId)
32        // MANDATORY CARVE-OUT: a cancel for a slot the ledger does not recognise must be FORWARDED.
33        // Rejecting it would make the risk layer block risk REDUCTION after a restart or ledger loss.
34        if (t.OrderTargetAction == Cancel) { flags |= LedgerUnknownSlot; goto EMIT_ACCEPT_NO_LEDGER }
35        else                                 reasons.Set(OrderNotFound)
36  else
37        ValidateOrderHeader(orderState.OrderHeader, t.OrderHeader)     // ServerAccess source; unchanged
38        if (t.OrderHeader.Seq <= p->LastSentSeq)  reasons.Set(TargetIsStale)   // strict; closes the GUI/
39                                                                              // algo duplicate-Seq race
40        if (p->Flags.IsDone)                      reasons.Set(StateIsDone)
41        if (t.Action == Amend && Sign(t.Qty) != 0 && Sign(t.Qty) != p->Sign) reasons.Set(SideNotValid)
42        sign = p->Sign
43
44  // ---- (I-order): binds the WIRE TOTAL. Fixes the ratchet at :269-277 where a partially filled order
45  //      went 10 -> 19 -> 28 -> 37 under a limit of 10, and removes the inconsistency an auditor finds
46  //      first (Create 19 rejected, Amend to 19 accepted).
47  qAbs = |t.OrderProfile.Quantity|
48  if (qAbs > limit.MaxOrderQuantity) reasons.Set(QuantityTooLarge)
49
50  // ---- TRIAL. Pure: mutates nothing, so a reject cannot corrupt the ledger.
51  mCand = max(p->M, qAbs)                       // Cancel carries the resting total (Algo.cs:425) => no-op
52  eCand = max(0, mCand - p->CreditedFillQty)
53  delta = eCand - p->E                          // >= 0 on the send path, always
54  resvBuy  = (long)_reservedByInstrument[i].Buy  + (sign > 0 ? delta : 0)
55  resvSell = (long)_reservedByInstrument[i].Sell + (sign < 0 ? delta : 0)
56  position = _serverPositionHeaders[i].GetReadonlyRef().Quantity
57  maxLong  = position + resvBuy ;  maxShort = position - resvSell        // long arithmetic
58
59  if (delta > 0) { ...the two one-sided tests from 1.3... }  else flags |= ReduceExemption
60
61  // ---- recovery gate: while any slot on i is SeededOnRestart, only delta<=0 is admitted
62  if (_recovering[i] && delta > 0) reasons.Set(PositionTooLarge), flags |= Recovering
63
64  // ---- pause and rate. The IsAlgoOrder() gate at :294 is REMOVED so manual orders are rate-limited.
65  //      AlgoIsPaused stays algo-only, but a manual order sent while paused is stamped
66  //      LedgerFlags.ManualWhilePaused so the override is greppable from the audit file.
67  if (t.Action != Cancel && localPosition.AlgoStatus == Paused && t.OrderId.IsAlgoOrder())
68        reasons.Set(AlgoIsPaused)
69  if (reasons.IsEmpty && !RateOk(i, t.OrderHeader.NicTimestamp)) reasons.Set(TooManyOrdersPerSecond)
70
71  EMIT:
72  accepted = reasons.IsEmpty
73  if (accepted) Commit(p, i, s, t, mCand, eCand, sign)
74  // caller order in Server::OnOrderTarget:  ValidateOrder -> forward to exchange -> PublishRiskState ->
75  // WriteToAudit(RiskDecision).  The seqlock write and the 112 B audit record are OFF wire-to-wire.
76  return accepted
```

```
Commit(p, i, s, t, mCand, eCand, sign):
    RingAppend(p, t.OrderHeader.Seq, qAbs)     // full => fold oldest into (OverflowQty, OverflowSeq)
    p->M          = max(mCand, p->OverflowQty)
    p->LastSentSeq= t.OrderHeader.Seq          // == max, since Seq <= LastSentSeq was rejected above
    if (Create) { p->Sign=sign; p->InstrumentId=i; p->StrategyId=s; p->ClientId=c;
                  ReleaseStore(p->OwnerOrderId, t.OrderHeader.OrderId) }
    ApplyDelta(p, i, s, eCand)                 // Reserved[side] += eCand - p->E ; p->E = eCand
    p->PeakExcess = max(p->PeakExcess, p->M - p->AckedQty)
```

`ClientIdIsWrong` (10) and `OrderTypeNotSupported` (23) and `PositionIsSuspended` (51) move from the declared-but-never-raised list into the enforced set; §6.3 artefact 4 shrinks accordingly.

### 1.5 Retire paths

```
OnOrderState(in OrderState st):     // Server.hpp:382-407 — hook placed ABOVE the isSafeToOverwrite gate
    p = &_reservations[st.OrderHeader.OrderId.GlobalIndex]
    // isSafeToOverwrite (:391) is computed from existingOrderTarget.OrderHeader.OrderId — a read of
    // _orderTargets, the one ClientAccess array. Retiring inside that gate would let a client that
    // garbles its own target slot pin its reservations forever, and because MaxPositionQuantity is
    // firm-wide that denies headroom to EVERY other client on the instrument. The ledger gates on its
    // own OwnerOrderId instead and runs before/independently of the orderStateEntry overwrite.
    if (p->OwnerOrderId != st.OrderHeader.OrderId) return                     // ABA guard
    if (st.OrderStateStatus == Done) { Release(p); Publish(i,s); return }      // cheapest bulk retire
    if (st.OrderHeader.Seq > p->Watermark)                                     // MONOTONE
        p->Watermark = st.OrderHeader.Seq
        p->AckedQty  = |st.OrderProfile.Quantity|                              // same critical section
        RingDropPrefix(p, p->Watermark)
        if (p->OverflowSeq != 0 && p->Watermark >= p->OverflowSeq)
              p->OverflowQty = 0; p->OverflowSeq = 0; p->Flags &= ~Overflowed
        if (p->Flags.SeededOnRestart && p->Watermark >= p->LastSentSeq) p->Flags &= ~SeededOnRestart
        p->M = max(p->AckedQty, RingMax(p), p->OverflowQty)
        Recompute(p, i, s)
    if (st.QuantityFilled != p->CreditedFillQty) { p->Flags |= FilledDivergence; _divergence++ }
    // divergence is REPORTED, never followed. QuantityFilled moves in OnOrderState (Server.hpp:400)
    // while the position moves in OnFill (:562) — the vendor decides the order.
    Publish(i, s)

OnFill(in Fill f):    // Server.hpp:560-569, AFTER both PositionHeader::OnFill calls
    p = &_reservations[gi];  if (p->OwnerOrderId != f.OrderHeader.OrderId) return
    p->CreditedFillQty = min(p->M, p->CreditedFillQty + |f.OrderProfile.Quantity|)
    Recompute(p, i, s);  Publish(i, s)

OnOrderRejected(in OrderRejected r):   // r.OrderRejectedSource == Exchange. THE non-prefix retirement.
    p = &_reservations[gi];  if (p->OwnerOrderId != r.OrderHeader.OrderId) return
    if (r.OrderTargetAction == Create) { Release(p); Publish(i,s); return }
    if (RingRemoveExact(p, r.OrderHeader.Seq))      // searches all 16 — unlike Client.cs:626 which only
        p->M = max(p->AckedQty, RingMax(p), p->OverflowQty)   // compares the newest
        Recompute(p, i, s); Publish(i, s)
    // not found (already prefix-retired) => no-op. Idempotent: a duplicate reject is harmless.

OnOwnerLost(clientId):   // OnClientClosed (Server.hpp:761-766), PollDisconnects (:201-226)
    for each g owned by clientId with OwnerOrderId != 0:
        p->Flags |= OwnerLost;  p->OrphanSince = Clock.Now      // E(g) UNCHANGED — no release
    RiskState.Flags |= OrphanedExposure for every affected instrument; alert
    // CancelAllOrders (:305-329) has only ENQUEUED cancels; nothing is exchange-confirmed. Releasing
    // here would hand a dead client's headroom to a live one under a firm-wide limit.
    // Bounded staleness alarm: any slot with OwnerLost and no terminal event within N seconds
    // re-alerts at escalating severity. It is an ALARM, never a release.

Release(p): ApplyDelta(p, i, s, 0); zero the 64-byte header; ReleaseStore(p->OwnerOrderId, 0)
    // called ONLY on exchange Done and on exchange-rejected Create. Not on disconnect, not on
    // SessionManager.Changed (which fires on OPEN as well as close — Data/InstrumentManager.cs:41-59 —
    // and would zero reservations against orders that legitimately survive the boundary), not on
    // restart. Belt and braces: do NOT rely on a Done OrderState surviving the lapping execution ring;
    // Reconcile (§4.1) and the staleness alarm are the backstops.
```

**Cancel needs no special case anywhere.** `Strategy/Algo.cs:425` puts the resting total on the wire, so `mCand = max(M, resting) = M`, `delta = 0`, no position test, no release. Exposure is released only when the exchange says `Done`. The blanket `if (!isCancel)` exemption at `RiskLayer.cs:257/:267` — which today also skips `AlgoIsPaused` and every quantity check — is deleted.

**`CancelAllOrders` needs no ledger change.** Injected cancels re-enter through `ValidateInjectedCancel` as ordinary appends. `LastSentSeq` and `Watermark` are both `max()`-maintained, so the drain-injection-queue-first reordering at `Server.hpp:249-260` and the `Seq += 1'000'000` bump are safe. Assert the injection queue can only ever carry `OrderTargetAction.Cancel`.

**Reconnect after `OwnerLost`.** The reconnecting client's `_isOrderActive` bitset starts empty, so `TryAllocate` hands out `LocalIndex 0` again and the Create hits `OrderIndexIsBusy` against the orphan. Three coordinated rules make that self-healing instead of a livelock:
1. server: `OrderIndexIsBusy` against an `OwnerLost` slot is stamped `LedgerFlags.OrphanedExposure` and classified `OrderRefused` (§3.4) — recorded and alerted, no pause;
2. client: `Client.OnOrderRejected` does **not** free the local index for that reason (it stays marked active), so the allocator advances to the next free slot;
3. client: `Client.OnOrderState` frees on `Done` by **(ClientId, LocalIndex)** match rather than full `OrderId` equality (`Client.cs:505-515` today requires equality, which a previous incarnation's generation can never satisfy), so the orphan's terminal event returns the slot.

### 1.6 Worked example

`MaxPositionQuantity = 20`, `MaxOrderQuantity = 20`, `Position(i) = +10`, one buy order in slot `g`, no other live orders, **no acks arrive**. `OrderProfile.Quantity` is the total (working + filled); `Filled = 0` throughout. Initial: `ring=[]`, `Ack=0`, `M=0`, `E=0`, `Reserved.Buy=0`, `MPL=10`, `MPS=10`.

| # | Message | ring after | Ack | M | E | Resv.Buy | delta | MPL | MPS | Verdict |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | Create Seq1 Q=+5 | (1,5) | 0 | 5 | 5 | 5 | +5 | **15** | 10 | ACCEPT |
| 2 | Amend Seq2 Q=+8 | (1,5)(2,8) | 0 | 8 | 8 | 8 | +3 | **18** | 10 | ACCEPT |
| 3 | Amend Seq3 Q=+4 | +(3,4) | 0 | 8 | 8 | 8 | **0** | 18 | 10 | ACCEPT — *no release* |
| 4 | Amend Seq4 Q=+7 | +(4,7) | 0 | 8 | 8 | 8 | 0 | 18 | 10 | ACCEPT |
| 5 | Amend Seq5 Q=+2 | +(5,2) | 0 | 8 | 8 | 8 | 0 | 18 | 10 | ACCEPT |
| 6 | Amend Seq6 Q=+10 | +(6,10) | 0 | **10** | 10 | 10 | +2 | **20** | 10 | ACCEPT (inclusive) |
| 7 | Amend Seq7 Q=+3 | +(7,3) | 0 | 10 | 10 | 10 | 0 | 20 | 10 | ACCEPT |
| 8 | Amend Seq8 Q=+8 | +(8,8) | 0 | 10 | 10 | 10 | 0 | 20 | 10 | ACCEPT |

Eight targets in flight, ring depth 8 of 16. `WorkingBuyQty = 10` — the **largest** unacked quantity, not the latest (8), not the sum (47). Steps 3–5 and 7–8 are exactly where today's code is wrong: `RiskLayer.cs:270` uses `orderTarget.OrderProfile.Quantity` alone, so an amend-down to 4 currently *releases* exposure that may still be resting at CME.

Continuing, with the same slot, showing the cases that discriminate the new design:

| # | Event | ring after | Ack | M | E | Resv.Buy | Pos | MPL | MPS | Verdict / note |
|---|---|---|---|---|---|---|---|---|---|---|
| 9 | **Create B Q=+10**, different localIndex | — | 0 | 10 | 10 | 10 | 10 | — | — | trial: `delta=+10`, `resvBuy'=20`, `maxLong=30 > 20`, `delta>0` → **REJECT PositionTooLarge**. *Today: `currentPosition=10`, `projected=20`, `20 > 20` false → ACCEPT. Repeat 6× and 60 lots rest under a 20 limit with zero rejects.* |
| 10 | Ack `OrderState Seq=6 Q=+10` | (7,3)(8,8) | 10 | 10 | 10 | 10 | 10 | 20 | 10 | Watermark 0→6, prefix-drop ≤6. `M=max(10,8,0)=10`. delta 0. Nothing released — correct, 10 rests and two smaller amends are in flight. |
| 11 | Stale `OrderState Seq=4` | (7,3)(8,8) | 10 | 10 | 10 | 10 | 10 | 20 | 10 | `4 > 6` false → **ignored**. The `ServerSimulator.cs:603` Seq regression cannot un-retire. |
| 12 | **Exchange rejects Seq=7** | (8,8) | 10 | 10 | 10 | 10 | 10 | 20 | 10 | `RingRemoveExact(7)`; `M=max(10,8,0)=10`. **This is the interleaving that kills the deque designs** — a prefix pop here would discard `(6,10)`, and if the ack in row 10 had been lost to a ring lap, `Reserved` would collapse to 0 against 10 resting lots. |
| 13 | Ack `OrderState Seq=8 Q=+8` | [] | 8 | 8 | 8 | 8 | 10 | **18** | 10 | Ring empty, `M=max(8)=8`, `delta=−2`. **Two lots released here and only here** — when the ack proves nothing is outstanding. A Create B of Q=+2 now passes (`maxLong=20`); Q=+10 still fails (`28`). |
| 14 | **Fill +3** | [] | 8 | 8 | 5 | 5 | **13** | **18** | **13** | `PositionHeader::OnFill` first (`Position 10→13`), then `CreditedFillQty=3`, `E=max(0,8−3)=5`. **MPL invariant at 18** across the fill, in either Fill/OrderState arrival order. MPS moved from 10 to 13 — exactly the fill size, as it must. |
| 15 | Client process dies here instead | [] | 8 | 8 | 5 | **5** | 13 | 18 | 13 | `OwnerLost` stamped, `OrphanedExposure` on the `RiskState` row, alert. **`Reserved.Buy` stays 5** — a second client cannot claim the headroom while 5 may still rest. |
| 16 | `OrderState Done` | — | — | — | 0 | 0 | 13 | 13 | 13 | `Release(p)`, slot zeroed and reusable; orphan alarm clears. |

**Adversarial extras, run in the harness:**

- **Amend-down trap.** After row 6 (`M=+10`), Amend Seq7 Q=+2, then Create B Q=+8: `M` stays 10, `maxLong = 10+10+8 = 28 > 20` → **REJECT**. *Today the target slot reads +2, so B is judged as `10+8=18 ≤ 20` → ACCEPT, and if A's 10 is still resting both fill to 28 against a limit of 20.*
- **Ratchet.** MaxOrderQuantity 10, Create Q=+10, 9 fill, Amend Q=+19: `|19| > 10` → **REJECT QuantityTooLarge**. *Today `19−9 = 10`, not `> 10` → ACCEPT, and CME holds OrderQty 19.*
- **Overflow.** Create Q=+20 then 16 amends of Q=+1: on the 17th the oldest `(1,20)` is folded into `(OverflowQty=20, OverflowSeq=1)`; `M` stays 20 until an ack with `Watermark ≥ 1` clears it. Conservative, never lower.

---

## 2. Data structures

### 2.1 Process-local, server-only (no wire, no mirror, no persistence)

Allocated once in the `RiskLayer` constructor, exactly like `_secondRateLimits` at `Provider/RiskLayer.cs:25-26`, but via `NativeMemory.AlignedAlloc(len, Protocol.CacheLine)` (`Socket/Protocol.cs:27`) rather than `new T[]`. Managed arrays are not 64-byte aligned in .NET, so a 64-byte struct in a managed array straddles two lines; and indexing a `fixed` buffer through a managed array element is CS1666 — the codebase has already hit this. Freed in `Dispose`.

```csharp
// Provider/ExposureLedger.cs — NEW FILE. 64 bytes, exactly one cache line, 64-B aligned.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ReservationHeader
{
    public ulong OwnerOrderId;    // 8  @0   full 64-bit id incl. 32-bit generation; 0 == free. ABA tag.
                                  //         written with a release-store, read with an acquire-load
    public int   M;               // 4  @8   high-water |quantity| over Ack ∪ ring ∪ OverflowQty
    public int   AckedQty;        // 4  @12  |OrderState.OrderProfile.Quantity| at Watermark
    public int   CreditedFillQty; // 4  @16  |fills already applied to _serverPositionHeaders[i].Quantity|
    public int   E;               // 4  @20  contribution currently inside Reserved[]
    public int   Watermark;       // 4  @24  monotone max OrderState.Seq
    public int   LastSentSeq;     // 4  @28  monotone max Seq the SERVER forwarded  <-- replaces _orderTargets
    public int   OverflowQty;     // 4  @32  sticky max |q| of ring entries evicted by overflow
    public int   OverflowSeq;     // 4  @36  highest Seq folded in; cleared when Watermark >= it
    public int   PeakExcess;      // 4  @40  instrumentation: max(M - AckedQty) over the order's life
    public sbyte Sign;            // 1  @44  +1 buy / -1 sell, fixed at Create
    public byte  Head;            // 1  @45  ring head
    public byte  Count;           // 1  @46  live ring entries (0..16)
    public byte  Flags;           // 1  @47  Overflowed|FilledDivergence|SeededOnRestart|IsDone|OwnerLost
    public int   InstrumentId;    // 4  @48  cached — retire paths never re-unpack the OrderId
    public int   StrategyId;      // 4  @52
    public int   ClientId;        // 4  @56
    public int   OrphanSinceMs;   // 4  @60  ms since session start when OwnerLost was set; 0 == live
}                                 // 64
```

| Allocation | Shape | Size | Indexed by |
|---|---|---|---|
| `ReservationHeader* _reservations` | `ServerHeader.OrdersCapacity` = `OrdersPerClient(64) × ClientIds.Length(64)` = 4096 | 256 KiB | `OrderId.GlobalIndex` |
| `int* _ringSeq`, `int* _ringQty` | `4096 × Depth(16)` each | 256 KiB each | `g*16 + slot` — a slot's 16 entries are 64 B contiguous in each array |
| `Reservation* _reservedByInstrument` | `InstrumentIds.Length` = 64, padded to 64 B | 4 KiB | `instrumentId` |
| `Reservation* _reservedByStrategyInstrument` | `64 × 64` = 4096, 16 B | 64 KiB | **`instrumentId * ClientIds.Length + strategyId`** — the *transpose* of `Context.GetLocalPositionIndex` (`Provider/Context.cs:270-275`), so one instrument's 64 strategy rows are one contiguous 1 KiB block owned by exactly one CoreGroup thread |
| `bool* _recovering`, `bool* _blocked` | 64 each | — | `instrumentId` |

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Reservation { public long Buy; public long Sell; }   // long: MaxOrderQuantity can be int.MaxValue
```

Total process-local: **~840 KiB**, allocated once, L2/L3-resident. Hot working set per order: `_reservations[g]` (1 line), the ring tail (1 line), `_reservedByInstrument[i]` (1 line). `Depth = 16` — the user's pipeline is 8 deep; overflow is folded, not dropped, and instrumented.

**Threading.** One `RiskLayer` **per CoreGroup** (`Downloads/Server.hpp:243-247` runs one thread per CoreGroup; `Server.hpp:43` today has a single instance and `RiskLayer.hpp:15` claims "not thread safe"). `instrumentId → coreGroupId` is fixed via `instrument.Header().CoreGroupId` (`Server.hpp:233`), so `_reservedByInstrument[i]`, `_reservedByStrategyInstrument[i*64+s]`, `_recovering[i]`, `_blocked[i]` and both `RiskState` rows for `i` have exactly one writer thread — plain non-atomic access, no locks, no false sharing. **No aggregate in this design spans instruments**, which is what makes the sharding total. The only cross-thread object is `_reservations[g]`, because one client's 64 slots can hold orders on instruments in different CoreGroups; the handoff happens only at Create, so a release-store / acquire-load pair on `OwnerOrderId` is sufficient and there is no lock on the hot path.

This argument is currently **unevidenced**: `ServerSimulator.cs:960` pins every simulated instrument to `ExecutionCoreGroupId = 1` and `:1320` reads only that one channel, so no scenario can exercise a second shard or the cross-thread `OwnerOrderId` handoff. Step 2 parameterises the CoreGroup and Step 4 adds harness case 24; without it, state plainly in the report that sharding correctness rests on argument, not evidence.

`_maxClientOrderIds` (`RiskLayer.cs:18,24,119-126`; `RiskLayer.hpp:20,101`) is moved **out** of `RiskLayer` onto `Server` as an atomic array, so per-CoreGroup instancing does not silently shard the ClientOrderId watermark. It stops being bumped on the reject path (today `:125` fires before `OrderIndexIsBusy` is even evaluated at `:127-130`, so a `PositionTooLarge` reject burns its id and the retry returns `ClientOrderIdOutOfOrder`, which is not in `OrderDiscarded` and therefore escalates to a strategy kill). It is documented as a **replay guard, not a risk limit**, and is not presented to Wedbush as one.

### 2.2 Wire structs

**`Execution.RiskState` — 64 bytes, `Pack = 1`, `[RegisterJson]`, new shared array, server-written.**

| off | type | field | note |
|---|---|---|---|
| 0 | `Header<OrderType>` | `Header` | `OrderType.RiskState = 17` |
| 4 | `int` | `StrategyId` | `-1` == the server-wide row |
| 8 | `Timestamp` | `Timestamp` | when computed (8-aligned) |
| 16 | `Timestamp` | `RiskLimitTimestamp` | **the `RiskLimit` version in force** (`Order.cs:177`) |
| 24 | `ulong` | `Revision` | monotonic per row — an audit gap is detectable |
| 32 | `int` | `InstrumentId` | |
| 36 | `int` | `Position` | `_serverPositionHeaders[i].Quantity` |
| 40 | `int` | `WorkingBuyQty` | saturating cast from `long` |
| 44 | `int` | `WorkingSellQty` | positive magnitude |
| 48 | `int` | `MaxPotentialLong` | `Position + WorkingBuyQty`, saturating |
| 52 | `int` | `MaxPotentialShort` | `Position − WorkingSellQty`, saturating |
| 56 | `int` | `MaxPositionQuantity` | snapshot — the row is self-describing |
| 60 | `short` | `ActiveOrders` | |
| 62 | `byte` | `Flags` | `ReconcileMismatch \| AnyOverflowed \| Recovering \| OrphanedExposure \| LimitsNotProvisioned \| Blocked` |
| 63 | `byte` | `_reserved` | |

Every 8-byte field is 8-aligned, so `Pack=1` and natural packing agree and the struct cannot silently drift if a `#pragma pack` is dropped. `GetAlignedEntryLength(64) = (64+64+63) & ~63 = 128` (`Socket/Protocol.cs:40`), so the payload is exactly one cache line.

Array: `_riskStates = NewSharedArray<RiskState>(serverName / "RiskStates", (ClientIds.Length + 1) * InstrumentIds.Length, ServerAccess)` — 4160 entries × 128 B = **520 KiB**. Row index `= (strategyId < 0 ? ClientIds.Length : strategyId) * InstrumentIds.Length + instrumentId`, the same ordering as `GetLocalPositionIndex` with the server-wide row appended.

**Placement matters and is not cosmetic.** Create it as the **last statement of the `ServerContext` constructor**, after `_serverPositionHeaders` (`Provider/Context.cs:734`) — *not* in the base ctor. `arrayId` is the insertion index into a `Dictionary` keyed by `Count` (`Context.cs:244-251`), `TCPServer`'s `default:` case mirrors blindly with no type validation, and `SharedArray.Write` only rejects a too-*small* span — a mixed-build mirror is a silent memory-corruption path, not an error path. Appending in `ServerContext` takes arrayId 13 and leaves every `ClientContext` arrayId at 0..11 unchanged. The GUI can still read it: `ContextManager` constructs a `ServerContext` in every process (`Context.cs:36`).

**`Execution.RiskDecision` — 112 bytes, `Pack = 1`, `[RegisterJson]`, written to the AUDIT SOCKET ONLY. Never in a shared array, never on a client-facing execution channel.**

| off | type | field |
|---|---|---|
| 0 | `Header<OrderType>` | `Header` — `OrderType.RiskDecision = 18` |
| 4 | `OrderHeader` (28) | `OrderHeader` — **offset 4 is deliberate**: it matches `LoggingServer`'s private `OrderHead` (`Logging/LoggingServer.cs:1117-1121`), so `GetCreationTimestamp` (`:119-128`) and `GetSymbol` (`:1137`) work unmodified and the 10 ms watermark sort on the audit channel is not poisoned |
| 32 | `OrderProfile` (8) | `OrderProfile` — exactly as submitted |
| 40 | `Timestamp` | `RiskLimitTimestamp` |
| 48 | `ulong` | `DecisionSeq` — monotonic per writer |
| 56 | `Bitset64` | `OrderRejectedReasons` |
| 64 | `int` | `MaxOrderQuantity` |
| 68 | `int` | `MaxPositionQuantity` |
| 72 | `int` | `Position` |
| 76 | `int` | `WorkingBuyQty` |
| 80 | `int` | `WorkingSellQty` |
| 84 | `int` | `ReservedBefore` — `E(g)` before |
| 88 | `int` | `ReservedAfter` — `E(g)` after (== before if rejected) |
| 92 | `int` | `CreditedFillQty` |
| 96 | `int` | `InFlightCount` — ring `Count` at decision time |
| 100 | `OrderTargetAction` | |
| 101 | `OrderRejectedSource` | |
| 102 | `RiskDecisionOutcome` | `Rejected=0, Accepted=1, AcceptedReducing=2` |
| 103 | `LedgerFlags` | `ReduceExemption \| Overflowed \| Recovering \| LedgerUnknownSlot \| ManualWhilePaused \| FilledDivergence \| OrphanedExposure` |
| 104 | `byte[8]` | `_reserved` |

An auditor holding one `RiskDecision` line recomputes the verdict with no joins: `Position + WorkingBuyQty ≤ MaxPositionQuantity`, `Position − WorkingSellQty ≥ −MaxPositionQuantity`, `|OrderProfile.Quantity| ≤ MaxOrderQuantity`. **Emitted on every decision, accept and reject.** Rejects-only makes the *accepts* unexplainable, and "why was this order allowed" is the question being certified.

**Routing is a correctness constraint, not a preference.** `Client.OnSocketMessage` (`Provider/Client.cs:451-470`) switches on the type byte with `default: throw new NotImplementedException(...)`. Any new type reaching a client's server→client execution channel **kills the trading process**. `RiskDecision` therefore goes to the audit socket on the instrument's CoreGroup channel (`Server.hpp:684-686` `WriteToAudit`; `ServerSimulator.cs:1510-1511` `_audit.Write`) — the client has no use for it. `RiskState` goes to the **admin audit channel** only (`LoggingServer.cs:190-191`), never an execution channel — it has no `OrderHeader` and `GetCreationTimestamp` has no type dispatch. Independently, Step 1 changes `Client.cs:468` from `throw` to a counted-and-ignored default so a future wire addition degrades instead of killing a trading process, and fixes the **existing** instance of this landmine: `ServerSimulator.OnRiskLimit` at `:1297-1298` writes `RiskLimit` (type 16) onto the client's execution channel whenever `StrategyId >= 0` — today that is a client crash, not merely a logger misparse.

At the configured 300/s/instrument × 64 instruments `RiskDecision` is 112 B × 19,200/s ≈ **2.2 MB/s** on top of existing OrderState/Fill/Position traffic, through a logger that per pass allocates two `List<PooledBuffer>`, rents a buffer per record, and runs `executions.Sort(...)` recomputing `GetCreationTimestamp` inside the comparator plus a `FindIndex` under a 10 ms watermark (`LoggingServer.cs:139-200`). **Step 4 measures the logger's sustained record rate before Step 6 commits to emit-on-every-decision** and sizes the audit channel lengths (`ServerSimulator.cs:988-990`, `SocketChannel.BuildChannelLengths`) from the measurement. If the logger cannot sustain it, the fix is a wider ring or a dedicated `RiskDecision` audit channel — **not** dropping accepts, which would invert the whole rationale above.

**Nothing else changes shape.** `RiskLimit` stays 52, `OrderState` 60, `OrderTarget` 44, `PositionHeader` 57, `Fill` 52, `OrderRejected` 52. `PositionHeader` in particular is left alone: +8 would push 57→65 and bump the stride 128→192, growing `_localPositionHeaders` 512 KiB→768 KiB and spilling the payload out of one cache line.

---

## 3. Where enforcement lives

### 3.1 Server authoritative

The certified gate is `Server::OnOrderTarget` (`Downloads/Server.hpp:443-488`), the single point where **all** targets converge — client channels (`Server.hpp:262-289`) *and* the injection queue (`:249-260`). That convergence is also the fix for the GUI blind spot: `ManualClient.Send` (`Provider/Client.cs:91-101`) writes the OrderTarget slot only `if (isManualOrder)`, so a GUI amend of an algo order records no target anywhere and the algo's next `Seq = max(existing.Seq + 1, ...)` (`Client.cs:143`) collides. `p->LastSentSeq` fixes this at the convergence point, and the strict `t.Seq <= LastSentSeq → TargetIsStale` closes the equality hole at `RiskLayer.cs:209` that let two identical-Seq amends race at CME.

`ReadExecution` must be changed to **pass the loop's `clientId` into `OnOrderTarget`** (`Server.hpp:262-289`, `ServerSimulator.cs:1317-1334`); see §1.4 line 6. Without it every identity gate in the ledger is anchored on a field the sender chooses.

### 3.2 What the client-side check is still for

**Advisory admission filter, not a control.** Write that in those words in the certification package. It keeps four jobs:

1. **Latency** — reject locally instead of paying a shared-memory round trip, so the algo re-plans inside the same `Algo.Target()` call.
2. **Budget** — a server reject costs a CME message-rate credit and, today, escalates to `AlgoStatus::Paused` (`Server.hpp:431-436`). Filtering locally is cheaper than being right slowly.
3. **Idempotence/sequencing filters the server structurally cannot do as well** — `SeqOutOfOrder`, `CancelIsActive`, `TargetIsActive` (`RiskLayer.cs:236-248`) compare against the client's *own* target slot. These are no-op suppressors, not risk limits.
4. **Differential oracle, over the risk subset only.** The client runs the identical `ExposureLedger` over its own 64 slots and tests against the published `_riskStates` rows. The two enforcers run structurally different branches (`RiskLayer.cs:196-214` vs `:216-249`) and raise different *sequencing* reasons by construction — the client raises `SeqOutOfOrder`/`CancelIsActive` against its own target slot, the server raises `TargetIsStale` against `LastSentSeq` — so byte-identical bitsets are unachievable and would fail on day one for reasons unrelated to the ledger. The provable claim, and the harness assertion, is: on a single-client instrument the **risk subset** `{QuantityTooLarge, PositionTooLarge}` is byte-identical, and on every other reason `serverReasons ⊇ clientRiskReasons` (the client is strictly more permissive). That is still a strong C#/C++ and client/server equivalence test.

It must be documented as bypassable. It cannot compute the true answer even in good faith — it sees only its own 64 slots and can only under-count relative to the server.

### 3.3 Containing a compromised client

The claim to make is narrow and provable. **The server's risk decision is a pure function of:**

- the `OrderTarget` bytes off the socket (untrusted, fully validated — every field bound-checked, no field trusted for state, **and `OrderId.ClientId` cross-checked against the channel the bytes arrived on**, §1.4 line 6),
- `_orderStates` (`ServerAccess`, server sole writer — `Context.cs:234`),
- `_serverPositionHeaders` / `_localPositionHeaders` (`ServerAccess` — `Context.cs:237`, `:734`),
- `_riskLimits` (`ServerAccess` — `Context.cs:231`),
- server-process-private ledger memory.

**It reads `_orderTargets` — the one `ClientAccess` array (`Context.cs:235`) — nowhere**, on the decision path *or* the retire path (§1.5 hoists the `OnOrderState` hook above the `isSafeToOverwrite` gate at `Server.hpp:391`, which is itself a `_orderTargets` read; leaving the hook inside would let a client pin its own reservations forever and, under a firm-wide limit, deny headroom to every other client on the instrument).

The claim that must **not** be made: that `Access` is a security boundary. `Context.NewSharedArray` (`Context.cs:245-251`) opens a second, fully writable handle to *every* array for the TCP mirror — the comment at `:248` says so — reachable via `Context.Mirror` (`:252-255`). `RecoveryWrite` ignores `Access` entirely. The OS mapping is always `PROT_READ|PROT_WRITE` (`Tools/Memory.cs:157`, `:199-200`). And every client process constructs a `ServerContext` (`Client.cs:215` → `Context.cs:36`), so every client holds a writable handle to `_riskLimits`. **Action:** gate the mirror handle behind a build flag (`HFT_MIRROR`) so the certified configuration has no writable second handle, or state the write-authority story as "by call-site and convention" and let Wedbush decide. Do not ship an unqualified tamper claim.

### 3.4 Reject disposition — resolve this before writing any ledger code

`OrderRejected.OrderDiscarded` (`Execution/Order.cs:146-157`) does not contain `PositionTooLarge` or `QuantityTooLarge`, and `Server::Reject` calls `OnControlAlgoStatus(..., AlgoStatus::Paused)` for anything not a subset of it. **Every risk reject this plan correctly introduces pauses the strategy on that instrument until a human intervenes.** Today `PositionTooLarge` essentially never fires; after the fix it fires exactly when the strategy is at its limit — i.e. when it is working — and the correct behaviour ("refuse this order, keep quoting the other side") becomes "stop trading, page someone." This is the biggest operability risk in the whole change.

Introduce a third disposition:

```csharp
public readonly static Bitset64 OrderRefused;   // Execution/Order.cs, alongside OrderDiscarded
static OrderRejected()
{
    // OrderDiscarded: intentional no-ops. Unchanged EXCEPT TooManyOrdersPerSecond is removed.
    ...
    OrderRefused.Set((int)OrderRejectedReason.QuantityTooLarge);
    OrderRefused.Set((int)OrderRejectedReason.PositionTooLarge);
    OrderRefused.Set((int)OrderRejectedReason.TooManyOrdersPerSecond);
    OrderRefused.Set((int)OrderRejectedReason.TooManyOrdersPerSession);
    OrderRefused.Set((int)OrderRejectedReason.NotInSession);
    OrderRefused.Set((int)OrderRejectedReason.PositionIsSuspended);   // Blocked instrument
    OrderRefused.Set((int)OrderRejectedReason.OrderTypeNotSupported); // spread admission
}
```

Rules:
- `reasons ⊆ OrderDiscarded` → swallowed client-side (`Client.cs:643-644` unchanged), written to execution server-side, no pause.
- `reasons ⊆ (OrderDiscarded ∪ OrderRefused)` → **always** written to the socket and the audit trail, `OrderRejected` event raised, alert raised, **no pause**. Never swallowed by `Client.Reject`.
- `OrderIndexIsBusy` is classified `OrderRefused` **only** when the blocking slot carries `OwnerLost` (§1.5 reconnect rule), stamped `OrphanedExposure`; otherwise it keeps today's escalation, because then it is a genuine client sequencing bug.
- anything else → pause, as today.
- **plus** a per-`(strategyId, instrumentId)` consecutive-refusal counter: N consecutive `OrderRefused` with no accept in between → pause. A wedged algo still gets stopped; a working algo at its limit does not.

**A slot must not be burned by a refusal.** `OrderIdAllocator.Free` is called only from `Client.cs:513` (on a `Done` OrderState) and `Client.cs:583` (local validate failure); `Client.OnOrderRejected` (`:620-628`) frees nothing. C++ survives that because `Server::OnOrderTarget` writes a `Done` OrderState for an invalid Create (`Server.hpp:452-470`). RXL rejects Creates far more often, so: (a) Step 2 makes the simulator mirror the Done-OrderState write, and (b) `Client.OnOrderRejected` frees the local index for `OrderTargetAction.Create` — except when `reasons` contains `OrderIndexIsBusy`, where the index stays marked active so the allocator advances past the orphan. Without both, a strategy sitting at its limit exhausts all 64 slots and wedges on `CantAllocateClientOrderId`.

**The pause must survive a client restart.** `ServerSimulator.cs:1269` sets `AlgoStatus = Live` unconditionally on every `AllocateInstrument`, so today the entire escalation story is defeated by restarting the strategy process. Paused state is persisted server-side keyed by `(strategyId, instrumentId)`, re-applied after `AllocateInstrument`, and cleared only by an explicit `ControlAlgoStatus` on the admin channel.

Moving `TooManyOrdersPerSecond` out of `OrderDiscarded` is what fixes the worst audit finding in the repo: today a hard rate limit fires and `Client.Reject` returns at `:643-644` **before** `_socket.Write` and before `OrderRejected?.Invoke`, leaving zero trace anywhere. With the refusal path it reaches the audit file without paging anyone.

---

## 4. Working quantity exposure

### 4.1 Maintenance

`Reserved(i).Buy/Sell` are maintained by **delta** through the single `ApplyDelta` helper, which is the only writer. `E(g)` changes on exactly five events: `Commit` (accept), `OnOrderState` (ack), `OnFill`, `OnOrderRejected` (exchange), `Release` (exchange Done / exchange-rejected Create). Each calls `ApplyDelta` then `PublishRiskState(i, s)`.

**Drift control (fail-closed).** Once per second per instrument, on the owning CoreGroup thread between polls, `Reconcile(i)` recomputes `Σ E(g)` by scanning **all `OrdersCapacity` (4096) `_reservations` slots filtered on `OwnerOrderId != 0 && InstrumentId == i`** — *not* by walking `_clientIdsByInstrumentId[i]`, which `Context.DeallocateClient` (`Context.cs:834-844`) clears on every disconnect. Because §1.5 deliberately keeps a dead client's reservations live, using the shared liveness map as the iteration domain would make `recomputed < maintained` permanently, stamping `ReconcileMismatch` on every `RiskState` for the rest of the day and turning the drift artefact — the thing offered as proof the counter has not drifted — permanently red and therefore ignored. The ledger's own owner set is the correct domain; the shared map is liveness state, not ledger state.

On mismatch: **adopt `max(maintained, recomputed)`**, set `RiskState.Flags |= ReconcileMismatch`, bump `Revision`, write a `RiskState` audit line, raise an alert. Never adopt the smaller value.

`PublishRiskState` writes the seqlock slot unconditionally (readers use the validating `TryRead` path — `Socket/Protocol.cs:205-249` — so no reader spins), but emits the **audit** record only when `WorkingBuyQty`/`WorkingSellQty`/`Position` actually move. `Revision` gives gap detection either way. Per-order exposure history is exactly reconstructible from the `RiskDecision` stream, so `RiskState` audit lines are limited to limit changes, session boundaries, reconcile mismatches, orphan/blocked transitions and recovery transitions.

### 4.2 Where they live — recommendation

**Not on `RiskLimit`. On the new `RiskState` array.** Four independent disqualifiers, any one of which is sufficient:

1. **Persistence.** `ServerContext.AllocateInstrument` restores the **whole** struct from the last line of `<symbol>.risklimit` — `Provider/Context.cs:915-918`: `ReadLastLine` → `Json.Deserialize<RiskLimit>` → `_riskLimits.GetEntry(instrumentId).Write(riskLimit)`. Mirrored at `Downloads/Context.hpp:563-571`. A persisted `WorkingBuyQty` comes back at startup as **phantom exposure with zero orders in flight**, and no code path zeroes it.
2. **Lost update via a UI dialog.** `Widget/RiskLimitsWidget.axaml.cs:350` reads the whole struct, `:352-353` awaits a modal `ShowDialog`, `:358-359` sends the whole edited struct, `Client.cs:55-60` queues it, `ServerSimulator.cs:1296` `Write(in riskLimit)` overwrites all 52 bytes. A counter on `RiskLimit` is silently rewound minutes every time an operator edits a limit.
3. **Torn reads on the risk check itself.** `RiskLayer.cs:252` reads `RiskLimit` via `GetReadonlyRef()` — a raw pointer deref with **no seqlock validation** (`Socket/SharedArray.cs:57-61`). That is sound only because the slot is written approximately never. Making it hot forces every reader onto the retrying `Read()`/`TryRead` path, i.e. a spin loop on the order hot path.
4. **Wrong cardinality.** `_riskLimits` is 64 entries indexed by `instrumentId` only, and `StrategyId` is a single scalar documented as "server-wide is the only mode implemented" (`Order.cs:173-178`). The required shape is `(strategy, instrument)` = 4096, which matches `_localPositionHeaders`, not `_riskLimits`.

`PositionHeader` is rejected for a different reason: 57 B + 8 = 65 pushes the stride 128→192.

`RiskState` is never persisted, never GUI-editable, read only through the validating `TryRead` path, and dimensioned `(strategy+1, instrument)`. It also gives the GUI something real to render: `HeadroomStr` (`RiskLimitsWidget.axaml.cs:41-50`) is today `Max − |position|` and ignores working orders entirely; it becomes `MaxPositionQuantity − max(MaxPotentialLong, −MaxPotentialShort)` with WorkingBuy/WorkingSell columns.

**The per-strategy rows are indicative, not reconciled.** `_serverPositionHeaders[i]` is restored from `<serverName>/<symbol>.position` at server start (`Context.cs:920-926`) while `_localPositionHeaders[c][i]` is restored from `<clientName>/<symbol>.position` at **client connect time, mid-session** (`Context.cs:947-953`), overwriting the live row. Two independent files, no reconciliation, no invariant. Step 9 adds the check — on client connect, if `Σ local != server` for an instrument, the instrument enters `Blocked` (reduce-only, `PositionIsSuspended`) until an operator acknowledgement — and until that check ships, `Σ local == server` is asserted only intra-run (§6.2 case 13).

---

## 5. Implementation sequence

Each step builds and is testable on its own. **⚠ WIRE** = must land in C# and C++ in the same deployment.

---

**Step 0 — ⚠ WIRE. Repair the live `RiskLimit` break and install drift protection.** *Do this first, in its own commit, before anything else touches these headers.*

Verified: `/c/Home/cpp/HFT/Execution/Order.hpp:141-191` is under `#pragma pack(push, 1)` and is `Header(4) + InstrumentId(4) + MaxOrderQuantity(4) + MaxPositionQuantity(4) + RateLimit(12) + RateLimit(12) = 40`. C# is `Header(4) + InstrumentId(4) + Timestamp(8) + StrategyId(4) + MaxOrderQuantity(4) + MaxPositionQuantity(4) + 2×RateLimit(24) = 52` (`Execution/Order.cs:168-182`). Both languages map the same region (`Context.cs:231` vs `Downloads/Context.hpp:177`). **The deployed DC3 server has been reading `MaxOrderQuantity` out of the C# `Timestamp` bytes**, and the glaze map at `Order.hpp:179-190` omits both fields so the two sides serialize `<symbol>.risklimit` differently — which `Context.cs:917` then restores from.

- `/c/Home/cpp/HFT/Execution/Order.hpp` — add `Timestamp Timestamp` at offset 8 and `int32_t StrategyId = -1` at 16, plus both in the glaze map.
- Add `static_assert(sizeof(T) == N)` **and** `offsetof` asserts for `RiskLimit(52)`, `OrderState(60)`, `OrderTarget(44)`, `OrderHeader(28)`, `PositionHeader(57)`, `Fill(52)`, `OrderRejected(52)`. `grep static_assert` over that header returns nothing today.
- C# mirror-image: a static ctor with `Unsafe.SizeOf<T>()` and `Marshal.OffsetOf` checks, following the only existing precedent — `ServerHeader`'s at `Provider/Allocate.cs:97-104`.
- Add a **layout dump** (struct name, sizeof, every field offset) emitted by both binaries and diffed in CI. ~50 lines, and the cheapest audit evidence in the package.
- Confirm against the deployed build and report to Wedbush as **found-and-fixed**, not discovered in review.

**Step 1 — ⚠ WIRE (behavioural, no layout change). Reject disposition and wire-safety.**
- `Execution/Order.cs:146-157` + `/c/Home/cpp/HFT/Execution/Order.hpp` — add `OrderRefused`, remove `TooManyOrdersPerSecond` from `OrderDiscarded`.
- `Downloads/Server.hpp:431-436` `Reject` — escalate only outside `OrderDiscarded ∪ OrderRefused`; add the consecutive-refusal counter.
- `Provider/Client.cs:643-647` — never swallow `OrderRefused`; delete the simulation-only `TooManyOrdersPerSession` drop at `:646-647`.
- `Provider/Client.cs:468` — replace `default: throw new NotImplementedException` with a counted, ignored default (`UnknownMessages += 1`) so a wire addition can never kill a trading process; add the same guard to the C++ client read loop.
- `ServerSimulator.cs:1297-1298` — stop writing `RiskLimit` to a client execution channel; route limit updates to the admin channel. (Today this is a client crash the moment `StrategyId >= 0`.)
- `Client.OnOrderRejected` — free the local index on a rejected Create except for `OrderIndexIsBusy`; `Client.OnOrderState` — free on `Done` by `(ClientId, LocalIndex)` rather than full `OrderId` equality (§1.5 reconnect rule).
- `Simulator/ServerSimulator.cs` reject path to match.

**Step 2 — C# only. Make the simulator able to demonstrate anything.**
- ctor: `_riskLayer = new RiskLayer(_context, OrderRejectedSource.Server)`. There is **no `new RiskLayer` anywhere under `Simulator/`** today, so the `OrderRejectedSource.Server` branch that runs in production has zero coverage in the only harness that can produce reproducible evidence.
- `OnOrderTarget` (`:1432-1459`) — take the arriving `clientId` from the read loop (`:1317-1334`) and pass it to `ValidateOrder`; on reject build an `OrderRejected` **and write the `Done` OrderState exactly as `Server.hpp:452-470` does** before routing as `:479-487`. Note the ordering: the Active `OrderState` is written at `:1457` *after* validation, matching `Server.hpp:452-470`.
- **Limits fail closed.** Delete `RiskLimit.GetMaxLimits` (`Order.cs:189-196`). `AllocateInstrument` with no `<symbol>.risklimit`: in realtime → `GetMinLimits` + `_blocked[i] = true` (reduce-only, `PositionIsSuspended`) until provisioned over admin; in simulation → the scenario's mandatory `DefaultRiskLimit` (finite, recorded in the run manifest), with `RiskState.Flags |= LimitsNotProvisioned` on the row so the audit shows which instruments ran on the default. Today the simulation default is `int.MaxValue`, so **no backtest in this repo's history has ever exercised a quantity limit**, and the harness would silently inherit that.
- `Strategy/Scenario.cs:50-57` — add `SetRiskLimit(Instrument, in RiskLimit)` (today it sets only the two `RateLimit`s) and `DefaultRiskLimit`.
- **Determinism.** `Execution/OrderIdAllocator.cs:44` seeds `s_generation` from `DateTimeOffset.UtcNow` — every run produces different generations, so every `.audit` file differs byte-for-byte and `_maxClientOrderIds` ordering (`RiskLayer.cs:119-126`) differs run to run. Seed from `Clock` when `Clock.Mode == ClockMode.Simulation`, else from the run-manifest seed. Same treatment for `Data/MarketByPrice64Test.cs:15` (unseeded `new Random()`) and `:84` (`DateTime.UtcNow`) before that test is offered as evidence.
- **CoreGroups.** Parameterise `ExecutionCoreGroupId` (`:960`, `:1174`) so a scenario can place instruments on ≥2 CoreGroups, and make the client read loop at `:1320` iterate `serverHeader.CoreGroupIds` instead of the single constant. Without this the §2.1 sharding argument can never be exercised.
- **Spreads.** Resolve `spread.ShortInstrumentId`/`LongInstrumentId` in `:1197-1198` (hardcoded `-1` today, and `Context.cs:443-444` calls `GetInstrument(sh.LongInstrumentId)`) so the spread-admission reject can be evidenced now and leg fan-out is buildable in phase 2.
- `ServerSimulator.cs:964` — the certification path passes `startLogginServer: true` so the run and its audit are produced by one command. Instrument the logger's sustained record rate here (§2.2).

**Step 3 — C# only. Fix the Seq regression.** `Simulator/ServerSimulator.cs:603` — assign `orderState.OrderHeader.Seq = orderTarget.OrderHeader.Seq` only on accept, mirroring the `isSeqInOrder` guard at `Downloads/Server.hpp:389-391`. The ledger's monotone watermark is immune either way, but the harness must not disagree with production.

**Step 4 — C# only. Certification harness skeleton, with the current bugs as *failing* cases.** New `Certification` Exe project in `HFT.slnx` (`HFT.targets:4` forces `OutputType=Exe`; the repo has no test framework and the house assertion idiom is `throw new Exception("invariant")` — `Data/MarketByPrice64Test.cs:59-70`). Land the scenarios from §6 against the *unfixed* `RiskLayer` and confirm they fail. This is what proves the harness detects the defects rather than merely agreeing with the new code. Publish the logger-throughput measurement here; Step 6 sizes channels from it.

**Step 5 — C# only, no wire change. The ledger.** New `Provider/ExposureLedger.cs` (`ReservationHeader`, `Reservation`, ring ops, `Trial`/`Commit`/`ApplyDelta`/`Release`/`Recompute`/`Reconcile`/`Seed`/`OnOwnerLost`). Rewrite `Provider/RiskLayer.cs`:
- ctor takes a **writable** `ServerContext` when `orderRejectedSource == Server`. Today `Client.cs:215` hands it the `Access.Read` context from `Context.cs:36`, so neither enforcer can publish anything.
- replace `:158-329` with §1.4; delete the `try`/`catch` at `:162`/`:321-325`; delete the `_orderTargets` reads at `:171`/`:209`; move `_maxClientOrderIds` to `Server`.
- **Rate limiters stay long-lived objects.** `ServerSimulator.OnRiskLimit` (`:1294-1300`) never stamps `Timestamp` despite the comment at `:1292-1293` claiming it does, so a "re-derive the limiter when `RiskLimit.Timestamp` changes" rule would either never fire or, when it did, allocate a `Timestamp[Limit]` (`RateLimit.cs:206-211`) **on the order path** and discard the rolling window — making a GUI limit edit a rate-limit reset. Instead: stamp `riskLimit.Timestamp = Clock.Now` in `OnRiskLimit` (and the C++ equivalent) so `RiskDecision.RiskLimitTimestamp` is a real join key, and **mutate the existing limiter's configured limit in place**. `UpdateRiskLimit` (`:32-38`) — whose only non-ctor caller is test-only (`Strategy/Scenario.cs:56`) — becomes the in-place mutator and is called from `OnRiskLimit`.
- **Reset triggers unified.** `SessionRateLimit.Reset` moves from `SessionManager.Changed` (`RiskLayer.cs:44-47`, which fires on open *and* close — `InstrumentManager.cs:41-59`, i.e. twice per boundary) to `Closed`, matching `MessageEfficiency.Reset` (`Context.cs:589-598`). No ledger hook on either event.
- new hooks `OnOrderState` / `OnFill` / `OnOrderRejected` / `OnOwnerLost`, wired into `ServerSimulator` at `:1545-1563`, `:1484-1513` (after both `OnFill` position updates at `:1498`/`:1505`), `:1462-1482`, `:334-364`.
- Step 4's cases now pass. Ledger is process-local; nothing is published yet.

**Step 6 — ⚠ WIRE. Publish and record.** `RiskState` + `RiskDecision` + `OrderType.RiskState = 17` / `RiskDecision = 18` + `RiskDecisionOutcome` + `LedgerFlags` in `Execution/Order.cs`; `_riskStates` as the last array in the `ServerContext` ctor (`Context.cs:734`) + `GetRiskState(strategyId, instrumentId)`; zero the instrument's `RiskState` rows in `AllocateInstrument` after the `RiskLimit` restore. `Logging/LoggingServer.cs:1112-1113` — add both cases **and** change `default: return ""` to emit `{"UnknownType":N,"Bytes":"<hex>"}`, which converts a silent evidence-loss mode into a detectable one. `RiskDecision` → audit socket, instrument CoreGroup channel; `RiskState` → admin channel; channel lengths sized from Step 4's measurement. Mirror `Order.hpp` + `Context.hpp` in the same commit with `static_assert`s. Ship the array name/version handshake in `Provider/TCPServer.cs:357-360` `ContextProxy.NewPacket` in this commit if the mirror is in the certified configuration.

**Step 7 — ⚠ WIRE (code, not layout). C++ enforcement.**
- New `/c/Home/cpp/HFT/Provider/ExposureLedger.hpp` — behavioural mirror; process-local, so no byte-for-byte obligation.
- `/c/Home/cpp/HFT/Provider/RiskLayer.hpp` — ctor takes `Provider::ServerContext&` (the server's writable one, `Downloads/Server.hpp:40`, `:107`) instead of constructing its own `Access::Read` context at `:19`, `:24-27`; replace `:224-262`; delete the `try`/`catch` + `std::cerr` at `:264-267`; delete `_maxClientOrderIds`.
- `Downloads/Server.hpp` — `RiskLayer _riskLayer` at `:43` / `:112` becomes `std::vector<RiskLayer>` indexed by `coreGroupId` (derive from `instrument.Header().CoreGroupId`, `:233`); `_maxClientOrderIds` becomes a `std::atomic<uint64_t>` array on `Server`. `ReadExecution` (`:262-289`) passes its loop `clientId` into `OnOrderTarget`. Hooks: `OnOrderTarget` `:450` (validate → forward at `:455` → publish → `WriteToAudit(riskDecision)`); `OnOrderState` **above** the `isSafeToOverwrite` gate at `:391`; `OnOrderRejected` at `:411`; `OnFill` after the two `PositionHeader::OnFill` calls at `:562`/`:569`; `OnOwnerLost` from `PollDisconnects` (`:201-226`) and `OnClientClosed` (`:761-766`). `CancelAllOrders` (`:305-329`) enters via `ValidateInjectedCancel`; assert `EnqueueOrderTarget` (`:230`) can only carry `Cancel`.
- **Port `RollingRateLimit`.** `RiskLayer.hpp`'s class body (`:16-272`) has no rate-limit members at all — in production today, `MaxOrdersPerSecond` and `MaxOrdersPerSession` are enforced **only inside the client process being policed**. Without this port the control does not exist.

**Step 8 — C# only. GUI.** `Widget/RiskLimitsWidget.axaml.cs:41-52` — `HeadroomStr` from `RiskState`; add WorkingBuy/WorkingSell/MaxPotential columns and the `OrphanedExposure`/`Blocked`/`LimitsNotProvisioned` indicators; `MaxOrdersPerDayStr` (`:51`) either gets a working `MaxOrdersPerSession` or is deleted (see §7).

**Step 9 — both. Restart, recovery, blocking.** `Seed()` at startup; `_recovering[i]` gate; the `Σ local != server` connect-time check (§4.2); the case-(b) hard `Blocked` state; persisted `AlgoStatus.Paused` re-applied after `AllocateInstrument` (`ServerSimulator.cs:1269`); orphan staleness alarm.

**Step 10 — both. Reconcile, `PeakExcess` instrumentation, alerting, re-enable the `Latency`/`CallId` probes** commented out at `Client.cs:592`, `:601`, `:605` and `Algo.cs:53`, `:69` (`Testing/Strategy.cs:58-74` already plots them) and publish the measured ns distribution.

---

## 6. Certification evidence

### 6.1 Harness

New `Certification` Exe project beside `Testing`. `CertificationScenario : Scenario` with `ClockMode.Simulation`, fixed `Begin`/`End` (`Clock.cs:104` makes simulation time fully deterministic), a mandatory `DefaultRiskLimit`, and an explicit RNG/generation seed carried in the manifest. `new ServerSimulator(name, startLogginServer: true)`. A `CertificationAlgo : Algo` replays scripted `OrderTarget` sequences through `Algo.Send(ref OrderTarget)` (`Algo.cs:458-461`) rather than market-driven `Target()`, with acks withheld via `ExchangeOrderQueueLatency` (`ServerSimulator.cs:944`, set per scenario at `Testing/Scenario.cs:171-173`) so several amends are unacked simultaneously. Assertions are `throw new Exception(...)` per the house idiom, caught by a case runner that prints PASS/FAIL and sets `Environment.ExitCode` — allocation is fine at the case boundary; the no-alloc rule binds `Scenario → Strategy → Algo → Client`.

**Determinism is an asserted property, not a claim.** The seeding fixes in Step 2 close the three known sources of run-to-run variation (`OrderIdAllocator.cs:44` `DateTimeOffset.UtcNow`, `MarketByPrice64Test.cs:15` unseeded `Random`, `:84` `DateTime.UtcNow`); case 21 then runs the same scenario twice and diffs the two `.audit` files byte-for-byte. That diff **is** the reproducibility artefact.

**Chain of custody.** Every run writes into an immutable per-run directory keyed by run id + git SHA, and must **not** inherit the wipe behaviour at `Provider/Client.cs:197-203` and `Strategy/Strategy.cs:112-128`, which delete the previous run's evidence. The `.audit` filename today comes from `DateTime.UtcNow.Date` (`LoggingServer.cs:995-1008`) while the simulated range is 2026-07-01..21 (`Testing/Scenario.cs:54-55`) — the manifest carries the business date and the run directory name carries both.

Also give `Data/MarketByPrice64Test.Run()` a caller; it is the only invariant-checking code in the repo and nothing invokes it.

### 6.2 Scenarios that must be demonstrated

| # | Scenario | Assertion |
|---|---|---|
| 1 | **User's worked example** — Create 5, amends 8/4/7/2/10/3/8, no acks | all 8 accepted; `WorkingBuyQty == 10`; `MaxPotentialLong == 20`; the state table in §1.6 reproduced from the `RiskDecision` stream |
| 2 | Create B `+10` while all 8 are unacked | **REJECT** `PositionTooLarge`; run against the pre-fix build to show it is accepted |
| 3 | Amend-down trap (Seq7 → `+2`, then Create B `+8`) | **REJECT**; `M` stays 10 |
| 4 | Unacked cancel | `delta == 0`; `Reserved` unchanged until `Done` |
| 5 | **Exchange rejects Seq7 with the OrderState for Seq6 lost to a ring lap** | `Reserved` stays 10. Run the same input through a monotonic-deque reference implementation and show it collapses to 0 — the evidence for the "why not the textbook algorithm" section |
| 6 | Fill/OrderState delivered in both orders | potential on the **fill's own side invariant**; opposite side moves by exactly the fill (§0 correction) |
| 7 | Stale `OrderState` (Seq regression) | ignored; nothing un-retires |
| 8 | Slot reuse: late ack for generation N after Create of N+1 | ledger untouched; `OwnerOrderId` gate holds |
| 9 | 17+ unacked amends | `OverflowQty` folded not dropped; released only when `Watermark ≥ OverflowSeq`; `Overflowed` stamped on every subsequent `RiskDecision` |
| 10 | Ratchet: `MaxOrderQuantity=10`, Create 10, 9 fill, Amend 19 | **REJECT**; and Create-19 vs Amend-to-19 produce identical reason bitsets |
| 11 | Reduce exemption: Position `+25`, Max 20 | sell 50 REJECT, sell 45 ACCEPT, sell 5 ACCEPT, buy 1 REJECT; every accept stamped `ReduceExemption` |
| 12 | Boundary | `|potential| == Max` accepts, `Max+1` rejects, both directions, both limits |
| 13 | Multi-client aggregation: 6 clients × `+10` | client 2 onward rejected; server-wide `RiskState` row == sum of per-strategy rows **within a single run** (cross-restart equality is not asserted — §4.2) |
| 14 | Rate limit fires, then the strategy process restarts | an `OrderRejected` reaches the socket **and** an audit line exists **and** the strategy is not paused; after N consecutive refusals it *is* paused, and the pause survives reconnect |
| 15 | Reconcile: inject deliberate drift, then disconnect a client holding reservations | `max(maintained, recomputed)` adopted, `ReconcileMismatch` set once; **no** spurious mismatch from the disconnect (domain = `_reservations`, not `_clientIdsByInstrumentId`) |
| 16 | Differential oracle on a single-client instrument | `{QuantityTooLarge, PositionTooLarge}` byte-identical client vs server; `serverReasons ⊇ clientRiskReasons` elsewhere |
| 17 | Restart with clients attached | seeded conservatively; `Recovering` admits only `delta ≤ 0`; clears on first ack per slot |
| 18 | Explicit `int.MaxValue` limits | no overflow; `MaxPotentialLong` saturates rather than wrapping negative |
| 19 | **Client dies with 8 unacked amends** | `Reserved` **unchanged**; `OwnerLost` + `OrphanedExposure` + alert; a second client's Create for the freed-looking headroom is **REJECTED**; `Reserved` drops only on exchange `Done` |
| 20 | **Reconnect over orphans** | Create hits `OrderIndexIsBusy`, classified `OrderRefused`, no pause; allocator advances to the next free local index; orphan `Done` frees the slot by `(ClientId, LocalIndex)` |
| 21 | **Same scenario run twice** | the two `.audit` files are byte-identical |
| 22 | **Cross-client slot theft** — client A sends an OrderTarget carrying client B's `ClientId` | **REJECT** `ClientIdIsWrong`; B's ledger slot untouched |
| 23 | **Unknown type byte injected into a client execution channel** | client counts and ignores; process survives; logger emits `{"UnknownType":...}` |
| 24 | **One client, two orders, two instruments in two CoreGroups** | both `RiskState` rows correct; no lost reservation across the `OwnerOrderId` handoff |
| 25 | **Spread instrument target** | **REJECT** `OrderTypeNotSupported`, classified `OrderRefused`; legs resolvable in the header so the phase-2 fan-out has a fixture |
| 26 | **Unprovisioned limits** | realtime path: instrument `Blocked`, increases REJECT `PositionIsSuspended`, cancels ACCEPT; simulation path: `DefaultRiskLimit` in force and `LimitsNotProvisioned` stamped on the row |
| 27 | **100 consecutive `PositionTooLarge` Creates** | `_isOrderActive` returns to empty; no `CantAllocateClientOrderId`; no pause until the consecutive-refusal threshold |
| 28 | **Session close/open with live orders** | reservations retire via exchange `Done` only; no reservation is zeroed at session *open*; `SessionRateLimit` resets exactly once, on `Closed` |

### 6.3 Artefacts handed to the auditor

1. **Run manifest** — git SHA, `SimulationBegin`/`End`, RNG + generation seed, the `RiskLimit` JSON in force per instrument (including which rows used `DefaultRiskLimit`), tick-file list + hashes, harness version. None of this exists today; the tick source is a bare unversioned network path (`Testing/Scenario.cs:174-182`).
2. **The `.audit` file** — one self-describing JSON line per record, including a `RiskDecision` for every decision and an `{"UnknownType":...}` line for anything unrecognised. `Provider/Context.cs:104-113`, `LoggingServer.cs:878` (`FileMode.Append | FileOptions.WriteThrough`).
3. **Reproducibility diff** — case 21's byte-identical pair of audit files from two independent runs.
4. **Layout dump diff** — C# and C++ struct sizes and field offsets, byte-identical, produced by both binaries.
5. **Enforced-limit matrix** — generated *from the enforced set*, not from the `OrderRejectedReason` enum: every reason × {enforced where, tested by which case}. `ClientIdIsWrong`, `OrderTypeNotSupported` and `PositionIsSuspended` move into the enforced set with this work; the remaining dead rows (`MessageEfficiencyViolated`, `NotEnoughMargin`, `TooManyActiveOrders`, `NotAuthorizedToTrade`, `DuplicateOrderId`, `ConnectionBroken`, `PriceNotValid`, `SeqIsWrong`, `CreateIsActive`) become the honest scope statement.
6. **Reset/lifecycle semantics table** — for each of {position, reservation, second-rate counter, session-rate counter, message-efficiency counter, algo pause}: what event resets it (`Closed` / exchange `Done` / never / admin command), and what explicitly does **not** (session `Open`, client disconnect, client reconnect, server restart).
7. **Hot-path cost** — the ns distribution around `ValidateOrder` from the re-enabled `Latency` probes, plus the logger's measured sustained record rate against the emitted `RiskDecision` rate.
8. **Prose certification report** in the style of the existing `OrderIdRefactorReport.md`, containing: the limit definitions in words (inclusive bounds, net-not-gross, total-not-remaining, firm-wide-not-per-strategy, outrights-only), the safety invariant including (I-life), the "why not a monotonic deque" section, the found-and-fixed `RiskLimit` wire break, and the residual items in §7 stated as limitations rather than omitted.

`AlertManager` is **not** offered as evidence — `Provider/AlertManager.cs:76-77`, `:88-89`, `:99-101` swallow every failure with bare `catch {}`, so alert loss is undetectable. It is the right channel for operator notification and the wrong one for the record.

---

## 7. What this does not cover

**Resolved but with a caveat, stated for the record:**

- **Server restart with clients still attached** — conservative seeding (`M = min(MaxOrderQuantity, MaxPositionQuantity)` for every slot with `OrderStateStatus == Active`) plus a per-instrument `Recovering` mode admitting only `delta ≤ 0` until every seeded slot's `Watermark ≥ LastSentSeq`. Self-heals on the first ack per slot. `LastSentSeq` is seeded from `max(OrderState.Seq, _orderTargets[g].Seq)` — the one place client-writable memory is read, and it is only used to *reject* stale targets, so a lying client can only hurt itself. The `Σ local == server` position check (§4.2) runs at the same point and blocks the instrument reduce-only on mismatch.

**Not resolved:**

- **Spread leg exposure.** Spreads are refused at admission (`OrderTypeNotSupported`), so the certified statement is "RXL covers outrights; spread trading is disabled in the certified configuration." Leg fan-out — testing and crediting `i`, `LongInstrumentId` and `ShortInstrumentId` with correct signs — is phase 2 and needs the leg-resolution fix from Step 2 plus a leg-aware `OnFill`, since `Server::OnFill` and `ServerSimulator.cs:1495-1506` move only the traded instrument's `PositionHeader` today.
- **Server restart with all clients also gone.** The shared regions are `/dev/shm` files (`Tools/Memory.cs:219-266`); `TryUnlinkIfOrphan` (`:226`, `:399-422`) takes `LOCK_EX|LOCK_NB` and **deletes the backing file** when no process holds `LOCK_SH`. On recreate, `_orderStates` reads back all-zero, i.e. every slot `Done`. `AllocateInstrument` restores `RiskLimit` and `_serverPositionHeaders` from disk but there is **no restore path for `_orderStates`**, and `CancelAllOrders` is a no-op because `Server.hpp:311-312` skips empty target entries. **Mitigation shipped:** if `_orderStates` is entirely zero and the restored `<symbol>.position` is non-flat, the instrument enters `Blocked` (reduce-only) until an operator acknowledgement arrives on the admin channel. **Phase 2:** the `RiskDecision` audit stream is a complete durable record of every target the server forwarded, with Seq and quantity — it can rebuild the ledger. Do not claim restart-exact recovery.
- **Per-strategy `MaxPositionQuantity`.** `RiskState` is dimensioned `(strategy, instrument)` and `_reservedByStrategyInstrument` is maintained, so per-strategy exposure is **published**. It is **not enforced**: `MaxPositionQuantity` binds the server-wide `_serverPositionHeaders[i].Quantity`, so two strategies at `+70` and `−70` both pass because the aggregate reads 0. Write down explicitly: *today `MaxPositionQuantity` is a firm-wide, per-instrument, net limit.* The per-strategy rows are additionally *indicative, not reconciled* until the connect-time `Σ local == server` check ships (§4.2).
- **Contract rollover.** There is no instrument deallocation anywhere (`InstrumentIds.LowestClear`, `Context.cs:905`; no `Clear`), so each roll consumes one of 64 `instrumentId`s and the new front month has no `<symbol>.risklimit`. With `GetMaxLimits` deleted the failure is now loud (`Blocked` + `LimitsNotProvisioned`) instead of silent-unlimited, but **automatic inheritance of the prior contract's limits by product root is not implemented** — it is a documented operational procedure in the certification package, not a control.
- **Gross exposure.** The model is net position plus a one-sided worst case, evaluated twice. If Wedbush wants gross, add `WorkingBuyQty + WorkingSellQty ≤ MaxGross` — one more comparison against the same ledger, no structural change.
- **Audit ring lapping.** `Socket/Protocol.cs:9-15` has no `Lapped` `ReadStatus` and `WriteToRing` (`:271-332`) never consults a reader cursor. `RiskDecision` records can be silently lost under load. `DecisionSeq` makes a gap **detectable**, not recoverable; the `{"UnknownType":...}` default (Step 6) closes the other silent-loss path. The ledger itself is immune (retirement is watermark/prefix-based and idempotent); the evidence file is not. Either add gap detection to the ring or state the retention limitation.
- **`Access` as a mechanism.** Until the `Context.Mirror` write handle is gated (Step 6 option), the tamper story is "by call-site and convention", not "by page protection".
- **`MaxOrdersPerSession`.** Entirely commented out (`RiskLayer.cs:298`, `:301`, `:303`, `:307`, `:314-317`) while being persisted in the wire struct, settable from `Scenario`, and displayed in the GUI as a live limit (`RiskLimitsWidget.axaml.cs:52`). **Recommendation: delete it from `RiskLimit`, the GUI and the enum before Wedbush sees it.** If kept, finish it in Step 7 alongside the C++ rate-limit port, and move its counter state out of the process-local `RollingRateLimit`/`SessionRateLimit` heap objects (`Execution/RateLimit.cs:171-239`) into shared or emitted state, because a replay cannot currently reconstruct whether the limiter would have fired at time T.
- **Message-rate driver.** `RiskLayer.cs:296` takes `orderTarget.OrderHeader.NicTimestamp`, which `Client.cs:530` sets from the last tick's timestamp, not `Clock.Now`. In a data gap the rolling window stops advancing (`RateLimit.cs:216-224`). Unchanged by this work; flag it in the report.
- **`MessageEfficiency`.** `Client.cs:546` performs a non-atomic read-modify-write on a `ClientAccess` shared entry (`Context.cs:232`) from **every** client process against the same product-group row, always counts (`RateLimit.cs:110-117`) including messages the server later rejects, and calls `.Send(...)` rather than `TrySend` so nothing is enforced. The server counts nothing, despite seeing every forwarded target at `Server.hpp:443`. Moving the counter server-side is the right fix and is out of scope; until then the report states that `MessageEfficiency` is a client-side estimate, not a control, and it is **not** offered as evidence.
- **Session gating** is unchanged by this work; `NotInSession` behaviour is as-is.
- **Latency is measured, not guaranteed.** The send path adds roughly 4–6 cache lines and ~20 instructions over `RiskLayer.cs:267-293`, and it *removes* the `try`/`catch` frame and the interpolated-string `Console.WriteLine` on the CoreGroup poll thread. The publish and the 112 B audit write are ordered **after** the exchange forward, so wire-to-wire grows only by the ledger arithmetic. Ship the measured before/after distribution rather than the estimate.

---

## Appendix — disposition of every fatal flaw raised

| # | Flaw | Raised against | Disposition |
|---|---|---|---|
| 1 | Create contributes zero exposure (`Done` short-circuit) | D1 | **Avoided.** Base is D2, whose send path never reads `OrderState`; `M=0` on a fresh slot ⇒ `delta` = full quantity. Verified against `RiskLayer.cs:127-130` and `Server.hpp:452-470`. |
| 2 | `OnOrderRejected` zeroes the peak / prefix-pops applied entries | D1, D3 | **Avoided.** `RingRemoveExact` removes exactly the rejected Seq. Case 5. |
| 3 | All-or-nothing release starves under continuous quoting | D1 | **Avoided.** Release is per-entry prefix-drop on `Watermark`. |
| 4 | Impure trial mutates the ledger during validation | D1 | **Fixed.** `Trial` is pure; nothing is written before `reasons.IsEmpty`. |
| 5 | No generation guard on `OnFill`/`OnOrderRejected` | D1 | **Fixed.** Every mutation gates on full 64-bit `OwnerOrderId`. |
| 6 | Exposure published on an execution channel poisons `GetCreationTimestamp` | D1, D2 | **Fixed.** `RiskState` → admin; `RiskDecision` → audit socket with `OrderHeader` at offset 4. |
| 7 | Overflow drops the oldest entry | D2 | **Fixed.** Folded into sticky `(OverflowQty, OverflowSeq)`. |
| 8 | Cancel rejected as `OrderNotFound` on `OwnerOrderId` mismatch | D2 | **Fixed.** §1.4 line 34 carve-out, stamped `LedgerUnknownSlot`. |
| 9 | Reconcile only alerts, leaving a drifted counter in force | D2 | **Fixed.** Adopts `max(maintained, recomputed)`, fail-closed, with an audit line. |
| 10 | Server branch still reads `_orderTargets` (`ClientAccess`) | D2 | **Fixed** on the send path (`LastSentSeq`) **and** the retire path (hook hoisted above `isSafeToOverwrite`, #29). |
| 11 | Two seqlock publishes on wire-to-wire | D2 | **Fixed.** Publish and audit ordered after the exchange forward. |
| 12 | Prose/pseudocode contradiction on `Overflowed`; misaligned 8-byte fields | D3 | **Avoided.** Every 8-byte field 8-aligned; overflow behaviour is in the algorithm. |
| 13 | `OrderState == 56` in the static-assert list | D3 | **Corrected to 60**, verified at `Execution/Order.cs:303-319`. |
| 14 | Per-CoreGroup `RiskLayer` silently shards `_maxClientOrderIds` | D3 | **Fixed.** Moved onto `Server` as an atomic array; documented as a replay guard. |
| 15 | Self-contradictory array placement | D3 | **Fixed.** `ServerContext` ctor only, after `_serverPositionHeaders`. |
| 16 | Removing `TooManyOrdersPerSecond` from `OrderDiscarded` turns a throttle into a kill switch | D3 | **Fixed.** `OrderRefused` disposition (§3.4). |
| 17 | **Every risk reject pauses the strategy** | all | **Fixed.** §3.4, plus the consecutive-refusal escalation. |
| 18 | Restart case (b): regions unlinked and zeroed | all | **Partially unresolved and stated as such.** Hard `Blocked` + operator ack shipped; journal rebuild is phase 2. |
| 19 | Managed-array alignment / CS1666 in the ledger | D2, D3 | **Fixed.** `NativeMemory.AlignedAlloc`; ring in flat `int` arrays. |
| 20 | C++ `RiskLimit` 40 vs C# 52, no `static_assert` anywhere | all | **Confirmed and fixed in Step 0.** Report as found-and-fixed. |
| 21 | `int` overflow of the aggregate at `int.MaxValue` limits | none | **Newly found and fixed.** `Reserved` and all limit comparisons `long`; `RiskState` saturates. |
| 22 | "Potentials invariant under a fill" | judges | **Newly corrected.** Only the fill's own side is invariant. §0, case 6. |
| 23 | Spreads generate no leg exposure; simulator cannot even build a legged spread | critic P0-1 | **Scoped and enforced.** Spread targets refused (`OrderTypeNotSupported`, case 25); leg resolution fixed in Step 2 so phase-2 fan-out is buildable; scope statement in §0 and §7. |
| 24 | Disconnect releases exposure still resting at CME | critic P0-2 | **Fixed.** (I-life): `OwnerLost` + alarm, never release. Cases 19, 20. |
| 25 | `OrderIdAllocator.cs:44` `DateTimeOffset.UtcNow` breaks reproducibility | critic P0-3 | **Fixed.** Seeded from `Clock`/manifest; case 21 asserts byte-identical audits. |
| 26 | `RiskDecision` on an execution channel crashes every client (`Client.cs:468` throws); same landmine live today for `RiskLimit` | critic P0-4 | **Fixed.** Audit-socket-only routing, `default:` de-fanged, `ServerSimulator.cs:1297` re-routed. Case 23. |
| 27 | Nothing binds `OrderTarget.ClientId` to the arriving socket | critic P0-5 | **Fixed.** §1.4 line 6, `ClientIdIsWrong`. Case 22. |
| 28 | Server-rejected Create permanently burns an order slot | critic P1-6 | **Fixed.** Done-OrderState write + `Client.OnOrderRejected` free, with the `OrderIndexIsBusy` exception. Case 27. |
| 29 | `Reconcile` domain emptied by `DeallocateClient` | critic P1-7 | **Fixed.** Scan `_reservations` filtered by `InstrumentId`. Case 15. |
| 30 | `Σ` local positions ≠ server position; case 13 over-asserts | critic P1-8 | **Fixed.** Case 13 restated intra-run; connect-time mismatch → `Blocked` (Step 9). |
| 31 | Pause cleared by client reconnect (`ServerSimulator.cs:1269`) | critic P1-9 | **Fixed.** Server-side persisted pause, admin-only clear. Case 14. |
| 32 | Roll-day provisioning; simulation default is unlimited | critic P1-10 | **Fixed.** `GetMaxLimits` deleted, fail-closed defaults, `LimitsNotProvisioned`. Case 26. Root-inheritance stays procedural (§7). |
| 33 | Sharding argument has zero test coverage | critic P1-11 | **Fixed.** CoreGroup parameterised, read loop iterates channels. Case 24. |
| 34 | `RiskLimit.Timestamp` never stamped; limiter re-derivation allocates and resets | critic P2-12 | **Fixed.** Stamp in `OnRiskLimit`; limiters mutated in place. |
| 35 | `MessageEfficiency` is a racing client-side estimate | critic P2-13 | **Out of scope, stated.** Not offered as evidence (§7). |
| 36 | `Release` on `SessionManager.Changed` fires at session *open*; split reset triggers | critic P2-14 | **Fixed.** Session hook deleted entirely; resets unified on `Closed`. Case 28, artefact 6. |
| 37 | `LoggingServer` `default: return ""` silently drops unknown records | critic P2-15 | **Fixed.** Emits `{"UnknownType":N,"Bytes":...}`. |
| 38 | `RiskDecision` volume vs a sorting logger | critic P2-16 | **Fixed as a gate.** Step 4 measures before Step 6 commits; channels sized from the measurement; accepts are never the thing dropped. |
| 39 | Differential oracle cannot produce byte-identical bitsets | critic P2-17 | **Restated.** Risk subset identical, `⊇` elsewhere. §3.2 item 4, case 16. |
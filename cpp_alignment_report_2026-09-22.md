# C++ alignment report — 2026-09-22: two session contracts the risk layer depends on

Scope: two rules that are now stated in Spec.md and that the C++ CME session and adapter must
honour, because `RiskLayer` is built on them and is deliberately not defensive about either.
No wire or shared-memory shape changes. Read after the 2026-09-14 report. C# is the source of
truth; Spec.md "Acceptance before trade: OrderRisk depends on it" and "In-Flight Mitigation is
always on" carry the rationale.

Related but separate, already in `cpp_alignment.md` §5 (2026-09-22): the per-CoreGroup `RateLimits`
shared array and the 64-byte `RollingRateLimit` row.

---

# Contract 1 — Acceptance before trade

**Rule.** The exchange acknowledges an order or a replace before it reports any trade that order or
replace causes. A marketable create arrives as `Acked` then its fills. A marketable amend arrives as
`Acked` carrying the new quantity, then its fills. A remainder rests at the limit with no further
acknowledgement. On iLink 3 this is how CME behaves: `ExecutionReportNew` (150=0) or
`ExecutionReportModify` (150=5) always precedes `ExecutionReportTradeOutright` (150=F), and a
replace that fails validation gets `OrderCancelReplaceReject` (35=9) with the original untouched.

**What the C++ adapter must do.**
- Deliver states to the server in the order the session delivers execution reports. Never coalesce
  an acceptance into a fill, a cancel or an elimination, and never reorder a fill ahead of the
  acceptance of the version it references (`OrderRequestID`, tag 2422, on the fill echoes the last
  accepted request, so sequencing on it gives the right order for free).
- On recovery / retransmit after a reconnect, replay acceptances before fills for the same order.
- If any venue or vendor mode ever reports a quantity change without a separate acceptance,
  synthesise the `Acked` state in the adapter before forwarding the fill. Do NOT add tolerance to
  `RiskLayer`.

**Why `RiskLayer` depends on it.** `RiskLayer::OnOrderState(state, beforeAckedOrderQuantity)` is two
branches and must stay that way:

```cpp
if (state.OrderStateReason == OrderStateReason::Acked)
{
    // retire the pending target, release the change in worst case from the old acked quantity to the new
    int before = orderRisk.GetAbsWorstOrderQuantity(beforeAckedOrderQuantity);
    orderRisk.Ack(state.OrderProfile.Quantity);
    int after  = orderRisk.GetAbsWorstOrderQuantity(state.OrderProfile.Quantity);
    ApplyWorstWorkingQuantityDelta(orderId, side, after - before);
}
else if (state.OrderStateStatus == OrderStateStatus::Done)
{
    // release what remains, measured from the acked quantity in the message
    int released = orderRisk.GetAbsWorstOrderQuantity(state.OrderProfile.Quantity) - std::abs(state.QuantityFilled);
    orderRisk = {};
    ApplyWorstWorkingQuantityDelta(orderId, side, -released);
}
```

`OnFill` releases each fill's own quantity. Per-fill releases plus the `Done` remainder telescope to
exactly the reserved worst case ONLY if every quantity change went through the `Acked` branch. A
fill that carries a quantity the server never saw acknowledged leaks the old-to-new difference into
`RiskLimit.WorstLong/ShortWorkingQuantity` permanently: the `Acked` branch never ran for it and the
`Done` release is measured from the new, smaller quantity.

**Evidence.** Until this date the C# simulator matched first and acknowledged only the remainder,
so an amend that filled on arrival produced a `Fill` with the new quantity and no `Acked`. One
simulated day of the `Make` ladder on MYM Dec25: 1,316 such orders, 12 of which leaked, leaving
10 long and 20 short reserved with nothing working. Fixed in `ServerSimulator.Enqueue`, which now
sends `Acked` (queue position 0 if marketable, else the book position) before `Take` trades. A
reconcile-on-any-quantity-change variant of `OnOrderState` shipped as 1eef81a and was reverted the
same day: correct, but it hid the sequence violation and made the code say something other than
the rule.

**Verification.** From the client's `.audit`, replay reserve/ack/fill/done per order exactly as
above and assert (a) every order nets to zero, and (b) no `Fill` carries a quantity that differs
from the last `Acked` quantity for that order. Both counts must be zero on every simulated day
and on every live day.

---

# Contract 2 — In-Flight Mitigation is always on

**Rule.** Every iLink 3 session logs on with In-Flight Mitigation enabled, tag 9768 = 1. There is
no non-IFM session anywhere on the platform.

**What it means for the state model.** `OrderState.QuantityFilled` is cumulative over the life of
the order across every cancel/replace: a replace never restarts it, a fill after a replace adds to
it. The server's `WriteOrderState` keeps `max(stored, reported)` only as a guard against a stale
message, never to bridge a reset. Under IFM, CME's `CumQty` (tag 14) is exactly this cumulative
count and maps straight onto `QuantityFilled`.

**Why the risk layer depends on it.** The `Done` release above is `worst - QuantityFilled`. A
non-IFM session restarts `CumQty` at zero on each modification; passing that through would make the
`Done` release over-release by everything filled before the replace, and the position ledger would
lose those fills. Both errors are silent.

**What the C++ adapter must do.** Log on with 9768 = 1 and assert it in the logon response. If a
session ever cannot get IFM, normalise `CumQty` to cumulative-across-versions in the adapter before
the state reaches the server, and never pass a reset through. The simulator is IFM-like by
construction.

---

# Checklist

1. `OnOrderState` is the two-branch form above; no quantity-change heuristics.
2. Adapter never coalesces or reorders acceptance and fill; recovery replays acceptances first.
3. Logon carries 9768 = 1; `QuantityFilled` is cumulative across replaces.
4. Day-end audit replay: zero orders with nonzero reserve/release residual, zero fills whose
   quantity differs from the last acknowledged quantity.

---

# Addendum 2026-09-23 — PendingNew carries a provisional QuantityAhead

**Rule.** When the server accepts a `Create` and writes the PendingNew `OrderState` (Seq 0,
`OrderStateReason::PendingNew`), `QuantityAhead` is the server's own book quantity at the order's
price on the order's side: `MarketByPrice64.Bids.GetQuantity(ticks)` for a buy,
`Asks.GetQuantity(ticks)` for a sell. Not 0.

**Why.** The ladder, and any strategy that reads `QuantityAhead`, sees a PendingNew order for the
whole round trip to the venue. With 0 every fresh order reads as front of queue; on a strategy that
re-quotes constantly that is the entire ladder. The book quantity is the best estimate the server
has before the venue answers: it is the position the order will hold if nothing at that price
changes while it is in flight.

**What the C++ server must do.** Read the book row before taking the order-row lock (the C# takes
a `ref readonly` into the shared `MarketByPrice64` entry), write the seeded value in the same
`OrderState` that carries PendingNew, and let the acceptance overwrite it. `OnQuantityAhead` /
`AheadOfOrder` are unchanged.

**Verification.** In the audit, every `OrderState` with `Seq == 0` and reason PendingNew for a
resting create carries a `QuantityAhead` equal to the book quantity at its price at that
`NicTimestamp`. The `Acked` state that follows may differ and does not have to match.

Checklist item 5: PendingNew `QuantityAhead` is seeded from the book, never left at 0.

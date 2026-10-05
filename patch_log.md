# Patch log

Newest first. Each entry says what changed, why, and what it broke or unblocked.

---

## 2026-10-05 — aligned with C++ (`csharp_alignment_report_2026-10-05.md`, HFT_cpp `persist-client-sockets`)

The seven "C# to implement" items of the C++ review, each agreed with the user, C++ the reference:

- `Provider/Server.cs` — `LoadInstruments` replays each line through `OnAllocateInstrument(clientId,
  ref …)` (client, house-book and poll bits, admin reply, `AllocateInstrument` callback), not the
  server-only overload. `InitDirectories` calls `ServerContext.ThrowIfInvalidServerName` first, so a
  simulation run against a live server's name throws before deleting any file.
- `Provider/Client.cs`, `Socket/Socket.cs` — the constructor checks all 64 slots once
  (`ThrowIfPreviousOrdersActive()`, no instrument filter), clears Done slots' `OrderRisk`, sleeps
  100 ms, then `_socket.Recover()` (new `ClientSocket.Recover` passthrough) skips the queued backlog;
  `OnInstrumentAllocated` only seeds `WorkingRisk`. Closes the startup double-count and the GUI
  re-allocate throw.
- `Tools/Memory.cs` — `mlockall` failure throws (locked run-once `EnsureMLocked`, retried after a
  throw like C++ `call_once`; replaces the `MLockGuard` static initialiser).
- `Tools/Application.cs` — the SIGHUP handler is registered only if SIGHUP was not inherited ignored
  (`nohup`).
- `Socket/SharedArray.cs` — a region over `int.MaxValue` bytes throws `OverflowException`.
- `Tools/Clock.cs`, `Tools/Collections/LockedPriorityQueue.cs` — `Start` clears `s_isRunning` in its
  `finally`; `ConsumeReminders` uses the new `TryDequeueIfAtMost` (check and pop under one lock).
- Not changed: the C++ erratum "Spec.md says 400 in 3 seconds" — Spec.md no longer contains that
  figure.
- Evidence: R1a/b/c (the harness applies the constructor's predicate before building the restarted
  client, since an in-process half-built client cannot be closed), G2c, G5d, G9, G14, scripted
  40/40, findings 14/14, 10 fuzz presets and the GUI fuzz PASS, 0 invariant failures, 0 clock
  exceptions. Not exercised: `mlockall` failure, SIGHUP/`nohup`, the 2 GiB limit and the Clock
  races (Windows box; no harness case). Spec.md "Process guards aligned with C++" and "Client
  restart".

## 2026-10-05 — the server's own refusals go through OnOrderRejected too

- `Provider/Server.cs` — `OnOrderTarget` always writes and publishes a Create's row as
  Active/PendingNew; a refusal calls `OnOrderRejected` instead of `Reject`, so the Done/Rejected
  state for a refused Create is built in one place for both the server's and the exchange's
  refusals. `OnOrderRejected` is unchanged apart from its comment (its same-order check still drops
  a late exchange reject for a reused slot).
- Behaviour: a server-refused Create now sends PendingNew, Done/Rejected (seq 1, was a single
  Done/Rejected at seq 0), then the reject — the same sequence as an exchange-refused Create. The
  server's `OrderState` event fires for the Done, and the server's `RiskLayer.OnOrderState` runs and
  releases 0 (the slot's row was reset by its previous order's Done).
- Not defended, by decision: a Create for a slot whose order is still live (`OrderIndexIsBusy`). The
  client's own `ValidateCreate` refuses it against the same row; if one ever reached the server it
  would overwrite the live order's row and release its reservation (before this change it overwrote
  the row and leaked the reservation). A busy-slot guard was written, tested and removed for
  simplicity: the platform relies on clients behaving.
- Evidence: G2c, G5d PASS; G1b/G2b unchanged (accepted); scripted 40/40; 10 fuzz presets PASS; GUI
  fuzz PASS with ~5,300 server-refused Creates and 0 invariant failures, reject counts identical to
  before. C++: route `Server::OnOrderTarget`'s refusals into `Server::OnOrderRejected` the same way.

## 2026-10-05 — ProcessId.IsAlive: a pid of 0 or below is dead

- `Tools/ProcessId.cs` — `IsAlive` returns false for `pid <= 0` before the platform call. On Linux
  `kill(0, 0)` and `kill(-1, 0)` succeed, so a client slot with `ClientProcessId == 0` read as alive
  and the listen thread's dead-client sweep (`Socket.cs` `PollPids`) never closed it or cancelled its
  orders; Windows already treated it as dead. Raised by the C++ side, which keeps the same guard in
  `IsProcessAlive`. Spec.md "Socket close protocol".

## 2026-10-05 — a refused Create: the server publishes Done first, then the reject

- `Provider/Server.cs` — `OnOrderRejected` (the exchange adapter's entry point for a refused target),
  for `OrderTargetAction.Create`, inside the same-order check and after the `OrderNotFound` →
  `StateIsDone` mapping, publishes a `Done`/`Rejected` `OrderState` through `OnOrderState` before the
  reject. One message now releases the reservation and hides the order; the reject only explains why.
  Before, the C++ router sent reject-then-Done, so for a tick the algo saw room released while
  `ActiveTargets` still showed the dead order.
- `Simulator/ServerSimulator.cs` — `OnExchangeOrderRejected` forwards only the reject (its own
  Done/Rejected synthesis is gone; the server does it now).
- C++: mirror in `Server::OnOrderRejected` and drop the router's post-reject Done
  (`InstrumentRouter.hpp` :338-344, :293-298). Closes the parked CME question and harness G5d.
- Evidence: G5d, G5a, G5c, G8 PASS (G8 now passes outright, reservations zero at the end); scripted
  40/40; 10 fuzz presets PASS. G1b unchanged (accepted session-close race, refused by the server).
  Spec.md "A refused Create: Done first, then the reject".

## 2026-10-04 — risk layer runs on every client; RiskLimit split into config + WorkingRisk; Reduce/Replace

All 2026-10-04 entries are one uncommitted piece of work. They change the wire and the shared-memory
array ids, so the server, every client, the GUI, the LoggingServer and the C++ side must all be
rebuilt together. cpp_alignment.md has the C++ handoff.

- `Execution/Order.cs` — **WIRE CHANGE:** `RiskLimit` 32 → 24 bytes and is config only: `Header` @0,
  `InstrumentId` @4, `Timestamp` @8, `MaxOrderQuantity` @16, `MaxPositionQuantity` @20.
  `WorstLong/ShortWorkingQuantity` are gone from it. `GetLong/ShortQuantityAllowance` now take
  `in WorkingRisk` instead of a position. The `.risklimit` JSON lines lose the two Worst* properties.
  Older lines that carry them still parse, because unknown members are skipped. Any audit reader keyed
  on the record size must use 24.
- `Execution/Order.cs` — **WIRE CHANGE:** new `OrderType.WorkingRisk = 17` and new 16-byte row
  `WorkingRisk`: `Header<OrderType>` @0, `int Position` @4, `int WorstLongWorkingQuantity` @8 (>= 0),
  `int WorstShortWorkingQuantity` @12 (<= 0). It holds what one RiskLayer has applied for an
  instrument: fills and worst-case working reservations. Never persisted. It carries its own
  `Position`, copied from the position row at allocation and moved by `RiskLayer.OnFill`, so position
  and reservations change in one row together with the order event that caused them. With two sources
  they would be out of step between a Fill and its OrderState.
- `Execution/Order.cs` — **WIRE CHANGE (shared memory):** `OrderRisk` stays 64 bytes but now stores
  the acked quantity: `ushort count` @0, `ushort worst in flight` @2, **new** `ushort
  _absAckedOrderQuantity` @4, `Array29<ushort>` @6..63. `MaxActiveTargets` 30 → 29, so `TryAdd` refuses
  the 30th in-flight target (`TooManyActiveTargets`, still discarded). `GetAbsWorstOrderQuantity()`
  takes no argument and returns max(acked, worst in flight). `Ack(q)` removes the entry, then records
  |q| as acked. `Reject(q)` only removes. New `IsFull` (29 in flight). Each RiskLayer copy is now
  self-contained: worst comes from the acks it applied itself, not from the shared OrderState row,
  which a client sees at a different time and which may still hold the slot's previous order.
  `Tools/Array.cs` gains `Array29<T>`. `Array30<T>` has no users left.
- `Execution/Order.cs` — **WIRE CHANGE:** `OrderTargetAction.Amend` renamed `Replace` (same value 1),
  new `Reduce = 3`. A Reduce is less quantity at the same price: it keeps queue priority and never adds
  risk. New `OrderProfile.IsReduceOf(in other)`: same `Ticks`, same `Sign`, strictly smaller |Quantity|.
  Every check that applied to Amend now applies to both Replace and Reduce (`isReduceOrReplace` in
  `ValidateOrder`). The JSON enum name changes from "Amend" to "Replace". The simulator and
  `Server.cs` compare only to Create/Cancel, so values 1 and 3 both take the amend path there.
- `Provider/Context.cs` — **WIRE CHANGE (array ids):** new per-context shared array
  `<directoryPath>/WorkingRisks`, one row per instrument id. It is created last among the base arrays,
  right after `LocalPositionHeaders`, and takes array id 15. The server-only `ServerPositionHeaders`
  moves from id 15 to **16**. Ids 0..14 are unchanged. The TCP mirror writes by array id, so a mixed
  old/new build breaks it. Old → new is silent: the old peer's position rows under id 15 land,
  truncated, in WorkingRisks. New → old throws on a C# receiver: a 16-byte WorkingRisk row is too short
  for the old `PositionHeader` array, and id 16 does not exist there. A C++ receiver without those
  checks corrupts memory (cpp_alignment.md §1.10). `OrderRisks` (id 12) is
  now keyed per context directory too (it was `serverName`): the server's covers every order, a
  client's only its own. New `GetWorkingRisk(instrumentId)`, which returns this context's own row.
  `GetPositionHeader(clientId, instrumentId)` moved from ServerContext to the base Context (read-only
  on a client): the client's `RiskLayer.ValidateOrder` reads it for the AlgoIsPaused check. `ServerContext.AllocateInstrument` writes
  `WorkingRisk { Position = server-wide position }` and no longer zeroes the removed RiskLimit fields.
  `PrintDebug` dumps WorkingRisks.
- `Provider/RiskLayer.cs` — built on the base `Context`. The same code runs on the server
  (`ServerContext`, source Server) and on every client (`ClientContext`, source Client), and the
  server-only early returns in the hooks are gone. The worst-case aggregates are written to each leg's
  `WorkingRisk` row with a seq bump (AcquireLock/ReleaseLock). Before, they went to RiskLimit by plain
  ref. The hooks:
  - `OnOrderState(in state)`: the `beforeAckedOrderQuantity` parameter is gone. On Acked it applies
    the worst delta around `Ack`. On Done it releases `worst − |QuantityFilled|` and zeroes the row.
    An ack that rides inside a Fill or Done no longer leaks: its target stays in flight, over-reserving
    until Done, which then releases exactly what the order holds.
  - `OnFill(in fill, isReserved = true)`: every fill moves `WorkingRisk.Position`. The release happens
    only when `isReserved` is true and the instrument is not legged. The IsLegged check moved here
    from `Server.OnFill`.
  - `OnOrderRejected`: returns early when the reject came from its own side or the rejected action is
    a Cancel (a cancel never reserves, and its profile could match an in-flight entry). It no longer
    reads the OrderState row.
- `Provider/RiskLayer.cs` — the position check is now pure, check-then-commit. The order is: per-leg
  `MaxOrderQuantity` on the working quantity, then `IsWithinRiskLimit`, then reset on Create, then
  `TryAdd`, then apply. Before, it was TryAdd, a tentative per-leg check, and `Reject` to back out.
  New `GetAbsAllowedOrderQuantity(target, isWithinLimit)`: per leg, the order's worst plus `room / |w|`,
  minimum over legs. Past the limit it allows the order to keep its worst. Two consequences:
  - A zero-delta target on an over-limit side now passes. Before, an over-limit long side refused it.
  - `PositionExceedsRiskLimit` (pauses) is now reported before `TooManyActiveTargets` (discarded).
  The client's `ValidateOrder` now runs the risk block for its own **algo** orders. Manual orders still
  return after the header checks, and the rate limit stays server-only. On the server, a Cancel or a
  **verified** Reduce (`IsReduceOf` the current OrderState profile) is counted with `SendOrder` and
  never refused. A Reduce behind an unacked Replace fails that test and is throttled like any amend.
  The unused `Exposure` struct was deleted.
- `Provider/Client.cs` — `RiskLayer = new RiskLayer(Context, OrderRejectedSource.Client)`, built over
  the client's own context (it was the server's). The hooks that feed this copy:
  - `OnOrderState` calls it once per real risk event: a Done, or an Acked with a seq above
    `_ackedSeqs[slot]`, for active algo orders only. It runs before the Done/Free handling.
    `_ackedSeqs[64]` is reset in `Create`. This gate stops an echoed Done or ack from releasing twice.
  - `OnFill` calls `RiskLayer.OnFill(fill, fill.ClientId == own)`. A manual (GUI) order booked to the
    strategy moves the position but releases nothing.
  - `OnOrderRejected` releases for the slot's current algo order **before** the IsDiscarded check, so
    discarded rejects still release.
  - `OnInstrumentAllocated` writes `WorkingRisk { Position = this strategy's own position row }` on
    every process start.
  By timing, the client is never looser than the server for this strategy's own orders: it counts its
  own sends before the server reads them, and it learns of reductions (acks, Dones, server or exchange
  rejects) after the server. It does not see other strategies, manual orders booked to the strategy,
  the house book or the server rate limit, so "never looser" holds exactly when this strategy's own
  orders are all that is on the instrument (Spec.md "The client copy is never looser than the server —
  and where it can be").
- `Provider/Position.cs` — `Target(Ticks, WorkingQuantity, TimeInForce = Day)`.
  `ActiveTarget(..., QuantityAhead, QuantityBehind, QuantityFilled, ...)`: the positional order
  changed. `ActiveTargetsEnumerator` changes:
  - IOCs are never active targets. They never rest, and they stay reserved until Done.
  - An order whose cancel has been sent is hidden even before the server has read its Create. Until
    the state row belongs to this order, the cancel test uses filled = 0, because the row still holds
    the slot's previous order.
  - `IsPendingBuyCancel` / `IsPendingSellCancel` are removed. A cancel-pending order stays reserved in
    the client's WorkingRisk until its Done.
  - Only an explicit `Reduce` one seq ahead of the state keeps the confirmed queue position. Before, it
    was inferred from |target| <= |state|, which ignored price.
  - An unconfirmed order reports the client book's quantity at its price as `QuantityAhead` (and 0
    behind) instead of `int.MaxValue`.
  - Ahead/behind are read with one 64-bit load, matching `Server.OnQuantityAhead`'s single store
    (2026-09-26).
- Evidence (C# harness in the scratchpad, not in the repo): layout test PASS for RiskLimit 24,
  WorkingRisk 16 (offsets 4/8/12) and OrderRisk 64. For OrderRisk the check is: after TryAdd 7,
  TryAdd −9, Ack 7, the u16s are count 1, worst 9, acked 7, entry 9, and worst = 9. The sweep and
  scripted results are under the robustness entry below.

## 2026-10-04 — Algo: strict Target and best-effort TryTarget

- `Strategy/Algo.cs` — the single public `Target` is split into two entry points over one private
  `Target(ref targets, bool isBestEffort)`. Entry sets `_isBestEffort` and `_isTargetAchieved = true`.
  - `Target(ref targets)` (void), **strict**: sends exactly what was asked. `Send` skips the
    session/slot/risk checks. Anything the client, server or exchange refuses for a non-discarded
    reason comes back as a reject and pauses the algo, so a strategy bug fails loudly. The self-cross guard still applies.
  - `TryTarget(ref targets)` → bool, **best effort**: never sends anything it can see would be
    refused. While `TradingStatus` is not Open it sends nothing at all, cancels included. A Create needs
    `Client.HasFreeOrderSlot` (new). Every non-cancel goes through `RiskLayer.TryClipToRiskLimit`. It
    returns false when any target was not sent exactly as asked (dropped or clipped), or when the algo
    is paused. Policy drops under `IsOneOrderPerPrice` do not count as misses. The server rate limit
    (`TooManyOrdersPerSecond`, discarded) is invisible to it, which is accepted.
- `Provider/RiskLayer.cs` — new `TryClipToRiskLimit(ref target)`, used only by TryTarget. It lowers a
  create or amend to the largest quantity within every limit:
  - position room with `isWithinLimit: true`;
  - 65535;
  - per leg `MaxOrderQuantity / |w|` plus filled.
  It returns false when that size is <= filled, or for an amend whose OrderRisk `IsFull`. It is
  idempotent, so the Algo clips in Phase 2 and again in Send.
- `Strategy/Algo.cs` — `Send` and `CancelAllOrders` became private. `NewAmend` labels a non-cancel
  amend `Reduce` when `IsReduceOf` the active's profile, otherwise `Replace`. `NewAmend` and `NewOrder`
  both copy `TimeInForce` onto the OrderTarget.
- Phase order within one call: Phase 1 reduces, Phase 2 passive reprices, Phase 5 cancels, Phase 7
  creates, Phase 6 delayed orders, then the **new Phase 8**, which sends every IOC target as a Create.
  IOCs are not aggregated, not zippered and not clamped. They go last, so this tick's cancels and
  amends reach the exchange first.
- No caller uses `TryTarget` yet. Every strategy in the repo still calls strict `Target`
  (`Proxy/Strategy.cs` and the `Testing/` scenarios).

## 2026-10-04 — Algo fixes from the harness report (B4, B5, B7, B9, B10, B11, B12, B15)

- **B4** `CancelAllOrders` (the paused branch) now builds each cancel as
  `NewAmend(active, active ticks, 0)`. That is the Phase 5 shape: quantity = working + filled at the
  active's price (acked, or the in-flight amend's). It sends through `Send`, in the mode of the calling Target. Before, the cancel quantity
  was the filled quantity, so with nothing filled it had no side. A cancel racing the last fill came
  back `OrderNotFound|QuantityNotValid|SideNotValid` and re-paused the algo.
- **B5** TryTarget clips to the largest size **within** the limit. Past the limit (room < 0), the clip
  cuts by the overshoot rounded up to whole orders per leg (ceil(overshoot / |w|)), instead of letting the
  order keep its worst. The server accepts a size within the limit whenever the client's room is no
  larger than the server's. By timing that holds for this strategy's own orders, but not when others
  use room on the instrument, and the server rate limit or a limit lowered in flight can still refuse
  it (Spec.md "The position check, and the within-limit clip"). `ValidateOrder` (both sides)
  is unchanged: past the limit an order may keep its worst, so a cut always passes and strict Target can
  still keep. Accepted cost: when several orders are cut in one call, each is cut by the whole
  overshoot.
- **B7** Phase 2 reprices the **largest** active at one price first (new
  `GetLargestActiveKeyIndexAtPrice`; ties go to the earliest in sort order). Across prices it keeps
  price order. A reprice forfeits the old queue anyway, and the largest active carries the most within
  its own reservation. Example (F9/F10): limit 12, a 1-lot ahead of a 10-lot, target 11 at a new
  price. The Algo now Replaces the 10 to 11 and cancels the 1. Before, it moved the 1-lot, which the
  clip let grow only to 2 because the 10-lot still held its reservation. With `IsOneOrderPerPrice`
  false, the remaining 9 went to the 10-lot, repriced to 9 as a second order at the new price; with it
  true, the 10 was cancelled and the level fell from 11 to 2. Related changes:
  - The delayed buffer grew to targets + actives.
  - A target can now consume several actives, with remainder tracking when `IsOneOrderPerPrice` is
    false.
  - In best effort, an active that nothing fits is left for the Phase 5 cancel. The target then tries
    the next same-side active before it becomes a Phase 7 Create.
- **B9** see `ActiveTargetsEnumerator` in the first entry: an order whose cancel has been sent is hidden
  before its Create is read. The Phase 5/7 per-side cancel lock (`_isPendingBuyCancel` /
  `_isPendingSellCancel`) is deleted. The client's RiskLayer copy now enforces capacity exactly,
  counting a cancel-pending order until its Done. This also closes the old known limit: an amend-up
  while another order's cancel was pending used to be double-counted at the server.
  Behaviour change for strict Target, which every current caller uses: a same-side Create sent while a
  cancel is pending used to be withheld by the Phase 7 lock, so the strategy waited a tick. It is now
  sent. The client's own `ValidateOrder` (new for algo orders, see the first entry) counts the
  cancel-pending order's reservation until its Done, so it refuses the Create with
  `PositionExceedsRiskLimit` when that leaves no room. The same applies to an amend-up, and to any
  other refusal by the client copy, which counts sends earlier and releases later than the server.
  That reject has source Client and is not discarded: `Client.Reject` writes it to the server on the
  CoreGroup channel, and `Server.ReadExecution` pauses the algo. TryTarget clips or drops the order
  instead.
- **B10** With 29 targets in flight on an order (an unacked Create counts), `TryClipToRiskLimit`
  returns false for an amend. For a Phase 2 reprice, TryTarget leaves the order for the Phase 5 cancel,
  and the target tries the next unmatched same-side active. With none left it becomes a Phase 7
  Create, which `Send` clips or drops. For a Phase 1 reduce, the order is cancelled in Phase 1 instead,
  the target is not placed in that call, and TryTarget returns false. Strict Target is unchanged: the
  30th amend is refused and discarded silently. The user's decision: it must never pause on this.
- **B11** In best effort, a Phase 1 reduce that nothing workable fits becomes a cancel, so a level never
  stays above its target. A reduce that is cut further, or turned into a cancel, marks the call missed.
- **B12** `Target` reads and clears `_hasSnapshot` at entry, before the paused return and before any
  validation throw, so a stale snapshot is never zippered by a later call.
- **B15** Validation is now one pass over the targets. It drops zero quantities, moves IOC targets to a
  separate list and compacts Day targets in place. Any other `TimeInForce` throws
  `NotImplementedException` before anything is sent. The self-cross bounds cover Day and IOC targets
  together. The protected `ThrowIfSelfCrossingTargets(ref targets)` is deleted. It classified a
  zero-quantity target as a sell, which could raise a false self-cross. The caller's StackList is
  mutated: zeros and IOCs are removed.

## 2026-10-04 — server/client robustness: restart refusal, manual rejects, spread shape, seq numbering, duplicate fills, read-loop guard

- **B2** `Provider/Client.cs` — `OnInstrumentAllocated` → `ThrowIfPreviousOrdersActive` scans this
  client's 64 slots. It throws `InvalidOperationException` while any slot still holds an **Active**
  order on the instrument from a previous process. That happens when a restart beats the server's
  cancel-on-close round trip, during an iLink outage, or in a no-cancel phase. For slots whose last
  order on the instrument is Done, it zeroes the client's OrderRisk row, so the new process inherits
  nothing. Then it rewrites WorkingRisk with zero reservations, which is exact because nothing is
  Active.
  Accepted residuals:
  - A fill landing between socket connect and the check could be counted twice.
  - The check reads only the OrderState row, so a previous process's Create the server has not read
    yet is not seen.
  - It also runs for the ManualClient: a GUI restarted before the server cancels its manual orders
    throws.
  - Because ManualClient never opens an instrument data socket (`OpenInstrumentDataSocket` is a
    no-op), `GetInstrument` re-runs `OnInstrumentAllocated` on every allocate request. A GUI that
    re-allocates an instrument while one of its own manual orders is live there throws
    `InvalidOperationException`, which is reported to the AlertManager. PositionsWidget and
    RiskLimitsWidget guard with `Context.InstrumentIds`; the InstrumentHeadersWidget allocate menu does
    not. C#-only.
- **B6** `Provider/Server.cs` — a manual order's reject never pauses the algo it books to. Both pause
  sites now gate the pause on `OrderId.IsAlgoOrder()` (ClientId == StrategyId):
  - `Server.Reject` (a server or exchange refusal) always forwards the reject, returns if it is
    discarded, pauses only an algo order, then raises `OrderRejected`, for manual orders too.
  - The `ReadExecution` OrderRejected case (a reject a client wrote to the server) only pauses an algo
    order. It forwards and raises nothing: the client already raised it in `Client.Reject`, which
    writes only non-discarded rejects.
  The other half of B6 is accepted: manual orders can use room the algo's client copy cannot
  see, so the algo may be refused and paused. That is the user's responsibility.
- **B8** `Provider/Client.cs` — `GetInstrument` throws `NotImplementedException` for any spread that is
  not a two-leg +1/−1 calendar (`LegCount == 2 && |w0| == 1 && w0 == −w1`). It throws before onboarding
  legs or sending the allocate request, because `Spread` builds its risk legs as +1/−1 and would
  mis-risk a butterfly. The check was first put in `Context.CreateInstrument`, but that runs on the
  server's admin thread, and the unhandled exception killed the whole server process. The server does
  not check, so a C++ client must mirror the check. Only the comment in `Context.CreateInstrument`
  changed.
- **B13** `Provider/Client.cs` — `ManualClient.Amend` owns seq numbering:
  - A cancel of an algo order is numbered existing target seq + 1,000,000, as `Server.CancelAllOrders`
    does, so it never collides with a racing algo amend.
  - A manual order's amend is existing seq + 1.
  - The caller's seq wins only if it is larger.
  The four widget-side offsets are removed: cancels at +1,000,000 (Ladder), +10,000 + age in seconds
  (Orders) and +1000 (SendOrder), and SendOrder's amend +1. `ManualClient.Amend` throws `InvalidOperationException` for any non-cancel
  on an algo order, so an algo's orders are cancel-only from the GUI.
- **B13** `Simulator/ServerSimulator.cs` — a refused target changes nothing. The reasons
  (SeqOutOfOrder, NotInSession, StrategyIdNotValid, InstrumentIdNotValid) are evaluated before the
  cancel path, and the order's Seq is overwritten only after those checks pass. Before, a stale target
  rewound the seq, and a refused cancel deleted the order **and** was rejected. TargetIsActive and
  SideNotValid still run after the seq assignment, so such a refused amend still advances the
  simulator's stored Seq (the order itself is unchanged).
- **Duplicate fill drop** `Provider/Server.cs` — `OnFill` drops a fill event whole when
  |QuantityFilled| does not exceed the order row's, treating it as an iLink resend (PossRetransFlag).
  The drop happens before stamping, locks, position, risk, forwarding and audit, and is not counted or
  logged. Before, a resent fill moved both position rows and released risk a second time.
  - The rule rests on two assumptions, which still need CME's confirmation: the session delivers an
    order's fills in order, and the adapter always sets the cumulative quantity (CumQty).
  - If either assumption fails, the fallback is a recent-set keyed by FillId (ExecID).
  - Also in `OnFill`, `_riskLayer.OnFill` is now called for every fill, legged instruments included, so
    the spread row's `WorkingRisk.Position` moves. Only the release is skipped for legged instruments.
- `Provider/Server.cs` — `WriteOrderState` no longer captures the pre-write acked quantity. It calls
  `_riskLayer.OnOrderState(in row)`. The `OnControlRiskLimit` comment now says the working quantities
  live on WorkingRisk.
- **Read-loop guard** — `Server.ReadAdmin` and `ReadExecution` document their contract: in realtime
  the caller wraps each call in try/catch → `AlertManager.OnException`, as Scenario's `ReadSocket`
  loop does. In simulation, the Clock's exceptions go to the AlertManager. `ServerSimulator.Init`'s
  pre-clock admin loop now wraps `ReadAdmin` and reports through the new `Clock.OnException`
  (`Tools/Clock.cs`), so a failed allocation is alerted instead of the process exiting. Nothing in the
  harness makes allocation throw any more, so the guard itself was not exercised.
- `Simulator/ServerSimulator.cs` — IOCs:
  - The Create's simulator state copies `TimeInForce`. Before, every order defaulted to Day, so an IOC
    rested.
  - A marketable order or an IOC publishes queue position 0/0 through `OnQueuePosition`, replacing
    the direct write of 0 to the copy. That direct write left the server at its PendingNew seed.
  - An IOC is acked, takes what it can if marketable, and the remainder is `Eliminated` (Done) at once.
- Open with CME, pending the user's call:
  - Can the session deliver a rejected new order without a terminal state? A proposal is on hold to
    move `ServerSimulator.OnExchangeOrderRejected`'s Done/Rejected synthesis into
    `Server.OnOrderRejected`.
  - The duplicate-fill assumptions above.
  - That an amend is never acked only inside a fill.
- Declined or accepted, not fixed:
  - Session-close race: orders in flight get `NotInSession` and pause even under TryTarget (G1b/G8).
  - B16: a limit lowered between the client's check and the server's read pauses the algo.
  - Near-vs-far room priority: Phase 7 creates go before delayed aggressive reprices.
  - The multi-order over-cut from B5.
- Evidence (C# harness in the scratchpad):
  - Final 137-job sweep: **137/137 PASS**, 0 invariant failures. Known findings appear only in the 8
    manual-order jobs, which is accepted behaviour.
  - Scripted 40/40 PASS.
  - Findings F1, F3–F12 PASS. There is no F2: the harness defines no case with that number, so
    nothing failed or was dropped under it.
  - Gap cases PASS: G1a, G2a, G2c (refused manual order never pauses), G5a–c, G5e (duplicate fill
    counted once), G6a, G7a/b, G9 (butterfly refused client-side, server keeps trading), G14, G15a/b,
    R1a/b/c.
  - Accepted: G1b/G8 and G2b/G6b. Parked for CME: G5d.
  - Fuzz presets with 1.4k–3.4k fills each, and two calendar-spread runs, PASS.

## 2026-10-04 — GUI: an algo's orders are cancel-only; widgets read WorkingRisk

- `Widget/LadderWidget.axaml.cs`, `Widget/OrdersWidget.axaml.cs` — "Amend Order" is offered only for
  non-algo orders. "Cancel Order" (and Cancel All) still covers every order. A GUI amend of an algo
  order would be undone on the algo's next tick, and the algo's RiskLayer copy cannot see it.
- `Widget/SendOrderWidget.axaml(.cs)` — new `AmendOrderWrapper.IsAmendable`. The Amend button binds to
  it (`FallbackValue=False`), and `OnSendAmendClick` returns early when it is false. A GUI amend is
  labelled `Reduce` when the new profile `IsReduceOf` the order's state profile, otherwise `Replace`.
  The widget seq offsets are removed (see B13).
- `Widget/RiskLimitsWidget.axaml.cs` — allowances and the worst working quantities come from the
  server's `WorkingRisk` row (`ContextManager.ServerContext.GetWorkingRisk`), since RiskLimit is config
  only. `RiskLimitEquals` compares config only, and the new `WorkingRiskEquals` compares the live
  numbers. A C# GUI on a C++ server that publishes no WorkingRisk rows does not list the instrument at
  all: `Read()` of an Empty row throws and the widget skips it. Zeros appear only if the rows are
  written but never maintained.

## 2026-09-26 — simulator: MaskMade switches Ghost on and off

- `Simulator/ServerSimulator.cs` — `ExchangeSimulator.MaskMade` (default true), beside `MaskCrossed`
  and `MaskTaken`.
- `Simulator/OrderManager.cs` — `QueueManager.OnTrade`: with `MaskMade` on, unchanged (the whole trade
  quantity goes into `_traded`, every fill-removal is skipped, the market queue our fill displaced stays
  as Ghost). With it off, only the market quantity the simulator removed at that level goes into
  `_traded`, so the feed's remaining fill-removals take out, by PriorityId, exactly the orders that
  really filled; no Ghost is created and the queue tracks the real book. Optimistic by design: our
  fills cost no historical maker its fill. Exact for MBO data only (MBP folds `_traded` into the level
  delta). Set per scenario, e.g. `server.ExchangeSimulator.MaskMade = false;`.
  `Simulator/spec.txt` "MaskMade (Ghost on or off)". Built, not yet run.

## 2026-09-26 — OrderState carries QuantityBehind (queue behind the order, simulator only)

- `Execution/Order.cs` — **wire change:** `OrderState` gains `int QuantityBehind` @60 after
  `QuantityAhead`, 60 → 64 bytes, now full. `AheadOfOrder` gains `QuantityBehind` @16 (20 bytes; its
  stale "56 bytes" comment corrected). Every process that maps `OrderStates` or reads the audit must be
  rebuilt together, the LoggingServer included (a stale logger misreads `OrderState` the way it misread
  `OrderTarget` on 2026-09-24). cpp_alignment.md §5.
- `Simulator/OrderManager.cs` — `QueueManager.PublishQuantityAhead` totals the level first and reports
  each user order's ahead and behind (behind = total − ahead − own quantity).
- `Simulator/ServerSimulator.cs` — `InstrumentSimulator.OnQueuePosition` stores both on the simulator's
  own copy of the order state, then sends `AheadOfOrder`. The release switch passes behind on. (Not a
  bug fix: `Server.WriteOrderState` copies selected fields only and never `QuantityAhead` or
  `QuantityBehind`, so fills and acks never touched the client's queue position. A day's run with and
  without `OnQueuePosition` gave identical output for all 6,807 fills.)
- `Provider/Server.cs` — `OnQuantityAhead(clientOrderId, quantityAhead, quantityBehind)` writes both.
- `Widget/OrdersWidget.axaml(.cs)` — StateQuantityBehind column beside StateQuantityAhead.
- `ServerSimulator` (user) — `OpenConsoleForLogger` defaults to false; the per-exchange session is
  `Session.CME` for every exchange instead of `details.Sessions[0]`.
- Review fixes (same day): `PublishQuantityAhead` drops the `PriorityId >= p` filter and reports every
  user order at the level, because any change moves either ahead or behind for each of them; market
  Adds (both the MBO and the MBP path) and a user order joining now publish too, so behind tracks the
  level's growth. `InstrumentSimulator.OnQueuePosition` (restored; it had dropped out of the working
  tree) is the single path and the store for skipping: it drops a pair equal to what the simulator's
  copy already holds. That is safe only while the copy matches the server's value, because the server
  changes these two fields in exactly two places, its PendingNew seed in `Server.OnOrderTarget` (ahead
  = book quantity at our price) and `AheadOfOrder`; fills and acks do not carry them. The one gap is a
  new order: the simulator's state starts at ahead 0 while the server's starts at the book quantity, so
  a first position of 0 ahead is dropped as unchanged and the server keeps the book quantity until the
  level next changes. Closed: the simulator's new order state for a Create now starts at
  `QuantityAhead = -1`, which no real position equals, so the first position always goes out.
  `Enqueue` also sets `QuantityBehind = 0` on the ack and on a resting remainder, which is harmless but
  redundant: `EnqueueUserOrder` has already published behind 0 for a joining order, and the server
  ignores the ack's value. `Server.OnQuantityAhead` writes both as one 64-bit store (the fields are
  adjacent at 56/60 and must stay so), so a reader never sees one without the other. Every add or cancel
  at a level holding our orders now sends up to one message per order there; skipping saves little
  because each add changes behind for all of them. Not yet run: Markout Claude to rerun the day and
  compare behind with the earlier run.

## 2026-09-26 — TickHistoryWriter: day headers carry the next PriorityId to issue

- `Data/TickHistory.cs` — `TickHistoryWriter._nextPriorityId` tracks one past the highest `Add` id
  written (trades and removals never lower it). `WriteTomorrowHeader` writes it as the new day header's
  `PriorityId` (the day's delta base) instead of the last order's id, so the footer is a correct seed for
  a resumed converter; `SetHeaders` restores it from the footer. Reader and file format unchanged.
  Round-trip test (5 days, a resume at every day boundary, cancels of old orders, days ending on a
  trade): 10,005 orders decode exactly, footer = next id. Existing MBO files still carry the old
  meaning and need rebuilding. Spec.md "TickHistoryWriter crash safety"; `priorityid_report_2026-09-26.md`.
- `Widget/PositionsWidget.axaml(.cs)` — ProfitPerSide column (Profit / QuantityTraded).

## 2026-09-25 — simulator: queue position no longer collapses to zero after the first day

- Root cause of the ladder showing 0 ahead on thick books: `OrderManager.PriorityId` was a running
  maximum that never reset, but MBO PriorityIds restart at every snapshot — the parser renumbers the
  book at each daily R snapshot, and the 6E Sep26 tick-history file stitches conversion runs with
  different bases (1.26e14 on 08-21, 2.64e14 on Sunday 08-23, 1.72e14 on 08-24, 1.18e14 on 08-25; two
  snapshots per midnight). After Sunday every user order was stamped above every real id, so
  `ReduceMarketBy`'s "first blob whose id >= p" took every cancel at the level off the blob in front
  of the user, including cancels of orders queued behind it.
- `Simulator/OrderManager.cs` — `OrderManager.OnMarketByOrderSnapshot(orders, snapshotPriorityId)`
  restarts the baseline from the snapshot's highest id and re-stamps every live queue;
  `QueueManager.RestampPriorityIds` gives each market blob the id of the snapshot order where its
  cumulative quantity lands and each user order the id of the blobs in front of it.
- `Simulator/ServerSimulator.cs` — the MBO snapshot branch computes the highest id across both sides
  and calls it for `Buys` and `Sells`.
- Verified with a replay of the real 6E order-by-order book (scratchpad `QueueAheadTruth`: true
  arrival-order queue vs the simulator's rules for virtual 1-lots at the touch, 3 and 10 ticks deep):
  unfilled-at-zero-ahead at 3 ticks after 5 min was 28-30% with the old baseline vs 0-1% in reality;
  with the fix the simulator matches reality in every row, in-session and across the midnight
  snapshot. The same replay showed the id-based delete routing and the trade/skip handling are exact
  once the baseline is right. `Simulator/spec.txt` "PriorityId Baseline (MBO)".
- Data note: the tick-history PriorityIds are not continuous across days (see above). The simulator
  no longer depends on it, but anything else that assumes a global id order would.

## 2026-09-25 — simulator: sell-aggressor trades no longer drain the bid queue twice

- `Simulator/ServerSimulator.cs` — `InstrumentSimulator.OnMarketByOrder` runs an MBO update in two
  passes: every `Trade` in the bid and ask arrays first (`OnMarketByOrderTrades`), then the
  Add/Reduce/Cancel deltas of both arrays (`OnMarketByOrder`, which no longer handles trades). The
  Databento parser puts a trade in its aggressor's array and the removals of the orders it filled in
  the resting side's, and the old bids-then-asks walk ran a sell-aggressor trade after its bid-side
  removals. The bid queue then lost the traded quantity twice (once as the exact removals, once as
  `QueueManager.OnTrade` filling from the front again), user bids within that distance of the front
  were filled on volume that went to the orders ahead of them, and `_traded` swallowed the next
  genuine cancels at the level. Buy-aggressor trades were already right. Effect on earlier backtests:
  bid-side queue positions too short and bid fills too generous at every traded bid level.
  `Simulator/spec.txt` "Trade Timing Assumption" extended to the MBO path.
- `Testing/Test.cs` — test algo: when flat, one bid of 1 lot 10 ticks under the best bid, repriced
  only once it is 5 or more ticks from there.

## 2026-09-24 — simulation clock speed control in the Workspace top bar

- `Workspace/Workspace.axaml(.cs)` — a speed drop-down left of the clock: `1x (real time)`, `Max`,
  or a custom number (`10` or `10x`, Enter or Set; anything not a positive finite number turns the
  box red and changes nothing). The button shows the speed the clock thread has applied. Visible only
  in simulation when a running strategy opened the workspace (`WorkspaceRunner.IsHostedByStrategy`):
  a standalone Workspace process only follows the server's clock, so its own speed would do nothing.
- `Tools/Clock.cs` — `SimulationSpeed`'s setter raises `s_isSimulationSpeedChanging`, which breaks a
  pacing wait in progress; the reminder clears it and re-anchors as before. Without it, switching
  from 1x to Max across a data gap (CME's daily break) waited out the gap in real time first.

## 2026-09-24 — session state comes from the exchange's TradingStatus, not a timetable

- `Data/Instrument.cs` — `SessionManager`, `IsInSession` and the empty `OnSessionChanged` removed.
  `TryGetQuote` returns nothing unless `Header.TradingStatus == Open`.
- `Provider/RiskLayer.cs` — `ValidateInstrument` rejects a create with `NotInSession` unless the
  header's `TradingStatus` is `Open`. `Provider/Position.cs` — the position quote uses the same test.
- `Provider/Context.cs` — `CreateInstrument` no longer attaches `SessionManager(Session.CME)`.
  `AllocateProductGroupId` ends the message-efficiency day on `TradingStatusUpdateEvent` with
  `Closed` instead of the timetable's `Closed`, converting to local time with `Session.CME`; `Reset`
  itself is unchanged.
- `Simulator/ServerSimulator.cs` — `SessionManagerByExchange`: one `SessionManager` per exchange,
  created from the first allocated instrument's `Sessions[0]` (throws if it has none).
  `OnServerAllocateInstrument` subscribes each newly built `InstrumentSimulator` to its exchange's
  manager and sends the current state at once if the clock is already running.
  `InstrumentSimulator.OnTradingStatus` replaces its own `SessionManager`: it sets `TradingStatus`,
  on `Closed` cancels all orders and clears queues and masks as before, then sends a
  `TradingStatusUpdate` through the exchange-to-NIC latency queue; `OnTickTock` releases it to
  `Server.OnTradingStatusUpdate`. The three `IsInSession` gates read `TradingStatus != Open`.
  `ExchangeSimulator.Allocate` returns whether it built the simulator, because the server raises
  `AllocateInstrument` once per client and a second subscription would double every status.
- Behaviour: Unknown counts as closed, so nothing trades and no quote exists until the first status
  arrives; live, the CME server must publish status at startup. Auction and Halted are not open.
  Spec.md "Session state is the exchange's TradingStatus"; cpp_alignment.md §5.

## 2026-09-23 — PendingNew carries the book's quantity at its price as `QuantityAhead`

- `Provider/Server.cs` — the `Create` branch of the order-target path wrote the PendingNew
  `OrderState` with `QuantityAhead = 0`, so every fresh order showed front-of-queue in the ladder
  until its ack arrived; with the Testing algo re-quoting constantly the ladder was a wall of
  zeros. The server now seeds it from its own book: the bid or ask quantity at the order's price on
  the order's side, read from the shared `MarketByPrice64` row before the order row is locked. It
  is provisional: the exchange's ack (the simulator's `Enqeue` position) replaces it and
  `AheadOfOrder` publishes keep it current after that. No wire-shape change; the C++ server must
  seed the same way (cpp_alignment.md §5; addendum in cpp_alignment_report_2026-09-22.md).

## 2026-09-23 — simulated queue: publish only the orders whose position moved

- `Simulator/OrderManager.cs` — `QueueManager.PublishQuantityAhead(ulong priorityId)` publishes
  `AheadOfOrder` only for user orders with `PriorityId >= priorityId`: a user order is stamped with
  the highest id seen when it was enqueued, so at-or-above means it joined the queue after the order
  that just changed and its position is what moved (at-or-above, not above: the seed blob and a user
  order queued right after it share an id, as do two user orders with no MBO event between them).
  The id defaults to 0, so a bare `PublishQuantityAhead()` still publishes the whole level. Callers:
  `ReduceMarketBy(priorityId, quantity)` passes the deleted order's id, so a cancel behind you no
  longer republishes your unchanged value on every event; `ReduceUserOrderTo` and `DeleteUserOrder`
  now publish at all, with the changed order's id, where before a user order behind another of ours
  kept a stale number until the next market event at that level. `OnTrade` and the MBP-path
  `ReduceMarketBy(int)` still publish the whole level; in `OnTrade` that is exact, since fills come
  off the front and everything left has moved.

## 2026-09-23 — CoreGroups named by the server; rate limits load from `.ratelimit` files

- `Provider/Allocate.cs` — `CoreGroup` row (36 B): `String16 CoreGroupName`, `CoreGroupId`, and the
  four core ids the group's threads pin to (`ServerCoreId`, `MarketDataCoreId`, `StrategyCoreId`,
  `ReservedCoreId`, -1 when unset).
- `Provider/Context.cs` — `CoreGroups` shared array (server-written, after `RateLimits`),
  `GetCoreGroup(id)`, `GetCoreGroupId(name)` (throws if absent: a client asking for a group the
  server does not have is a setup error), `EnumerateCoreGroups()`. A `ServerContext` opened for
  write loads every `<server>/CoreGroups/*.coregroup` (static whole-file JSON `CoreGroup`; never
  amended at runtime, so not the appended-line form) into the
  row at its `CoreGroupId`, throwing if that id is not in `ServerHeader.CoreGroupIds` (channels and
  threads were built from it), then loads that group's rate limit from
  `<server>/RateLimits/<CoreGroupName>.ratelimit` (static whole-file JSON as well), else
  `RateLimit.GetMaxLimits` in simulation / `GetMinLimits` in realtime, the defaults `.risklimit` uses.
  `GetCoreGroupFilePath`, `GetRateLimitFilePath`, `CoreGroupsDirectoryPath`, `RateLimitsDirectoryPath`.
- `Execution/RateLimit.cs` — `GetMaxLimits` (1 s, int.MaxValue) and `GetMinLimits` (1 s, 0) replace
  the hard-coded `CMEOrderEntry`: New Release, Certification and Production publish different limits,
  and the server directory already is the environment. A production server with no file now refuses
  every create and amend until the operator writes the number.
- `Provider/Server.cs` — the constructor no longer writes a default rate limit per CoreGroup.
- `Simulator/ServerSimulator.cs` — seeds `CoreGroups/Simulation.coregroup` (id 1, no cores; a
  simulation never pins) before building the server if nobody has written it, so the context's file
  load names the trading group and its rate limit comes from `Simulation.ratelimit`.
- `Data/Instrument.cs` — `CoreGroupId` enum deleted. `Strategy/Scenario.cs` — `CoreGroupName` is a
  string; the strategy thread pins to the chosen group's `StrategyCoreId` from the server's row after
  `BuildRealtime` (the context exists only then); the `coreGroupId * 4 + n` core helpers are gone.
  `Testing/Scenario.cs` — keeps a CME-only `CMECoreGroupId` enum for its prompt and branches.
- `Widget/RateLimitWidget.axaml.cs` — the CoreGroup column reads the name from the `CoreGroups` row.
- `Tools/Json.cs` — `MutableResolver.GetTypeInfo` walks the contexts by index instead of `foreach`.
  The simulator's `.coregroup` seed was the first JSON call of the run; walking the chain for
  `CoreGroup` triggered a not-yet-initialised module's `[ModuleInitializer]`, which registered its
  own context on the same thread mid-walk (the lock is re-entrant) and the `foreach` threw
  "Collection was modified" (8 contexts registered, a 9th arriving). The index walk survives that
  and visits the newcomer in the same call.
- Spec.md "Order rate limit" rewritten around the file, new "CoreGroups are named by the server, not
  by an enum"; cpp_alignment.md §5 (new region, C++ server must allocate its groups by name).

## 2026-09-23 — cancels are counted by the rate limit but never refused

- `Execution/RateLimit.cs` — `RollingRateLimit.SendOrder`: rolls the ring and counts the send like
  `TrySendOrder`, but skips the Limit check; the byte still stops at 255 rather than wrapping.
- `Provider/RiskLayer.cs` — `ValidateOrder` calls `SendOrder` for a `Cancel` and `TrySendOrder`
  for everything else. Found on the first test on a new CME release: the limit had been set to 10
  and a pause could not cancel the algo's orders, so they sat in the book. Spec.md "Order rate
  limit" paragraph updated: the combined window now costs create/amend throughput while cancels
  fly, not cancel throughput.

## 2026-09-22 — order-state reconcile before release (risk aggregate leak)

- Root cause of the leaked `WorstLong/ShortWorkingQuantity` (10 long / 20 short reserved with
  nothing working after one simulated day of `Make`): the simulator acknowledged a marketable amend
  or create only for the remainder, so an order that filled on arrival delivered a `Fill` carrying
  a quantity the server had never seen acked. `RiskLayer.OnOrderState` releases the old-to-new
  change only on `Acked`, so the difference leaked for good. Ledger replay of the 2026-09-22 sim
  audit: 1,316 such orders, 12 leaking. Fixed in the simulator (below); `OnOrderState` keeps its
  two-branch form with a comment stating the acceptance-before-trade contract it depends on. A
  reconcile-on-any-quantity-change variant went in as 1eef81a and is reverted here: correct, but
  it hid the sequence violation and made the code say something other than the rule.
  Spec.md "Acceptance before trade: OrderRisk depends on it"; cpp_alignment.md §5.
- `Simulator/ServerSimulator.cs` — `Enqueue` now acks before it trades, as CME does: the `Acked`
  state goes out first (queue position 0 if marketable, else the book position), then `Take` sends
  the fills, and a remainder rests at the limit with no second ack. Before, a marketable new order
  or replace produced fills with no ack at all, and a partial fill produced fills then the ack; the
  2026-09-22 MYM audit had 1,316 such orders. `IsMarketable` probes the opposite best rather than
  enqueueing first, because a crossing order in our own book flips `IsCrossed` and `Take` would
  never trade. Create, reprice and amend-up all funnel through `Enqueue`; `Reduce` was already right.
- Spec.md — "In-Flight Mitigation is always on": tag 9768 = 1 on every session; `QuantityFilled`
  is cumulative across every replace; an adapter that cannot get IFM must normalise `CumQty` before
  the server sees it.
- `cpp_alignment_report_2026-09-22.md` (new) — the two contracts for the C++ session and adapter:
  acceptance before trade (the two-branch `OnOrderState`, why it is not defensive, the leak
  evidence, the audit check) and IFM always on (`CumQty` = `QuantityFilled`, cumulative).

## Unreleased (working tree, 2026-09-14)

### One strategy run per `ReadSocket` pass (see Spec.md "One strategy run per pass")

- `Provider/Client.cs` — two-phase pass: fold everything queued, then raise once per dirty book
  (`Instrument.RaiseChanged`) and position (`Position.RaiseChanged`). Each instrument ring is read
  at most 64 times per pass. Strategies keep their `QuoteChanged` / `PositionChanged` subscriptions.
- `Data/Instrument.cs` — `OnMarketByPriceDelta` split into `ApplyMarketByPriceDelta` (per delta:
  image + `MarketByPriceDelta`) and `RaiseChanged` (per pass: `QuoteChanged` only if the quote
  differs from the start of the pass, then `MarketByPriceChanged`).
- `Provider/Position.cs` — `OnPositionHeader` → `ApplyPositionHeader` (keeps the latest header) +
  `RaiseChanged`.
- `cpp_alignment_report_2026-09-14.md` (new) — port note: the two phases, the 64-read bound and
  why the execution channels are unbounded, the net-change `QuoteChanged` rule, event ordering,
  verification.
- `Workspace/Workspace.axaml(.cs)`, `Workspace/WorkspaceRunner.cs` — File → Save Screenshot...:
  save picker, PNG of this workspace window via `CaptureScreenshotAsync(path, window)`.

## 2026-09-10 — single-writer server rows, server-wide RiskLimit, audit-trail fixes

### `TradingStatus`: the runtime path (header byte + ring tick)

- `Data/Tick.cs` — `TickType.TradingStatus = 20` (outside the audit's OrderType byte range),
  `TradingStatusUpdate` (TickHeader + fold byte, padded to 64 like Trade), `Tick.AsTradingStatusUpdate()`;
  the `TradingStatus` enum moves here from Instrument.cs, values unchanged.
- `Provider/Server.cs` — `OnTradingStatusUpdate(in tick)`: stores the byte at `InstrumentHeader`
  offset 7 and broadcasts the tick on the instrument's data ring. Call it on the thread that owns
  that ring.
- `Provider/Client.cs`, `Data/Instrument.cs` — ring case → `Instrument.OnTradingStatusUpdate`,
  which raises `TradingStatusUpdateEvent` on change against a private mirror (the header row is
  already updated by the time the tick arrives, so it cannot be the guard).
- Not yet: `IsInSession` still follows `SessionManager`; no `HaltReason`, no audit, no simulator
  emission. C++ note: `cpp_alignment_report_2026-09-10.md`, amendment T1–T5.

### Audit-trail fixes from the 2026-09-08 live run (see Spec.md "Cancel-pending orders")

- `Provider/Position.cs`, `Strategy/Algo.cs` — a side with an unconfirmed cancel takes no new
  orders: the actives enumerator reports `IsPendingBuyCancel` / `IsPendingSellCancel`,
  `SnapshotActives()` captures them under the era rule, Phase 5's per-tick lock is seeded from
  them. RTY, NQ and NKD each hit `PositionExceedsRiskLimit` ~400 µs after a cancel-all on
  2026-09-08 because the replacement was counted alongside the still-reserved cancel.
- `Provider/RiskLayer.cs` — the server's `TargetIsActive` no-op check is Amend-only. A Cancel
  always equals the acked profile, so with the check applied every first cancel was refused (69 of
  69 on 2026-09-08) and only the Seq+2 retry got through a round trip later.
- `Execution/Order.cs`, `Provider/Client.cs`, `Provider/Server.cs` — one clock for the audit:
  `OrderTarget` gains `TriggerTimestamp` (NIC arrival of the message the target reacted to; 44 →
  52 bytes, wire), `OrderHeader.NicTimestamp` on a target is the client's send time, the server
  stamps `NicTimestamp = now` on every state it applies (fills share their state's stamp) and on
  its own rejects (so a reject sorts after the target it copies).
- `Logging/LoggingServer.cs`, `Widget/AuditTrailWidget.axaml.cs` — the logging server orders
  execution records by `NicTimestamp` (`RiskLimit` by its own `Timestamp`, control requests at the
  watermark); the audit widget uses the same key, stable-sorted, and reverses the live tail so ties
  sort as in history.
- `Provider/AlertManager.cs` — alert wire is `Header | [OrderRejected | String64 Symbol] | ASCII
  message`; the symbol is resolved and an exception rendered on the alert thread, never the
  caller's. `AlertWriter` decodes through the same `Alert.FromBytes`.
- `Strategy/Scenario.cs`, `Testing/*` — `GetFuture` only registers a product search in
  simulation; the Testing scenario pairs micro/full contracts (MYM/YM, M2K/RTY, MNQ/NQ, MNK/NKD);
  profit series per root/total via before/after tick hooks.
- `cpp_alignment_report_2026-09-10.md` (new) — amends the 2026-09-08 report for everything in this
  entry: `OrderTarget` 52 B, `RiskLimit` 32 B, `ControlRiskLimit`, `OrderRisk` layout, threading
  model, `NicTimestamp` stamping, verification checklist.

### `OrderRisk` quantity ceiling 55 → 65535 (see Spec.md)

- `Execution/Order.cs` — `OrderRisk` is a count + cached max + 30 × `ushort` compact array, still
  64 bytes, same API and multiset semantics. The 55 cap was the `Bitset64` + 56-counter byte
  budget. Benchmarked equal to the bitset over 1M order lifecycles; every SIMD layout was 2× slower
  (store forwarding). `Algo.NewAmend`'s clamp is unchanged and now clamps at 65535.
- `Tools/Array.cs` — `Array30<T>` (+ converter, mirrors `Array32`). `Array56<T>` is now unused.
- `cpp_alignment_report_2026-09-10_orderrisk.md` — port note for the C++ side (layout, semantics,
  reference implementation, and the differential test that validated the C# struct — 400k random
  ops against a plain list; the C# test itself is not in the repo).

### Risk-limit edits are requests: `ControlRiskLimit` on the execution channel (see Spec.md)

- `Provider/Allocate.cs` — `ControlRiskLimit` (`ControlType.RiskLimit = 201`): config fields only.
- `Provider/Server.cs` — `ReadExecution` applies it on the CoreGroup thread (`OnControlRiskLimit`:
  fields in place under the row's seq bump, aggregates untouched, the row posted to the server's
  audit socket — the request itself is not audited, the client tap already logs it). `OnRiskLimit`
  and `SaveRiskLimit` deleted: the server no longer accepts a `RiskLimit` row from a client and no
  longer writes `.risklimit` files.
- `Logging/LoggingServer.cs` — the `AuditWriter` appends a posted `RiskLimit` row to
  `RiskLimits/<symbol>.risklimit` (`_riskLimitWriters`, same path helper the server reads back at
  allocation; the reader flushes every writer per batch, like fills and positions). Only the
  server's audit socket ever carries a row now. `ControlRiskLimit` gets a serializer case so the
  tapped request appears in the client's audit.
- `Provider/Client.cs`, `Provider/TCPServer.cs`, `Widget/RiskLimitEditDialog.axaml.cs`,
  `Widget/RiskLimitsWidget.axaml.cs` — send the request on the instrument's execution channel
  instead of the row on the admin channel.
- `ControlAlgoStatus` moved the same way: `Client.OnControlAlgoStatus` writes it on the instrument's
  execution channel, `ReadExecution` audits and applies it, `ReadAdmin` no longer accepts it. The
  admin thread now touches no instrument row after allocation.
- `Widget/RiskLimitsWidget.axaml.cs`, `Widget/PositionsWidget.axaml.cs` — the server polls a
  client's execution channel only for CoreGroups it has allocated in, so the GUI allocates the
  instrument to its manual client first when it has not (queued ahead of the control).
- `cpp_alignment.md` §5 and the 2026-09-08 report C3/C4 — the copy-the-live-`Worst*` item replaced;
  the queue-routing prescription amended to channel routing.
- `Provider/Context.cs` — client-level `AllocateInstrument(clientId, instrumentId)` returns early
  when the client's bit is already set. It used to rewrite the live local position row from file
  (forcing Paused) and RMW the bitsets on every call from the admin thread — the strategy-0 union
  rule re-ran it on every other client's allocation of the same instrument, against a row the
  CoreGroup thread may be filling. Rows are now initialised exactly once, like the instrument-level
  half; the admin thread no longer writes any row a client is trading.
- `Provider/Server.cs` — threading comments rewritten: there is no RX thread. One CoreGroup thread
  per segment runs `ReadFromIlink()` then `ReadFromClients()`, so exchange events and client
  targets/controls apply on one thread; the injection queue's only producers are off-thread cancels
  (client close on the listen thread, hub); the return-channel spinlock is uncontended insurance.
- **`StrategyId` removed from `RiskLimit` and `ControlRiskLimit`** — risk limits are server-wide,
  one row per instrument; the field was never enforced or restored per strategy and only decided
  where an echo went. `RiskLimit` is 32 bytes (was 36; `sizeof` assert amended in the C++ note),
  `ControlRiskLimit` 20. The strategy echo is gone (no client consumed it); the row is posted to
  the server audit only. RiskLimits widget drops its StrategyId column.
- `cpp_alignment.md` §3 and the 2026-09-08 report — layout paragraph amended; `sizeof == 64` and
  the reject reasons are unchanged, so only the field list moves for the C++ port.

---

## Unreleased (working tree, 2026-08-11)

### Strategy 0 / house book (see Spec.md)

- `Spec.md` (new) — normative model: strategies, strategy 0, workspaces, allocation union.
- `Provider/Server.cs` — every allocation also provisions strategy 0 (the union rule), which is
  what lets a server workspace create orders without a validator special-case.
- `Provider/Context.cs` — `ServerStrategyName` (house directory = Strategies tree under the
  server's leaf name), used for strategy 0's position files; `AllocateClientId` throws if a client
  takes the server's name.
- `cpp_alignment.md` (new) — full handoff list to bring HFT_cpp in line (wire structs, renames,
  RiskLayer port, strategy 0, guards).

### GUI: allocate instruments from the grid

- `Provider/Client.cs` — `ManualClient.OnAllocateInstrument` (any-thread, drains on owner thread).
- `Widget/InstrumentHeadersWidget.axaml.cs` — right-click → Allocate <symbol>; row refreshes alone
  via the client's `Instrument` event (the subs-diff timer never fires for GUI allocations in a
  strategy workspace).

### Fixes

- `Strategy/Scenario.cs` — type-guard before `AsFuture()` in the three lookup loops; a realtime
  context contains spreads/empty slots and the blind cast crashed live startup with
  NotSupportedException.
- `Provider/RiskLayer.cs` — reserve path applied the order sign three times (cancels for sells:
  WorstShortWorkingQuantity climbed positive, the +13 in the widget); now magnitude deltas with
  the direction applied once at the aggregate. Exchange-reject release direction fixed the same
  way.

---

## Unreleased (working tree)

### `Header<T>.Type` is no longer `readonly` — JSON round-trip was silently zeroing the message type

`Json.Options` sets `IncludeFields = true`. System.Text.Json will *serialise* a readonly field but
cannot *deserialise into* one, so it skips it. For any struct whose outer `Header` field is writable
(`RiskLimit`, `OrderState`), STJ built a fresh zeroed `Header<T>`, failed to populate `Type`, and
assigned that over the value the field initializer had put there. `PositionHeader` and
`AllocateInstrument` escaped only because their outer `Header` field is `readonly`, so STJ never
touched them.

Every dispatcher switches on the first byte, so the effect was: `ServerContext.AllocateInstrument`
restores a limit from `<symbol>.risklimit` with `Header = 0`; the GUI reads that struct, an operator
edits it, and `Server.ReadAdmin`'s `switch (rdst[0])` hits `default: break`. **Editing a risk limit
did nothing for any instrument whose limit had been restored from file** — no reject, no log line.

`[StructLayout(Size = 4)]` and `fixed byte _reserved[3]` unchanged, so no wire-layout consequence.

- `Data/Instrument.cs` — drop `readonly` from `Header<T>.Type`

### Risk limit working quantities were only ever growing

Three independent causes:

1. **Cancel never released.** `InstrumentSimulator.Delete` called `Update` with `Quantity` rewritten
   to `QuantityFilled`, so `Quantity == QuantityFilled` was always true and every cancel came back
   labelled `Filled` — which `RiskLayer.OnOrderState` explicitly excluded. The whole reservation
   leaked, permanently, on every cancelled order.
2. **Sign convention was inconsistent.** `GetWorstOrderQuantity` returns a magnitude, but
   `GetShortQuantityAllowance` and `OnFill` treat `WorstShortWorkingQuantity` as signed-negative. The
   reserve and ack paths added the unsigned magnitude, driving the short aggregate positive — so
   shorts grew on reserve, and `OnFill`'s `-= Quantity` (negative for a sell) grew them again.
3. **The short position limit could never trip.** Falls out of (2): with the short aggregate positive,
   `worstShortQuantity = position + worstShortWorkingQuantity` was compared against
   `< -MaxPositionQuantity` and never satisfied it, however much was working.

Fixes:

- `Simulator/ServerSimulator.cs` — `Update` takes an `OrderStateReason`; the caller states the
  terminal reason (`Canceled` from `Delete`, `Filled` from the fill/amend sites) instead of `Update`
  inferring it. `Delete` now follows the FIX shape: `OrderQty` survives a cancel, `CumQty` reports
  what filled, `LeavesQty` goes to zero via `OrdStatus`. That also preserves the order's **side**,
  which was being destroyed when nothing had filled.
- `Provider/RiskLayer.cs` — `OnOrderState` releases on `OrderStateStatus.Done` rather than on a
  reason match, so a cancel/reject/expiry cannot leak by carrying a label the switch doesn't know.
  Reserve and ack deltas now carry `OrderProfile.Sign`.

### Server startup

- `Provider/Server.cs` — `InitDirectories()` creates `Alerts`, `Audit`, `Fills`, `Positions`,
  `Series`, `Clients`, `Instruments` under the server directory, and clears them outside Realtime so
  a backtest starts from a clean slate.
- `Provider/Context.cs` — `AllocateInstrument` stamps `riskLimit.InstrumentId`, so a limit restored
  from file carries the id of the instrument it was loaded for rather than the one it was saved under.

---

## `01830c1` — ServerSimulator wraps Server; delete the duplicated server implementation

`ServerSimulator` now holds one `Server` and supplies only the timing around it:

```
exchange -> _byClientTimestamp -> ServerSimulator -> Server -> socket -> client
client   -> socket -> Server -> ServerSimulator -> _byExchangeTimestamp -> exchange
```

`OnInterject` calls `Server.ReadExecution`/`ReadAdmin` directly, so the client→server leg has no
delay; only the exchange→client leg is queued. Latency is unchanged — same enqueue timestamps, same
`<= now` release condition; only the handler on the far side of the queue moved.

Removed from `ServerSimulator` (−314 lines): its own `ServerSocket`, audit and logging sockets,
`ServerContext`, `RiskLayer`, instrument rings, `OnClientAllocated`, `OnClientDeallocated`,
`AllocateInstrument`, `OnControlAlgoStatus`, `OnRiskLimit`, `SaveRiskLimit`, `OnOrderTarget`,
`OpenInstrumentData`, and the whole `FromNicToClient_*` family.

Also:

- `Provider/Server.cs` — `Timestamp.UtcNow` → `Clock.Now` throughout. Wall-clock is correct for the
  C++ realtime server but wrong the moment a backtest drives the same code.
- `Provider/Server.cs` — `OnRiskLimit` carries the live working quantities across an operator edit,
  since the sender read-modify-writes the whole struct.
- `Provider/Context.cs` — `AllocateInstrument` sets `AlgoStatus` explicitly: `Live` in simulation (no
  operator to un-pause a backtest), `Paused` in realtime rather than inheriting whatever the restored
  row said, so a persisted `Live` cannot re-arm a strategy at startup.

**Consequence:** risk validation is now live in backtests. The simulator previously constructed a
`RiskLayer` and never called it.

---

## `ef0f733` — Mirror C++ persistence protocol, port Server, add risk limit editing

### Socket layer — byte-for-byte with the C++ `persist-client-sockets` branch

- `ServerHeader.Persistance` wire field (`sizeof` 173, offset 172)
- Shared-memory region names **path-joined** via a new `FileSystemPath operator/`, so they sanitize to
  the same string the C++ `std::filesystem` join produces. Concatenation named a different, empty
  region — `CreateOrOpen` creates it happily, so the symptom was a hang or a permanently empty read,
  never an error. The `.server`/`.audit`/`.alert` suffixes stay concatenated: they are extensions,
  and `LoggingServer` parses them with `Path.GetExtension`.
- `ClientStatus.Detached` — client sockets outlive their client process, so the server keeps writing
  into the ring (an iLink3 retransmit lands somewhere) and the audit tap keeps reading. Write gate
  accepts `Open || Detached`; reads stay `Open`-only; reconnect reuses the existing socket.
- `Protocol.SkipRing` + `Recover()` on both socket halves. Nothing clears shared memory any more:
  `Reset()` cannot work as a synchronisation mechanism because it clears one side's cursors while the
  peer's live in another process. Both sides recover instead.
- `GetReadStatus`/`GetReadStatusFromRing` check `Magic` and resolve the cursor the way the read path
  does, so probe and read cannot disagree permanently.
- `LoggingServer` — resubscribe hardened against the deferred-removal race, identity-checked
  `TryRemove`, and `RiskLimit` recorded in the audit.

### `Provider/Server.cs` — port of `Server.hpp`

Method-for-method, latency-free. Divergences, all forced: `NewSeries`/`LoggableManager` omitted
(they live in Strategy, which references Provider); `WriteToExecution`'s one-arg template takes the
header explicitly (C# generics can't read a field off an unconstrained `T`); `ExecutionLock` replaces
`RAIISpinLock`; `CancelAllOrders` builds a probe `OrderId` per slot because Context keys order rows by
`OrderId` rather than a raw global index.

### Risk limit editing

Right-click a row → dialog → admin channel → server applies, appends to `<symbol>.risklimit`, grid
picks it up. `RiskLimit` gained `Timestamp` and `StrategyId`.

---

## Open / known incomplete

- ~~**`RiskLayer` reservation throws client-side.**~~ — resolved 2026-10-04: the client's RiskLayer
  runs over the client's own writable context and reserves for its algo orders (see the top entry).
- ~~**Acked amend-down releases nothing.**~~ — resolved 2026-10-04: `OrderRisk` records the acked
  quantity, so an acked amend-down releases at the ack.
- ~~**Rate limits are enforced nowhere.**~~ — resolved: order entry is rate-limited per CoreGroup by
  `RollingRateLimit` (`TooManyOrdersPerSecond`), server-side only.
- ~~**`OrderRisk` caps order quantity at 55**~~ — resolved 2026-09-09: ceiling is 65535 (compact
  array layout, see the top entry and Spec.md).
- **Order.hpp is not yet mirrored** for `RiskLimit`'s new fields. C++ `RiskLimit` is 40 bytes; C# has
  moved on. See `RiskLayerRefactorPlan.md` §5 Step 0.
- **Simulation defaults limits to `int.MaxValue`** (`GetMaxLimits`), so no backtest has ever exercised
  a quantity limit. `Scenario.SetRiskLimit(instrument, maxOrderQuantity, maxPositionQuantity)` is the
  hook for the certification harness.
- **Unknown message types are dropped silently** — `default: break` in `Server.ReadAdmin`/
  `ReadExecution`, `default: return ""` in `AuditWriter.SerializeToLine`. The `Header<T>` bug above
  was invisible for exactly this reason; counting or logging them would have surfaced it immediately.

See `RiskLayerRefactorPlan.md` for the full risk-layer design and the certification evidence plan.

# Spec

Normative model for the server, strategies, and workspaces. Code comments stay one line and point
here; the reasoning lives here.

## Strategies and clients

- A **strategy** is a book. Every strategy is backed by exactly one algo client — there is no
  strategy without an algo client, with one exception:
- **Strategy 0** is the reserved **house book** (`OrderIdAllocator.ServerStrategyId`). It has no
  algo. Its client-id bit is pre-set at server startup so the allocator can never hand id 0 to a
  connecting client, and so the slot stays addressable (`ThrowIfClientIdOutOfRange`, RiskLayer's
  `StrategyIdNotAllocated`).
- Manual orders always have `ClientId != StrategyId`, which is what `IsAlgoOrder()` keys on: no
  pause gate, no algo attribution. Algo orders have `ClientId == StrategyId`.

## Strategy 0 — the house book

- **Allocation invariant:** strategy 0 is provisioned with the **union of every allocation — algo
  and GUI alike**. Whenever any client is allocated an instrument, strategy 0 is allocated it too
  (`Server.OnAllocateInstrument`).
- **Why:** a server workspace can always trade whatever anyone can see, and `ValidateOrder` needs
  no house special-case — a manual create with `StrategyId 0` passes the same
  `InstrumentNotAllocated` check as any algo order. The invariant "every order's strategy is
  provisioned for its instrument" holds uniformly.
- Strategy 0 gets no core-group poll bit (it has no socket; order states route on the sender's
  channel, which registers itself on allocation) and no admin echo (nobody is listening).
- **Files:** strategy 0 has no socket to take a name from. Its directory is
  `ClientContext.GetDirectoryPath(ServerName)` — the Strategies tree under the server's leaf name —
  exposed as `Context.ServerStrategyName`. Its position files therefore live alongside every other
  strategy's, and cannot collide with the server-wide aggregate rows in `<serverDir>\Positions`.
- **Name reservation:** because the house directory is named after the server, no client may take
  the server's leaf name — `AllocateClientId` refuses it (`ReservedClientName`). (`<serverName>_GUI<n>`
  workspaces are unaffected; only an exact leaf match is reserved.)

## Workspaces (GUIs)

- **Any number of workspaces may share a principal** (the server, or one strategy), on any mix of machines: each is a
  client in its own right, named `<principal>_GUI<n>` (on a remote desk `<principal>_GUI<n>@<machine>`, see "Remote
  workspaces") with its own client id, order slots and folder, booking to the principal. `ManualClient.Open` takes the lowest free number from 0, skipping a name a live GUI holds
  (`AlreadyConnected`); a name whose previous GUI's orders are still being cancelled (`NotReady`) is asked again once a
  second (a new number would take another client id for the week), for up to 5 s: a cancel normally completes in
  milliseconds, so longer means the exchange cannot confirm it, and a GUI must still open; at most 8 per principal. Why the lowest free
  number and not a name unique per launch: under `Persistance` a name keeps its client id for the week, so per-launch names
  would use up the 64 ids; numbered names use only as many as the most GUIs ever open at once. Saved layouts live under
  the principal, so every number shares them.
- A **server workspace** sends every OrderTarget with `StrategyId = 0`. It points at the
  house book. It sees ALL orders, ALL positions, ALL risk limits across every
  allocated instrument of every strategy, and its position view is the sum over all strategies.
  It can cancel any client's orders and amend any manual (non-algo) order (global order-id
  addressing); its creates always book to strategy 0. An algo's orders are cancel-only from a GUI
  (see "Manual orders" below).
- A **strategy workspace** shadows one algo client. Its ManualClient has its own client id — its
  order targets allocate in its own id space — and books manual orders to the principal strategy.
  It lists every order **booked to that strategy**, whichever client sent it: the algo's, its own, a sibling GUI's, a
  remote desk's through the Mirror (`WorkspaceContext.EnumerateOrders` walks every slot, filtered by the order id's
  `StrategyId`). Listing by client instead (the algo's slots plus this GUI's) hid a sibling GUI's manual orders for the
  same strategy: the position moved on their fills, but this workspace could neither show nor cancel them. A server
  workspace lists every client's orders. The Audit Trail still reads only this GUI's own audit folder.
- Allocating an instrument from a GUI (InstrumentHeaders right-click) allocates it to the GUI
  client, which is what makes the server open the instrument's data feed; by the union invariant it
  also provisions strategy 0.
- **An order shows from the click.** The order walk (`OrderEnumerator`) finds a slot by its target row, which the GUI
  writes as it sends. Until the server's state row is about that order (a fresh slot has none; a reused one still holds
  the previous order), the walk shows `OrderState.NewInFlight(target)`, the same Active/InFlight state the server then
  writes; a Create refused before the server saw it has its target marked Done and is not shown. Once the server's row is
  the order's, that row decides, and Done is not shown. On a remote desk the target row is written by the ManualClient
  beside the server, so the order shows once that row returns (see "Remote workspaces").
- **Another client's order shows once the server has it.** The walk shows an unanswered order only for the workspace's
  own GUI client (`inFlightClientId`); another client's (the algo's, a sibling GUI's, a remote desk's) is listed from the
  server's first state row about it. Why: a cancel from this GUI travels its own channel, so it could reach the server
  ahead of the other client's Create and be dropped, leaving the order working while the operator thinks it cancelled. Its
  own orders need no such wait: its cancel follows its Create on the same channel. Hiding the order, rather than guarding
  each action (context menu, Cancel All, amend, ladder), means no widget can act on it too early. The cost: another
  client's order is invisible for the microseconds before the server reads it (longer in a paused simulation).
- **Every grid column sorts (2026-10-26).** A header click sorts its column, a second click reverses it. Each column names
  its key (`SortMemberPath`): the grids use compiled bindings, from which the DataGrid infers none. A column that shows
  formatted text sorts by the number behind it (prices by ticks or price, `AvgPrice`, `EfficiencyRatio`,
  `DurationSeconds`), since "10,000" sorts before "9,000" as text. The DataGrid finds the key by name (reflection), which
  AOT trimming strips, so `Workspace.csproj` roots the `Widget` assembly (`TrimmerRootAssembly`); before, no column sorted
  in a published desk Workspace (a sort by name left the rows as they were, without an error), though it did under the
  debugger. `Timestamp` implements `IComparable` because the grid compares cell values as objects. Avalonia has no
  unsorted state: a widget's own order (Orders by symbol and price, Fills newest first) returns when it is reopened. A row
  whose value changes in place keeps its place until the next click; added and removed rows land in order. Fills and the
  Audit Trail sort the rows loaded so far (older pages load as the grid scrolls).
- **Chart sessions (2026-10-24).** A chart's right-click menu has *Add Session* with a tick-box per `Session.AllWithHours`
  (every coded session but Perpetual, which is always open). A ticked session is shaded in its own pale colour across
  every panel, behind the series, from each open to its close (`Session.EnumerateOpenIntervals`: the session's time
  zone with its daylight saving, and its weekend close); the shading runs past the data to the session's next close, so the
  session open now shows. A saved layout keeps the ticked sessions by name. The hours are `Session`'s weekly schedule: no
  holidays or early closes, and most non-US sessions are coded 10:00-16:00 local as placeholders.

## The era rule (algo tick consistency)

Position and order state are updated by separate messages (Fill, then OrderState), and in realtime
they can be applied on a different thread than the one running the algo. A tick therefore cannot
get an atomic snapshot of both — and no write ordering fixes that; it only picks which half is
stale. The two failure directions are not symmetric:

- actives/fills **fresher** than the position used for targets → the amend total
  (`working + filled`) comes out too large → **amend up, exposure grows**. Unsafe.
- position fresher than the actives used for amends → under-quote by the skew → amend down,
  corrected on the next tick. Safe.

**Rule: the actives an amend is built from must never be fresher than the position its targets came
from.** Implemented by `Algo.SnapshotActives()` — capture the actives at the top of the tick,
BEFORE reading position (`GetPositionQuantity()` bundles the ordering); `Target()` zippers against
the snapshot. Fills landing after the snapshot can only under-quote, and the in-flight remainder is
absorbed at the venue by In-Flight Mitigation, which is mandatory (IFM=Y hardcoded on every
cancel/replace). No alarm sits behind those two: a `Done` with `|QuantityFilled| > worst` would
apply a negative release silently (see Open items).

`Target()` / `TryTarget()` fall back to snapshotting on entry for un-migrated callers — era-unsafe
but identical to pre-snapshot behaviour. They consume the snapshot flag at the very top, before the
paused early return and before any validation throw, so a stale list can never be zippered by a
later call: a tick that returned early (paused) or threw (self-crossing targets, an unsupported
`TimeInForce`) leaves no snapshot behind, and the next call takes a fresh one.

The client's `RiskLayer` copy (see "Risk layer: one code, a server copy and a client copy") is not
part of the snapshot and needs no era handling. It moves only when this client's `ReadSocket` folds
a message, on the strategy thread between ticks, so it is constant for the length of a `Target`
call except for that call's own sends, which it counts as they are made. Its `Position` and its
reservations move in the same message (see "RiskLimit is config, WorkingRisk is the live
numbers"), so a fill is room-neutral there whenever it arrives.

### One strategy run per pass

`Client.ReadSocket()` is two phases. Phase 1 folds every queued message — fills, states, position
rows on the execution channels, then every delta on every subscribed instrument ring — into the
images and only marks which books and positions changed. Phase 2 raises `QuoteChanged` /
`MarketByPriceChanged` once per changed book and `PositionChanged` once per changed position, on
the final state. Strategies subscribe to those as before; a pass with three deltas on one book
raises `QuoteChanged` once, after all three, never on a state the next message in the same buffer
has already superseded. `QuoteChanged` compares the quote against the start of the pass, so a
quote that moves and moves back within one pass is not a change. Trades still fire per print
(`TradeChanged`), and `MarketByPriceDelta` still
fires per delta for consumers that need every one (ladders, queue tracking). Each instrument ring
is read at most 64 times per pass so a saturated feed cannot starve phase 2; the remainder is
picked up next pass, still coalesced. The server side does the same thing one hop up: the book
builder folds every packet the NIC has queued and publishes one tick per touched instrument.

### Cancel-pending orders are not free capacity

`Position.ActiveTargets` is the *expected future state*: an order whose cancel is sent (a Cancel
target, or an amend down to at most the filled quantity) is not in it, because its future is
"gone" and the zipper must never amend it. But it is not gone yet: the exchange holds it until it
acks, and the server's `RiskLayer` keeps its worst-case quantity reserved until the exchange
reports `Done`. Until 2026-09-09 the algo saw only the empty side, Phase 7 created a replacement,
and the server counted both — `PositionExceedsRiskLimit`, instrument paused. 2026-09-08
(SandP500_Testing on CME_NewRelease): RTY, NQ and NKD each did exactly that, ~400 µs after a
cancel-all, on the first quote back from a book clear.

**Rule: a cancel frees nothing until its `Done`, on the client as on the server.** The enumerator
hides an order as soon as its cancel is sent, and the client's own `RiskLayer` copy keeps that
order's worst case reserved in the client's `WorkingRisk` until the same `Done` that releases the
server's. A replacement therefore sees exactly the room the server will see: `TryTarget` clips it
to that room, and strict `Target` sends it as asked, so the client's own `ValidateOrder` refuses it
with `PositionExceedsRiskLimit` and the algo pauses. The same check covers an amend-up of a live
same-side order while another order's cancel is pending, which the old per-side lock did not.
(From 2026-09-09 to 2026-10-04 this was a lock instead: the enumerator reported
`IsPendingBuyCancel` / `IsPendingSellCancel` and Phase 7 created nothing on such a side. Both the
flags and the lock are gone; the reservation check replaces them exactly.)

The order is hidden even before the server has read its Create. Until then the state row still
holds the slot's previous order, so the cancel test uses `filled = 0` unless the state row is this
order (full `ClientOrderId`, generation included): the previous order's fills are not ours and must
not turn a fresh Create into a phantom cancel. (Before 2026-10-04 a cancel sent behind an unread
Create was still yielded as active, and the zipper could amend it again.) A rejected cancel
flips `targetRejected`, the order reappears as active, and Phase 5 re-cancels it.

Related: the server's `TargetIsActive` no-op check applies to Replace and Reduce only, never to
Cancel. A Cancel is built as
`working + filled` at the active's price, i.e. it equals the active's profile: the acked one once
the state row is current (`stateIsTruth`), otherwise the latest in-flight amend's own. With the check
applied to cancels every first cancel of an acked order was refused (69 of 69 on 2026-09-08) and
only the client's Seq+2 retry got through — one round trip later, on the next quote. The paused
branch's `CancelAllOrders` builds the same shape (`NewAmend(active, ticks, 0)`). It used to send
`Quantity = filled`, which for an unfilled order is 0 and has no side, so a cancel racing the last
fill came back `OrderNotFound | QuantityNotValid | SideNotValid` and re-paused the algo.

### In-Flight Mitigation is always on

Every iLink session this platform trades on runs with In-Flight Mitigation enabled, tag 9768 = 1,
and the whole order model assumes it. `OrderState.QuantityFilled` is cumulative over the life of the
order across every cancel/replace: a replace never restarts it, a fill after a replace adds to it,
and the server's `WriteOrderState` keeps the larger of the stored and reported values only as a guard
against a stale message, never to bridge a reset. Without IFM CME restarts `CumQty` at zero on each
non-IFM modification, the risk layer's `Done` release of `worst - QuantityFilled` would over-release
by everything filled before the replace, and the position ledger would lose those fills. The
simulator is IFM-like by construction. A C++ session that cannot get IFM must normalise `CumQty`
to cumulative in the adapter before the state reaches the server; it must never pass a reset through.

### Acceptance before trade: OrderRisk depends on it

**Contract: the exchange acknowledges an order or a replace before it reports any trade it
causes.** A marketable create arrives as `Acked` then its fills; a marketable amend arrives as
`Acked` with the new quantity then its fills; a remainder rests at the limit with no further
acknowledgement. As we understand iLink 3, the New or Modify execution report always precedes the
trade reports (still to be confirmed by CME, see "CME confirmations pending", item 3), and the
simulator honours it in `Enqueue`, which sends the `Acked` state before
`Take` trades. Any adapter that sits between a venue and the server must preserve this order and
must never coalesce an acceptance into a fill or a cancel; if a venue ever does, the adapter
synthesises the acceptance, the risk layer is not the place to cope.

The risk layer's arithmetic is built on the contract. `OnOrderState` has two branches. On `Acked`
it retires the pending target, records the acked quantity in `OrderRisk`, and applies the change in
worst case around that (`max(acked, max in flight)` before and after). On `Done` it releases what
remains, measured from the worst case `OrderRisk` itself holds (`GetAbsWorstOrderQuantity() -
|QuantityFilled|`), and zeroes the row. Only `Acked` retires an amend.

**Contract on the shape of a state.** Two rules follow from how `OnOrderState` reads a state, and an
adapter (C# or C++) must keep both:

- **A `Done` state never carries reason `Acked`.** The `Acked` branch is tested first, so a state
  that is both `Done` and `Acked` takes only the `Acked` branch: it never releases the rest and never
  zeroes the `OrderRisk` row, and the reservation leaks. A terminal state carries a terminal reason.
- **Every state carries the order's own signed profile, terminal states included** (the FIX shape).
  Both branches take the release side from `OrderProfile.Side` (`Buy ? 1 : -1`), and `Side` is the
  sign of the quantity, so a `Done` or `Acked` carrying a zero-quantity profile would be released on
  the short side.

What a violation costs changed on 2026-10-04. Before, the acked quantity came from the message, so
a fill carrying a quantity the server had not seen acknowledged leaked the difference into the
working aggregates for good. Now the acked quantity lives in `OrderRisk`, so an ack that rides
inside a Fill or a Done is simply not seen: that target stays in flight, its worst case stays
reserved (over-reserving, and holding one of the 29 in-flight slots) until `Done`, and `Done`
releases exactly what the order holds. The sum of every delta applied for an order is zero whenever
the fills this `RiskLayer` saw sum to the `Done`'s `QuantityFilled`, acked or not. The contract still
matters, for timely release: without the `Acked` a reservation shrinks only at `Done`. Whether CME
can ever send an amend acknowledged only inside a fill is one of the open CME confirmations (it
does not happen on CME as we understand it, nor in the simulator).

That was not hypothetical. Until 2026-09-22 the simulator matched first and acknowledged only the
remainder, so a marketable amend that filled on arrival produced a `Fill` with the new quantity and
no `Acked` at all. Over one simulated day of `Make`'s ladder that was 1,316 orders, twelve of which
leaked, enough to leave 10 long and 20 short reserved with nothing working. The fix was in the
simulator, not the risk layer. A variant that reconciled on any quantity change regardless of
reason was tried and reverted: it was correct, but it hid the sequence violation instead of
surfacing it, and it made the code say something other than the rule.

**The check.** A fill whose quantity differs from the last acknowledged quantity is a contract
violation. The per-order ledger replay from the client's audit reports both that count and any
order whose reserve and release do not net to zero; run it on every simulated day that touches the
order path, and expect zero of each.

### OrderRisk: in-flight quantities are a scanned array, not a bitset

Per order slot each `RiskLayer` (the server's, and each client's for its own orders) reserves the
largest quantity that could end up working: the acked quantity or any unacked amend, whichever is
bigger (`OrderRisk.GetAbsWorstOrderQuantity()`). Amends
are pipelined, so the unacked set is a multiset — the same quantity at two prices is two entries
and one ack retires one of them. Until 2026-09-09 that multiset was a `Bitset64` over quantities
plus 56 byte counters, which is the only reason order quantity was capped at 55: the cap was the
64-byte budget, not a risk decision.

It is now a count (@0), a cached max (@2), the last acked quantity (@4, 0 until the first ack) and
29 `ushort` entries (@6..63) in the same 64 bytes. The worst case is `max(acked, max in flight)`.
`Ack` removes one matching entry and records the acked quantity; `Reject` only removes. Holding the
acked quantity in the row (since 2026-10-04; it used to be passed in from the `OrderState` row)
makes each `RiskLayer` copy self-contained: the client computes worst from the acks it applied
itself, not from a shared state row it reads at a different time and that may still hold the slot's
previous order. One entry was given up to keep 64 bytes. Add appends and raises the max; remove
scans for one matching entry, swap-removes it, and rescans for the max only when the max itself
left. The scan is fine because the in-flight count on one order is one to three in practice (a
same-profile amend is refused while one is active, an order with a cancel sent is never amended
again) and the worst case is a 29-entry scan. Measured against the bitset over 1M
order lifecycles it is within 1 ns per lifecycle. Every SIMD layout tried was 2× slower: a 2-byte
lane store followed by the 64-byte reload `RiskLayer` makes right after every `TryAdd`/`Ack`
defeats store forwarding, and the horizontal max ran on every query. A pessimistic high-water mark
is faster still but over-reserves during pipelined amend-downs and collapses on a stray ack — the
multiset's tolerance for an ack it never reserved is deliberate and kept.

Limits: quantity 1..65535 (`QuantityNotValid` outside), 29 in-flight targets per order
(`OrderRisk.IsFull`; `TooManyActiveTargets` on the 30th). What happens at the cap is in "The
29-amend cap". `RiskLimit.MaxOrderQuantity` remains the operative per-order bound; `Algo.NewAmend`
still clamps to `OrderRisk.MaxOrderQuantity`. The differential test against a plain list, re-derived
for the acked field and the 29-entry cap, is in `cpp_alignment.md` §6 and
`cpp_alignment_report_2026-10-04.md` §6.2; it supersedes §6 of
`cpp_alignment_report_2026-09-10_orderrisk.md` and its op counts.

### Order rate limit: the one limit whose default is the exchange's own number

CME Globex throttles order entry per session and publishes two lines: messages other than cancels
are rejected past the first and the session is terminated past the second, at 500 and 750. Cancels
are counted separately with their own, higher, pair. We deliberately do not split the buckets: one
combined window sized at the tighter line can never breach either. A cancel, or a Reduce verified
against the order's current state (`OrderProfile.IsReduceOf` the server's `OrderState` profile:
same price and side, less quantity), is counted in that window but never refused by it
(`SendOrder` rather than `TrySendOrder`): both only take risk off, and a throttle that holds one
back turns a pause into a book of orders nobody is watching, which is what the first test on a new
CME release did with the limit set to 10. A Reduce that cannot be verified — one queued behind an
unacked Replace, so the state row still shows the pre-Replace profile — is throttled like any
Replace, so mislabelling an amend as a Reduce cannot skip the throttle. So what the combined window
costs is create and replace throughput while cancels and reduces are flying, which is the right
thing to give up. The throttle is server-only: the client's `RiskLayer` copy does not run it, so
`TryTarget` cannot see it (see Accepted residuals).

CME's page does not settle whether the window is one second or three. The application section reads
like a count within an interval, while the mass quote and admin sections say MPS. Three seconds is
the stricter of the two readings, so a production file should say 3 seconds and sit under the
reject line rather than on it. That is safe under either reading, and only loosening it needs an
answer from the GCC.

The number lives in a file, not in code, because New Release, Certification and Production publish
different limits and the server directory is the environment:
`S:\Servers\Realtime\CME_NewRelease\RateLimits\SandP500.ratelimit` holds one static JSON `RateLimit`,
the whole file, read when the server context loads the CoreGroup's file; the defaults without one are
those of `.risklimit`. One file per CoreGroup, named by the CoreGroup's name, since a
CoreGroup maps to an iLink session and that is the scope CME throttles. With no file the default is
the same as every other limit: `GetMaxLimits` in simulation and `GetMinLimits` in realtime, so a
production server nobody configured refuses every create, replace and unverifiable reduce with
`TooManyOrdersPerSecond`,
the same refusal an unset risk limit gives, until the operator writes the real number for that
environment. `GetMaxLimits` is `int.MaxValue` in a 1 second window, which leaves only the burst cap
below, 255 sends in one 32 ms bucket. The ring is not persisted: it is a few seconds of state and is
rebuilt empty on load.

The window is `RollingRateLimit`, 64 bytes, so that it can sit in a shared array and the GUI can show
CoreGroup, Duration, Limit and Count from another process. The id is `RateLimit.RateLimitId`, generic
on purpose: the risk layer happens to key it by CoreGroup, the struct does not know that. The array is
`Context._rateLimits`, one row per CoreGroup, server-written like `_riskLimits` and reached through
`GetRateLimit`; it is named for the rate limit rather than the rolling model because another model
could occupy the same 64-byte row later, cast by the caller. The server writes the file's `RateLimit`
into the row when it loads the CoreGroup; the risk layer takes the row by ref with no seq bump (unlike
the working-risk aggregates, which live on the seq-bumped `WorkingRisk` row). Count is not
state and must not be
published as a number: it is a function of the ring and of the reader's clock, and a published
integer freezes the moment the algo stops sending. So the ring itself is what is shared, and every
reader derives Count at its own `Clock.Now` with `GetCount`, which mutates nothing.

To fit 64 bytes the exact timestamps are replaced by 32 byte-sized buckets. The trap in any bucketed
count is that it under-states: the newest bucket is only partly elapsed, so N buckets of width
Duration/N cover less than Duration and a message can drop out of the sum while still inside the
true window. That is the unsafe direction for a throttle. The buckets are therefore Duration/31 wide,
so the 32 of them span one bucket more than Duration and the count covers a superset of the window.
It can over-state by at most one bucket, about 97ms of a 3 second window, and never under-state.
A bucket refuses at 255 rather than wrapping, which makes 255 messages in one bucket the burst limit.
`Total` carries the sum of the buckets incrementally, so a send is one compare against it and never
loops; a reader starts from `Total` and subtracts only the buckets that expired since the writer last
rolled, usually none. Summing the 32 bytes was 16 ns and was the entire cost of a send.
The property that matters, that no true window ever contains more than Limit admitted messages, is
what the test checks; the over-count is the price of the 64 bytes.

### CoreGroups are named by the server, not by an enum

A CoreGroup is an execution channel, a server thread and, on CME, an iLink session. Its id is a
small integer everywhere: `InstrumentHeader.CoreGroupId`, the socket channel index, the audit
channel, the `RateLimits` row. Its name used to be a `CoreGroupId` enum in Data, which meant the
platform knew CME's grouping (S&P 500, Equity, Forex, Crypto) and nothing else's. A Eurex, NYSE or
Binance server has its own natural split, so the name is now data the server publishes: the
`CoreGroups` shared array, one `CoreGroup` row per id, loaded by the server context when it opens
for write from `<server>/CoreGroups/<CoreGroupName>.coregroup`, one static JSON `CoreGroup` per
file. Static, whole-file JSON rather than the appended-line form of `.risklimit`: a CoreGroup, like
a rate limit, is never amended at runtime, so there is no last line to win. The row carries the name and the
cores the group's threads pin to (`ServerCoreId`, `MarketDataCoreId`, `StrategyCoreId`,
`ReservedCoreId`), chosen for the machine the server is on. `ServerHeader.CoreGroupIds` still says
which ids exist, because channels and threads are built from it before the context exists; a file
whose id is not in it fails the load loudly. Each group's rate limit is read right after its row,
by name, which is why the name has to exist first.

Each server keeps its own enum for its own groups, as a convenience for naming them, and the enum
stays on that server's side: a CME server files the four CME groups, a NYSE server its stock groups,
and the C# simulator seeds one file called `Simulation` if none exists. Clients choose a group by
name. `Scenario.CoreGroupName` is a string; after connecting, the scenario looks the name up in the
server's array, which throws if no such group exists, and pins its strategy thread to that row's
`StrategyCoreId`. The Testing scenario keeps a `CMECoreGroupId` enum only because it is a CME
strategy and prompts for one of CME's groups. The GUI shows a rate limit's CoreGroup by looking its
id up in the same array.

### Risk-limit edits are requests; the CoreGroup thread owns the row

There are two levels of limit, both `RiskLimit` rows the server owns: the firm's, one per instrument
in `ServerRiskLimits`, and each strategy's, one per strategy and instrument in `LocalRiskLimits`
(see "Two levels of risk limit"). `StrategyId` was removed from the row and the request on
2026-09-10, when it only chose where an echo went; it came back on the request on 2026-10-15 to
choose the row edited.

A `RiskLimit` row (28 bytes) is operator config only: `MaxOrderQuantity`, `MaxPositionQuantity`,
`MaxFillsPerSession` and the edit `Timestamp`. The risk layer's live numbers are on a separate `WorkingRisk` row (see
"RiskLimit is config, WorkingRisk is the live numbers"). Until 2026-09-10 the GUI edited by sending
the whole row back on the admin channel, and the admin thread wrote it whole while the CoreGroup
thread was reserving and releasing in the same row (the aggregates sat on `RiskLimit` then);
copying the live aggregates across before the write only shrank the window in which an edit rewound
a reservation. Since 2026-10-04 the aggregates are not on the row at all, so an edit cannot touch a
reservation.

**Rule: a client sends `ControlRiskLimit` (config fields only) on the instrument's execution
channel, and only the CoreGroup thread writes the row.** `ReadExecution` applies the request in
place under the row's seq bump and stamps `Timestamp`. The server never writes `.risklimit` files: it posts the row to
its audit channel and the logging server appends it through the same path helper the server reads
back at allocation. A crash between the post and the append can lose that one line — the row in
shared memory is the truth while the server is up, and the logging server flushes its writers
after every batch it drains, so the window is one poll pass (100 ms live). A `.risklimit` line now has no
`WorstLong/ShortWorkingQuantity` properties; older lines that carry them (or the removed
`StrategyId`) still parse, because unknown JSON members are skipped.

Lowering a limit takes effect on the next check, on both sides. Past the limit `ValidateOrder`
still lets an order keep its worst (a cut always passes), so working orders are never forced off by
an edit; `TryTarget` cuts an order back within the new limit the next time it reduces or reprices
it (an order the zipper keeps as is is never clipped). Both rows live in shared memory, so a
strategy sees an edit of its own limit as soon as the server writes it. A limit lowered between
a client's check and the server's read of the order can refuse that order at the server and pause
the algo (accepted; see Accepted residuals).

`ControlAlgoStatus` follows the same rule: it travels on the instrument's execution channel and
`ReadExecution` applies it, so the local position row's status has the same single writer as its
fills. The admin thread's only remaining row writes are allocation. One consequence for GUIs: the
server polls a client's execution channel only for CoreGroups that client has allocated in, so a
workspace allocates the instrument to its manual client before sending a control for it.

### Algo pause: two switches, the strategy's and the server's master (2026-10-15)

An algo may trade an instrument only while two switches are both Live: its strategy's own (`AlgoStatus` on the strategy's
row in `LocalPositionHeaders`) and the server's master for that instrument (`AlgoStatus` on the server row in
`ServerPositionHeaders`). `Context.IsAlgoLive(strategyId, instrumentId)` is the one check; the algo (`Position.IsAlgoLive`
in `Algo.Target`), the client's risk copy and the server's risk copy all use it, so a paused algo sends nothing, and an
order that races a pause is refused `AlgoIsPaused` at the server.
- A strategy workspace's Pause/Resume turns its strategy's switch, as before. A server workspace's turns the master: its
  control carries strategy 0, and `Server.OnControlAlgoStatus` writes the server row only, never a strategy's (the house
  book has no algo, so strategy 0's own status means nothing). Pause All in a server workspace stops every algo.
- The master is a kill switch: Live at every start, whatever its file says (`ServerContext.AllocateInstrument`). Its
  changes go on record as a fill's server row does, on the audit channel to `<server>/Positions`.
- A user's Pause (the control, from either workspace) also cancels at once, on the server, the working algo orders it
  stops: every strategy's on the instrument for the master, that strategy's for its own switch (`Server.CancelAlgoOrders`,
  2026-10-20). Algo orders only (`ClientId == StrategyId`): a manual order, a GUI's booked to a strategy or the house
  book's, is never cancelled. The cancel is numbered as a GUI's cancel of an algo order is
  (`OrderTarget.s_foreignCancelSeqJump`, see "Manual orders"), so the algo's own cancel on its next tick, or a racing
  amend, finds the order gone and is discarded (`StateIsDone`); a Create still unread is refused `AlgoIsPaused`. A pause after an algo
  order's reject cancels nothing: the algo cancels its own on its next tick, as before. Without this a quiet or stuck
  algo's orders kept working until its next tick.
- A refusal for `AlgoIsPaused` alone is discarded (`OrderRejected.IsDiscarded`), so it never auto-pauses the strategy:
  turning the master off and on leaves every strategy's own switch as it was.
- Every context maps `ServerPositionHeaders` (read-only in a client).
- Cost: one more byte read per algo decision and per algo order check. The server rewrites the row on every fill of the
  instrument, so a client reading it each tick costs that fill's write a cache-line transfer (measured on the CME box at
  about +330 ns on the first write after a read); a Local Mirror already reads every row each pass. If it shows in fill
  latency, the master moves to a row the server rarely writes.

### Two levels of risk limit: the firm's and each strategy's (2026-10-15)

Each `RiskLayer` checks one limit, its context's own, through the same `Context.GetRiskLimit(instrumentId)`:

| Where | Exposure (`WorkingRisk`) | Limit (`RiskLimit`) |
|---|---|---|
| Server | every order on the instrument, firm-wide | the firm's, `ServerRiskLimits[instrumentId]` |
| Strategy process | its own algo orders | its own, `LocalRiskLimits[strategyId, instrumentId]` |

So the firm's limit is the master: a strategy works within its own limit, and the server refuses whatever would take the
firm past the firm's. Example: firm limit 10, strategy X's 4, strategy Y's 8. X asks for 5 and its clip sends 4; Y then
asks for 8, which passes its own check (8 <= 8), and the server refuses it (4 + 8 > 10), which pauses Y.
- **Manual orders get only the firm's limit.** A client checks only its own algo orders (see "Why the client keeps its
  own copy"), and the server checks only the firm's. Strategy 0 (the house book) has no strategy limit: its orders are
  manual, and its edits address the firm's row.
- **No cross reads.** A strategy never reads the firm's exposure (the server writes it on every order, so reading it would
  cost the server a cache-line transfer per write), and the server never reads a strategy's. Declined 2026-10-15: clipping
  a strategy to the firm's room as well, and the server checking strategy limits (which would cover manual orders booked
  to a strategy, at an estimated +5–10 ns per order).
- **The server owns every row.** `ServerContext.AllocateInstrument` loads the firm's row from `<server>/RiskLimits`;
  `AllocateInstrument(clientId, instrumentId)` loads the strategy's from `<strategy>/RiskLimits` (`RiskLimit.FromFile`:
  with no file, Max limits in a simulation and Min in realtime, as the firm's). A GUI client gets a row too; it is never
  read.
- **Edits** carry `ControlRiskLimit.StrategyId`, the workspace's principal (`Context.StrategyId`): 0 edits the firm's row,
  any other id that strategy's. `Server.OnControlRiskLimit` writes it on the CoreGroup thread and puts it on record with
  `WriteToRecord`, as the master switch is: the firm's on the audit channel (Logging keeps `<server>/RiskLimits`), a
  strategy's on its execution channel (Logging's tap keeps `<strategy>/RiskLimits`; the strategy ignores the message). An
  edit of a strategy that is not connected writes the row, but its record can be lost (out of contract: a strategy workspace
  edits its own running strategy).
- **Out of contract, not checked:** the server applies the `StrategyId` a request names, so a request carrying 0 from a
  strategy workspace would edit the firm's limit, and one left at -1 throws on the CoreGroup thread and cuts that read pass
  short. The widget always sends its principal's id, on the box and on a remote desk (the Mirror passes the request through
  unchanged), so only a crafted packet on the unauthenticated Mirror link or a broken Workspace build could send either;
  the link's whitelist and VPN are the protection, as for orders and pause controls. The guard if one is wanted: the Local
  Mirror's stand-in refuses a control whose `StrategyId` is not its GUI name's principal (C#-only, about 4 lines).
- **The Risk Limits widget** shows the principal's own rows: a server workspace the firm's limits and exposure, a strategy
  workspace the strategy's, on the box and on a remote desk alike (all four arrays are under the server's name, so the
  Mirror copies them).
- Two arrays, named like the position rows, because the server writes both; `RiskLimits` was renamed `ServerRiskLimits`.
- Cost: the client's check reads a different row through a virtual `GetRiskLimit` (as `GetPositionHeader`); about 1–2 ns
  per leg, no allocation.

### Max fills per session (2026-10-15)

`RiskLimit.MaxFillsPerSession` caps the lots filled on an instrument in one session, buys and sells both counted:
`WorkingRisk.FillsThisSession` adds `|fill quantity|` in `RiskLayer.OnFill` for an algo order's fill only
(`OrderId.IsAlgoOrder()`), on both sides: the server counts every strategy's algo fills against the firm's limit, a
strategy its own against its own. A manual fill, house book or booked to a strategy, never counts (decided 2026-10-16),
though it still moves the position. `RiskLayer.OnSessionClosed` zeroes it
at the instrument's `Closed` status, on the server beside the message-efficiency day's end and in each client for its
own copy. The check is strict, assuming every working algo lot on both sides fills: the fills room is
`MaxFillsPerSession - FillsThisSession - WorstWorkingFills` per leg, where `WorkingRisk.WorstWorkingFills` is the working
algo orders' worst case, both sides summed, kept in the same `ApplyWorstQuantityDelta` as `WorstWorkingLong/ShortQuantity`. An
order may carry its current worst plus that room, so an amend is measured by its delta (amending a working buy of 10 to
20 needs room 10). `ValidateOrder` checks the position room first (`PositionExceedsRiskLimit`) and then, for an algo
order only, the fills room (`TooManyFillsPerSession`, value 57, renamed from the never-raised `TooManyOrdersPerSession`);
`TryClipToRiskLimit` clips to the smaller of the two. `GetAbsAllowedOrderQuantity(target, isFillsRoom, isWithinLimit)`
holds both rooms.
- **A manual order is never limited by fills** (decided 2026-10-16): its fills do not count, its working lots do not take
  fills room, and it is never refused `TooManyFillsPerSession`. Why: an algo that runs away and uses up the fills is
  exited by hand, and that exit must go through. Example: limit 10, an algo filled 10: every algo order is refused, a
  manual sell of 10 is accepted (the position limit still applies to it, as to any manual order).
- The count is never persisted (accepted 2026-10-15): a server restart in a session starts it at 0, so the cap is
  granted again; a strategy restart resets only its own copy. The design parked for it: the count on the server's
  position row (posted on every fill), zeroed at `Closed`, and dropped at load when the row was last written before
  `Session.CME.GetLastClose`.
- A `.risklimit` line saved before 2026-10-15 has no `MaxFillsPerSession`, so it loads 0 and refuses every order until
  the limit is set: set it on every instrument before deploying.
- Renamed at the same time: `WorstLongWorkingQuantity`/`WorstShortWorkingQuantity` to `WorstWorkingLongQuantity`/
  `WorstWorkingShortQuantity` (2026-10-16; briefly `WorstLong/ShortQuantity`), and `ApplyWorstWorkingQuantityDelta` to
  `ApplyWorstQuantityDelta`.
- The Risk Limits widget shows `Position`, `FillsThisSession`, `WorstWorkingLongPosition` (= `Position +
  WorstWorkingLongQuantity`, the position if every working buy filled), `WorstWorkingShortPosition` (the same for the
  sells) and `WorstWorkingFillsThisSession` (= `FillsThisSession + WorstWorkingFills`, the session's fills if every
  working algo lot filled); the three raw worst quantities are columns a user turns on.
- `Client.IsDiscarded`, whose simulation-only carve-out discarded a refusal for `TooManyOrdersPerSession` alone, was
  deleted: with 57 renamed it would discard `TooManyFillsPerSession` in a simulation. Both callers use
  `OrderRejected.IsDiscarded`, so the refusal pauses the algo in a simulation as in realtime.

## Risk layer: one code, a server copy and a client copy

### Why the client keeps its own copy

`RiskLayer` is built on the base `Context` and runs the same code in two places: on the server over
the `ServerContext` (source `Server`, every order on the instrument) and in every client over that
client's own `ClientContext` (source `Client`, only its own orders). Each side keeps its own
`OrderRisk` rows, one per order slot (`OrderId.GlobalIndex`), in a plain array inside its `RiskLayer`: only that
`RiskLayer` reads or writes them and no other process needs them (shared memory under each context's directory until
2026-10-16; the Mirror skipped them anyway, since they were never stamped). Its `WorkingRisk` rows are its own too, but under the
server's name, two arrays named like the position rows: the server's in `ServerWorkingRisks` [instrument],
each strategy's in `LocalWorkingRisks` [strategy, instrument], which each strategy writes (as it writes its
`OrderTargets`) and the Mirror copies to a remote desk (2026-10-16; until then a strategy's were under its own
directory, which the Mirror does not copy, so a remote desk could not see a strategy's exposure).
`GetWorkingRisk(instrumentId)` picks the context's own, as `GetRiskLimit` does. The limit rows (each side reads
its own, see "Two levels of risk limit"), `OrderStates`, `OrderTargets` and the position rows stay the one
server-wide set.

The client needs the numbers so the algo can clip a target to what the server will accept (see
`TryTarget`), and it needs its own copy rather than the server's rows for two reasons: reading the
server's rows would be reading something another thread is writing (cache lines bouncing between
the CoreGroup thread and every strategy thread, and torn reads), and the server's rows reflect sends
the client has made but the server has not read yet, so they would be the wrong numbers anyway. The
server never depends on the client doing this: it validates every order independently.

On a client the copy covers algo orders only. `ValidateOrder` with source `Client` returns after the
header and seq checks for a manual order: a manual order's position sits on another strategy's row,
which the manual client does not track. The rate limit is server-only on both counts.

### RiskLimit is config, WorkingRisk is the live numbers

`RiskLimit` (28 bytes: `Header`, `InstrumentId`, `Timestamp`, `MaxOrderQuantity`,
`MaxPositionQuantity`, `MaxFillsPerSession`) is configuration, written only by operator edits. The live numbers are a
separate row, `WorkingRisk` (24 bytes: `Header` with `OrderType.WorkingRisk = 17` @0, `Position` @4,
`WorstWorkingLongQuantity` @8, >= 0, `WorstWorkingShortQuantity` @12, <= 0, `FillsThisSession` @16, >= 0, `WorstWorkingFills` @20,
>= 0), one per instrument per
context, never persisted. The split is what lets the same `RiskLayer` run on both sides (each side
has its own `WorkingRisk` and reads its own `RiskLimit`), and it means an edit cannot rewind
a reservation.

`WorkingRisk` carries its own `Position` rather than reading the position row, because position and
reservations have to change together, in the same message that caused them. A fill moves `Position`
by the fill and releases the same quantity from the reservation, so it is room-neutral. If the
position came from the server-written row instead, the client would see the position move as soon
as the server wrote it, while its own reservation is released only when it reads the `Fill`: for
that interval the fill is counted twice. With the reverse ordering it is counted zero times, which
is looser than the server. One row moved by one `RiskLayer.OnFill` cannot be out of step with
itself.

Seeding: the server writes `WorkingRisk { Position = server-wide position }` when it first
allocates an instrument; a client writes `WorkingRisk { Position = its own strategy's position row }`
on every process start (see "Client restart"). The server also writes that same strategy row when it allocates the
instrument to the strategy (`AllocateInstrument(clientId, instrumentId)`, 2026-10-16), so the row is readable before the
strategy first runs, for example after a server restart replays the allocations (a Risk Limits grid, on the box or a
remote desk, would otherwise have nothing to read); the two writes never overlap, since the server seeds before it
replies to the allocation and the strategy reseeds after. On a reconnect the allocations survive (the client is only
Detached), so only the instruments the new process asks for are reseeded; one it does not ask for keeps the dead
process's last row, worst-working quantities included, though the server has since closed those orders (accepted
2026-10-16; the declined fix: the server reseeds every allocated row of the strategy when `IsClientReady` admits it).
After that `Position` moves only in
`RiskLayer.OnFill`, for every fill, a legged instrument's own fill included (accounting only); only
an outright fill of an order this `RiskLayer` reserved releases a reservation. On a client that is
`isReserved = (fill's ClientId == own ClientId)`: a fill of a manual order booked to the strategy
moves the position but releases nothing, because this client never reserved it.

Every `WorkingRisk` write is seq-bumped (`AcquireLock`/`ReleaseLock`; single writer, so two
volatile stores), so `Read()` and the TCP mirror see untorn rows. `OrderRisk` rows are private to their `RiskLayer`
(see above), written by plain ref. `ServerWorkingRisks` was `WorkingRisks` until 2026-10-16.

The shared arrays are created grouped by concept (positions, limits and exposure in server/local pairs, then orders,
then the rest). Creation order is each array's id between the two C# Mirror ends and nothing else, so it may change
freely: the layout hash refuses two different builds, and both ends ship together. (Until 2026-10-16 every new array
was appended last to keep the others' ids, which bought nothing and scattered the pairs.)
While a Local Mirror runs, its pass reading a strategy's row costs that strategy's next write to it one cache-line
transfer (about +330 ns measured on the box), at most once per pass, as the server's own row already pays.

Reservations are per leg (an outright is its own single leg, weight +1).
`ApplyWorstQuantityDelta` applies an order-unit delta to each leg as `legSide = orderSide ×
sign(weight)` and `legDelta = delta × |weight|`. The sign is applied exactly once, there: a buy
calendar reserves its back leg short. Signing anywhere else as well squares it away and drives the
short aggregate positive.

### The client copy is never looser than the server — and where it can be

By timing, the client is always at least as tight as the server:

- it counts its own sends in `ValidateOrder` before the server has read them;
- it learns of every reduction (an `Acked` that shrinks the worst case, a `Done`, a server or
  exchange reject) after the server has applied it, because it reads the server's forwarded message;
- a fill is room-neutral on both sides (see above).

What the client cannot see is everyone else on the instrument. The server's room is
`MaxPositionQuantity` minus the server-wide position and the reservations of every order on the
instrument; the client's is the same limit minus its own strategy's position and its own
reservations. With other strategies, manual orders booked to this strategy, or the house book on
the same instrument, the server has less room than the client sees, and the server refuses (and
pauses the algo) an order the client thought fit. That is accepted (see "Manual orders" and
Accepted residuals). So "never looser" holds exactly when this strategy's own orders are all that is
on the instrument, and otherwise only up to what the others use.

### The echo gate: an OrderState applies risk once

The client feeds `RiskLayer.OnOrderState` only on a real risk event for one of its own active algo
orders: the state's full `ClientOrderId` (generation included) matches the slot's target row, the
slot is still active, and either the state is `Done` or it is `Acked` with a `Seq` newer than the
last ack applied for that slot (`_ackedSeqs[slot]`, reset to 0 on every Create). Anything else the
execution channel delivers for the order — a repeated reason, a state for the slot's previous
order, a state after the slot's `Done` — would apply a reduction twice and make the copy looser than
the server. The risk call runs before the client's own `Done` handling, so the slot is still active
for the `Done` itself and freed straight after; a later echo of it fails the active test.

Rejects are fed the same way: a server or exchange reject of the slot's current algo order is passed
to `RiskLayer.OnOrderRejected` before the discard check, so a discarded reject (`TooManyActiveTargets`,
`TooManyOrdersPerSecond`, ...) still releases what the client reserved when it sent the target.
`OnOrderRejected` returns early for a reject from its own side (a client-side refusal happens before
or at `TryAdd` and reserved nothing) and for a rejected Cancel (a cancel never reserves, and its
profile — working + filled at the active's price, which is the in-flight Replace or Reduce's own
profile while that amend is unacked — could match an in-flight entry exactly and remove it
wrongly).

### The position check, and the within-limit clip

One function holds the room arithmetic for both the check and the clip:
`GetAbsAllowedOrderQuantity(target, isFillsRoom, isWithinLimit)` is the largest `|order quantity|` (filled
included) the order may carry; with `isFillsRoom` it measures the fills room instead (see "Max fills per
session"). For each leg, with `room = MaxPositionQuantity - Position -
WorstWorkingLongQuantity` on the long side and `MaxPositionQuantity + Position + WorstWorkingShortQuantity` on the short side
(64-bit), it allows `worst + room / |weight|`, and the minimum over legs wins. `worst` is the
order's current `GetAbsWorstOrderQuantity()`, or 0 for a Create (the row still holds the slot's
previous order until `ValidateOrder` resets it). Since the room already counts this order's own
worst, `|q| <= worst + room / |w|` is the same as "what `TryAdd` would add fits the room".

The difference is what happens past the limit (`room < 0`):

- **`ValidateOrder` (both sides), `isWithinLimit = false`:** a negative room counts as 0, so an order
  may keep its worst and any cut passes. An order already over the limit (after a lowered limit, or
  room taken by others) must never be refused for shrinking, and strict `Target` must be able to
  keep what it has. The check is pure — per-leg `MaxOrderQuantity` on the working quantity, then the
  room check, then the `OrderRisk` reset on a Create, then `TryAdd`, then the delta applied — so a
  refusal needs no back-out. (Before 2026-10-04 it was `TryAdd`, a tentative per-leg check, and
  `Reject` to back out; an over-limit side then refused even a zero-delta cut.)
- **`TryClipToRiskLimit` (client, `TryTarget` only), `isWithinLimit = true`:** past the limit the
  order is cut by the overshoot, rounded up to whole orders per leg (`ceil(overshoot / |weight|)`).
  A size within the limit passes the client's own check, and the server accepts it whenever the
  client's room is no larger than the server's. By timing that always holds for this strategy's own
  orders; it does not hold when others use room on the instrument (see "The client copy is never
  looser than the server — and where it can be"), and the server rate limit and a limit lowered in
  flight are not seen either (see Accepted residuals). In those cases the server can still refuse
  the clipped order, and a non-discarded refusal pauses the algo. The clip also caps at 65535 and at `filled + MaxOrderQuantity /
  |weight|` per leg (the per-order check solved for `|q|`), and returns false — nothing workable
  fits — when the allowed size is at or below the filled quantity (an amend down to the filled
  quantity would be a cancel) or when the order already has 29 targets in flight. It only ever
  lowers the quantity and is idempotent, so the algo can clip in Phase 2 and again in `Send`.

Example: limit 10, position 0, one acked buy of 8 working (worst 8), and the operator lowers the
limit to 6. Room = 6 - 0 - 8 = -2. `ValidateOrder` allows `8 + 0 = 8`: keeping 8 or reducing to 7
both pass. `TryClipToRiskLimit` allows `8 - 2 = 6`: `TryTarget` repricing the order to 8 at a new
price sends 6 (and reports a miss). Asking for 8 at the same price sends nothing, because the zipper
keeps an active no larger than its target as is and never clips it; the clip applies only to an
order that is repriced or reduced, or to a Create. The keep-or-cut rule is what keeps strict `Target` from pausing on an edit; the clip is what
brings `TryTarget` back within the limit.

The cost of the clip, accepted: several orders cut in one call are each cut by the whole overshoot.
A cut does not lower the worst until its ack (`worst = max(acked, in flight)`), so the next order
still sees the same negative room. Two acked buys of 6 with the limit at 10 (room -2), both repriced
(or reduced) in one call, are both cut to 4: 8 working instead of 10.

### Reduce vs Replace

`OrderTargetAction` is `Create 0`, `Replace 1` (the old `Amend`, same wire value), `Cancel 2`,
`Reduce 3`. A Reduce is less quantity at the same price and side: it keeps queue priority and never
adds risk. `OrderProfile.IsReduceOf(other)` decides it — same ticks, same sign, strictly smaller
`|quantity|` — and the sender labels the amend with it (`Algo.NewAmend` against the active's
profile, `SendOrderWidget` against the order state's). Anything else, including an unchanged size
at the same price, is a Replace and loses priority.

Where the label matters:

- **Rate limit (server):** a Reduce that is `IsReduceOf` the server's current `OrderState` profile
  is counted but never refused, like a cancel; any other Reduce is throttled like a Replace (see
  "Order rate limit").
- **Queue position (client):** an in-flight amend keeps the order's confirmed queue position only
  when it is an explicit `Reduce` one seq ahead of the state; a Replace takes the book estimate of an
  unconfirmed order. An order the exchange has not confirmed (a pending Create, or an in-flight
  Replace) is assumed to join behind everything resting at its price: it reports the client book's
  quantity at that price as `QuantityAhead` and 0 behind, as the server's InFlight seed does (until
  2026-10-04 it reported `int.MaxValue`). The two can differ by market data in flight between the
  server's book and the client's. `QuantityAhead` and `QuantityBehind` are one queue observation:
  `Server.OnQuantityAhead` writes them with one 64-bit store and no lock, and
  `Position.ActiveTargetsEnumerator` reads them with one 64-bit load, so a reader never pairs a new
  ahead with an old behind. They must stay adjacent and 8-aligned at `OrderState` @56/@60.
- **Validation:** every check that applied to Amend applies to Replace and Reduce alike (header,
  `TargetIsActive`, `SideNotValid`).

The simulator, like CME, decides reduce versus reprice from the price and quantity change itself,
not from the action byte. An exchange adapter maps Reduce to the venue's quantity-down modify.

### Client restart: the server admits a client once its previous orders are Done; the client skips the backlog

- **The server decides** (`Server.IsClientReady`, asked by `ServerSocket` only for a name no live process holds). While any
  of the name's 64 orders is still working, the connect is refused `NotReady` and `TryCloseOutOrders` runs again: the cancel
  on close can fail (iLink down, a no-cancel phase). "Working" is the server's `OrderState` row being Active once that row is
  about the order; before the server has read the order, the client's `OrderTarget` row. The target row cannot decide once
  the state row is the order's: `TryCloseOutOrders` stamps it Active and nothing stamps it back, so a process that died with a
  working order would be refused forever after the exchange cancelled it.
- **`Server.TryCloseOutOrders` closes out a gone client's 64 slots in one walk** (on close, and as `IsClientReady` on every
  reconnect attempt): it cancels what is working, marks a Create the server never read Done, and repairs a target row the
  client left mid-write (odd seq: readers would skip it forever and the next process's seqlock would run inverted), marked
  Done with no cancel when the slot is not working (its torn id may name no instrument). True once nothing waits on the exchange. Its target stays Active (with
  `Cancel`) while the exchange may hold the order, which the vendor's replay-on-ack reads to cancel a still-InFlight order.
  It reads the state row with `TryRead` (waits out a write in progress: the CoreGroup thread is alive and may be writing
  that order's ack or fill; a torn read could skip a live order), and the gone client's target row plainly.
- **The cancel needs no wait.** Nothing left unread in a closed client's ring is ever processed (reads stop at Detached).
  A Create the CoreGroup thread was already handling at the close is finished within its pass, and the close's cancel,
  on that CoreGroup's injection queue, is drained at the start of the next pass, so it always follows the Create: it
  cancels the order, or is refused harmlessly if there was none. Draining only decides the bookkeeping below. Never wait
  for it on the listen thread: with the simulation clock paused the CoreGroup threads do not run, and every connect and
  dead-client check would stop.
- **A Create is "unread" only once the client is drained.** At close a CoreGroup thread may be part-way through a Create it
  read just before the client was marked Detached; until it has finished, that Create's row keeps Active + Cancel. The proof
  is a handshake, not a timing margin: `OnClientClosed` bumps a drain request with `Interlocked.Increment` (a full fence, so a
  thread that reads the new request also sees the client Detached) and records it for the client; each CoreGroup thread, at
  the end of a `ReadExecution` pass, reads the request (one cached read per pass) and acknowledges it, writing only when it
  moved. Once every CoreGroup thread has acknowledged a request at least as new as the client's, everything read from it has
  been handled, and later passes cannot read it. Cost: nothing extra per pass but that read; per close, one fence on the
  listen thread and one cache miss and write per CoreGroup thread. While the simulation clock is paused the threads do not
  run, so a name with an unread Create stays `NotReady` until they do.
- **A Create the server never read is closed out**: its target row Active while the state row is still the slot's
  previous order. With the client gone its ring is never read again, so it can never reach the exchange; the cancel sent for
  it is refused (the state row is another order's) and nothing marked it back, so the name stayed `NotReady` forever. Its
  target is stamped Done (which also hides it from the order walk) and does not count as working. The cancel is still sent:
  at close the CoreGroup thread may be mid-way through a Create it read just before, and then the cancel finds its order.
- **The server's cancels are audited on the client's channel.** The client never sent them, so no tap would record them and
  its audit file would show a Done/Canceled with no cancel before it. The CoreGroup thread writes each injected cancel to the
  client's server-to-client channel before sending it, so the Logging tap records request, then reject or Done, in order.
  A client ignores an `OrderTarget` there (only its previous process's cancels arrive that way, and `Recover` skips them).
- A name that is Open (connected and trading) is refused `AlreadyConnected`, its orders untouched; one still closing is
  refused `Closing`, and `Connect` asks again every 100 ms.
- **Then the client skips the backlog** (`ClientSocket.Recover()` after building its context): everything queued is about
  the previous process's finished orders, already in the position rows `WorkingRisk` is seeded from. No wait is needed: the
  server writes each order's row and its ring message microseconds apart on one thread, and `Recover` runs milliseconds after
  admission.
- **`OrderRisk` rows start empty with each process:** they are a plain array in its `RiskLayer`, allocated at startup;
  a validated Create resets its slot's row anyway, so nothing of a previous order is ever read.
- Instrument allocation then only rewrites `WorkingRisk { Position = own strategy position row, reservations 0 }`; with no
  Active orders zero reservations is exact. The region can outlive a process (the GUI maps it), so it is rewritten on every
  allocation rather than trusted.
- Accepted residual (unchanged): a manual order booked to this strategy carries another client's id; if it fills during
  the client's startup its fill can be counted twice (operating rule: no manual orders on an algo while it starts).

### A strategy ends when its server goes (2026-10-23)

- **Why:** a server that shuts down clears every client's instrument allocations (`ServerSocket.Dispose` → `DeallocateClient`),
  which is right, but nothing told the strategy. Its timers kept reading books no longer allocated: MicroOutright's
  once-a-second Profit series raised "instrumentId has not been allocated" every second until someone stopped it, and after
  a restart the strategy carried on with state from before it.
- **The rule:** `Client.IsServerAlive` is false once the server the client connected to has exited, or its header names
  another process (a restarted server); the same test `RemoteClient.IsSessionEnded` makes for a desk, inverted. In realtime a
  `Scenario` checks it once a second on the clock thread (a `TickTocker` made before the strategies', so it runs ahead of
  their timers); when it turns true, one alert ("server … has stopped or restarted; … ends") and a flag the strategy thread's
  loop ends on. The loop then disposes the `AlertManager` (sending that alert) and runs the exit actions. The strategy is
  started again by hand once the server is up, so every start is fresh; after a server restart its algos come back paused.
- **Why a flag, not an exit from the clock thread:** the exit actions include stopping the clock, which waits for the clock
  thread to end; only the strategy thread may run them.
- **Cost:** one `volatile bool` read per pass of the strategy loop (never written until the end); one process-alive system
  call a second on the clock thread; nothing allocated until the alert.
- **Not built:** reconnecting in place (every strategy would need a teardown of its timers and series, and its own state
  reconciled with the new server). An unattended restart, if wanted, is the same end plus an exit code and a restart loop
  around the process; MicroOutright would then take its environment and CoreGroup as arguments instead of console prompts.
- **Residual:** a strategy timer that fires in the same second, before the check, can still raise one last error.

## Algo: Target and TryTarget

### Strict Target fails loudly; TryTarget never sends a refusal it can see

`Algo` is a convenience class with two entry points over one implementation:

- **Both modes keep the session rule** in `Send`: nothing the exchange's session would refuse is sent
  (`RiskLayer.MaySend`, see "Session state is the exchange's TradingStatus"), and the miss is noted.
  Best effort is about the risk limits, not the exchange's rules.
- **`Target(ref targets)`** — strict. It sends what was asked (after the zipper) and skips the slot
  and risk checks in `Send`. Anything the client's own `ValidateOrder`, the server or the exchange
  refuses comes back as a reject, and a reject that is not discarded pauses the algo, so a strategy
  bug fails loudly. Existing strategies call this.
- **`TryTarget(ref targets) -> bool`** — best effort on the risk limits. It never sends anything it
  can see the limits would refuse: a Create needs a free client order slot
  (`Client.HasFreeOrderSlot`); every non-cancel is clipped by `TryClipToRiskLimit` to the biggest
  size within the limits, or dropped when nothing workable fits.

`TryTarget` returns false when any target was not sent exactly as asked: an order dropped or cut in
`Send`, a Phase 1 reduce cut further or turned into a cancel, a Phase 2 target retired with a
remainder, or the algo paused. Under `IsOneOrderPerPrice` the remainder of a partially matched
same-price target is dropped by policy and is not a miss. `true` says nothing about what the server
or exchange will do with what was sent: the server rate limit, the session-close race, room used by
others and a limit lowered in flight are all invisible to it (see Accepted residuals).

Both modes keep the guards that make a target list meaningful: more than 64 targets throws,
self-crossing targets throw (IOC targets included in the bounds), a `TimeInForce` other than `Day`
or `ImmediateOrCancel` throws, and a zero-quantity target is dropped (it has no side). While paused
both send the Phase 5 cancel for every active, through `Send`, in the mode of the call; nothing is
validated while paused.

The mode is per call: `_isBestEffort` and `_isTargetAchieved` are set at entry, and `Send` and
`CancelAllOrders` (both private now) follow the call that runs them.

### Phase order

One call sends in this order:

1. **Phase 1** (zipper, inline): same-price matches. An active no larger than its target is kept as
   is (never clipped); a larger one is reduced to the target.
2. **Phase 2** (reprice): each unmatched target takes unmatched same-side actives in price order,
   largest first at one price. A passive reprice is sent inline; a reprice to a more aggressive
   price is delayed.
3. **Phase 5**: cancel every active left unmatched.
4. **Phase 7**: create the remaining targets; one that would cross our own resting opposite side is
   delayed.
5. **Phase 6**: send the delayed orders (aggressive reprices, crossing creates).
6. **Phase 8**: send the IOCs.

Reductions (Phase 1) and passive reprices (Phase 2, which may grow) are sent inline, before the
Phase 5 cancels; the cancels go before the Phase 7 creates; only what moves more aggressive or
crosses is held back to Phase 6, and the IOCs go last. In `TryTarget` each
send is clipped against the client copy as it stands at that moment, which already counts every
earlier send of the same call; a cancel sent in the same call frees nothing (its `Done` is still to
come), so its room is not available to a create in that call.

### Largest first when repricing

A reprice gives up the order's queue position whichever active is moved, so among our unmatched
actives at one price Phase 2 moves the largest first (`GetLargestActiveKeyIndexAtPrice`; ties go to
the one nearest the front). The largest active carries the most of the target within its own
reservation. Across prices the price order is unchanged.

Example (harness F9/F10): limit 12, position 0, two acked buys at P: a 1-lot at the front and a
10-lot behind it (worst long 11, room 1). The target is 11 at a new price Q. Front-first, the 1-lot
is repriced: it may carry `1 + 1 = 2`, so `TryTarget` sends 2 at Q and cancels the 10 — 2 lots
working where 11 were asked — and strict `Target` sends 11, which the client refuses
(`PositionExceedsRiskLimit`) and the algo pauses. Largest-first, the 10-lot is repriced: it may carry
`10 + 1 = 11`, the Replace fits in both modes, and the 1-lot is cancelled.

### When nothing fits: cancel

In best effort a level never stays above its target:

- **Phase 1 reduce:** the reduce is clipped; if nothing workable fits it is replaced by the Phase 5
  cancel shape (working + filled at the active's price). The target is consumed either way, so the
  level is not re-created as a new order in that call.
- **Phase 2 reprice:** if the clip fails the active is left for Phase 5 to cancel, and the same
  target tries the next unmatched same-side active (the largest at the same price, then the next
  price). With none left it becomes a Phase 7 Create, which `Send` clips or drops.

The usual reasons nothing fits are a position already past a lowered limit by more than the cut,
and the 29-amend cap.

### The 29-amend cap

An order can have 29 targets in flight (`OrderRisk.IsFull`); the 30th `TryAdd` is refused with
`TooManyActiveTargets`, which is in `OrderDiscarded`.

- **`TryTarget`** sees `IsFull` in `TryClipToRiskLimit` (false for any non-Create), so the order is
  cancelled. Phase 1: the reduce becomes a cancel and the target is consumed, not placed in that
  call. Phase 2: the active is left for Phase 5, and the target is placed on another unmatched
  same-side active or, with none left, as a Phase 7 Create.
- **Strict `Target`** sends the amend; the client's own `TryAdd` refuses it and, being discarded,
  it is dropped silently — no reject written, no pause — and the next tick retries once acks have
  freed a slot. Never pausing on this is deliberate: 29 amends in flight on one order is a burst, not
  a bug, and pausing a healthy algo in the middle of one is the larger risk. The position check runs
  before `TryAdd`, so a 30th amend that also breaches the limit is reported as
  `PositionExceedsRiskLimit`, which pauses.

### TimeInForce: Day and IOC only

`Target` carries a `TimeInForce` (default `Day`), copied onto every Create and amend. The algo
models resting `Day` orders and `ImmediateOrCancel` orders only; any other value (`GoodTillCancel`,
`FillOrKill`, `OpeningAuction`, `ClosingAuction`) throws `NotImplementedException` in
`Target`/`TryTarget` before anything is sent. The check is in the algo, not on the wire.

### IOCs: never active, sent last, free for all

An IOC never rests, so it never takes part in the zipper. Targets with `ImmediateOrCancel` are taken
out in the validation pass (they still count for the self-cross guard), are not aggregated (two IOC
targets at one price send two orders), are not matched against anything, and are sent as Creates in
Phase 8, after everything else in the call, so this tick's cancels and amends reach the exchange
first. `ActiveTargets` never yields an IOC, so there is no match-off against IOCs already in flight:
each call's IOCs are free for all. An IOC's reservation stays in the client's `WorkingRisk` until its
`Done`, like any order. In best effort an IOC goes through the same session, slot and clip checks.
The simulator acks an IOC, takes what it can if it is marketable, and eliminates the remainder at
once; an IOC and any marketable order publish queue position 0/0.

## Manual orders

A manual order is one whose `ClientId != StrategyId` (`IsAlgoOrder()` false): a GUI's order booked
to a strategy, or to the house book.

- **An algo's orders are cancel-only from a GUI.** `ManualClient.Amend` throws
  `InvalidOperationException` for a Reduce or Replace of an algo order, and the ladder, orders grid
  and send-order widget offer Amend only for manual orders (the throw is a backstop). A GUI amend of
  an algo order would be undone on the algo's next tick, and the algo's `RiskLayer` copy cannot see
  it (the GUI never writes the algo's target row). A cancel may always intervene.
- **`ManualClient` owns seq numbering.** A cancel of an algo order is numbered existing target seq +
  1,000,000, as `Server.TryCloseOutOrders` does, so it never collides with the algo's own next seq; any
  action on a manual order (amend or cancel) is existing seq + 1; the caller's seq wins only if
  larger. The widgets used four ad-hoc offsets (+1,000,000 on the ladder's cancel, +10,000 + age in
  seconds on the orders grid's cancel, +1000 on the send-order cancel, +1 on the send-order amend)
  and no longer add any.

  What makes the jump legal, and what follows it. The server's seq check compares a target only with
  the owner's target row (the shared `OrderTargets` row the owning client writes before it sends) and
  refuses only a lower seq (`TargetIsStale`); there is no `seq == previous + 1` rule, so gaps are
  legal (`Server.TryCloseOutOrders` relies on this too). The GUI never writes an algo's target row, so
  the server never sees the GUI cancel's seq: the algo's own next amend of that order passes the seq
  check against its own row and is refused only once the order is `Done` (`StateIsDone`, discarded,
  no pause), at the server or, if it gets to the exchange first, as `OrderNotFound` alone, which
  `Server.OnOrderRejected` turns into `StateIsDone` when the state is `Done`. The seq that matters is
  the exchange's: the simulator refuses a target whose seq is not above the order's stored seq
  (`SeqOutOfOrder`, not discarded), which is what a GUI cancel at `+1` could have collided with. The
  stored seq advances for every target that passes the `SeqOutOfOrder`, `NotInSession`,
  `StrategyIdNotValid` and `InstrumentIdNotValid` checks, including an amend then refused
  `TargetIsActive` or `SideNotValid`.
  The forwarded reject reaches the algo client's `OnOrderRejected` like any server reject, which
  releases what that client reserved for the amend. A C++ manual client numbers the same way and
  refuses non-cancel targets on algo orders; a C++ server keeps the "lower is stale, gaps are legal"
  rule.
- **A manual order's refusal never pauses the algo.** Both server pause sites pause only when the
  order is an algo order. `Server.Reject` (a server or exchange refusal) still forwards the reject
  and, unless it is discarded, raises it for alerting. A reject a client writes to the server (a
  client-side refusal) is raised by the client itself in `Client.Reject`, which sends it; the
  server's handler for it only applies the pause gate, and forwards and raises nothing.
- **Manual orders may use room the algo cannot see.** A manual order is risk-checked only on the
  server, against the server-wide room. Its reservation is not in the algo client's copy, and its
  fills move the algo client's `Position` without a release. So a manual order booked to a strategy
  can leave the algo believing it has room the server no longer has, and the algo's next order is
  refused and pauses it, in either mode. Accepted: trading manually on an algo's book is the user's
  responsibility.

## Spreads must be two-leg +1/-1 calendars

`Spread` builds its risk legs as +1/-1, so any other shape (a butterfly, a ratio spread) would be
mis-risked. `Client.GetInstrument` throws `NotImplementedException` for a spread that is not
`LegCount == 2 && |weight0| == 1 && weight0 == -weight1`, before onboarding its legs and before
sending the allocate request. The check was first put in `Context.CreateInstrument`, but that runs
on the server's admin thread, and an exception there killed the whole server process. The server
does not check, so every client — a C++ one included — must refuse the shape itself.

## Duplicate fills are dropped by cumulative quantity

iLink may resend an execution report (`PossRetransFlag`). A resent fill must not move position or
release risk twice. `Server.OnFill` drops the whole fill event — no stamping, locks, state write,
position, risk, forwarding or audit — when the event's cumulative `|QuantityFilled|` does not exceed
the order row's. That needs no extra state, and it rests on two assumptions, both to be confirmed
with CME:

1. the session delivers an order's fills in order;
2. the adapter always sets the event's `OrderState.QuantityFilled` to the cumulative filled quantity
   (`CumQty`, which In-Flight Mitigation keeps cumulative across replaces).

A corollary of the first: `WriteOrderState` keeps the larger filled quantity from any state, so a
non-fill state (a terminal cancel, say) whose `CumQty` already includes a fill not yet delivered
would make that fill look like a resend. In-order delivery rules that out. If CME says either
assumption does not hold, the fallback is a recent-set keyed by `FillId` (ExecID), on both the C#
and the C++ server. Drops are not counted or logged.

## A refused Create: Done first, then the reject

CME answers a refused new order with one report; the C++ InstrumentRouter turned it into two
messages, reject then Done/Rejected state, and the C# simulator synthesised the same pair the other
way round. The order matters to the algo. With the reject first, the reject releases the in-flight
reservation while the state row still says InFlight, so for a tick `ActiveTargets` still shows an
order that is dead and the algo sees room it cannot account for. With the Done first, one message
releases the reservation and hides the order together, and the reject after it only explains why:
the same shape as a fill, where the state carries the risk change and the event explains it.

So the server owns it. `Server.OnOrderRejected` is the entry point for every refused target: the
exchange's (the adapter calls it) and the server's own (`OnOrderTarget` calls it when `ValidateOrder`
refuses, after writing and publishing the Create's InFlight row, so a server- and an
exchange-refused Create send the client the same sequence: InFlight, Done/Rejected, reject).
`OnOrderTarget` does not check the slot is free: a Create for a slot whose order is still live
(`OrderIndexIsBusy`) is refused by the client's own `ValidateCreate` against the same row, and the
platform relies on clients behaving. If one ever reached the server it would overwrite the live
order's row and release its reservation. For a refused Create it
publishes a `Done`/`Rejected` state through
`OnOrderState` before the reject: `WriteOrderState` merges it into the InFlight row (keeping
`TimeInForce` and the queue fields), `RiskLayer.OnOrderState` releases everything the order holds,
and the reject that follows finds nothing left to remove. Adapters send only the reject; the
simulator's `OnExchangeOrderRejected` now does exactly that. An adapter that still sends its own
Done afterwards is harmless (`WriteOrderState` ignores a second Done, so nothing is released twice)
but the duplicate is forwarded to the client, so the C++ router should stop sending it. The
synthesis sits after the `OrderNotFound` → `StateIsDone` mapping and inside the same-order check,
so the mapping sees the row's real status and a reject for a slot reused by another order writes
nothing. A refused Replace or Cancel gets no state at all: the order is still working, unchanged.

**How a client ends an order.** A `Client` frees an order's slot (target Done, the position stops counting it, the slot
id is free) only on the order's `Done` state (`OnOrderState`):

- **Refused by the server or the exchange** (source `Server` or `Exchange`): the reject alone never deallocates. The
  server saw the order, so it owns the order's end: the slot is freed by the `Done` state, which for a refused Create the
  server publishes just before the reject (above). The reject only marks the target answered.
- **Refused by the client's own checks** (`Validate` in `Send`, or no free slot): refused synchronously inside `Create`,
  before the target row is written or the position counts it, so `Create` just frees the slot id it took.

Nothing refuses on a client's behalf after it sent an order: a remote desk's orders are sent by the ManualClient beside
the server, and a call clicked on a stale screen is dropped by the Local Mirror before it reaches one (see "Remote
workspaces"). A refusal of an amend or a cancel never deallocates: its order may be working at the server, whose state
rows still decide its end.

## NIC timestamps (2026-10-24)

`OrderHeader.NicTimestamp` is when a message reached this machine, or, for a message the server or a client makes, when
it was made. Each stamp has one owner: whoever receives or creates the message.

- **From the exchange** (order states, fills, rejects): stamped where it is received. Live (C++): the router's NIC
  hardware stamp when it has one, else the kernel-socket read stamps `Clock::UtcNow()` as it reads the message; zero never
  reaches the server. Simulation: `ServerSimulator` stamps each at its arrival time (exchange time + latency). The server
  keeps the stamp: `OnOrderState`, `OnOrderRejected` and `OnFill` write none. A fill's state and its fills share the one
  stamp of their execution report, which keeps ring order (state, fill, position) in the audit.
- **Made by the server**: stamped `Clock.Now` where it is made: its own refusal (`OnOrderTarget`, after
  `NewOrderRejected`, whose header is the target's and so carries the client's send time), the InFlight state, the cancels
  it sends, position rows. A refused Create's Done takes its reject's stamp (`NewRejected`), so the two match whichever
  side refused.
- **Made by a client**: its targets, stamped `Clock.Now` as sent (`Client.Send`).
- **Why not "stamp when zero":** the server's own refusal arrives at `OnOrderRejected` already stamped (the client's send
  time), so a zero test would keep it, and the reject would sort before the InFlight state it answers. Until 2026-10-24
  the server overwrote every exchange message's stamp with its own clock, so a hardware stamp never reached the row.
- **Clock:** a hardware stamp must be on the same clock as `Clock::UtcNow()` (the NIC's clock disciplined to the system
  clock); Logging sorts each audit by `NicTimestamp`, and strategy latency compares `TriggerTimestamp` with send stamps.
- Checked (scratch MicroOutright backtest, 2026-01-01 to 01-20, before and after): every audit record's stamps identical;
  the server's audit identical record for record. The strategy audit's record order differed, as it does between any two
  runs: Logging writes a few dozen records per run out of stamp order (22 to 39 in three runs), so sort by
  `NicTimestamp` before reading cause and effect.

## Process guards aligned with C++ (2026-10-05)

Agreed item by item with the C++ side (C++ `csharp_alignment_report_2026-10-05.md`); C++ was the
reference for each:

- **`mlockall` failure stops the process.** `Memory.EnsureMLocked` (first shared or anonymous
  region) throws when `mlockall(MCL_CURRENT | MCL_FUTURE)` fails, typically a memlock limit
  (`ulimit -l`, `LimitMEMLOCK`) set too low. An unpinned process can take page faults of several ms
  on its hot path, and a misconfigured live box would otherwise trade with one stdout line as its
  only warning. It is a locked run-once method, not a static initialiser: a throwing type
  initialiser would cache its exception for the life of the process, whereas C++ `call_once`
  retries after a throw.
- **SIGHUP shuts down cleanly unless the process was started under `nohup`.** `Application` reads
  the inherited SIGHUP disposition (`sigaction`) before registering its handler; if it is `SIG_IGN`
  (what `nohup` does) no handler is installed and the process keeps running when the session drops.
  Otherwise a closed terminal runs the exit actions (a server cancels its orders) and exits.
- **The server name is validated before `InitDirectories` deletes anything.** In simulation
  `InitDirectories` wipes the server's sub-directories; it now calls
  `ServerContext.ThrowIfInvalidServerName` first, so a simulation run pointed at a live server's
  name throws without touching a file (before, the name was checked only in the `ServerContext`
  constructor, after the delete).
- **A `SharedArray` larger than `int.MaxValue` bytes is refused.** C++ memory takes `int32` lengths;
  both sides throw rather than map or truncate a larger region.
- **`Clock.Start` clears `IsRunning` however it exits** (in its `finally`, before `Stopped`): a
  `Started` or `Exception` handler that throws skipped the `Run*` epilogue, and the "Stop Clock"
  exit action then waited forever. **`ConsumeReminders` pops only a due reminder**:
  `LockedPriorityQueue.TryDequeueIfAtMost` checks and pops under one write lock, so a
  `TryRemoveReminder` between the peek and the pop cannot make the next, not-yet-due reminder run
  early.
- **Server restart replays allocations through the client path.** `LoadInstruments` calls
  `OnAllocateInstrument(clientId, ref …)` for every recorded line, restoring the client's and the
  house book's allocation bits, the CoreGroup poll bit, the admin reply and the `AllocateInstrument`
  callback an exchange adapter needs to rebuild routers for orders still working after a restart.
  A host must call `LoadClients` before `LoadInstruments`.

## Server read loops throw; the caller reports

`Server.ReadAdmin` and `Server.ReadExecution` may throw (for example an allocation request whose
client or instrument header id is out of range, or a spread whose legs are missing). They do not
catch, because the right reaction is the caller's, and an exception that escapes a thread kills the
process — which is what the butterfly check did when it lived on the admin thread.

- **Realtime:** the caller wraps each call in try/catch and reports to `AlertManager.OnException`,
  then keeps polling, as `Scenario`'s `ReadSocket` loop does.
- **Simulation:** the calls run inside the Clock's `Interject` callback; the Clock catches each step
  and raises `Clock.Exception`, which `Scenario` and the workspace wire to the `AlertManager`. The
  simulator's pre-clock admin drain on its Init thread runs outside any Clock callback, so it wraps
  `ReadAdmin` itself and reports through `Clock.OnException`, which raises the same event.

One call is not covered: after the drain, the same Init thread calls `OnInterject(Timestamp.MinValue)`
directly, outside any Clock callback and outside any try/catch, and that runs
`_server.ReadExecution(ServerCoreGroupId)` once. An exception there escapes
the Init thread and kills the process. Known; listed under Open items.

## Instrument header immutability

Instrument headers are append-only: a header's identity (exchange, root, type, maturities) never
changes after it is written, and header slots are never reused within a server's run. The symbol strings a
GUI shows (`ServerContext.GetSymbolStrings`, computed once per headerId and cached on the server context) depend
on this; a restarted server is a new run, so a remote desk rebuilt on it renews its contexts and so starts them over (`ContextManager.Renew`, see
"Restarts" under Remote workspaces). If slot reuse or identity rewrites within a run are ever introduced, the
cache needs a generation check. Mutable header fields
(`TradingStatus`, `TickSize`, `InstrumentId`) are outside the cache and always read live.

## The 64-level book: deleting a price better than the best changes nothing

`SideByPrice64` is a 64-slot ring indexed from the best, so a price 1 to 63 ticks better than the
best shares its slot with the price 64 ticks further back (1 to 63 ticks worse). Nothing can rest
better than the best, but feeds do send deletes for such prices, and the delete path used to run
on the shared slot: it cleared the worse, real level there and reported delta 0. A delta of 0 is
not broadcast (`TrySetAsDeltas` returns false, `Server` skips the write), so the server's book lost
the level while every client kept it, and the two books stayed different until that price was set
again; a stale level can later cross the book. Example: best bid 13230 with 13190 resting
(40 ticks back); a delete for 13254 (24 better) lands on 13190's slot and wiped it. Now
`TrySetQuantity` returns false with delta 0 for any delete of a better price, which also covers
the far-better (64+) delete the code already ignored. Found by the self-crossing investigation of
2026-10-13: a server-to-client replica fuzz diverged in 616 of 3,000 runs before, 0 after.
Cost, measured (BenchmarkDotNet, JIT, dev PC, 4,096 mixed updates): about +1 ns per update
(19.9 → 21.4 ns with identical work, 18.6 → 19.5 ns with 2% better-price deletes), within about one
standard deviation per run, no allocation; kept, because correctness comes first. Not measured
under NativeAOT. The C++ server is a port of this type and needs the same change
(cpp_alignment_report_2026-10-12.md P23).

## Two kinds of book: the server's is shared, a strategy's is its Instrument's (2026-10-16)

- **The server's book** is the shared region `<server>/MarketsByPrice`, one row per instrument. The server writes it (under
  the row's sequence bump); every other process reads it through its own `Context` (`GetMarketByPrice64`,
  `TryReadMarketByPrice64` for a whole copy): the GUI's ladders and Send Order, each strategy's starting snapshot, and the
  Mirror, which copies it to a remote desk. A GUI's (and the server's) positions mark P&L to it through
  `Context.TryGetQuote(instrumentId)`, which reads the best bid and ask in place between two reads of the row's sequence,
  copying nothing, and returns false rather than spin when the row is mid-write (the caller asks again next refresh).
- **A strategy's own book** is a plain `MarketByPrice64` field of each `Instrument` (`Instrument.MarketByPrice`). Its Client
  fills it on the feed thread: the server's book as the snapshot when the instrument's data socket opens, then every
  delta, update and snapshot from the feed. Only that thread reads it (the algo's quotes, `Position.TryGetQuote`, the
  queue estimate, and the strategy's own P&L), so it takes no sequence bumps. In a process with no feed (the server, a
  GUI) the field stays empty and nothing reads it.
- **Which book a quote comes from** is the context's to decide (`Context.TryGetQuote`, which `Position.Profit` marks to):
  a context open for writing, which only a strategy's own process has, gives its instrument's own book; any other (a GUI,
  the server) the server's. So a strategy never reads the server's book for P&L, and a GUI's P&L, on the box or a remote
  desk, is the server's.
- **Open (2026-10-16): a strategy's timers race its trading thread in realtime.** In simulation one thread reads the
  socket and fires every Clock reminder; in realtime `Scenario.Start` reads the socket on the main thread and runs the
  Clock (reminders, `TickTocker`s, `TickTock`) on a background thread. A reminder that reads strategy state, such as a
  P&L series calling `Position.Profit` (the instrument's own book and cached quote), can see a torn value. The cached
  quote was already read this way before; the own book now is too. Proposed fix: in realtime the trading thread fires
  the due reminders on its idle passes, as simulation does, and a strategy process runs no Clock thread.
- `Instrument.TryGetQuote(out quote)` is the own book's quote: the one the feed last set, else read from the book;
  `Instrument.TryGetQuote(in book, out quote)` is the one quote rule (open, both sides present) for any book.
- Until 2026-10-16 each strategy's book was a shared region under its own directory, written under sequence bumps
  because a strategy workspace's P&L read it from the GUI process (through a retry loop that would have spun forever on
  a row left mid-write). That P&L now marks to the server's book, which also gives a remote strategy workspace its P&L
  (the strategy's region was never copied to a desk).

## Removing our own orders from the book: only states the book already shows (2026-10-21)

`Position.TryGetQuote` is the strategy's quote with its own resting orders taken out of the instrument's own book. The
book (market data) and each order's state (execution reports) arrive on separate channels, so at any moment one can be
ahead of the other: the book can already show an amend while the state still holds the old price (the simulator did this
from fdae2ab on, see `Simulator/spec.txt`), or the ack can arrive before the book shows it (usual at CME: iLink beats
MDP). Subtracting a state the book does not show leaves our own order in the quote, the strategy re-prices off it, and
the re-price opens the same gap again (EMD_TESTING, 2026-03-19: 604,330 re-prices in 16 hours instead of about 23,000).

- **The rule.** CME stamps both streams with the matching engine's time for the same event (iLink and MDP TransactTime),
  so the book shows an order's state exactly when the state's `OrderHeader.ExchangeTimestamp` is at or before the book's
  `MarketByPrice64.ExchangeTimestamp`. A state is used only when, in addition, the exchange has answered the order's latest
  target (`OrderTarget.IsConfirmedBy`, the decision `ActiveTargets` also uses); while a create, amend or cancel is in
  flight the book may or may not show it.
- **The hold.** When any active order fails the rule, `TryGetQuote` returns the last quote it computed from a book that
  passed, and still returns true: false would hand the strategy no quote, and a strategy that then sends an empty target
  list cancels every order. The hold ends at the first book update at or after the ack: microseconds live, and in
  simulation the republish that carries the ack's book change. The held quote is dropped at every close (not Open).
- **IOCs** never rest, so they are never subtracted (they used to be, taking other participants' size away).
- **No copy.** The book is read in place; `SideByPrice64.TryGetBestLevelExcluding` walks each side from the best and
  stops at the first level with size left once ours is taken out. Measured (BenchmarkDotNet, JIT, dev PC, 20-level book,
  our first order covering the whole best level): 0 orders 11.1 → 4.6 ns, 2 per side 24.8 → 13.9 ns, 4 per side
  38.6 → 19.6 ns; no allocation either way. Not measured: the extra read of each order's target row (one cache line).
- **Accepted residual: an order that has just left.** A Done (cancel or final fill) clears the order from
  `_isOrderActive` at once, so until the next book update the book still shows it and nothing subtracts it. Live this is
  the time iLink leads MDP; in simulation the Done and its republish arrive together. The user will close this separately.
- **Live dependency:** the C++ server must set `OrderState.OrderHeader.ExchangeTimestamp` from the iLink TransactTime and
  the book's `ExchangeTimestamp` from the MDP TransactTime (cpp_alignment_report_2026-10-12.md C23).

## Session state is the exchange's TradingStatus

Whether an instrument is trading is what the exchange says, not what a timetable predicts. The
instrument header's `TradingStatus` byte is the only session state. What may be sent in it is one
rule, `RiskLayer.MaySend(tradingStatus, orderTargetAction)`, used by `Algo.Send` (both modes),
`RiskLayer.ValidateInstrument` (a create, client and server) and the simulated exchange (every
target), so the three cannot disagree (2026-10-21):

| Status | Create | Amend | Cancel |
|---|---|---|---|
| Open, Auction | yes | yes | yes |
| Halted | no | no | yes |
| Closed, Unknown | no | no | no |

An instrument whose first status has not arrived yet (Unknown) cannot trade; a live server must
publish status from the snapshot at startup. A refusal is `NotInSession` (or `NotInSessionInFlight`,
below). The quotes are stricter: `Instrument.TryGetQuote` and the position's quote return nothing
unless the status is `Open`, so a quoting algo sends no creates in an auction, and an empty target
list there cancels its orders, which CME accepts in a pre-open or velocity-logic pause (2026-03-23,
EMD: before, every cancel in the 10 s auction was refused, the algo paused 1.156 s in, and each
later cancel of the paused algo was refused and alerted again). The risk layer checks only creates;
amends and cancels reach the exchange's rule. Residual: the simulator fills a marketable order sent
in an auction at once, where CME would hold it for the reopening.

### An order in flight at a status change (2026-10-21)

An algo sends only while it sees `Open`, but it learns of a close through the feed, late, so an order sent
microseconds before a close meets the close at the client's risk check, the server's, or the exchange. It
used to come back `NotInSession` and pause the algo. Now:

- **The time of the change is in the header.** `InstrumentHeader.TradingStatusTimestamp` (offset 64) is the
  exchange time of the update that last changed `TradingStatus`. Only `Server.OnTradingStatusUpdate` writes
  it, on a change only (a repeated status keeps the time of the change), before the status byte, which it
  stores with `Volatile.Write`, so a reader that sees the new status sees its time.
- **The refusal says whether it was in flight.** `RiskLayer.GetNotInSessionReason` returns
  `NotInSessionInFlight` (47) while `Clock.Now` is within `RiskLayer.NotInSessionInFlightWindow` (1 s) of
  that time, else `NotInSession`. `ValidateInstrument` sets it for our own checks, and
  `Server.OnOrderRejected` rewrites an exchange refusal's `NotInSession` bit the same way, and a done
  order's `OrderNotFound` bit to `StateIsDone`, each on its own (`Bitset64.Replace`), so a refusal made
  only of races is discarded. `NotInSessionInFlight` is discarded: no pause, no alert, and the risk the
  client reserved is still released. Until 2026-10-21 both rewrites needed the refusal to be exactly that
  one reason, so a replace that reached the simulated exchange just after a close (which cancels every
  order) came back `OrderNotFound|NotInSession` and paused the algo (EMD, 2026-06-15 21:00:00.000150;
  reproduced in scratch on 2026-03-19, now `StateIsDone|NotInSessionInFlight`, discarded).
- **Outside the window** a session refusal still pauses: a strict `Target` while closed, or an exchange
  that refuses while our feed still says `Open` (the server then has no recent change) fail loudly.
- **Residuals.** The window runs from any change, so a strict order within 1 s of a close-to-pre-open
  change is discarded too. An exchange refusal that reaches the server before the status update does
  (live: within the few µs iLink leads MDP) still pauses.
- **Verified** (scratch EMD_TESTING, 2026-03-19, close at 21:00:00.000000): a create sent at
  20:59:59.999990 was refused by the simulated exchange at 21:00:00.000290 as `NotInSessionInFlight` and
  the algo stayed live (before: `NotInSession`, paused); a strict create at 21:00:05 was refused by the
  client as `NotInSession` and paused.
- **Layout.** `InstrumentHeader` grew from 64 to 128 bytes and the row from 128 to 256
  (`InstrumentHeader128` renamed `InstrumentHeader256`), so the field has room and `LeggedHeader` (now
  192 bytes) still fits. Its overlay members (`AsInstrumentHeader`, `AsFuture`, `AsForex`, `AsLegged`,
  `Symbology`) are `readonly`: rows are read through `ref readonly`, where a non-readonly call copies the
  row and the returned ref pointed into the dead copy (`Instrument.Header` read garbage once the copy's
  stack was reused; found by this change's test, latent before).

The status reaches every process the same way live and in simulation: `Server.OnTradingStatusUpdate`
writes the header byte and broadcasts a `TradingStatusUpdate` on the instrument ring, and the client
raises `Instrument.TradingStatusUpdateEvent` on a change. The server ends message efficiency's day on a `Closed`,
converted to local time with `Session.CME` (below).

**The server owns message efficiency (2026-10-13).** Each product group (CME's message-efficiency group, which MDP3
calls `SecurityGroup`) has one row in `<server>/MessageEfficiency` shared memory. Strategies only count into it; the
server does everything else:
- **Naming the group.** CME's security definition carries it (tag 1151), and it is not always the root: MNQ counts
  under NQ, M2K under RY, MBT under BF, MES under MS. It reaches `InstrumentDetails.ProductGroupRoot` (Databento's instrument
  definitions carry it), and whoever builds the instrument header copies it into `InstrumentHeader.ProductGroupRoot` (a
  `String4`): the C++ server live, `ServerSimulator` in simulation. A header without one cannot be allocated
  (`ServerContext.AllocateProductGroupId` throws), so details files written before the field existed must be regenerated.
  The group's file is named after the group, so a group whose code is not its old root (RY, NK, BF, EZ) starts a new file.
- **Assigning the row.** `ServerContext.AllocateInstrument` gives each instrument its group's row in
  `InstrumentHeader.ProductGroupId` (-1 until allocated; a byte, as there is at most one row per instrument id). The
  group's first instrument claims the next empty row and loads the last line of
  `<server>/MessageEfficiency/<group>.messageefficiency` into it. A client reads its instrument's row from the header, so
  only one thread ever claims rows (before, each client scanned and claimed, and two clients could claim the same row).
- **Ending the day.** On a `Closed` status, `Server.OnTradingStatusUpdate` resets the group's row
  (`MessageEfficiency.Reset`). Only when the reset changes the row (the group's first instrument to close) does the server
  post the day that ended on its audit channel, and Logging appends it to the group's file. The CoreGroup thread does no
  I/O. The row itself is the record: it starts with `Header<OrderType>` (`OrderType.MessageEfficiency = 18`).
- **Shutdown.** `Server.Dispose` posts every group's row on the audit link's admin channel just before closing that link
  (after the server socket, whose admin thread is that channel's other writer). Logging drains a link on close: the pass
  that reads the close message still writes what it read, and the reader's `Dispose` reads once more and flushes what the
  watermark held, so the rows reach the files.

`MessageEfficiency` has a parameterless constructor so its field initialisers, the header among them, run on every
construction, JSON loads included: a line written before the header existed still loads as type 18.

**Two CME limits on one row (2026-10-20).** The row judges a product group's day against both of CME's messaging
limits (CME's "Messaging Efficiency Program" document, sections 6 and 15):
- **MEP tiers, `Tier1`-`Tier3`** (per Globex firm): the tier the day's `RawMessages` falls in (>50k, >100k, >150k; at or
  below 50k none) holds `Efficiency` (`WeightedMessages` per lot: amend 1, cancel 3, ×0.1 outside 07:00-16:00 CT) below
  its benchmark. Values per group from CME's quarterly benchmark list.
- **EMT, `TierEMT`** (CME's iLink session Excessive Messaging Threshold): once `RawMessages` passes 1,000,000,
  `EMTEfficiency` (`EMTWeightedMessages` per lot: the same weights with no overnight discount) must stay at or below 500.
  It applies to every group, at all hours, with no waivers; a breach costs $10,000 and closes the iLink port, so every
  product on that CoreGroup stops. CME counts it per (iLink session, product group); on this platform a product group's
  orders always go through the same session, so one row per group is that count. Not checked at run time.
- A message is within when it is within the EMT and the MEP tier (`MessageEfficiency.CanSend`); a cancel always is. A tier
  of 0 is one the group does not have (`MessageEfficiencyTier.IsApplied`): a group outside CME's MEP list has `TierEMT`
  only. In realtime a group with no file loads `GetMinLimits`: the EMT and no MEP tiers (before, all zeros, which showed
  red). The widget colours `Efficiency` against the MEP tiers and `EMTEfficiency` against the EMT by a warning ratio
  (`MessageEfficiencyTier.GetWarningRatio`, 1 = breached; bands at 0.75, 0.9, 1): once a tier applies, the score's share of
  its benchmark; before that, if the score is already at or over the benchmark, the raw messages' share of the threshold,
  so a day heading into a tier it would breach turns yellow at 75% of the threshold and orange at 90%. The MEP cell takes
  the highest of Tier1-3 (`GetMEPWarningRatio`), which also warns ahead of the next, stricter tier. Display only.
- `S:\CME\MessageEfficiency` holds one file per group: the 75 groups of CME's Q3 2026 list with their tiers and every
  other group in the instrument details with `TierEMT` only (1,000,000 / 500). "J1-JT" in CME's list is written out as
  J1-J6, J9, JT.
- Not counted, accepted for now: eliminated FAKs (CME counts each as a raw message weighted 3), front/back-month and spread
  weights (CME weighs back months and spreads in regular hours ×0.1, and triples spread volume), and the firm-level EMT
  (10 million / 500; SOFR futures 5 million / 50). The counts are the clients' (see above), so a message the server refuses
  is counted and the server's own cancels are not.

Only Realtime writes the files. Clients never write them: before, every process that allocated the group (each
strategy, each Workspace, each Local Mirror stand-in) appended its view of the same shared row at every close and at
exit. Both of the server's copies are plain reads, not seqlocked (a strategy that died mid-write must not hang the close),
so a strategy writing at that instant can leave a torn line, which the next load takes as it is. Accepted: the counts are
off by at most the message being written.

The simulator has no exchange feed for status, so it keeps timetables at the exchange:
`ServerSimulator.SessionManagerByExchange` holds one `SessionManager` per exchange (XCME, XCBT, ...),
created from the first allocated instrument's `Sessions[0]`. Each allocated `InstrumentSimulator`
subscribes to its exchange's manager and, on every open or close, sets its own `TradingStatus` at
once (a close cancels every order and clears the queues first) and sends a `TradingStatusUpdate`
through the exchange-to-NIC latency queue, so the server hears it when a real one would arrive.

**A crossed book in session is an auction (2026-10-13).** The timetable alone said Open through
CME's pre-open, so on 2025-11-28 (the Globex outage) the replayed 6E/6A/6C books, crossed by up to
50 ticks from 13:02 to the 13:30 reopen, reached algos as a valid Open quote, and `Algo.Target`
threw "self-crossing targets" for every quote Make built from them (61% of 8,309 alerts in that
run). Live CME reports that window as PreOpen, mapped to `Auction`. Now `InstrumentSimulator` keeps the
timetable's verdict (`_sessionTradingStatus`, via `OnSessionChanged`) and publishes `Auction` while
that verdict is Open and its book is crossed (`UpdateTradingStatus`, also run on every market-data
update just before the book is published: status and book share the instrument's queue, so a
client hears Auction before it sees the crossed book). It publishes only on a change; Auction cancels
nothing (only Closed does), and the simulator's matching already skips a crossed book. As live,
anything not Open is refused (`RiskLayer`, the simulated exchange), cancels included; CME itself
accepts cancels in pre-open. Accepted gap: a halt with an uncrossed book (that day 02:49 to 13:02)
still reads Open, since the data carries no status.

## Socket close protocol (server side)

A client's close is two different jobs on two different kinds of thread:

- **Detection — any reader thread.** `ServerSocket.TryRead`/`GetReadStatus` see the client's close
  message and *request* the close: one atomic `Open → Closing` transition. Wait-free, no callbacks,
  nothing else on the hot path.
- **Transition — the listen thread only.** `PollPids` sees `Closing` and performs `CloseClient` →
  `Detached` (persist) or `Closed`. `CloseClient` therefore has exactly one calling thread: the
  historical double close (two threads interleaving check-then-store, `ClientClosed` firing twice —
  and on an execution thread, where the cancel-all callback has no business) is gone by
  construction.

The request is a CAS against the **snapshot the close was read under** (`Tools/AtomicEnum`:
{state, epoch} packed in one 64-bit word, the epoch advancing on every transition). That kills the
two races the naive `if (Status == Open) Status = Closing` has:

1. **Re-arm:** a reader preempted between check and store wakes after the close completed and
   re-marks `Closing` — `CloseClient`/`ClientClosed` fire a second time. The epoch check fails the
   stale store instead, so `ClientClosed` stays exactly-once without requiring the app callback to
   be idempotent.
2. **ABA across reconnect:** a reader that slept through close *and* reconnect sees `Open` again —
   the new session's `Open` is the same bit pattern as the dead one's — and a plain enum CAS would
   tear down the healthy new session on the dead session's evidence. The epoch never repeats, so
   the CAS fails.

A fresh `Load()` at the transition site would adopt the new epoch and defeat the whole check — the
CAS must use the snapshot taken when the evidence was observed.

### A reader never latches closed

A close message ends the **writer's session, not the ring**. `ReadOnlySocket.TryRead` returns `Closed` for the close
message itself, that one read only, and its next read continues right after it; the reader keeps no "closed" state at all
(no flag, no `Close()`, no `IsClosed`). Only a writer closes: `WriteOnlySocket.Close` writes the close message and from then
on emits only close messages, until a reconnect's `Recover` clears it.

What a `Closed` read means is decided by **whoever owns the session**, not by the reader:

- **`ServerSocket`** (the trading server, Logging's own sockets, the Mirror's remote): `Closed` requests `Open → Closing`,
  and every later read and peek is gated on the client being `Open`, so the session ends there until the client
  reconnects; the reconnect's `Recover` skips anything left unread.
- **Logging's audit tap** (`SocketListener`, which reads a trading server client's channels with no `ServerSocket` in
  front, for the whole life of the socket): the close message is a point in the record; the tap keeps reading, and a
  reconnect under the same name writes its next session right after it, so that session is audited too. Every channel
  closes in the client-to-server direction only, so a pass never reports `Closed` overall and the tap is never dropped,
  which is right: under `Persistance` the socket, and the server's writes into it while the client is away (fills, the
  cancels on close, their Done), outlive each client process.

Why not keep the latch (as it was): it made a close permanent for the ring, which no reader needed (the `ServerSocket` gate
already ends the session) and one reader could not have: Logging's tap went deaf to the client-to-server direction at the
first close, so after any reconnect under the same name (a Workspace reopened, an algo restarted, every Mirror link drop
over 10 s) the audit kept the server's answers but lost every order, amend and cancel the operator sent. Rebuilding the
tap per session instead would lose the server's writes while the client is away, and either skip or duplicate the new
session's first records at the rebuild.

Consequences kept deliberately:

- **Write gate:** `Open || Detached || (Persistance && Closing)`. Without the third term, the ≤1ms
  `Closing` window silently drops server→client writes — a fill landing there would vanish from the
  very ring persistence exists to preserve. Non-persist keeps dropping there, matching the old
  semantics.
- **`PollLetterBox` answers `Closing` and `Closed` with `ConnectRefusal.Closing`**, and `ClientSocket.Connect` asks again
  every 100 ms: opening there would swallow the close in flight.
- **Recover-on-reconnect skips the dead client's unread backlog** (`Socket.Recover` parks readers
  at the writer's head). Consistent with the persist design — the client is gone and its orders get
  cancelled — but it is a choice, not a neutral fact.
- **A pid of 0 or below is dead.** `PollPids` closes an `Open` client whose process
  `ProcessId.IsAlive` reports dead, and closing is what cancels its orders. On Linux `IsAlive` is
  `kill(pid, 0)`, and `kill(0, 0)` (own process group) and `kill(-1, 0)` (every signalable
  process) both succeed, so without a guard a client whose header never got its `ClientProcessId`
  would read as alive forever and its orders would never be cancelled; on Windows `OpenProcess(0)`
  fails, so the two platforms disagreed. `IsAlive` returns false for `pid <= 0` on every platform:
  the safe direction, because closing cancels. The C++ `IsProcessAlive` keeps the same guard.

**Logging polls every 100 ms live.** `LoggingServer.PollerLoop` checks every reader once and then, in `Realtime`, sleeps
100 ms; a worker then drains each reader with news to its writer's head. Why: to learn whether a ring has news a poll reads
the line the writer will write next, and the writer's next write must then take that line back from Logging's cores
(about +330 ns measured on the CME box, cross-chiplet). Polled continuously, nearly every message write paid that; polled
every 100 ms, at most the first one or two writes per ring after each pass do. Everything else a pass reads is lines the
writer has finished with. What it costs: alerts and strategy logs reach their files up to 100 ms later; a client's audit
file up to about 200 ms later, because records within 10 ms of the newest at a poll are held for the next one to keep the
file in timestamp order (measured 2026-10-13: typically 80 ms, 2–10% of records held over, worst 167 ms in normal
running). A newly announced client is tapped up to 100 ms later (no loss: a tap reads its rings from the start), and a
crash can lose up to about 200 ms of not-yet-appended lines. The rings are 2 MB per channel and writers never
wait for a reader, so a live channel would have to write more than 2 MB in 100 ms before the tap lost records.
`Simulation` keeps the continuous poll: no live latency is at stake there, and a backtest at Max speed could lap a ring
in 100 ms. The ordering margin (`WatermarkMargin`, 10 ms) does not depend on the poll interval: every pass drains every
channel, so a record read in a later pass was written after this pass read its channel; the margin must cover one
pass's drain and how stale a timestamp can be when written. Not fixed by this: when a writer comes round its ring, its
first write to each line the tap read on the previous lap still pays to take it back (a writer-side prefetch ahead of
its cursor would hide that; not done). Considered and not done: a per-ring letterbox where each writer publishes the
sequence the tap may read up to, which removes the last one or two penalties per pass at the cost of work on every
writer's hot thread; worth it only if a measurement shows those remaining penalties.

### Connect: every request is answered

- The server's reply carries `ClientId`: the client's id when 0 or more, otherwise a `ConnectRefusal`
  (`NoClientIdAvailable`, `ServerError`, `AlreadyConnected`, `Closing`, `ReservedClientName`, `NotReady`). `SocketHeader`'s
  layout is unchanged. Before this, three cases got no reply and `Connect` waited forever.
- `Connect` throws `ConnectRefusedException` (its reason in `ConnectRefusal`) and only `Dispose`s the socket: a refused second
  process maps the same rings as the live one, and `Close` would write a close message into them, ending the live session
  and cancelling its orders. Its exit action (close at exit) is cancelled on `Dispose` for the same reason.
- A throw while deciding is answered `ServerError`; each listen pass is wrapped too, since that thread is what notices dead
  clients and cancels their orders. A reply left by a client that died before reading it is emptied before the next is
  stored (Linux keeps the region).
- A request's own `ClientId` is ignored (forced to -1): only `LoadClients` may pass a stored id.
- A client's constructor that throws after `Connect` disposes itself, so the server releases the name; a retry in the same
  process used to hang.

## Remote workspaces (the Mirror)

People outside the colo (arb's risk desk, the home office) run the normal Workspace against a copy of the CME server, with
full order entry. Either end may open the link, set in the JSON alone; which one does is the networks' choice (the CME box
dialling out needs no inbound rule at TNS; a desk dialling the box needs none at the office).

- **Realtime only (2026-10-21).** A desk mirrors only a live server: the Mirror always runs `ClockMode.Realtime`, has no
  `ClockMode` setting (an old config's `"ClockMode"` is ignored), and a server restart is always rebuilt on the desk.
  Until then a simulation Mirror followed the simulated server's header clock, refused another run's copy, and stopped
  at a server restart. A test server for the Mirror must run Realtime too: server directories are
  `<root>/Servers/<ClockMode>/<name>`, so the Mirror finds no server under `Servers/Simulation` (market data is not needed
  to test links and restarts).
- **Two ends, one program.** `Mirror.exe <absolute config path>` reads that file; with no argument it reads
  `MirrorConfig.json` in the working directory. A missing file is written with defaults and the program stops, naming its
  full path. One build can so serve both roles from two files (a relative path would resolve from the working directory;
  it is not checked). `MirrorRole Local` runs beside the real server, `Remote` where people sit. Either end may
  listen (`ListenPort`), dial (`ConnectAddresses`, redialled every second), or both. A listening end accepts only its
  `IPWhitelist` (the box: each risk desk's address; a desk: the box's), and refuses to start with it empty: links are
  unauthenticated, so whoever reached a listening Local end would get full order entry. Until 2026-10-14 a Local end
  refused to listen at all; it may now, behind the whitelist and a firewall rule to the same addresses, inside a VPN.
- **Rows down.** The Local end attaches read-only and publishes every 100 ms on its own clock while a remote is connected
  (`LocalMirror.IsPublishDue`; with none connected nothing is published). One cadence, however many desks: each pass
  reads every row (the cost below), so passes must not scale with desks. Until 2026-10-27 a publish ran on the pass any
  remote's ping arrived (every 100 ms on that remote's clock) and on every order state a stand-in read, so three desks
  made about 30 passes a second plus one per remote trader's order; now 10 a second flat, a remote's own order shows
  within 100 ms instead of on the next pass, and a new remote's snapshot comes on the next tick. A pass sends each remote
  every row whose bytes changed since the last pass; a remote that just connected gets every row. Bytes, not seqs: the
  server writes TradingStatus, Timestamp, QuantityAhead and the rate limits in place without moving the seq. A pass sends
  the server header, then rows, then `AllocatedClientId`s, then a heartbeat, so no GUI gets its id before the rows that hold it.
  The header is its own packet, not a row: the remote sizes its copy from the first one and posts it in the copy's
  `LetterBox`, as a server posts its own; later ones are written into that entry. It also carries the Local end's
  `Context.GetLayoutHash()` (FNV-1a over each array's row size and capacity, in creation order), and the Remote throws at
  startup, naming both values, when its own differs: rows are tagged only by array number, so a desk on a different build
  (an array added, removed or resized) would put rows into the wrong arrays and show plausible but wrong orders and
  positions. Both ends must run the same build. A row being written as the pass reads it
  (odd seq, or the seq moving during the copy) is skipped and caught next pass (`SharedArray.TryCopy`, which never waits):
  `TryRead` would spin, forever on a row a dead client left mid-write. The order walk (`OrderEnumerator`, the GUI's order
  grids) does the same for the same reason: a slot mid-write is skipped for that refresh, never shown torn (a torn row can
  mix two orders, and the GUI acts on what it shows).
- **Every `SharedArray` row is framed at construction** (`Protocol.Stamp`: `Magic` and `Length = sizeof(T)` where missing,
  `Sequence` untouched, by any write-capable opener). Rows the server edits under the lock (order states, positions) never
  get a frame from `Write`, and `TryRead`/`Read` refuse a row without one: the mirror saw every order state as Empty. An
  unwritten row still reads Empty (`Sequence` 0). The server header is no longer also opened as a one-row array
  ("array 0"): that region is a `LetterBox`, whose `Length` means "posted", and a frame stamped on it would fake a post.
- **Cost to the server.** A line the server wrote since the last pass costs its next write a cross-chiplet ownership recall
  (measured about +330 ns on Zen 4, once per hot line per pass; comparing bytes or checking seqs first costs the server
  the same). Writing everything twice to a ring for the mirror measured worse: it pays on every event. The Local end runs
  on the housekeeping cores, since each CoreGroup fills its chiplet.
- **One ManualClient per open remote GUI; the Workspace's client is a pass-through to it.** The remote lists its open GUIs
  every second (`GuiList`), and at once when one connects. The Local end starts a ManualClient, the GUI's stand-in, under
  the same name for each newly listed GUI and closes it when the GUI is no longer listed or its remote drops; the server
  then cancels what it left working, as for any client that closes, and refuses a reopen `NotReady` while those cancels
  are pending (a stand-in it could not start is tried again on a later `GuiList`, at most every 3 s). The stand-in's id is
  the GUI's id on the remote, so the copied order rows and the ids in the GUI's orders are its own: no id translation.
- **Which Workspace is remote: only a live Remote Mirror says so.** `ManualClient.Open` first opens a plain ManualClient.
  The Remote Mirror's `ServerSocket` refuses every name that is not a remote desk's (`ConnectRefusal.RemoteDesk`), and
  Open then opens the same number as a `RemoteClient`; the real server never gives that answer. Why not a marker file or a
  flag: a file outlives the Mirror that wrote it, and a Workspace that once saw it beside a real server would send nothing
  that server reads. A remote desk's GUI names end `@<machine>` (`Client.GetGuiName`): no Workspace on the CME box or
  on another desk can hold one, so after a link loss nobody else can take a remote GUI's name, and with it its client id,
  before its stand-in is started again. The Local end reads the principal from the name before `_GUI<n>@`
  (`ManualClient.TryParseRemoteGuiName`). Names still fit 256 characters, folder included (see "Setting up a remote desk").
- **A `RemoteClient` holds nothing.** Each call (order target, allocation, algo status, risk limit) goes unchanged into its
  socket's admin channel; the Remote Mirror forwards it, and the Local end makes the same call on the stand-in, which takes
  the slot, runs the RiskLayer, writes the target row and sends. Nothing comes back down the GUI's socket. The
  RemoteClient holds no order state, writes no row and raises no event: a remote Workspace
  shows only what the copy holds. The InstrumentHeaders grid watches the server's allocated instruments
  (`ServerContext.InstrumentIds`), so an allocation shows there without an event.
- **The copy is read-only on the remote.** The Remote keeps it under the same server leaf name and runs a `ServerSocket`
  under that name, where only `RemoteClient`s attach. The Mirror is its only writer, and it writes only the Local end's
  rows. Rows are stored with `RecoveryWrite`, which repairs one a killed Mirror left mid-write. Names travel relative to
  `Context.DirectoriesRoot`; each end puts its own root in front (`MirrorPacket.RewriteNames`). Why: a remote GUI that wrote
  its own target rows beside the Mirror made two writers per row, which needed a per-GUI reconcile after a link loss and a
  close-out after a Workspace restart; with one writer neither exists.
- **A remote order shows once its row returns.** Its target row is written beside the server, so it appears when the next
  pass brings the row down: the stand-in's first order state from the server triggers that pass at once, so about one round trip, then the Workspace refresh. The GUI never shows its own action
  before the Local end has it. A click lost in a dying link never appears.
- **Answers.** A `RemoteClient` connecting on a remote waits for the Local end's `AllocatedClientId`: its id, or the server's refusal
  (`AlreadyConnected`, `NotReady`), which `ManualClient.Open` handles as on the CME box (the next number, or ask again in
  a second). A second Workspace under a name already open on the same remote is refused at once.
- **The latency display.** One link per remote machine, so one number, kept in the copy for every Workspace there:
  `Context.GetLatency(coreGroupId)`, a `Tools.Duration` per CoreGroup (the shared array `<server>/Latency`), below 0
  while disconnected; on a remote copy, index 0 is the Mirror link. The Remote Mirror sends a `Ping` every 100 ms carrying
  its own monotonic clock (`Timestamp.StopwatchNow`, the machine's `Stopwatch` as a `Timestamp`); the Local end answers it at once as a `Pong` (2026-10-27; until then after the publish it triggered, which made publishes scale with desks), so the round trip is the Local loop and the link; the
  Remote times it on that same clock (no clocks need to agree) and writes `Latency[0]` every pass: the worst of the last
  10 round trips, or, when longer, the age of the oldest unanswered ping (the newest answered one's age less one interval),
  so it climbs through a stall (a new link starts afresh, 2026-10-14: its round trips cleared, its unanswered age counted
  from when it connects, its first ping sent at once; before, the old link's last pong made a Local Mirror restart read as
  a latency of the whole outage, climbing until the new link's first pong); -1 ns at
  once when the link drops, and from 10 s without a heartbeat (`RemoteClient.s_silenceTimeout`, also `TcpLink`'s silence
  limit): a Local end that answers pings but no longer publishes reads as broken, not as a healthy number over a frozen
  screen. A remote Workspace shows it beside its clock exactly as written, to the microsecond (`232.417 ms`, so a steady
  link visibly ticks), and below 0 as "Connection broken – reconnecting…": the number only ever comes from the Remote
  Mirror (2026-10-14; before, the Workspace showed the row's age when longer, so a closed Remote Mirror read as a latency
  climbing for 10 s). Two states come from the Workspace itself: "Remote Mirror offline" once the Remote Mirror its
  `RemoteClient` connected to has exited (`RemoteClient.IsRemoteMirrorAlive`, the process id from
  `<server>/RemoteMirrorProcessId`, checked each 100 ms refresh, so within about 100 ms however it ended), and "Remote Mirror
  not responding" when it is alive but has not rewritten the row for 10 s (a hung main thread). The Workspace reads the
  value plainly, not through the seqlock: one aligned 8-byte `Duration` cannot tear, and a seqlocked read would spin
  forever on a row a killed Remote Mirror left mid-write, and would throw on an unwritten row. The Local end never writes the array (the server's rows are unwritten, so they are never
  published over the copy's). The ping is also the link's keep-alive. A Workspace beside the real server shows no number;
  once the server's process has exited (`ProcessId.IsAlive(ServerHeader.ServerProcessId)`, each refresh) it shows
  "Disconnected – server stopped, close this Workspace" there instead: it cannot reconnect by itself, and while it maps the
  dead run's header the server cannot restart (see "A server never starts on a previous run's memory").
  Clicks while the link is down are dropped, with no refusal: the display is the signal. The clock beside it is
  `Context.GetLatestMarketDataTimestamp()`: the newest `NicTimestamp` among the books of the window's principal (every
  instrument for a server workspace, the strategy's own for a strategy workspace), read from the copy's book rows, so it
  moves only while the server is alive and those markets update. Read together: a moving clock and a healthy latency mean
  all is well; a frozen clock with a climbing latency or "Connection broken" means the link or a Mirror is down; a frozen
  clock with a healthy latency means the server has stopped or those markets are quiet (no code needed for that case).
  Until 2026-10-21 the clock was `ServerHeader.Timestamp`, which every CoreGroup thread rewrote on every book update: one
  shared line, also read by every order's risk check, pulled between chiplets on each update. The server no longer writes
  it; it is the simulation clock only (written by `ServerSimulator`, followed by a simulated Workspace).
- **Folder sync (2026-10-21).** Each desk keeps a byte-for-byte copy, at the same relative paths under its `Data`, of every
  file in the folders of the Workspaces open on it: each GUI's principal (the server for a server workspace, the strategy
  for a strategy workspace) and the GUI's own folder (its stand-in's audit), everything except `Workspaces` folders, which
  stay each machine's own. The widgets that tail files (Audit Trail, Fills, Charts) work on the copy unchanged; they open
  files with `FileShare.ReadWrite`, so the desk writes while they read.
  - *Off unless the desk asks (2026-10-22).* `MirrorConfig.IsFolderSyncOn` (default `false`): a desk syncs only when its
    config says `true`; otherwise `RunRemote` builds no `FileSyncRemote`, the desk's GuiList says `IsFolderSyncOn = false`,
    and the box sends it nothing (its Audit Trail, Fills and Charts stay empty; rows and orders are unchanged). The
    risk team's package (the Production template) leaves it out, so it is off; our own desks' templates (NewRelease,
    Adrian) turn it on (2026-10-23: the risk team no longer gets NewRelease). It is the desk's preference, not access control: a desk that edits
    its config to `true` is served, so keeping files from a desk needs a box-side rule (not built).
  - *No subscription message (2026-10-22).* The GuiList a desk already sends every second carries one byte,
    `MirrorGuiList.IsFolderSyncOn`, and the box derives the folders from the listed GUI names itself
    (`FileSyncLocal.Serve`): the GUIs and the sync cannot disagree, and the folder rule lives on the box only. A change of
    folders announces every file afresh. The same-day `FileSubscribe` packet (the desk listing folders) was removed.
  - *One link.* The sync rides the desk's single row link, so rows and files connect and disconnect together. A row waits
    behind at most the desk's requests in flight (4 of 512 KB: about 160 ms at 100 Mbps, 16 ms at 1 Gbps, and only while
    files catch up). A first two-link design (2026-10-21, same day) let the links fail apart and was replaced.
  - *Each fact has one owner.* The box owns what a file is: every 100 ms (`FileSyncLocal`, one per `MirrorSession`, on
    the Local's main thread) it announces `(path, length, write time)` for each file under the desk's GUIs' folders
    whose version changed, and it keeps no picture of what the desk holds. The desk owns what it holds: its own files.
    `FileSyncRemote` (on the Remote's main thread, inside `RemoteMirror.Read`, one object for the desk's life) compares
    each announcement with its copy on disk, and asks for what the copy lacks: a file of 64 KB or less, or a copy that is not a shorter start of the box's file, from 0; a bigger
    file from the copy's length (big files only grow). The box answers a request (up to 512 KB) with consecutive 64 KB
    chunks from the offset asked for, a short or empty chunk ending the answer. It serves only files it announced; a
    request for any other, or for one gone since, is answered with a withdraw (`FileVersion.s_withdrawn`, length −1), and
    the desk stops asking for it (its copy stays). A
    copy made whole takes the box's write time, so the copy itself says what the desk holds; a copy whose length and
    write time match (or, for a big file, whose length matches) is settled. The desk asks only up to the length announced:
    the box serves what the file holds when it answers, so a request past the announcement would let the copy outrun the
    box's word and look rewritten (verification, 2026-10-22: a live 1 GB audit was refetched from 0 forever). One request
    per file in flight, 4 in all; when an answer ends, its slot goes to the file waiting longest, so a live fill never
    waits behind catch-ups; a request's slot is held until its answer ends, whether or not the copy took its chunks.
  - *Why pull.* A first design had the box decide what the desk lacked from its own model of the desk's disk (lengths,
    last 64 bytes, a generation per subscription). Every failure the adversarial tests found (2026-10-21) was that model
    drifting from the real disk: a refused write, a subscription that failed to build, a same-length edit, an append
    without a moved write time, chunks in flight across a resubscribe, an unbounded push. With the desk asking from what
    it really holds, a chunk always fits, and there is nothing to drift.
  - *Refusals.* A desk file another program holds without sharing (an editor, Excel) is not written: logged once, and
    asked for again once a write open succeeds. A box file the Mirror cannot read is answered empty; the desk asks again
    on its next 100 ms check. A box name this machine cannot hold (a `:` on Windows, `..`) is not synced (logged).
  - *Measured* (scratch harness `filesync2`, both ends in one process over loopback, 2026-10-22): append median 63 ms
    (max 105); config rewrites shorter, same length (a change far from the end) and longer, 63-102 ms; 40 MB at 65 MB/s;
    a live append during a 200 MB catch-up 31 ms; 20 link cuts in 6 s and 1,519 appends in 3 s caught up at once; a locked
    file synced 17 ms after release; a desk restart over its copy fetched no file again.
  - *Accepted residuals.*
    - A file bigger than 64 KB rewritten in place to the same or a longer length is taken for an append (big files are
      append-only logs by contract); a whole refetch of a big copy, which only an out-of-contract rewrite causes, cuts it
      to one chunk while a widget may tail it (its rows can repeat).
    - A file of 64 KB or less is fetched whole on each announced change (at most about 640 KB/s per live small file).
    - Deletions are not mirrored: a box file deleted is withdrawn when next asked for, and the desk's copy stays.
    - A box file the Mirror cannot read is answered empty, which cuts a copy being fetched whole to empty until it can be read.
    - A desk that can open but not write a copy (a full disk, a byte-range lock) keeps the link as busy as a catch-up.
    - Box names that differ only in case, or end in a dot or a space, collide or change on a Windows desk (out of contract).
    - The desk's Data folder must be on NTFS or ReFS: settling a small copy compares 100 ns write times.
    - The box scans each desk's folders every 100 ms on the Local's main thread (about 2 ms per 1,000 files on Linux):
      folders up to a few thousand files, a few desks. A Local Mirror on Windows (a test box) refreshes each file during
      the scan, since NTFS keeps a held-open file's listed size stale (LoggingServer appends to a file it holds open all
      day): about 16 ms per 1,000 files there. A file named for a day before yesterday (`yyyy-MM-dd.*`: audits, alerts,
      LoggingServer logs) is finished and closed, so its listing is right and it is not refreshed; the day's margin covers
      UTC, Chicago and local dates. A 200-file folder (90 days of audits and alerts, 20 undated) scans in 0.8 ms on Windows.
      A finished day's file written again (a backfill) while held open would be missed on a Windows box until it closes.
- **Link loss.** Ten seconds of silence ends a link; both ends send at least once a second. The Remote closes a dead link at
  once, so the Local end sees it end and redials without waiting for its own 10 s; the Local end drops what was still
  queued on it, closes that link's stand-ins, and the server cancels what they left working. While the link is down a
  GUI's calls are dropped. When the link returns the remote gets a full snapshot (a row skipped mid-write during it is
  sent the next pass) and its GUIs are listed again; a call that reaches the Local end before its GUI's stand-in is started
  again is dropped (a residual: the latency is the link's, not that GUI's, so it does not show this). Nothing is reconciled: the remote holds no order state, and every
  order's end is in the copied rows.
- **A Remote Mirror restart (2026-10-14).** Workspaces still open keep the last Remote's copy mapped, header posted. A new
  Remote no longer waits for them to close: when its header cannot be posted it reuses that copy, wiping it as on a server
  restart (`newServerHeader`), and posts its own process id in `<server>/RemoteMirrorProcessId` once it listens. Each
  Workspace's `RemoteClient` noted the Remote's id at connect; `IsSessionEnded` (a new server or a new Remote) makes it
  rebuild in place, under its own GUI number. The old sessions need no close: the new Remote's `ServerSocket` starts with
  no clients, and every socket skips to the head of its rings when built, so a call left unread in a ring is not
  replayed. Not reused, as before (the Remote stops, saying to close the Workspaces): a copy sized for other capacities
  or another build (its layout hash differs from the box's). Restart
  harness R8: two GUIs open, the Remote killed and started, both rebuilt under their own names and their calls reached
  the server.
- **Restarts (2026-10-13).** The Remote tells them apart by the server's process id in each server header, which opens every
  publish pass, so it is checked before any row from a new link is applied; until a link's first header is checked,
  nothing goes up it (no GUI calls, no GuiList). Every header's `LayoutHash` must also equal this desk's, or the Remote stops
  (a Local end of another build would put rows in the wrong arrays). **A Local Mirror restarted over the same server is a
  link drop**: its stand-ins died with it (the server cancels what they left working), the new one gets a full snapshot and
  the GuiList, and starts stand-ins under the same names, which the same server gives the same client ids; every Workspace
  carries on. **A restarted server is a new run** (client ids handed out in whatever order GUIs reconnect, instrument ids
  that can shift when an instrument is no longer listed, rows it has not written yet), and the desk rebuilds on it in
  place:
  - *The Remote.* One `RemoteMirror` object serves one server process. Seeing another process id, it stops reading (the
    rest of the link is the new server's) and `RunRemote` disposes it (its `ServerSocket`, every GUI slot, the pending
    table; a GUI waiting in `AllocateClientId` is answered NotReady and asks again) and builds a fresh one over the same copy
    and the same link. The fresh one resets every row of the copy to unwritten (`SharedArray.Clear`: sequence 0, zero bytes,
    frame kept, so none of the old run's orders or positions shows where the new server has not written), applies the new
    server's snapshot, and at the heartbeat that closes it writes the new header and starts listening. Old GUIs are attached
    to no socket, so their calls are never read and they are never listed, as long as no other window takes their names (each
    rebuilds under its own GUI number, below): there is nothing stale to track. Why one object
    per server: the Remote's slots are indexed by the server's client ids, and a new server can give a GUI an id another
    GUI's old slot holds, whose socket was built for the other name (tested: calls silently lost).
  - *Each remote Workspace.* On its next refresh after that header (so against a complete copy) it sees the new process id
    and rebuilds every Workspace of its process in place (`Workspace.RestartWorkspaces`): `ContextManager.Renew()` once
    (fresh contexts: everything a context learnt of the old run starts over, its cached instruments and positions, the
    server context's symbol strings, a strategy context's client id); every window's layout captured and **every** window
    `Disconnect()`ed (stop its client thread, dispose its widgets and client) before any reconnects, so no window can take a
    name another still holds; then per window: take the renewed context for its principal, `Connect()` (a new client asking
    for the window's **own GUI number first**, wrapping round the 8 if it must fall back, its id from the new server), and
    restore its widgets only (`RestoreWidgets`: its size, position and state are left alone). The window and its menus
    never close; one that cannot reconnect (or whose strategy is not in the new run) closes, saying why. Only a connected
    window starts a rebuild, so refreshes such a closed window had queued cannot start another. Nothing is unmapped: the
    replaced contexts, and the GUI client's own context (`RemoteClient.Dispose` retires it, `ContextManager.Retire`), stay
    mapped until exit, views of the same copy, so a timer, the simulation clock thread, a warm-up or a widget's late
    callback (a dialog it awaited) still reading through an old one reads valid memory, and nothing waits. Why its own GUI
    number: a GUI's ring is named after it, and the fresh RemoteMirror holds no record of who had which name; a Workspace
    asking for the lowest free name could take another window's that has not rebuilt yet, so its stale calls would reach the
    wrong stand-in and one window's close would end the other's session (tested with two GUIs rebuilding seconds apart).
  - *The Workspace keeps its own list of open windows (2026-10-14).* `Workspace.OpenWorkspaces`: a window adds itself
    when built and removes itself when closed; the rebuild, closing (the last window shuts the app), saving a workspace's
    other windows, and `WorkspaceRunner` (screenshot, close all) all use it. Why: Avalonia 12's `desktop.Windows` lists only
    windows opened after the lifetime starts, and the first Workspace opens before it (checked in a scratch Avalonia 12
    app: `Count` 0 with the first window up, 1 once a second opened). The first box test of a server restart showed it: the
    rebuild found no window and was retried every 100 ms (a fresh `ServerContext` each time), the latency text stayed
    blank and the window's calls went to the closed stand-in.
  - *A rebuild never closes the old name (2026-10-14).* A new window can take a name in the fresh copy before the window
    that held it on the old server notices the restart (the copy's header, with the new process id, is posted before the
    Remote listens). `RemoteClient` remembers the server process it connected to and its `Dispose` skips the close message
    when the copy's header names another: the old session already ended with its server, and the close would end the new
    window's. Before, it did: the new window's stand-in closed and its calls went nowhere (restart harness T4b).
  - *Widgets restore by instrument id, by design.* A ladder or other widget that saved an `InstrumentId` reopens on that
    id, also after a rebuild: if the restarted server allocated its instruments in another order (one no longer listed
    shifts the later ids), the widget shows whatever that id holds now. Declined (2026-10-13): restoring by symbol.
  - *Start order on the CME box.* A rebuild uses the first complete snapshot of the new run, so the Local Mirror must start
    after the server has loaded its clients and instruments (`LoadClients`, `LoadInstruments`), not merely after it posts
    its header; otherwise Workspaces rebuild against a run with no instruments yet.
  - *Residual: a torn row across the wipe.* `Clear` returns a row's sequence to 0, so a Workspace reader descheduled between
    reading a row's sequence before the wipe and re-reading it after the new run's first write (which can reach the same
    even value) could accept a row mixing both runs. It needs a stall of milliseconds at exactly that moment, during a
    rebuild that discards that Workspace's view anyway; accepted.
  A layout change stops the Remote instead (a desk mirrors only a live server, so a restart is always rebuilt): it
  writes -1 ns into `Latency[0]` first, so every Workspace reads "Connection broken" at once, and is
  restarted by hand. A Remote stopped any other way (killed, closed) leaves its last latency value, and the 10 s age check
  catches it. A server restart still cancels every working order (the old run's state is gone). Replaced: the heartbeat's
  Local start time, which ended the desk on any Local restart.
- **A connect request leaves the letterbox only as it is answered (2026-10-22).** A client posts its request once and waits
  for the reply (`ClientSocket.RequestClientId`). `ServerSocket.PollLetterBox` reads it with `TryPeek`, decides, and empties
  the letterbox just before it replies. Until then it emptied the letterbox first, and on a desk deciding takes a round trip
  to the box (`RemoteMirror.AllocateClientId`, up to 10 s): a Remote killed in that window lost the request, and its
  Workspaces waited on "Waiting for server" for good (lifecycle harness, S7b, about 1 run in 3). Emptied before the reply,
  never after it: a listener that died between replying and emptying would leave the request for the next one, which
  would answer it again, start a second stand-in, and bring the real Workspace back as GUI1. While a request is being
  decided, another client's post waits in its `TryStore` loop; the listener answers one at a time either way.
  Residual: a listener that dies in the microseconds between emptying and replying leaves that client waiting; close and
  reopen the Workspace.
- **The Local Mirror lives and dies with the server.** It reads `ServerHeader.ServerProcessId` once at startup and stops
  when that process has exited (`ProcessId.IsAlive`, every pass): a dead server's stand-ins would be read by no one, and
  the server cannot restart while this process maps its header (next bullet). Its exit closes its TCP links, so a remote
  sees the link end within one network trip and shows "Connection broken" at once; once the server is back, the new Local
  end's server header carries the new process id and the desk rebuilds on it ("Restarts" above). Each server run needs its own Local Mirror (on the CME box the C++ server's
  main starts it after posting its header). One started before any server waits in `EnsureConnected` until a header is
  posted, which blocks nothing (an empty box takes the server's post). One started on a dead run's header (still posted
  because some process maps it), or on a `ServerProcessId` of 0, exits at once: it checks right after attaching, before
  its `AlertManager`, whose connect would wait forever for the dead server's Logging (that Logging exits with its
  server). Residual: a server that dies while a starting Local Mirror is inside that connect leaves it waiting there, and
  it must be killed by hand (the restart's refusal names "Local Mirror"). Residual: the check trusts a bare process id, so
  if the dead server's id has been given to another process, that process reads as the server. The refusal then names
  it as "already running", and a Local Mirror started on that header keeps running until it exits. Not fixed: the CME
  box's ids exceed 3.4 million since its July boot, so they wrap about every four months and one coming back within a
  restart window is unlikely; Windows reuses ids sooner, but only the simulation runs there. The fix, if ever needed, is
  the server's start time stamped beside its id (a header layout change). Only the whole box going dark sends no close;
  the 10 s silence catches that.
- **A server never starts on a previous run's memory.** `ServerContext.Connect` posts the header into a `LetterBox`, which
  refuses a post while a value is there, and nothing ever empties it. So while any process still maps the dead run's
  header (a Local Mirror, an algo, a Workspace) a restart throws, naming the case: "already running as process N" when
  that process is alive, otherwise "previous server (process N) has exited, but other processes still map its shared
  memory; stop its clients…". Once nothing maps them the regions are unlinked, and the restart starts on empty memory.
  Declined (2026-10-13): overwriting a dead run's header. The server would start on that run's order rows, rings and
  allocations (a state row still `Active` for an order CME may already have cancelled), and every client left running
  would be read by no one, silently: a restarted server reads only clients that connected to it. Residual: a server that
  dies inside the header's lock (`AllocateClientId`, nanoseconds) leaves its sequence odd, and the restart's post then
  spins in `AcquireLock` instead of throwing.
- **`ServerHeader.ServerProcessId`** (offset 173; the header is now 177 bytes): the publishing server's process id, stamped
  by `ServerContext.Connect`, the one place that publishes the header, so no host sets it. Read by the Local Mirror's
  liveness check and by `Connect`'s refusal. A remote copy carries the CME box's process id unchanged: never test it
  against the desk's processes.
- **No Logging on a remote desk.** The Remote starts none, taps no GUI socket, and a Workspace running a `RemoteClient`
  creates no `AlertManager` (its client thread's exceptions go to its console). Rejects alert beside the server: the Local
  Mirror runs one `AlertManager` on the server's context, subscribed to every stand-in's `OrderRejected`, and reports its own
  exceptions to it too (so the Local Mirror starts only once the server's Logging listens). It connects as
  `<server>/LocalMirror.alert`, not the context's default `<server>.alert`, which the trading server itself holds: Logging
  keeps one connection per name and refused the Mirror `AlreadyConnected` (seen on the CME box, 2026-10-13); its alerts
  land in `<server>/LocalMirror/Alerts/`. The stand-ins' sockets are tapped by the server's Logging like any client's, so the CME box has
  the full audit, which the folder sync copies to each desk that has those Workspaces open. The `AlertManager` also alerts
  `TaskScheduler.UnobservedTaskException`, so the dial loop cancels a connect that times out (`TcpLinkConnector.DialTcpLink`,
  a 2 s `CancellationTokenSource`) instead of abandoning it: an abandoned connect task faulted unobserved, and with the home
  PC off overnight (a dropped SYN every redial) that was an Exception alert about every 3 s, over 1,000 an hour.
- **Spread symbols resolve through the process's ServerContext.** `LeggedHeader.GetLegHeader` is one static per process,
  set only by the first `ServerContext` created (`??=`); a `ClientContext` never touches it. Every process has a
  ServerContext that lives as long as it does (the server's own, `ContextManager`'s, the Remote's copy). Not
  `ContextManager.ServerContext` itself: the simulation server builds its own before `ContextManager.Initialize`, and the
  Remote Mirror never calls it. `ServerContext.Dispose` clears the lookup if it is its own, so a test that starts another
  server in the same process takes it over. Why: every context used to set it to itself, so it pointed at the newest; in the
  Local Mirror that is the last stand-in started, and once that stand-in closed (a GUI closing, a link drop) the next spread
  lookup went through that disposed context: it threw `ObjectDisposedException` (the disposed `ServerHeader` box is read
  first), so a remote desk's spread allocation was silently dropped (only an Exception alert showed it) and a spread reject
  alert named `UnknownSymbol_<id>`; a lookup on the AlertManager thread racing the stand-in's `Dispose` could read unmapped
  memory and crash the process (verified on the loopback harness, 2026-10-12). All contexts of one server map the same
  header array, so the answer is the same.
- **Calls are not aged.** Every call that reaches the Local end is made on its stand-in, however long it took. A call
  clicked during a stall shorter than the 10 s silence limit (the network, either Mirror, or a stale copy) reaches the
  server when the stall ends, acting on the market as it is then: a Buy clicked 1 s into a 9 s stall goes to the exchange
  about 8 s later. The latency display, climbing through the stall, is the trader's signal to wait before clicking. A call
  made while the link is declared dead is dropped (`TcpLink.Write`), and the stand-ins' orders are cancelled. (Until
  2026-10-12 the Local end dropped any call whose copy was over 1 s old by its own clock; removed by the user's decision.)

### Risk desk package (2026-10-16)

Both deploy scripts live under `Publish\`, each building into a `Staging` folder beside itself (ignored by git):
`Publish\CME\PublishAndDeploy.ps1` publishes the solution for Linux into `Publish\CME\Staging` and sends it to the CME box
(rsync plus a dated snapshot there); `Publish\RiskDesk\Publish.ps1` builds the exes once into
`Publish\RiskDesk\Staging\Build`, then one desk package per folder in `Publish\RiskDesk\Templates` (Adrian,
NewRelease, THREADRIPPER, ...), `Staging\BigajRiskDesk_<template>`, each zipped beside its folder, sending nothing
(2026-10-23; from 2026-10-20 two fixed flavour folders beside the script; before, one `BigajRiskDesk` package). A new
desk is a new template folder; the script needs no edit.

A desk gets the zip it is set up for. It unzips to a
`BigajRiskDesk_<template>` folder holding `Mirror.exe`, `Workspace.exe` (NativeAOT, no .NET install, built for `x86-64-v3` (AVX2):
the box's `x86-64-v4` build stops with an illegal instruction on most desk PCs), Avalonia's three native libraries,
`Start.bat`, the short guide, and everything in `Publish\RiskDesk\Templates\<template>\`: its `MirrorConfig.json` and
`WorkspaceConfig.json` and its `Data` folder (the server's folder, and any strategy's, with default layouts).
- **`Start.bat`**, the double-click: starts this folder's `Mirror.exe` unless one runs from this folder (matched by full
  path, so a Mirror run from elsewhere on the same PC does not count), with the folder as its working directory, so it
  reads `MirrorConfig.json` there; then starts `Workspace.exe`. Both can start at once: the Workspace waits for the
  Mirror's copy (`Context.EnsureConnected` retries every second). Closing the Workspace leaves the Mirror running; a
  second double-click starts another Workspace beside the first.
- **`WorkspaceConfig`** (`Workspace\WorkspaceConfig.cs`, static JSON beside the executable, read when `Workspace.exe` has
  no arguments): `ServerName`, `DirectoriesRoot` (as `MirrorConfig`'s, so `"Data"` on a desk; the two must agree, or the
  Workspace waits forever on a copy that does not exist, naming the path it waits for), and `Workspaces`, one window
  each: `StrategyName` (`""` a server workspace) and `WorkspaceName` (a saved layout, `""` a blank window). Always
  Realtime. With arguments the Workspace runs as before (one window from `-s -m -c -w -d`); both build the same list of
  `WorkspaceRequest`s, the first the main window. Every window, startup ones included, goes through one queue that
  `WorkspaceRunner.FlushQueuedWorkspaces` posts to Avalonia's loop. A window that cannot open (e.g. a strategy the server
  does not know) is reported on the console and skipped; the others open.
- **No kernel wait on a GUI thread.** `LowLatency.StartBackgroundThread` waits for its new thread to start by spinning on a
  flag (`Volatile.Read` + `Thread.Yield`), not on an event. Why: under NativeAOT a kernel wait on the UI thread (a COM STA
  thread) pumps window messages (`CoWaitForMultipleHandles`), so Avalonia re-enters and builds the next window inside the
  wait; that window's own start-up wait reuses the thread's single monitor waiter and can consume the outer wait's signal,
  which then sleeps forever although its thread is running. Seen 2026-10-19 on a desk with four windows: 2 of 7 AOT starts
  loaded every window (native stacks: the UI thread in `Workspace.Connect` → `StartBackgroundThread` →
  `ManualResetEventSlim.Wait` with all four client threads already looping), 10 of 10 with the spin; the JIT build never
  hung. `Workspace.Disconnect` waits for its client thread to end the same way (`Thread.Sleep(1)` polls, not `Join`), since
  a desk rebuilding after a server restart could otherwise start a second rebuild inside the first (2026-10-20).
- **`MirrorConfig.IsConsoleVisible`** (default true): false makes the Mirror leave its console (`Application.DetachConsole`:
  `FreeConsole`, which closes the window under Windows Terminal and the classic console alike, where hiding it does not)
  after the config checks, appending its output to `Mirror.log` beside the executable. It is then stopped from Task
  Manager.
- The templates carry the box's address and the server name, so a release changes them in one place. In JSON a backslash
  is doubled (`"C:\\Desk\\Data"`).

### Relay: a desk serves its copy onward (2026-10-27)

A Remote whose config sets `RelayListenPort` (with `RelayIPWhitelist`) also serves its copy to further Remotes that dial
that port: a phone, which cannot reach the box's network, dials the desk it can reach (over a personal tunnel that gives
it a fixed address to whitelist). `Program.RunRelay` runs a `LocalMirror` over the copy on its own thread: the same
class as beside the server, with the copy as its `ServerContext`, publishing every 100 ms, answering pings, serving the
desk's synced files. Each GUI a phone lists stands in as a `RemoteClient` of the relay desk (the class a desk Workspace
uses), attached to the relay's own ServerSocket under the phone GUI's name: the relay's `RemoteMirror` lists it to the
box as one of this desk's GUIs, the box starts the real stand-in `ManualClient`, and its id comes back through the usual
`AllocatedClientId` path. A phone's call travels phone `RemoteClient` → phone `RemoteMirror` → `Message` → relay
`LocalMirror.ApplyMessage` → relay `RemoteClient` → relay `RemoteMirror` → `Message` → box stand-in → server. Each hop
has one representative per GUI, as the box has.
- **To the box the phone is one more Workspace window on the relay desk**: the same session, the same 10 publishes a
  second, one more stand-in (what a second window costs). The box never knows the phone; nothing on it is configured.
  Declined (2026-10-27): the phone as a second session of the box through a port forward on the desk, which would have
  cost the box a session, a stand-in and a forward to an address it cannot reach, for no gain.
- **Why its own thread:** the Remote's listen thread answers a GUI's connect by waiting for the main thread to deliver
  the box's `AllocatedClientId`; a stand-in built on the main thread would wait on itself. The relay thread is to the
  main thread what a desk's Workspace process is to its Mirror.exe: it reads the copy with `TryCopy` and its stand-ins
  write their rings, which the main thread reads.
- **Server restart:** the relay's `RemoteMirror` wipes and refills the copy; at the new server's first heartbeat the copy's
  header carries the new process id, and the relay thread ends every phone's session (`LocalMirror.Dispose`: stand-ins
  closed, links dropped) and starts afresh with an empty published cache, so each phone redials and takes a full snapshot
  of the new server, header first, as a desk does when the Local Mirror beside a restarted server restarts. Until the
  relay's own first snapshot is complete (its `RemoteMirrorProcessId` letterbox posted) it serves nobody.
- **Relay restart:** the phones redial; their stand-ins restart on the box through the relay's next `GuiList`.
- **The `Latency` array never travels** (`LocalMirror.PublishRows` skips it): each end's row is its own link's. A phone's
  number is phone ↔ relay; the relay's link health shows on the relay's own Workspace.
- The folder sync chains: the relay serves what it has synced. The box's rule is unchanged: a desk's own `IsFolderSyncOn`.
- Residuals: a phone's call crosses two Mirror loops (about 2 ms more than a desk's); the relay thread blocks for a
  round trip to the box while it starts a phone GUI's stand-in (other phones' packets wait those milliseconds); GUI names
  stay unique per machine name, so a relay desk and its phones must have different names (a phone's is its device's).

### The phone: a desk in one process (2026-10-27)

The Android app (`Phone/`) is a remote desk whose Mirror and GUI share one process. Nothing in it is new to the box or the
relay: it dials a relay desk (see "Relay") exactly as a desk dials, and to the box it is one more GUI of that desk.
- **One process.** `PhoneView` starts the Remote end's loop (`MirrorRunner.RunRemote`, the code `Mirror.exe` runs on a
  desk, moved verbatim into a callable class) on a thread, with `Memory.IsInProcess = true`: every region (the copy of the
  server's arrays, the letterboxes, the GUI client's rings) is a plain zeroed block in process memory found by name, so
  `Context`, `SharedArray`, `LetterBox` and the sockets run unchanged, and no mapping, lock, `mlockall` or `/dev/shm`
  exists (Android has none). The GUI is the desk's: `ManualClient.Open` is answered `RemoteDesk` by the in-process Remote
  Mirror and opens a `RemoteClient`, and the widgets are the desk's over the same `WorkspaceContext`.
- **One view, one widget at a time** (a phone has no windows): the bar shows the clock and the link's latency (the desk's
  rule, `Widget.LatencyText`), a picker names the row-fed widgets (Orders, Positions, Instrument Headers, Risk Limits,
  Message Efficiency, Rate Limits; built by `Widgets.NewWidget`, the desk's table) and the Ladder, which picks its
  instrument as every ladder does (a picker in its header lists the primary client's instruments, refreshed when opened:
  `LadderWidget.FillInstrumentPicker`), and a server workspace is shown: every order and position. A ladder also opens from Instrument Headers' menu (a long press is the phone's right-click; the ladder's own menu answers
  the Holding gesture too) and Send Order from either, as on a desk: `PhoneView` is the `IWidgetHost` (`AddToHost` finds the nearest host above a widget, a desk's
  Window or the phone's view), and the opened widget replaces the one shown. The phone's orders are a desk GUI's: the box
  cancels them when its session ends (the link silent 10 s). Charts, Fills and the Audit Trail tail synced files; the phone
  syncs none.
- **The widget's title bar** (Positions' Resume/Pause All and total PnL) shows in a light strip under the green bar, on a
  desk title bar's background so its colours read as there; hidden for a widget with none.
- **Rotation** follows the phone (its auto-rotate setting): the activity handles orientation and size changes itself
  (`ConfigurationChanges`), so a turn only lays the view out again at the new size; the Mirror thread and the link carry on.
- **Connect off the UI thread.** The copy's header and the box's `AllocatedClientId` can take seconds over a mobile link,
  and Android stops an app whose UI thread blocks for 5 s. A server restart rebuilds the view as a desk Workspace does
  (`RemoteClient.IsSessionEnded`: renew the contexts, close the client, connect anew under the same GUI number).
- **Config built in** (2026-10-27): `Phone/MirrorConfig.json` is an Android asset of the APK, read at start, because every
  phone dials the one relay (the Threadripper, which is always on: `100.86.63.127:27794`) and Android gives a phone's owner
  no way into the app's folder without USB debugging. A phone is admitted by its Tailscale address in the THREADRIPPER
  template's `RelayIPWhitelist`, so a new phone needs no new APK, and a new relay address needs one for every phone. `Data`
  (`DirectoriesRoot`) is under the app's external files folder.
- **Names.** `Platform.Name` is the device model (Android reports "localhost" as the machine name), so the phone's GUIs
  are `<principal>_GUI<n>@<model>` (`Client.GetGuiName`), unique beside the relay desk's own.
- **Pace.** The Mirror and client loops pass every 20 ms (a desk's 1 ms would drain the battery); rows still arrive every
  100 ms. ARM64 runs none of the x86 intrinsics: `X86BaseWrapper.Pause` spins instead (the x64 code is unchanged,
  benchmarked), and `ProcessId.IsAlive` answers for this process without the OS. `Application` registers only `ProcessExit`:
  the phone has no terminal, and its runtime refuses the SIGHUP and Ctrl+C hooks (`PlatformNotSupportedException`).
- **Backgrounding.** Android freezes a backgrounded app: its link falls silent, the relay closes the phone GUI's stand-in
  after 10 s (cancelling its orders, as any GUI's), and on resume the app redials and takes a fresh snapshot. No foreground
  service in this version.
- **Build.** `Publish\RiskDesk\Publish.ps1` publishes the Release `Staging\HFT.apk` (trimmed, 48 MB) that every phone
  installs by tapping the file (once allowing "install unknown apps"); a newer one installs over it because every build signs
  with this PC's Android debug key (lose it and phones must uninstall first). `dotnet build Phone\Phone.csproj -f
  net10.0-android` builds a Debug one for adb. Both need the Android workload and SDK; the project is not in
  `HFT.slnx`, so the desktop builds need neither. It references every library explicitly without the Android runtime
  identifiers (`HFT.targets` makes each an executable, which a self-contained app would otherwise rebuild per identifier).
  The APK runs on Mono, not NativeAOT; `Widget` is rooted for the grids' sorting as on a desk. A box deploy that changes
  any shared array needs a new APK (the layout hash refuses a mismatch, as a desk's does).
- Not built: a settings page, folder sync, a foreground service, several widgets at once,
  an emulator path (a device over USB).

### Setting up a remote desk

- The usual way is the risk desk package above. By hand: ship the Build output folder (Mirror, Workspace and their libraries; Logging is not run there) with a `MirrorConfig.json`,
  and start `Mirror.exe` from that folder or with the file's absolute path:
  `{ "MirrorRole": "Remote", "ServerName": "<server leaf>", "ListenPort": 7400, "IPWhitelist": ["<CME box egress IP>"], "DirectoriesRoot": "Data" }`.
  Nothing lives outside that folder: `DirectoriesRoot` is relative to the executables, so no S: drive is needed. Keep the
  folder near the drive root: every name (folder included) must fit 256 characters, and the Mirror stops with a message
  naming the path that does not. A remote GUI's name also carries `@<machine>` (up to 15 characters on Windows).
  Workspace shortcut: `Workspace.exe -s <server leaf> -m Realtime [-c <strategy>] -d Data`.
- The CME box's config: `MirrorRole Local`, the same server name and mode, and each remote's address in
  `ConnectAddresses`. Whatever starts the Local Mirror (the C++ server's main) passes the file's absolute path, since its
  working directory may be anywhere. The CME deploy never ships a `MirrorConfig.json`; it would replace the box's.
- Reversed, when the networks make a desk dialling the box easier: the box's config has `"ListenPort": 7400,
  "IPWhitelist": ["<each risk desk's IP>"]` and no `ConnectAddresses`; each desk's has `"ConnectAddresses": ["<CME box
  IP>:7400"]` and no `ListenPort` or `IPWhitelist`. One box listener serves every desk (each link is its own session).
- A relay desk adds `"RelayListenPort": 27794, "RelayIPWhitelist": ["<each phone's tunnel address>"]` (the THREADRIPPER template,
  this PC's desk: Tailscale, the S22+ at 100.99.246.93); a phone's config is a desk's with `"ConnectAddresses":
  ["<relay desk's tunnel address>:27794"]` (the Threadripper is 100.86.63.127), and no folder sync.
- A listening end accepts only `IPWhitelist`, each entry one address or a CIDR range (`"10.10.0.0/16"` admits 10.10.*.*, for desks whose VPN address changes; keep the range as small as the VPN pool) (`TcpLinkConnector.AcceptTcpLinks` closes any other connection at once; `MirrorConfig.ThrowIfInvalid` refuses to start a listener with the list empty). Why: on a Remote any new link replaces the live one, and the Local end then closes that desk's stand-ins, so the server cancels every remote order; an internet scanner touching the port would do that several times a day. Firewall the port to the same address too: links are still plain TCP, unauthenticated and
  unencrypted for now.

### Residuals

- An order that reached the exchange just as the link died works unseen until the link is declared dead (up to 10 s),
  when the Local end closes its ManualClient and the server cancels it; the snapshot then shows its real end (Canceled, or
  Filled). A shorter silence limit would shrink the window at the cost of dropping links on ordinary Wi-Fi hiccups.
- A remote GUI name (`@<machine>`) can only be taken by another Workspace on the same desk, which its own Remote refuses
  at once (`AlreadyConnected`); the next number is tried, as on the CME box. Within 3 s of a refused stand-in start the
  Local end repeats that refusal without asking the server again.
- Right after a reconnect, a GUI's calls are dropped until the Local end has started its stand-in again (milliseconds, or
  seconds while the server finishes the old cancels; `NotReady` is retried). The latency is the link's, so it reads healthy
  meanwhile. Machine-unique names (`@<machine>`) remove the lasting case, a CME-box Workspace taking the name.
- The latency includes each end's polling: the Remote Mirror's 1 ms sleeps and the Local end's pass, so it reads a little
  above the click's own path (a loopback smoke run showed 30 to 50 ms). On Windows a sleep lasts a 15.6 ms timer tick
  unless the process asks for 1 ms (`LowLatency.RaiseTimerResolution`, called at Mirror startup), and Windows 11 drops
  that request after about 4 s for a process with no visible window of its own (a hidden desk Mirror) unless the process
  also opts out of timer power throttling (`SetProcessInformation`, `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION`).
  Measured 2026-10-22: `Sleep(1)` 1.99 ms, then 15.4 ms from 4 s on without the opt-out; about 2 ms for 20 s with it.
- A remote desk writes no audit and sends no alerts; its Audit Trail shows the box's files, copied by the folder sync. The audit is the stand-ins' on the CME
  box, and their rejects reach Telegram from the Local Mirror.
- `ManualClient.Open` on a remote desk first builds a plain ManualClient to hear `RemoteDesk`, which creates that GUI
  number's empty audit folder under the copy's root.
- Remote GUI names are unique per host name: two desks with the same machine name would share GUI names and ids.
- A remote GUI whose connect the Remote's own socket refuses after the CME box has answered (`DecideConnect`'s last checks:
  the same name reconnecting within the ~1 ms "Closing" window after a clean close, or an error building the socket's
  shared memory) stays in `_pendingClientIds`, because `ClientOpened` never fires: it is listed until the Remote restarts,
  so its stand-in on the CME box holds a client id meanwhile, and a retry under the same name waits 10 s for an answer the
  CME box does not send (it already serves that stand-in), gets `NotReady` and moves to the next number. Accepted
  (2026-10-12): no person reconnects within 1 ms and `ManualClient.Open` waits a second between tries, and a socket-build
  error means the desk's shared memory is failing anyway; nothing is sent wrongly. A GUI that dies while connecting is not
  this case: the Remote's listen thread opens its socket regardless (`OpenClient` needs nothing from the GUI) and
  `PollPids` then closes it as dead.
- Publishes have no minimum gap (declined 2026-10-12; superseded 2026-10-27 by the one 100 ms cadence, see "Rows down":
  the declined reasoning missed that each desk's pings added their own passes).
- The Local Mirror alerts every exception its loop catches, unthrottled: one that recurs every pass (1 ms) floods
  Telegram, and a name that does not fit 256 characters under the Local root alerts once per `GuiList` (each second).
- Links are not encrypted or authenticated yet.
- **Simulation, by design:** an algo restarted under the same name within one simulation run fails to start, because it
  deletes its audit files before connecting and that run's Logging still holds today's file open (`Persistance` keeps the
  tap). Algo audit files are never shared between runs and never multiplied. A GUI (`ManualClient`) never touches audit
  files, so GUIs restart freely within a run, and the mirror recreates a remote GUI's stand-in after a link drop; a GUI's
  file for one date is not cleared between runs that simulate that date. Realtime never deletes audit files.

## TickHistoryWriter crash safety (day-boundary resume only)

A `TickHistoryWriter` may only be stopped at a day boundary and resumed on a later day. **Stop/resume
within the same day is not safe**: same-day continuation appends into the last day's block by
overwriting the footer and trailing snapshot with update frames, and the first zstd flush destroys
the recovery point mid-frame.

Day-boundary resume, by contrast, is corruption-proof — including power loss — because every write
of the resume rollover lands on bytes that are already identical or equivalent on disk:

- **Previous day header rewrite** is byte-identical: its `PositionOfTomorrow` is set to the value it
  already has (no compression stream exists at resume, so no empty zstd frame shifts the position —
  that ~13-byte shift was the original corruption mechanism).
- **New day header over the footer** differs in one field (`ExchangeTimestamp`), a single-sector write;
  either version resumes correctly.
- **The rollover snapshot overwrites the trailing snapshot byte-identically**, so torn pages splice
  identical bytes into identical bytes. Identity holds because the encoded record is the same (same
  book, same delta baseline, and timestamps encode as zero deltas — `WriteSnapshot` stamps the exact
  timestamp it deltas against), and the compression is the same (fresh stream, same dictionary and
  level, and the load-bearing `Flush()` after `WriteSnapshot` closes the block at the same input
  boundary `Dispose` does). The frames diverge only at the old 3-byte end-of-frame marker, past the
  one record resume ever decodes.

Preconditions for the byte-identity — changing any of these silently reopens a torn-page window
under power loss: same ZstdSharp version and dictionary across sessions, same compression level, the
snapshot written as a single `Write`, no zstd checksum flag, and the `Flush()` directly after
`WriteSnapshot` in `WriteTomorrowHeader`.

Each day header's `PriorityId` is the MBO delta base the reader decodes that day from, and the writer
sets it: `WriteTomorrowHeader` writes one past the highest `Add` id written (`_nextPriorityId`), not the
id of the last order written. The footer is the last day header, so it carries the next id to issue, and
a converter resuming a file seeds its counter from it; a fresh file's footer is 0, so the first order is
id 0. `SetHeaders` restores `_nextPriorityId` from the footer, so the resume rollover rewrites the header
and snapshot from the same base: byte identity holds only while that restore sits directly after the
footer `Navigate`.

## Open items

- **House position persistence, write side:** position lines are written by the logging tap
  following each client's socket. Strategy 0 has no socket, so nothing writes its `.position` files
  yet; the restore path finds no file and starts a zeroed row.
- ~~**Multiple server workspaces collide**~~ — resolved 2026-10-07: each workspace takes the next free
  `<principal>_GUI<n>` name (see "Workspaces (GUIs)").
- **C++ mirror:** the union rule lives in `Server.OnAllocateInstrument` and must be mirrored in
  `Server.hpp` before live behaves like sim.
- **No `filled > worst` alarm:** a `Done` with `|QuantityFilled|` above the order's worst case
  would apply a negative release (a re-reservation) silently. Nothing checks for it yet; the
  per-order ledger replay from the audit is the only detection.
- **Init thread's first `OnInterject` is unguarded:** `ServerSimulator`'s Init thread calls
  `OnInterject(Timestamp.MinValue)` after the guarded admin drain, outside the Clock and outside any
  try/catch, so an exception from that one `ReadExecution` kills the process (see "Server read loops
  throw; the caller reports"). The C# code is frozen; the fix is to wrap it like the drain.
- ~~**GUI re-allocate re-runs the restart check**~~ — resolved 2026-10-05: the check moved to the
  client constructor (see "Client restart"), so a re-allocate only reseeds `WorkingRisk`.
- **Remote links: encryption and authentication** (pinned certificates were designed, then deferred).
- **An on-screen notice in the Workspace when an order is rejected** (today only the Audit Trail and
  Telegram show it).
- **Server idle-loop prefetch of hot lines** (cuts the mirror's per-pass recall cost, and the recall
  a writer pays when it comes round to a line Logging read on the previous lap). Logging's poll is
  backed off to one pass per 100 ms live (2026-10-13, "Logging polls every 100 ms live"); measure
  what is left before considering a per-ring letterbox.
- **Not planned:** a workspace registering a strategy whose algo has not connected (the server
  workspace covers emergencies).
- ~~**TCP mirror dispatches header-less rows by their first byte (latent)**~~ — resolved 2026-10-07:
  `TCPServer.cs` is deleted; the Mirror dispatches rows by array id and type. Former text: `TCPServer.ReadTCP`
  switches on the first byte of every mirrored row as if it were an `OrderType`/`AllocateType`.
  `OrderRisk` (array id 12, the server's copy) has no `Header`: its first byte is the low byte of
  `_activeTargetsCount`. Today no `OrderRisk` row is shipped: the rows are written by plain ref and
  never seq-bumped, so they read `Empty` (seq 0) and the mirror enumerator skips them, snapshots
  included. If they ever were seq-bumped, a count of 10..14 (possible with `MaxActiveTargets = 29`)
  would hit the `OrderState`/`OrderTarget`/`OrderRejected`/`Fill`/`Position` cases (12 and 13
  forwarded to a client instead of mirrored, 11 mirrored conditionally); 15..17 have no case, reach
  `default:` and mirror correctly. Every mirrored array without a `Header` has the same exposure.
  Predates this work. Keep `OrderRisk` writes plain, or fix on both the C# and C++ ends: dispatch by
  array id (or give `OrderRisk` a header); a C++ mirror dispatcher must not header-dispatch
  `OrderRisk` rows.

### Accepted residuals and declined changes

Each of these was seen in the harness or reasoned through and left as is on purpose.

- **Session-close race (closed 2026-10-21).** Orders in flight when the session closes come back
  `NotInSessionInFlight`, a discarded reason, and do not pause the algo; see "An order in flight
  at a status change". Left: an exchange refusal that reaches the server before the status does.
- **Risk limit lowered mid-flight.** A limit lowered between the client's check and the server's
  read of the order refuses it at the server and pauses the algo. A fix (a discarded
  `RiskLimitChanged` reason) was declined.
- **Manual orders use room the algo cannot see.** See "Manual orders": the algo may be refused and
  paused; that is the user's responsibility. Only the manual order's own refusal is kept from
  pausing the algo.
- **The server rate limit is invisible to `TryTarget`.** A create or replace over the limit is
  refused with `TooManyOrdersPerSecond`, which is discarded: dropped silently, no pause, and
  `TryTarget` still returned true for it.
- ~~**Startup window.**~~ Resolved 2026-10-07: the server admits a client only once every previous
  order's state row is Done, and `Recover` runs milliseconds later (see "Client restart"). Former
  text: A previous order's final fill that lands in the microseconds between the
  client connecting and its restart check passes the check (the order is now `Done`) and can be
  counted twice in the client's `WorkingRisk.Position`: once in the seeded position row, once when
  the new process reads the forwarded `Fill`, which also releases a reservation this process never
  made.
- **Near-vs-far room priority (harness C25).** Phase 7 creates are sent before Phase 6's delayed
  aggressive reprices, so in `TryTarget` a new far level can take room the near, more aggressive
  level wanted, and the reprice is clipped at Phase 6. Declined, together with the multi-order
  over-cut (several orders each cut by the whole overshoot, see "The position check, and the
  within-limit clip"): a strategy that targets more than its limits is the bigger problem, and the
  fix belongs there.
- **CME confirmations pending** (the user is asking CME):
  1. Resolved 2026-10-05, see "A refused Create: Done first, then the reject".
  2. The duplicate-fill drop's two assumptions: in-order fills per order, and cumulative `CumQty`
     on every fill event (see "Duplicate fills are dropped by cumulative quantity").
  3. An amend acknowledged only inside a fill does not occur: CME (as we understand it) and the
     simulator send `OrderState(Acked)` then the fill. `RiskLayer` retires amends only on `Acked`;
     a violation over-reserves until `Done` rather than leaking (see "Acceptance before trade").

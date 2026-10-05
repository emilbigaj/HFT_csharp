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
  the server's leaf name — `AllocateClientId` throws on the collision. (`<serverName>_GUI`
  workspaces are unaffected; only an exact leaf match is reserved.)

## Workspaces (GUIs)

- A **server workspace** sends every OrderTarget with `StrategyId = 0`. Any number may be open;
  they all point at the house book. It sees ALL orders, ALL positions, ALL risk limits across every
  allocated instrument of every strategy, and its position view is the sum over all strategies.
  It can cancel any client's orders and amend any manual (non-algo) order (global order-id
  addressing); its creates always book to strategy 0. An algo's orders are cancel-only from a GUI
  (see "Manual orders" below).
- A **strategy workspace** shadows one algo client. Its ManualClient has its own client id — its
  order targets allocate in its own id space — and books manual orders to the principal strategy.
- Allocating an instrument from a GUI (InstrumentHeaders right-click) allocates it to the GUI
  client, which is what makes the server open the instrument's data feed; by the union invariant it
  also provisions strategy 0.

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

Risk limits are server-wide: one `RiskLimit` row per instrument, applied to every strategy that
trades it. `StrategyId` was removed from the row and the request on 2026-09-10 — it was never
enforced or restored per strategy and only chose where an echo went; a per-strategy limit is a
future feature, not a routing choice.

A `RiskLimit` row (24 bytes) is operator config only: `MaxOrderQuantity`, `MaxPositionQuantity`
and the edit `Timestamp`. The risk layer's live numbers are on a separate `WorkingRisk` row (see
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
after every batch it drains, so the window is one poll pass. A `.risklimit` line now has no
`WorstLong/ShortWorkingQuantity` properties; older lines that carry them (or the removed
`StrategyId`) still parse, because unknown JSON members are skipped.

Lowering a limit takes effect on the next check, on both sides. Past the limit `ValidateOrder`
still lets an order keep its worst (a cut always passes), so working orders are never forced off by
an edit; `TryTarget` cuts an order back within the new limit the next time it reduces or reprices
it (an order the zipper keeps as is is never clipped). Both copies read the one server-wide
`RiskLimit` row, so the client sees an edit as soon as the server does. A limit lowered between
a client's check and the server's read of the order can refuse that order at the server and pause
the algo (accepted; see Accepted residuals).

`ControlAlgoStatus` follows the same rule: it travels on the instrument's execution channel and
`ReadExecution` applies it, so the local position row's status has the same single writer as its
fills. The admin thread's only remaining row writes are allocation. One consequence for GUIs: the
server polls a client's execution channel only for CoreGroups that client has allocated in, so a
workspace allocates the instrument to its manual client before sending a control for it.

## Risk layer: one code, a server copy and a client copy

### Why the client keeps its own copy

`RiskLayer` is built on the base `Context` and runs the same code in two places: on the server over
the `ServerContext` (source `Server`, every order on the instrument) and in every client over that
client's own `ClientContext` (source `Client`, only its own orders). Each side keeps its own
`OrderRisks` and `WorkingRisks` rows under its own directory; `RiskLimits`, `OrderStates`,
`OrderTargets` and the position rows stay the one server-wide set.

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

`RiskLimit` (24 bytes: `Header`, `InstrumentId`, `Timestamp`, `MaxOrderQuantity`,
`MaxPositionQuantity`) is configuration, written only by operator edits. The live numbers are a
separate row, `WorkingRisk` (16 bytes: `Header` with `OrderType.WorkingRisk = 17` @0, `Position` @4,
`WorstLongWorkingQuantity` @8, >= 0, `WorstShortWorkingQuantity` @12, <= 0), one per instrument per
context, never persisted. The split is what lets the same `RiskLayer` run on both sides (each side
has its own `WorkingRisk`, while both read the same `RiskLimit`), and it means an edit cannot rewind
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
on every process start (see "Client restart"). After that `Position` moves only in
`RiskLayer.OnFill`, for every fill, a legged instrument's own fill included (accounting only); only
an outright fill of an order this `RiskLayer` reserved releases a reservation. On a client that is
`isReserved = (fill's ClientId == own ClientId)`: a fill of a manual order booked to the strategy
moves the position but releases nothing, because this client never reserved it.

Every `WorkingRisk` write is seq-bumped (`AcquireLock`/`ReleaseLock`; single writer, so two
volatile stores), so `Read()` and the TCP mirror see untorn rows. `OrderRisk` rows are still written
by plain ref. The `WorkingRisks` array is created last among the base arrays, so the array ids
before it keep their mirror order; the one cost is that the server-only `ServerPositionHeaders`
moved from id 15 to 16.

Reservations are per leg (an outright is its own single leg, weight +1).
`ApplyWorstWorkingQuantityDelta` applies an order-unit delta to each leg as `legSide = orderSide ×
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
`GetAbsAllowedOrderQuantity(target, isWithinLimit)` is the largest `|order quantity|` (filled
included) the order may carry. For each leg, with `room = MaxPositionQuantity - Position -
WorstLong` on the long side and `MaxPositionQuantity + Position + WorstShort` on the short side
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
  quantity at that price as `QuantityAhead` and 0 behind, as the server's PendingNew seed does (until
  2026-10-04 it reported `int.MaxValue`). The two can differ by market data in flight between the
  server's book and the client's. `QuantityAhead` and `QuantityBehind` are one queue observation:
  `Server.OnQuantityAhead` writes them with one 64-bit store and no lock, and
  `Position.ActiveTargetsEnumerator` reads them with one 64-bit load, so a reader never pairs a new
  ahead with an old behind. They must stay adjacent and 8-aligned at `OrderState` @56/@60.
- **Validation:** every check that applied to Amend applies to Replace and Reduce alike (header,
  `TargetIsActive`, `SideNotValid`).

The simulator, like CME, decides reduce versus reprice from the price and quantity change itself,
not from the action byte. An exchange adapter maps Reduce to the venue's quantity-down modify.

### Client restart: refuse while the previous process has Active orders

On every instrument allocation the client scans its own 64 order slots. If any slot's `OrderState`
row is this instrument and `Active`, it throws `InvalidOperationException` and the process does not
start: "start again once the server has cancelled it". A previous process's Active order would hold
room, a slot and future fills this process never made, and it can still be live after a restart:
the restart was faster than the server's cancel-on-close round trip, iLink was down, or the session
is in a no-cancel phase. For every slot on the instrument whose last order is `Done` it clears the
client's `OrderRisk` row, so the new process inherits nothing (a Create resets the row anyway; the
clear makes the starting state explicit). Then it rewrites `WorkingRisk { Position = own strategy
position row, reservations 0 }`; with no Active orders zero reservations is exact. The region can
outlive a process (the GUI maps it), so it is rewritten on every start rather than trusted.

The scan reads only the `OrderState` rows. A previous process's Create the server has not read yet
(its state row still holds an older, Done order) is not detected. The check runs for a
`ManualClient` too, so a GUI restarted before the server cancels its previous manual orders refuses
to open.

It also fires inside a running GUI. A `ManualClient` never opens a data ring (its
`OpenInstrumentDataSocket` is a no-op), so `_instrumentData[instrumentId]` stays null and
`GetInstrument`'s early return never fires: every allocate request from a GUI re-runs
`OnInstrumentAllocated` — the scan, the `OrderRisk` clear and the `WorkingRisk` rewrite. A GUI that
already has a live manual order of its own on the instrument therefore throws
`InvalidOperationException` on a re-allocate (on the owner thread; the AlertManager reports it). The
Positions and RiskLimits widgets allocate only when `Context.InstrumentIds` says the GUI is not yet
allocated, so they avoid it; the InstrumentHeaders grid's Allocate menu does not check. Known; listed
under Open items.

## Algo: Target and TryTarget

### Strict Target fails loudly; TryTarget never sends a refusal it can see

`Algo` is a convenience class with two entry points over one implementation:

- **`Target(ref targets)`** — strict. It sends exactly what was asked (after the zipper) and skips
  the session, slot and risk checks in `Send`. Anything the client's own `ValidateOrder`, the server
  or the exchange refuses comes back as a reject, and a reject that is not discarded pauses the algo,
  so a strategy bug fails loudly. Existing strategies call this.
- **`TryTarget(ref targets) -> bool`** — best effort. It never sends anything it can see would be
  refused: while the instrument's `TradingStatus` is not `Open` it sends nothing at all, cancels
  included (outside the session a venue may refuse anything, and the simulator cancels every order
  at a close anyway); a Create needs a free client order slot (`Client.HasFreeOrderSlot`); every
  non-cancel is clipped by `TryClipToRiskLimit` to the biggest size within the limits, or dropped
  when nothing workable fits.

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
  1,000,000, as `Server.CancelAllOrders` does, so it never collides with the algo's own next seq; any
  action on a manual order (amend or cancel) is existing seq + 1; the caller's seq wins only if
  larger. The widgets used four ad-hoc offsets (+1,000,000 on the ladder's cancel, +10,000 + age in
  seconds on the orders grid's cancel, +1000 on the send-order cancel, +1 on the send-order amend)
  and no longer add any.

  What makes the jump legal, and what follows it. The server's seq check compares a target only with
  the owner's target row (the shared `OrderTargets` row the owning client writes before it sends) and
  refuses only a lower seq (`TargetIsStale`); there is no `seq == previous + 1` rule, so gaps are
  legal (`Server.CancelAllOrders` relies on this too). The GUI never writes an algo's target row, so
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
reservation while the state row still says PendingNew, so for a tick `ActiveTargets` still shows an
order that is dead and the algo sees room it cannot account for. With the Done first, one message
releases the reservation and hides the order together, and the reject after it only explains why:
the same shape as a fill, where the state carries the risk change and the event explains it.

So the server owns it. `Server.OnOrderRejected` is the exchange's (adapter's) entry point for a
refused target, and for a refused Create it publishes a `Done`/`Rejected` state through
`OnOrderState` before the reject: `WriteOrderState` merges it into the PendingNew row (keeping
`TimeInForce` and the queue fields), `RiskLayer.OnOrderState` releases everything the order holds,
and the reject that follows finds nothing left to remove. Adapters send only the reject; the
simulator's `OnExchangeOrderRejected` now does exactly that. An adapter that still sends its own
Done afterwards is harmless (`WriteOrderState` ignores a second Done, so nothing is released twice)
but the duplicate is forwarded to the client, so the C++ router should stop sending it. The
synthesis sits after the `OrderNotFound` → `StateIsDone` mapping and inside the same-order check,
so the mapping sees the row's real status and a reject for a slot reused by another order writes
nothing. A refused Replace or Cancel gets no state at all: the order is still working, unchanged.

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
changes after it is written, and header slots are never reused. The GUI's `SymbolCache` (symbology
strings computed once per headerId for the process lifetime) depends on this; if slot reuse or
identity rewrites are ever introduced, the cache needs a generation check. Mutable header fields
(`TradingStatus`, `TickSize`, `InstrumentId`) are outside the cache and always read live.

## Session state is the exchange's TradingStatus

Whether an instrument is trading is what the exchange says, not what a timetable predicts. The
instrument header's `TradingStatus` byte is the only session state: `RiskLayer.ValidateInstrument`
rejects a create with `NotInSession` unless it is `Open`, and `Instrument.TryGetQuote` and the
position's quote return nothing unless it is `Open`. There is no `SessionManager` on the
instrument any more. Unknown, Closed, Auction and Halted all count as not open, so an instrument
whose first status has not arrived yet cannot trade and has no quote; a live server must publish
status from the snapshot at startup. Amends and cancels are not session-checked, as before.

The status reaches every process the same way live and in simulation: `Server.OnTradingStatusUpdate`
writes the header byte and broadcasts a `TradingStatusUpdate` on the instrument ring, and the client
raises `Instrument.TradingStatusUpdateEvent` on a change. Message efficiency ends its day on that
event's `Closed`, converted to local time with `Session.CME`.

The simulator has no exchange feed for status, so it keeps timetables at the exchange:
`ServerSimulator.SessionManagerByExchange` holds one `SessionManager` per exchange (XCME, XCBT, ...),
created from the first allocated instrument's `Sessions[0]`. Each allocated `InstrumentSimulator`
subscribes to its exchange's manager and, on every open or close, sets its own `TradingStatus` at
once (a close cancels every order and clears the queues first) and sends a `TradingStatusUpdate`
through the exchange-to-NIC latency queue, so the server hears it when a real one would arrive.
The simulator produces Open and Closed only; auctions are not modelled.

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

Consequences kept deliberately:

- **Write gate:** `Open || Detached || (Persistance && Closing)`. Without the third term, the ≤1ms
  `Closing` window silently drops server→client writes — a fill landing there would vanish from the
  very ring persistence exists to preserve. Non-persist keeps dropping there, matching the old
  semantics.
- **`PollLetterBox` refuses `Closing` like `Closed`** ("try again in a moment"): an unhandled
  status falls through into `OpenClient`, which would silently swallow the close in flight.
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
- **Multiple server workspaces collide** on the client name `<serverName>_GUI` (same shared-memory
  region). Deferred deliberately.
- **C++ mirror:** the union rule lives in `Server.OnAllocateInstrument` and must be mirrored in
  `Server.hpp` before live behaves like sim.
- **No `filled > worst` alarm:** a `Done` with `|QuantityFilled|` above the order's worst case
  would apply a negative release (a re-reservation) silently. Nothing checks for it yet; the
  per-order ledger replay from the audit is the only detection.
- **Init thread's first `OnInterject` is unguarded:** `ServerSimulator`'s Init thread calls
  `OnInterject(Timestamp.MinValue)` after the guarded admin drain, outside the Clock and outside any
  try/catch, so an exception from that one `ReadExecution` kills the process (see "Server read loops
  throw; the caller reports"). The C# code is frozen; the fix is to wrap it like the drain.
- **GUI re-allocate re-runs the restart check:** a `ManualClient` never opens a data ring, so every
  allocate request re-runs `OnInstrumentAllocated`, and a GUI with its own live manual order on the
  instrument throws on a re-allocate from the InstrumentHeaders Allocate menu (see "Client restart").
  Known; whether to accept it or guard the menu is the user's call.
- **TCP mirror dispatches header-less rows by their first byte (latent):** `TCPServer.ReadTCP`
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

- **Session-close race.** Orders in flight when the session closes come back `NotInSession` and
  pause the algo, under `TryTarget` too: it saw `Open` when it sent them. Accepted.
- **Risk limit lowered mid-flight.** A limit lowered between the client's check and the server's
  read of the order refuses it at the server and pauses the algo. A fix (a discarded
  `RiskLimitChanged` reason) was declined.
- **Manual orders use room the algo cannot see.** See "Manual orders": the algo may be refused and
  paused; that is the user's responsibility. Only the manual order's own refusal is kept from
  pausing the algo.
- **The server rate limit is invisible to `TryTarget`.** A create or replace over the limit is
  refused with `TooManyOrdersPerSecond`, which is discarded: dropped silently, no pause, and
  `TryTarget` still returned true for it.
- **Startup window.** A previous order's final fill that lands in the microseconds between the
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

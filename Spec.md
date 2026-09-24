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
  It can amend and cancel any client's orders (global order-id addressing); its creates always book
  to strategy 0.
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
cancel/replace). Behind those two sits the invariant alarm: anything that still produces
`filled > worst` at retirement halts the instrument loudly.

`Target()` falls back to snapshotting on entry for un-migrated callers — era-unsafe but identical
to pre-snapshot behaviour — and consumes the snapshot, so a stale list can never be zippered twice.

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

**Rule: a side with an unconfirmed cancel takes no new orders.** The enumerator keeps hiding the
order but reports *why* through `IsPendingBuyCancel` / `IsPendingSellCancel` (side from the acked
profile, which is never zero for an acked order). `SnapshotActives()` captures the flags in the
same pass as the actives, under the era rule, and Phase 7's per-tick lock is seeded from them. The
lock releases on the same `Done` that releases the server's reservation, so client and server agree
on capacity by construction. A rejected cancel flips `targetRejected`, the order reappears as
active, and Phase 5 re-cancels it — the lock is set in that tick as before.

Known limit: the lock covers new orders only. An amend-up of a *live* same-side order while
another order's cancel is pending is still double-counted at the server. `Make` cannot produce it
(one target per side); closing it needs the pending quantities, which the enumerator could report
the same way.

Related: the server's `TargetIsActive` no-op check is Amend-only. A Cancel is built as
`working + filled` at the acked price, i.e. it always equals the acked profile, and with the check
applied to cancels every first cancel of an acked order was refused (69 of 69 on 2026-09-08) and
only the client's Seq+2 retry got through — one round trip later, on the next quote.

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
acknowledgement. CME guarantees this on iLink 3, the New or Modify execution report always precedes
the trade reports, and the simulator honours it in `Enqueue`, which sends the `Acked` state before
`Take` trades. Any adapter that sits between a venue and the server must preserve this order and
must never coalesce an acceptance into a fill or a cancel; if a venue ever does, the adapter
synthesises the acceptance, the risk layer is not the place to cope.

The risk layer's arithmetic is built on the contract and is deliberately not defensive about it.
`OnOrderState` has two branches. On `Acked` it retires the pending target and releases the change in
worst case from the previously acked quantity to the new one. On `Done` it releases what remains,
measured from the acked quantity in the message. If a fill ever carried a quantity the server had not
seen acknowledged, the drop from the old quantity would never be released and the `Done` release
would be measured from the new, smaller one, and the difference would sit in
`WorstLong/ShortWorkingQuantity` for good.

That is not hypothetical. Until 2026-09-22 the simulator matched first and acknowledged only the
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

Per order slot the server reserves the largest quantity that could end up working: the acked
quantity or any unacked amend, whichever is bigger (`OrderRisk.GetAbsWorstOrderQuantity`). Amends
are pipelined, so the unacked set is a multiset — the same quantity at two prices is two entries
and one ack retires one of them. Until 2026-09-09 that multiset was a `Bitset64` over quantities
plus 56 byte counters, which is the only reason order quantity was capped at 55: the cap was the
64-byte budget, not a risk decision.

It is now a count, a cached max and 30 `ushort` entries in the same 64 bytes. Add appends and
raises the max; remove scans for one matching entry, swap-removes it, and rescans for the max only
when the max itself left. The scan is fine because the in-flight count on one order is one to
three in practice (a same-profile amend is refused while one is active, nothing is sent while a
cancel is pending) and the worst case is a 30-entry scan. Measured against the bitset over 1M
order lifecycles it is within 1 ns per lifecycle. Every SIMD layout tried was 2× slower: a 2-byte
lane store followed by the 64-byte reload `RiskLayer` makes right after every `TryAdd`/`Ack`
defeats store forwarding, and the horizontal max ran on every query. A pessimistic high-water mark
is faster still but over-reserves during pipelined amend-downs and collapses on a stray ack — the
multiset's tolerance for an ack it never reserved is deliberate and kept.

Limits: quantity 1..65535 (`QuantityNotValid` outside), 30 in-flight targets per order
(`TooManyActiveTargets` on the 31st, discarded silently as before, the algo retries next tick).
`RiskLimit.MaxOrderQuantity` remains the operative per-order bound; `Algo.NewAmend` still clamps to
`OrderRisk.MaxOrderQuantity`. The differential test that validated the struct (400k random ops
against a plain list) is specified in `cpp_alignment_report_2026-09-10_orderrisk.md` §6.

### Order rate limit: the one limit whose default is the exchange's own number

CME Globex throttles order entry per session and publishes two lines: messages other than cancels
are rejected past the first and the session is terminated past the second, at 500 and 750. Cancels
are counted separately with their own, higher, pair. We deliberately do not split the buckets: one
combined window sized at the tighter line can never breach either. A cancel is counted in that
window but never refused by it (`SendOrder` rather than `TrySendOrder`): it is the message that
reduces risk, and a throttle that holds one back turns a pause into a book of orders nobody is
watching, which is what the first test on a new CME release did with the limit set to 10. So what
the combined window costs is create and amend throughput while cancels are flying, which is the
right thing to give up.

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
production server nobody configured refuses every create and amend with `TooManyOrdersPerSecond`,
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
into the row when it loads the CoreGroup; the risk layer takes the row by ref with no seq bump, exactly
as it writes the risk-limit aggregates. Count is not
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

A `RiskLimit` row mixes operator config (`MaxOrderQuantity`, `MaxPositionQuantity`)
with server state (`WorstLong/ShortWorkingQuantity`, `Timestamp`). Until 2026-09-10 the GUI edited
by sending the whole row back on the admin channel, and the admin thread wrote it whole while the
CoreGroup thread was reserving and releasing in the same row; copying the live aggregates across
before the write only shrank the window in which an edit rewound a reservation.

**Rule: a client sends `ControlRiskLimit` (config fields only) on the instrument's execution
channel, and only the CoreGroup thread writes the row.** `ReadExecution` applies the request in
place under the row's seq bump and stamps `Timestamp`; the aggregates are written by nothing but
the risk layer on that same thread. The server never writes `.risklimit` files: it posts the row to
its audit channel and the logging server appends it through the same path helper the server reads
back at allocation. A crash between the post and the append can lose that one line — the row in
shared memory is the truth while the server is up, and the logging server flushes its writers
after every batch it drains, so the window is one poll pass.

`ControlAlgoStatus` follows the same rule: it travels on the instrument's execution channel and
`ReadExecution` applies it, so the local position row's status has the same single writer as its
fills. The admin thread's only remaining row writes are allocation. One consequence for GUIs: the
server polls a client's execution channel only for CoreGroups that client has allocated in, so a
workspace allocates the instrument to its manual client before sending a control for it.

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

## Open items

- **House position persistence, write side:** position lines are written by the logging tap
  following each client's socket. Strategy 0 has no socket, so nothing writes its `.position` files
  yet; the restore path finds no file and starts a zeroed row.
- **Multiple server workspaces collide** on the client name `<serverName>_GUI` (same shared-memory
  region). Deferred deliberately.
- **C++ mirror:** the union rule lives in `Server.OnAllocateInstrument` and must be mirrored in
  `Server.hpp` before live behaves like sim.

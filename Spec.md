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

### An order state that carries a new quantity is an acknowledgement, whatever its reason says

The server reserves each order's worst case, the larger of its acked quantity and any unacked
target, and releases the difference when the exchange acknowledges a target. Until 2026-09-22 that
release ran only on a state whose reason was `Acked`. But an amend is not always acknowledged on its
own: when the amended order is matched the instant the amend lands, the exchange reports the new
quantity and the fill in one message, reason `Fill`, and the same can happen with a cancel. The
`Acked` branch never ran, the drop from the old quantity to the new one was never released, and the
`Done` branch then measured the order's worst case against the new, smaller quantity in the message
rather than the old one the aggregate was holding. Each such order leaked the difference into
`WorstLong/ShortWorkingQuantity` for good. `Make`'s ladder produced it on twelve of 1.3 million
orders in one simulated day, enough to leave 10 long and 20 short reserved with nothing working.

**Rule: `OnOrderState` reconciles first, then releases.** Any state whose quantity differs from the
previously acked quantity is treated as the acknowledgement of that target and released through the
same arithmetic as an `Acked` state, and only then does a `Done` release the remainder. The two steps
telescope even when the acknowledged quantity matches no pending target: reconcile applies the change
in worst case, `Done` releases the rest, and the sum is exactly what was held. The rule was checked by
replaying the server's ledger per order from the day's audit: twelve residuals under the old rule,
none under this one.

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
combined window sized at the tighter line can never breach either, and what it costs is cancel
throughput, which is the right thing to give up.

CME's page does not settle whether the window is one second or three. The application section reads
like a count within an interval, while the mass quote and admin sections say MPS. Three seconds is
the stricter of the two readings, so `RateLimit.CMEOrderEntry` uses it: 400 in 3 seconds, under the
reject line rather than on it. That is safe under either reading, and only loosening it needs an
answer from the GCC.

Unlike every other limit in the system, the default is neither permissive nor zero. `RiskLimit` and
`MessageEfficiency` default to unlimited in simulation and zero in realtime, because an unset
quantity limit should refuse to trade. A rate limit cannot work that way: zero blocks everything and
unlimited protects nothing, so the default is the real exchange number and a live session is
protected before anyone configures it. `RiskLayer` builds one rolling window per CoreGroup, since a
CoreGroup maps to an iLink session and that is the scope CME throttles.

The window is `RollingRateLimit`, 64 bytes, so that it can sit in a shared array and the GUI can show
CoreGroup, Duration, Limit and Count from another process. The id is `RateLimit.RateLimitId`, generic
on purpose: the risk layer happens to key it by CoreGroup, the struct does not know that. The array is
`Context._rateLimits`, one row per CoreGroup, server-written like `_riskLimits` and reached through
`GetRateLimit`; it is named for the rate limit rather than the rolling model because another model
could occupy the same 64-byte row later, cast by the caller. The server writes the CME default into
every CoreGroup's row at construction; the risk layer takes the row by ref with no seq bump, exactly
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

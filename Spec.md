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

The request is a CAS against the **snapshot the close was read under** (`Tools/AtomicTransition`:
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

## Open items

- **House position persistence, write side:** position lines are written by the logging tap
  following each client's socket. Strategy 0 has no socket, so nothing writes its `.position` files
  yet; the restore path finds no file and starts a zeroed row.
- **Multiple server workspaces collide** on the client name `<serverName>_GUI` (same shared-memory
  region). Deferred deliberately.
- **C++ mirror:** the union rule lives in `Server.OnAllocateInstrument` and must be mirrored in
  `Server.hpp` before live behaves like sim.

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

## Open items

- **House position persistence, write side:** position lines are written by the logging tap
  following each client's socket. Strategy 0 has no socket, so nothing writes its `.position` files
  yet; the restore path finds no file and starts a zeroed row.
- **Multiple server workspaces collide** on the client name `<serverName>_GUI` (same shared-memory
  region). Deferred deliberately.
- **C++ mirror:** the union rule lives in `Server.OnAllocateInstrument` and must be mirrored in
  `Server.hpp` before live behaves like sim.

# Patch log

Newest first. Each entry says what changed, why, and what it broke or unblocked.

---

## 2026-10-20 — Names are 256 bytes; IPWhitelist ranges; Disconnect polls instead of Join

- `Socket/Socket.cs`, `Provider/Allocate.cs`, `Provider/Context.cs`, `Provider/Server.cs`, `Simulator/ServerSimulator.cs`,
  `Mirror/MirrorPacket.cs`, `Mirror/LocalMirror.cs`, `Mirror/RemoteMirror.cs` — `SocketHeader.ServerName/ClientName`,
  `ServerHeader.ServerName`, `AllocateClient.ClientName` and `MirrorGui.ClientName` are `String256` (were `String128`);
  `ServerHeader` is 305 bytes (`Persistance` @300, `ServerProcessId` @301). Why: a name is the full folder path, and a
  desk's install folder is part of every name it sends, so 128 ran out on ordinary desk folders. C++: P30, MUST PORT,
  deploy in lockstep with fresh shared memory (the Mirror's layout hash changes).
- `Mirror/MirrorConfig.cs`, `Mirror/TcpLink.cs` — an `IPWhitelist` entry may be a CIDR range (`"10.10.0.0/16"` admits
  10.10.*.*), for desks on a VPN whose addresses change; a plain address still means exactly that address.
- `Workspace/Workspace.axaml.cs` — `Disconnect` waits for its client thread with `Thread.Sleep(1)` polls instead of
  `Join`: the same STA message pumping as the start-up freeze, here letting another window's refresh start a second
  rebuild inside the first after a server restart.
- Verified: whole solution builds; a scratch check of the whitelist parser (exact address, /16, /24, /8, and refusals of
  `10.10.*`, `/33`, empty, text); an end-to-end run (simulated server, Local and Remote Mirror on loopback, desk Workspace
  with two windows) with ~185-character names and a `127.0.0.0/8` whitelist: both windows connected and loaded. The
  `Disconnect` path (a server restart) was not exercised.
- Guides: the folder-path limit reads 256.

---

## 2026-10-19 — Desk Workspace froze with several windows (NativeAOT): no kernel wait in StartBackgroundThread

- `Tools/LowLatency.cs` — `StartBackgroundThread` waits for its thread to start by spinning on a flag instead of a
  `ManualResetEventSlim`. Why: a desk `WorkspaceConfig` with several windows froze the NativeAOT `Workspace.exe` at random.
  Native stacks (scratch dbghelp walker against the AOT PDB) showed the UI thread in `Workspace.Connect` →
  `StartBackgroundThread` → `ManualResetEventSlim.Wait` → `CoWaitForMultipleHandles`, with every client thread already
  running: a kernel wait on the STA UI thread pumps messages under NativeAOT, Avalonia re-entered and built the next
  window inside the wait, and the nested wait consumed the outer one's wake-up. 2 of 7 AOT starts loaded every window
  before, 10 of 10 after; the JIT build never hung. Every caller keeps "running and pinned on return".
- `Workspace/WorkspaceRunner.cs` — startup requests go into the same queue as later ones (`s_startupRequests` is gone), and
  a window that cannot open (a strategy unknown to the server) is printed and skipped instead of ending the Workspace.
  An earlier entry today blamed Avalonia's XAML load before the loop for the freeze; that was wrong, and posting the
  windows alone did not fix it.
- Open: `Workspace.Disconnect` joins its client thread on the UI thread (a desk rebuild), the same kind of wait.
  C++: nothing to port.

---

## 2026-10-19 — Recorded trading status: plumbing (applying it pending)

- `Data/Tick.cs` — `TickTypeExtensions.IsTick()`: the "one 64-byte Tick record" decision, written five times as
  `Trade || Settlement` (TickHistory decode, both TickQueue paths, DataSimulator), now in one place and including
  `TradingStatus`; `Tick.SizeOf` knows `TradingStatusUpdate`.
- `Data/TickHistory.cs` — `WriteTick`/`ReadTick` store Settlement and TradingStatus header-only in one shared case;
  `DeltaSettlement`/`RestoreSettlement` (one-line wrappers) are gone.
- `Simulator/ServerSimulator.cs` — `ExchangeSimulator.OnTick` routes a TradingStatus tick to
  `InstrumentSimulator.OnRecordedTradingStatus`, which does nothing yet (the status is not applied) until the final piece is
  written. A `{Symbol}.TradingStatus.Tick.zstd` beside the MBO file is found by the
  existing search and merged by time. Why: the session schedule has no holidays or halts and the crossed-book guess
  flickers through a pre-open (2025-11-28). Verified: a three-update file written with `TickHistoryWriter` reads back
  identical (status, exchange and NIC time, instrument) through `TickHistoryReader`. C++: nothing to port (simulator and
  tick history are C#-only).

---

## 2026-10-19 — Simulator: one Mask per side; Ghost decays against the current depth

- `Simulator/OrderManager.cs`, `Simulator/ServerSimulator.cs`, `Simulator/Program.cs` — `OrderManager.CrossMask` is
  gone: a cross adds everything it fills on our side (our orders and the Ghost market ahead of them) to the opposite
  side's `_bidMask`/`_askMask` (`OrderManager.OnTrade` returns market + user filled), which decays only on historical
  trades. Why: CrossMask decayed on same-side book activity and was subtracted only during cross processing, not at
  publication or in Take, so the published book showed spent asks beside our own bid at the same price: a locked book
  with status Open, which made Testing's `Make` throw "Self-crossing targets" (reproduced on 6J and 6A, 2025-11-25).
  Trades, not book activity, are the clock: a maker pegged to the mid shows book activity at every mid move without
  its liquidity returning. One trade of Q now releases at most Q, not Q from each of two masks.
- `QueueManager.DecayGhost` divides by the side's current depth instead of a Ghost-weighted depth snapshot taken when
  Ghost was added; the snapshot fields are gone.
- `Simulator/spec.txt` — Ledger Catalog, Ghost Decay, Crossed Market Sweeps and both lifecycle tables rewritten; Ghost
  decays on its own side (the text said "anywhere in the book", the code never did).
- Simulated fills change: after a cross the opposite side shows less depth until real trades eat the mask. C++: nothing
  to port (no C++ simulator).
- `Simulator/ServerSimulator.cs` — `OverwriteBid`/`OverwriteAsk` (mirror copies) are one `OverwriteLevel` that finds the
  price anywhere in the update being built. They assumed price order and inserted before the first worse price, but the
  list starts in feed order (MBO: event order), so a masked level could be published ahead of its own raw entry and the
  raw quantity won at the client: seen as `bids[ 12771x0 12770x-1 12771x1]` on 6J, a masked bid beside our ask at 12771.

---

## 2026-10-17 — File menu: New Server Workspace

- `Workspace/Workspace.axaml(.cs)` — "New Global Workspace" is "New Server Workspace" (handler
  `OnNewServerWorkspaceClick`): it opens a workspace on the server context, which the rest of the GUI calls a server
  workspace. "New Strategy Workspace..." keeps its ellipsis (it opens a picker first). C++: nothing to port (GUI only).

---

## 2026-10-17 — Positions pause/resume sends the workspace's strategy id

- `Widget/PositionsWidget.axaml.cs` — `ControlAlgoStatus.StrategyId` is `_context.Primary.StrategyId` (as Edit RiskLimit
  sends), not the row's `OrderHeader.OrderId.StrategyId`. Why: `PositionHeader.OnFill` copies the last fill's header,
  so after an algo fill a server workspace's Pause turned that algo's own switch instead of the master switch, the row
  stayed Live and the other algos kept trading; Resume could switch on an algo paused in its own workspace. Now a
  server workspace always turns the master switch and a strategy workspace its own. The hidden StrategyId column still
  shows the row's last-fill strategy. C++: nothing to port (GUI only).

---

## 2026-10-17 — Risk Limits menu: no file path or folder items

- `Widget/RiskLimitsWidget.axaml.cs` — the row menu lost "Copy .risklimit File Path" and the menu lost "Open RiskLimits
  Folder"; a row shows its instrument and Edit RiskLimit, empty space shows nothing (as Rate Limits). Why: they date
  from editing `.risklimit` files by hand, which ControlRiskLimit replaced, and on a desk they point at an empty local
  folder. `Context.RiskLimitsDirectoryPath` stays. C++: nothing to port (GUI only).

---

## 2026-10-16 — a strategy's own book is a field of its Instrument; a GUI's P&L marks to the server's book

- `Provider/Position.cs`, `Provider/Context.cs` — `Profit` marks to the new `Context.TryGetQuote`, which picks the book:
  in a strategy's own process (its context open for writing) its own book, anywhere else (a GUI, the server) the
  server's, read in place between two sequence reads (no copy of the book; false instead of a spin
  when the row is mid-write). Why: a strategy workspace's P&L read the strategy's own book from the GUI process, which
  is what made that book shared; marking a GUI to the server's book also gives a remote strategy workspace its P&L
  (only the server's book is copied to a desk). A strategy never reads the server's book for P&L.
- `Widget/RiskLimitsWidget.axaml.cs` — the Position column reads the position row, not `.Profit` (which built a quote it
  never used).
- `Data/Instrument.cs` — the book is a plain `MarketByPrice64` field (`ref MarketByPrice`); the constructors no longer take
  a shared entry; `TryGetQuote(out quote)` lost its sequence-retry loop (only the feed thread reads the field); the quote
  rule is written once in `TryGetQuote(in book, out quote)`.
- `Provider/Client.cs` — the snapshot is copied into the instrument's field; the three `AcquireLock`/`ReleaseLock` pairs
  around book updates are gone. `Provider/Context.cs` — `MarketsByPrice` is the server's book only (`<server>/...`); no
  shared array uses a context's own directory now.
- Hot path: two volatile stores fewer per book update on the strategy's feed thread, and the book sits in the instrument
  object. Not benchmarked (the user's call). C++: C18 (nothing to port).
- Breaks (realtime only, open): a strategy's P&L series runs on the Clock thread and now reads the instrument's own book
  without a sequence check, as it already read the cached quote; torn values are possible. Proposed fix in Spec.md (the
  trading thread fires the due reminders, as in simulation).
- Tested: the risk suite (50/50, 13/13 on reload), with a new check that a strategy's P&L marks to its own book's quote
  and a GUI-style read-only view of the same strategy marks to the server's book's quote (`Context.TryGetQuote`).

---

## 2026-10-16 — OrderRisk rows move from shared memory into the RiskLayer

- `Provider/RiskLayer.cs` — `private readonly OrderRisk[] _orderRisks` (`OrdersCapacity` rows, indexed by
  `OrderId.GlobalIndex`, allocated in the constructor) and a private `GetOrderRisk`; every use reads it instead of the
  context's shared region.
- `Provider/Context.cs` — the `OrderRisks` region (one per context, under its directory), `GetOrderRisk` and the dump's
  risk line are gone.
- Why: only the RiskLayer of that process ever read or wrote the rows, the Mirror already skipped them (never stamped),
  and no restart reads an old row (a Create resets its slot). One owner; one 512 KB region less per process; rows 64
  bytes apart instead of 128. Not benchmarked (the user's call). C++: P29 (optional, internal). The C# Mirror's
  array ids shift down by one from `OrderTargets` (C3).

---

## 2026-10-16 — shared arrays declared and created grouped by concept

- `Provider/Context.cs` — fields and creation now go: client headers, instruments, subscriptions and the book; then
  positions, limits and exposure as server/local pairs; then orders; then message efficiency, rate limits, core groups
  and latency. Until now each new array was appended last to keep the others' Mirror ids, which bought nothing (the
  layout hash refuses two different builds, and both Mirror ends ship together) and scattered the pairs. The "created
  last" comments are gone; one line says why the order matters. No array changed; the C# array ids did (C++ report C3
  lists them). Deploys with the layout change already pending (L11).

---

## 2026-10-16 — strategy exposure rows under the server's name, so a remote desk sees them

- `Provider/Context.cs` — `_workingRisks` (one region per context, under its own directory) is now two arrays named like
  the position rows: `ServerWorkingRisks` (the firm's, in the old slot, same id) and `LocalWorkingRisks` ([strategy,
  instrument], created last), which each strategy writes, as it writes its `OrderTargets`. `GetWorkingRisk(instrumentId)`
  is per context (as `GetRiskLimit`): each override reads its own array, and there is no "server" accessor (on the server
  there is nothing to tell apart). `GetServerRiskLimit` and `GetServerPositionHeader` deleted for the same reason (the
  server's `GetRiskLimit(instrumentId)` / `GetPositionHeader(instrumentId)` are those rows). `_marketsByPrice` and
  `_orderRisks` stay under each context's directory (the user's call).
- The server also seeds a strategy's `LocalWorkingRisks` row when it allocates the instrument to it (so the region is
  writable on both sides, as `MessageEfficiency`): after a server restart replays the allocations, a strategy that has
  not reconnected yet still has a readable row. The strategy reseeds it at its start, as before.
- Why: the Mirror copies only the server's regions, so a remote strategy workspace's Risk Limits widget could not read
  the strategy's exposure; its `Read()` threw on the unwritten row and the grid came up empty (found on a desk copy of
  `CME_NewRelease`, `Equity_Testing`). The RiskLayer and the widget are unchanged.
- Hot path: the strategy's row moves region, and a client's `GetWorkingRisk` is now a virtual call plus the local index
  (about 1–2 ns per leg, as `GetRiskLimit`). While a Local Mirror runs, its read of a strategy's row costs that
  strategy's next write a cache-line transfer, at most once per pass. C++: P28, L11. Layout hash changes.

---

## 2026-10-16 — the risk desk package: one zip, one double-click

- `Publish\` (new) holds both deploy scripts, each building into a `Staging` folder beside itself (ignored):
  - `Publish\CME\PublishAndDeploy.ps1`: the CME deploy, copied in from `C:\Home\csharp\Scripts\deploy_aot.ps1` (that copy
    is unchanged). It now finds the solution from its own location when no folder is passed, and stages into
    `Publish\CME\Staging` instead of `deploy_stage`; the build, rsync and snapshot are as before.
  - `Publish\RiskDesk\Publish.ps1` builds `Mirror.exe` and `Workspace.exe` (NativeAOT for AVX2, one build node) with
    `Start.bat` and the two config templates into `Staging\BigajRiskDesk\`, and zips it to `Staging\BigajRiskDesk.zip`
    beside that folder. `Start.bat` starts this folder's Mirror unless it runs, then the Workspace.
- `Workspace\WorkspaceConfig.cs` (new) and `Workspace\Program.cs`: with no arguments the Workspace reads
  `WorkspaceConfig.json` beside it (server, directories root, one window per entry, Realtime); the command line becomes
  a one-entry list through the same code. `WorkspaceRunner.RunOnThisThread` takes the list (`WorkspaceRequest` is now
  internal and shared). `Main` brought up to the naming rules (`argumentIndex`) and numbered steps.
- `Mirror\MirrorConfig.cs` `IsConsoleVisible`, `Tools\Application.cs` `DetachConsole`: a hidden Mirror leaves its
  console and writes `Mirror.log`.
- Why: the second trader, the risk desk and a home desk each get the GUI from one zip and a double-click.
- Tested on this PC with a localhost-only copy: a first double-click starts the folder's Mirror (hidden, no console host,
  `Mirror.log` written) and a Workspace that reads its config and waits on `Data\Servers\Realtime\CME_NewRelease`; a
  second double-click starts no second Mirror; a Mirror run from another folder is ignored. Not tested: windows opening
  against a live copy (needs the box).

---

## 2026-10-16 — WorkingRisk names; the Risk Limits widget shows worst-case positions and fills

- `Execution/Order.cs`, `Provider/RiskLayer.cs` — renamed `WorstLongQuantity`/`WorstShortQuantity` to
  `WorstWorkingLongQuantity`/`WorstWorkingShortQuantity` and `AlgoWorstQuantity` to `WorstWorkingFills`. Layout unchanged.
- `Widget/RiskLimitsWidget` — after the limits: `Position`, `FillsThisSession`, `WorstWorkingLongPosition` (position +
  working buys), `WorstWorkingShortPosition`, `WorstWorkingFillsThisSession` (fills + working algo lots); the raw
  `WorstWorkingLongQuantity`, `WorstWorkingShortQuantity` and `WorstWorkingFills` are hidden until turned on. Why: the
  sums are what a limit is compared with. C++: P26's field names.

---

## 2026-10-16 — a manual order is never limited by fills; the risk-limit tests

- `Provider/RiskLayer.cs`, `Execution/Order.cs` — a manual order (house book, or booked to a strategy) is out of the fills
  limit entirely, on the server and in a strategy: `OnFill` counts only an algo order's fill; the new
  `WorkingRisk.AlgoWorstQuantity` (24 bytes) holds only the working algo orders' worst, kept in `ApplyWorstQuantityDelta`,
  and the fills room uses it instead of `WorstLong/ShortQuantity`; `ValidateOrder` runs the fills check for algo orders
  only. Why: an algo that runs away and uses up the fills is exited by hand, and that exit must go through. Manual
  orders still move the position and still meet the firm's position limit.
- Hot path: one more add per leg per reservation change, and one select per fill: about 1 ns. C++: P26 updated.
- Spec.md — the server trusting a control's `StrategyId` (0 from a strategy workspace, or a stray -1) recorded as out of
  contract, with the Local Mirror guard it would take.
- Tested: a scratchpad simulation harness ran 54 checks on 2026-10-15 (strategy clip, firm refusal, manual orders, edit
  routing and files, reload, fills room and amend delta, counting, the Closed reset, reason order, refusals not
  discarded): all passed; rerun after this change below.

---

## 2026-10-15 — strategy risk limits; the firm's limit is the master

- `Provider/Context.cs` — two limit arrays named like the position rows: `ServerRiskLimits` (was `RiskLimits`, the firm's,
  per instrument) and the new `LocalRiskLimits` (per strategy and instrument, created last so every other array keeps its
  id). `GetRiskLimit(instrumentId)` is now per context, as `GetPositionHeader` is: the firm's on the server, the strategy's
  own in a client, so the `RiskLayer` is unchanged. `GetRiskLimit(clientId, instrumentId)`, `GetServerRiskLimit`,
  `Context.StrategyId` (the idiom `CreatePosition` used). The server loads each strategy's row at its allocation from
  `<strategy>/RiskLimits`. `PrintLocalSharedArray` dumps both [clientId, instrumentId] arrays.
- `Execution/Order.cs` — `RiskLimit.FromFile`: the last saved line, or Max in a simulation and Min in realtime; the firm's
  and the strategy's loads share it.
- `Provider/Server.cs` — `OnControlRiskLimit` edits the firm's row for strategy 0 and the strategy's otherwise;
  `WriteToRecord` (shared with `OnControlAlgoStatus`) puts a server row on the audit channel and a strategy's on its
  execution channel, where Logging's tap already saves `RiskLimit` rows to that client's `RiskLimits` folder.
- `Provider/Allocate.cs` — `ControlRiskLimit.StrategyId`. `Provider/Client.cs` — a client ignores a `RiskLimit` on its
  execution channel.
- `Widget/RiskLimitsWidget` — shows and edits the principal's rows: the firm's in a server workspace, the strategy's in a
  strategy workspace (on a remote desk a strategy's exposure columns read 0: its `WorkingRisks` are not mirrored).
- Why: the risk desk wants each strategy capped below the firm. Manual orders keep the firm's limit only. Declined: a
  strategy clipping to the firm's room too, and the server checking strategy limits (see Spec.md "Two levels of risk
  limit"). Hot path: the client's check reads its row through a virtual call, about 1–2 ns per leg; none on the server.
- Breaks: a strategy with no file is Min (blocked) in realtime until its limits are set. C++: P27, L11. Builds; not yet run.

---

## 2026-10-15 — max fills per session

- `Execution/Order.cs` — `RiskLimit.MaxFillsPerSession` (28 bytes), `WorkingRisk.FillsThisSession` (20 bytes),
  `OrderRejectedReason` 57 renamed `TooManyFillsPerSession` (was `TooManyOrdersPerSession`, never raised); the worst fields
  renamed `WorstLongQuantity`/`WorstShortQuantity`. `Provider/Allocate.cs` — `ControlRiskLimit.MaxFillsPerSession`.
- `Provider/RiskLayer.cs` — `OnFill` counts `|q|`; `GetAbsAllowedOrderQuantity(target, isFillsRoom, isWithinLimit)` holds
  the fills room (every working lot on both sides may fill, so an amend counts its delta); `ValidateOrder` checks it after
  the position room; `TryClipToRiskLimit` clips to the smaller room; `OnSessionClosed` zeroes the count;
  `ApplyWorstWorkingQuantityDelta` renamed `ApplyWorstQuantityDelta`.
- `Provider/Server.cs`, `Provider/Client.cs` — the instrument's `Closed` zeroes the count, beside the message-efficiency
  day's end. `Client.IsDiscarded` deleted: its simulation-only discard of 57 would now swallow `TooManyFillsPerSession`.
- `Widget/RiskLimitsWidget`, `Widget/RiskLimitEditDialog` — `MaxFillsPerSession` and `FillsThisSession` columns, a "Max
  Fills / Session" input.
- Why: the risk desk's new limit. Not persisted (accepted): a server restart in a session starts the count at 0.
- Breaks: a `.risklimit` line saved before today loads `MaxFillsPerSession` 0 and refuses every order until it is set.
  Hot path: one add per fill, one more leg loop per check and per clip. C++: P26, L11. Builds; not yet run.

---

## 2026-10-15 — a second algo switch: the server's master per instrument

- `Provider/Context.cs` — `IsAlgoLive(strategyId, instrumentId)`: the strategy's row and the server row both Live.
  `ServerPositionHeaders` is now mapped by every context (read-only in a client), created last among the shared arrays
  so a ServerContext's array ids are unchanged; `GetServerPositionHeader`. The server row starts Live at every
  allocation, whatever its file says (a kill switch).
- `Provider/RiskLayer.cs`, `Provider/Position.cs` (`IsAlgoLive`), `Strategy/Algo.cs` — the algo, the client's risk copy and
  the server's risk copy all check both switches.
- `Provider/Server.cs` — `OnControlAlgoStatus` for strategy 0 (a server workspace's Pause/Resume) writes the server row
  only and audits it; a strategy's control writes its own row, as before. Why: the house book has no algo, so a server
  workspace's pause used to set a status nothing read.
- A refusal for `AlgoIsPaused` alone was already discarded, so the master never auto-pauses a strategy's own switch.
- Hot path: one more byte read per algo decision and order check; the server's fill write may pay a cache-line
  transfer when a client has just read the row (see Spec.md "Algo pause"). Builds; not yet run.

---

## 2026-10-14 — either Mirror end may dial, set in the JSON alone

- `Mirror/MirrorConfig.cs` — the Local end may now listen: the guard that refused a Local `ListenPort` is gone. Every
  listening end still needs its `IPWhitelist` (a listening box: each risk desk's address) and refuses to start without
  one; the link stays unauthenticated, so the whitelist, a firewall rule to the same addresses and the VPN are its only
  protection. Why: the network path to the ARB office may make a desk dialling the box easier than the box dialling the
  desk. The connector and the Local end already handled links in either direction; nothing else changed.

---

## 2026-10-14 — the GUI shares its plumbing: one refresh timer, row reconciliation, hit-test and menus; three thread races fixed

- Three races between a widget's refresh (timer thread) and a click (UI thread), each touching one collection: Orders'
  Cancel All copied `_active` while a refresh refilled it (could miss orders or throw); Positions' Pause/Resume All
  enumerated `_positions` while a refresh added to it ("Collection was modified"); Send Order's wheel and middle-click
  rebuilt the ladder on the UI thread into the buffers the timer pass was writing (`RefreshLadder`, deleted: the next pass
  redraws within 100 ms). The first two now iterate `Rows`.
- `Widget/Widget.cs`: `WidgetRefreshTimer` (one pass at a time; the next starts after the UI thread has applied the
  last, so a pass never rewrites what a pending post reads; a failed pass is logged, not swallowed), `DataGridRows`
  (`GetDataGridHit`, `ReconcileRows`, `IsScrolledNearEnd`, `CompareOrders`), `WidgetMenus` (`NewMenuItem`,
  `NewTitleMenuItem`, `AddColumnVisibilityMenuItems`, `AddOrderMenuItems`, `NewCopyPathMenuItem`,
  `NewOpenFolderMenuItem`, `OpenInExplorer`), `WidgetHost.OpenMenu`, `IWidget.TypeKey` defaulting to the class name.
  Elsewhere: `OrderState.NewCancel`, `Instrument.PriceFormat`/`TicksToPriceString` (`TicKDecimals` renamed
  `TickDecimals`), `Context.DirectoryName`, `WorkspaceContext.AllocateToManualClient`, `Palette.GetSignBrush`.
- Every widget uses them (8 timers, 7 hit-tests, 6 row reconciliations, 8 column menus, 23 menu items). Also:
  `ColumnRegexFilters` keeps one `Regex` per column and owns its saved state (`ColumnRegexFilterState`, same JSON as
  before); Chart's four series bindings are one `SeriesBinding<T>`; `WidgetContainer` shares drag and resize state;
  Workspace builds widgets in one `NewWidget(typeKey)` (the Widgets menu passes the type in `Tag`) and containers in one
  `AddWidgetContainer`; Send Order keeps each tab's inputs in one place; row view-models notify every pass instead of
  comparing; dead code deleted (unused colours and converter, unbound properties, events with no subscriber, the Widget
  project's own never-run app template: Widget is now a class library).
- Behaviour changes, approved: menu title lines are bold and disabled everywhere; rows follow the source's order; a
  ladder redraws on the next 100 ms pass after an instrument change, the Show My Orders toggle, a wheel or a middle-click.
  Column widths saved before today without a kind: above 1 is a hand-sized pixel width, 1 or 0 was Auto.
- Checked: every project builds; every `{Binding}` and XAML handler resolves; the real Workspace opened all 11 widget
  types against a simulated server (screenshots: columns restored, title-bar buttons, filter and column menus); an
  independent review of the whole diff found no regression. Net about −1,900 lines (Widget, Workspace and helpers).
- After the box test: while a restarted Remote Mirror (or server) refills a desk's wiped copy, the Ladder and Send Order
  read an empty book (`Read()` throws "Slot is empty"), which now logged ten lines a second for the ~5 s refill
  (the old code swallowed it). `Context.TryReadMarketByPrice64` returns false for an empty book; both widgets skip that
  pass (the Ladder before flipping its row cache), and Send Order's instrument picker leaves the price blank. The shared
  timer logs a repeated failure once, and again only after a pass succeeds.
- Not done, by decision: a shared Fills/Audit pager (they sort differently); persisting Audit Trail's filters (new
  behaviour). Kept: Risk Limits' unbound `LongQuantityAllowance`/`ShortQuantityAllowance` (owner to decide), the
  widgets' design-time constructors.

---

## 2026-10-14 — saved grid columns keep their width's kind; one column-layout helper for five widgets

- `Widget/Widget.cs` — `DataGridColumnState` (header, width, width kind, position, visibility) with
  `NewDataGridColumnStates` / `ApplyDataGridColumnStates`, and `DataGridWidgetState` for the widgets whose state is their
  columns only. Replaces five copies: `InstrumentHeadersWidget` (keeps its filters), `OrdersWidget`,
  `MessageEfficiencyWidget`, `RateLimitWidget`, `RiskLimitsWidget` (their save and load are now one line each).
- Why: every copy saved `Width.Value` without its kind, so an Auto column (value 1) came back as 1 px; on the CME desk
  (2026-10-14) a saved default workspace reopened Instrument Headers and Orders with empty 1-px columns. A file saved
  before this change has no `WidthUnit`, so it now loads as Auto (checked against that file: its columns, hidden ones and
  the Instrument Headers filter all load). Net −160 lines.

---

## 2026-10-14 — a Remote Mirror restart rebuilds the open Workspaces; the latency number is the Remote's alone

- `Mirror/Program.cs` — `RunRemote` no longer waits for the last run's Workspaces to close (`WaitForPreviousCopyToClose`
  deleted): when the copy's header is still posted it reuses the copy, wiped as on a server restart. It stops, as before,
  on a copy of other capacities or another build, and on a simulation desk on a copy of another run.
- `Mirror/RemoteMirror.cs` — posts its process id in `<server>/RemoteMirrorProcessId` once it listens.
- `Provider/RemoteClient.cs` — notes the Remote's id at connect; `IsSessionEnded` (a new server or a new Remote) replaces
  the Workspace's own server-id check and decides whether `Dispose` closes; `IsRemoteMirrorAlive`.
- `Workspace/Workspace.axaml.cs` — rebuilds on `IsSessionEnded`. The latency text is `Latency[0]` exactly as the Remote
  writes it, to the microsecond (`0.000 ms`), "Connection broken – reconnecting…" below 0, "Remote Mirror offline" once
  its process has exited (within about 100 ms, however it ended), "Remote Mirror not responding" when alive but not
  rewriting the row for 10 s. Why: the Workspace showed the row's age when longer, so a closed Remote read as a latency
  climbing to 10 s; and a whole-millisecond figure on a steady link looked frozen.
- Restart harness: new R8 (two GUIs open, the Remote killed and restarted: both rebuild under their own names, both calls
  reach the server) passes; R1 now judges the latency's return by its samples (a new link starts at exactly 0).

---

## 2026-10-14 — the Workspace finds its own windows: a server restart rebuilds them

- `Workspace/Workspace.axaml.cs`, `Workspace/WorkspaceRunner.cs` — `Workspace.OpenWorkspaces`, a list each window joins
  when built and leaves when closed, replaces every use of Avalonia's `desktop.Windows`: the server-restart rebuild,
  closing (shut down after the last), saving a workspace's other windows, and `WorkspaceRunner`'s screenshot and close-all.
  Why: Avalonia 12 lists only windows opened after the lifetime starts, and `WorkspaceRunner` opens the first one before
  (a scratch Avalonia 12 app showed `Windows.Count` 0 with it up, 1 after a second opened). On the CME box (2026-10-14) a
  server restart found no window to rebuild: `RestartWorkspaces` ran every 100 ms creating a `ServerContext` each time,
  the latency text stayed blank and the window's calls went to the stand-in the Remote had closed. The same gap made
  closing the first window shut the app with others open, and left it out of saved layouts and close-all.
- Close-all now closes only Workspace windows; other top-level windows go when the app shuts down. Net −16 lines.

---

## 2026-10-14 — a Local Mirror restart no longer reads as a latency spike

- `Mirror/RemoteMirror.cs` — taking a new link starts its latency afresh: the last 10 round trips are cleared, the
  newest-answered-ping time is reset to now, and the first ping goes out at once. Why: the desk's latency includes the age
  of the oldest unanswered ping, measured from the last pong; after a Local Mirror restart that was the old link's last
  pong, so the reconnected desk showed the whole outage (8329 ms on the CME box, 2026-10-14) climbing until the new
  link's first pong; the old link's round trips (high if it was degrading) also stayed on screen for 10 more pongs. Now
  it starts at 0 and climbs only while the new link's snapshot is in flight.

---

## 2026-10-14 — a Workspace rebuilt after a server restart no longer closes another window's name

- `Provider/RemoteClient.cs` — remembers the copy header's `ServerProcessId` at connect; `Dispose` skips the close message
  when the header now names another process. Why: after a server restart a new window can take the old window's GUI name
  before the old window rebuilds; the old window's close then ended the new window's session on the box (its stand-in
  closed, its calls went nowhere). The old session needs no close: it ended with its server.
- Verified with the restart harness rebuilt on today's code: every check passes, T4b included (it failed before); the new
  window's allocation now takes effect in about 1.1 s instead of timing out.

---

## 2026-10-13 — the server owns message efficiency; groups come from CME's security group

- `Data/Instrument.cs` — `InstrumentHeader` gains `sbyte ProductGroupId` (offset 7, was reserved; -1 until allocated)
  and `String4 ProductGroupRoot` (offset 60, was reserved); still 64 bytes. `Instrument.ProductGroupId` reads the header.
- `Data/InstrumentDetails.cs` — new `ProductGroupRoot`: CME's security group (tag 1151), which is not always the root (MNQ
  is NQ, M2K is RY). Databento fills it (the user is adding it there). `Simulator/ServerSimulator.cs` copies it into the
  header. Details files without it can no longer be allocated: regenerate them.
- `Provider/Context.cs` — `ServerContext.AllocateInstrument` assigns the row (`AllocateProductGroupId`, moved from
  `ClientContext`: the group's first instrument claims the next row and loads the group's file). Clients no longer scan,
  claim, load, reset or append; `SaveMessageEfficiceny` is deleted. Why: one shared row was appended by every process
  that allocated it (strategies, Workspaces, the Local Mirror's stand-ins); the day flipped only on the first instrument
  the first client allocated; two clients could claim the same row. The array is writable by the server now (it was
  mapped read-only there).
- `Provider/Server.cs` — on `Closed`, `OnTradingStatusUpdate` resets the instrument's group row, and in Realtime posts the
  day that ended on the audit channel only when the reset changed the row, so once per group. `Dispose` posts every
  group's row on the audit admin channel just before closing the audit link; Logging drains it on close.
- `Execution/RateLimit.cs` — the row is its own record: `MessageEfficiency` starts with `Header<OrderType>` (new
  `OrderType.MessageEfficiency = 18`, `Execution/Order.cs`), 84 bytes, and its constructor is parameterless so a JSON load
  runs the initialisers (an old line, with no `Header` key, loads as type 18; checked in a scratch harness against
  `S:/CME/MessageEfficiency/6B.messageefficiency`).
- `Logging/LoggingServer.cs` — `AuditWriter` appends a posted row to `<server>/MessageEfficiency/<group>.messageefficiency`;
  the sort key puts it at the high-water mark (it has no `OrderHeader`).
- `Execution/RateLimit.cs` — `MessageEfficiency.ProductGroup` renamed `ProductGroupRoot` (one name with the header and the
  details), and the widget's column with it. The files' JSON key changes; an old line still loads, as the server sets the
  field after reading it.
- `Testing/Scenario.cs` — its MNQ/M2K/MYM group dictionary is gone; the details carry the group.
- `Widget/MessageEfficiencyWidget.axaml.cs`, `Widget/RiskLimitsWidget.axaml.cs` — "Copy … File Path" gave
  `<server>/MessageEfficiency/MessageEfficiency/<group>…` (and the same for `RiskLimits`): each passed the folder to a
  helper that adds it again. Both now pass the server's directory.
- Deploy: the C++ server first fills both header fields (report P24, L10); until then C# clients on the box count every
  instrument into row 0. Untested end to end; all projects build.

---

## 2026-10-13 — the Local Mirror's alerts connect under their own name

- `Provider/AlertManager.cs` — an optional `directoryPath` names the Logging connection (`<directoryPath>.alert`; default
  the context's folder, as before). `Mirror/Program.cs` — the Local Mirror passes `<server>/LocalMirror`. Why: on the CME
  box the Local Mirror stopped at startup with `ConnectRefusedException … CME_NewRelease.alert: AlreadyConnected`; the
  C++ server (`CmeServer`) holds `<server>.alert` itself, and Logging keeps one connection per name. Its alerts now go to
  `<server>/LocalMirror/Alerts/<date>.alert` and the same Telegram chat. Deploy the new Mirror to the box.

---

## 2026-10-13 — Instrument Headers opens order entry and ladders; the search widget is gone

- `Widget/InstrumentHeadersWidget.axaml.cs` — right-click a row: "Create Order" and "Open Ladder" after "Allocate X",
  greyed out unless the instrument is allocated to this Workspace's principal (order entry and the ladder need its id
  there).
- Deleted `Widget/SearchWidget` (Instrument Headers finds and filters instruments better), with its two openers: the
  Workspace's Widgets → Ladder menu item and the ladder's "Set Instrument" title-bar item. Ladders are opened from
  Instrument Headers or Positions.
- `Widget/Widget.cs` — `WidgetHost.AddToHost(control, widget)`, with `SendOrderWidget.NewCreateOrder`/`NewAmendOrder` and
  `LadderWidget.NewLadder`, replaces the six copies of "find the Workspace window, build the widget, add it" in Ladder,
  Orders and Positions (each is now one line). A widget outside a Workspace window now logs that it opened nothing.
- Net about −180 lines. Not covered by the restart harness (it has no widgets): check in a Workspace.

---

## 2026-10-13 — restarts: a Mirror restart is a link drop; a server restart rebuilds the desk in place

- `Mirror/RemoteMirror.cs`, `Mirror/Program.cs` — the server's process id in each server header decides: the same server
  means a restarted Local Mirror is just a link drop (Workspaces carry on, stand-ins restart under the same names and
  ids). Another one: that `RemoteMirror` stops reading and `RunRemote` replaces it with a fresh one over the same copy and
  link, which resets every row of the copy to unwritten, applies the new snapshot, and writes the new header and starts
  listening at the heartbeat that closes it. One object per server process, so no old GUI slot survives (a first version
  tracked "stale" GUIs instead; the harness showed a reconnecting GUI landing in another name's old slot and its calls
  lost). Every header's `LayoutHash` is checked; a mismatch, or a server restart on a simulation desk, stops the Remote.
  Nothing goes up a new link until its first header is checked. The start-time check
  (`MirrorHeartbeat.LocalStartTimestamp`, `_localStartTimestamp` on both ends) is deleted.
- `Socket/SharedArray.cs` — `Clear(index)`: a row back to unwritten (sequence 0, zero bytes, frame kept), for the wipe.
- `Workspace/Workspace.axaml.cs` — a remote Workspace that sees a new `ServerProcessId` rebuilds every Workspace of its
  process in place (`RestartWorkspaces`): `ContextManager.Renew()`, then per window capture its layout, `Disconnect()`, take
  the renewed context for its principal, `Connect()` asking for its own GUI number first, `RestoreWidgets` (size,
  position and state untouched; `RestoreLayout` now calls it). The constructor and close handler use
  `Connect()`/`Disconnect()`; closing a window disposes its widgets and stops its refresh timer (they used to run on after
  it closed). A second round of the harness found two GUIs rebuilding seconds apart could swap names (the lowest free one)
  and hit each other's rings; asking for the own number fixes it. A third review: every window is disconnected before any
  reconnects (a fallback number could otherwise be another window's that had not rebuilt), `Open` wraps round the 8
  numbers, only a connected window starts a rebuild (a closed one's queued refreshes started another), and
  `RemoteClient.Dispose` retires its context (`ContextManager.Retire`) instead of unmapping it (a dialog awaited across
  a rebuild read the unmapped view and killed the desk).
- `Provider/Context.cs` — `ContextManager.Renew()`: fresh contexts over the same copy (a strategy context's client id,
  cached instruments, positions and symbol strings start over); the replaced ones stay mapped until exit, so nothing reading
  them can touch freed memory. The GUI's symbol strings move from the static `SymbolCache` (deleted) to
  `ServerContext.GetSymbolStrings` and `WarmSymbolStrings`. `Provider/Client.cs` — `ManualClient.Open` takes the GUI
  number to try first; `TryGetPrincipalName` is renamed `TryParseRemoteGuiName` and also returns that number.
  (`Widget/SearchWidget` is deleted: see the next entry.)
- Kept by design: widgets restore by instrument id (declined: by symbol). Residual: a reader descheduled across the wipe
  and the row's first rewrite could accept a torn row. Start order on the CME box: the Local Mirror after the server has
  loaded its clients and instruments.
- Breaks: the heartbeat packet shrank 8 bytes, so both Mirror ends deploy together. Nothing for C++ beyond the start order
  (P22, C15).

---

## 2026-10-13 — "self-crossing targets" in the Forex simulation: two book fixes

- Symptom: the Testing scenario's Make threw `Self-crossing targets` thousands of times on 2025-11-28 (8,309 alerts),
  because its quote showed the bid 10 or more ticks above the ask. Diagnosis in Spec.md; harnesses in the session
  scratchpad (`selfcross`).
- `Simulator/ServerSimulator.cs` — `InstrumentSimulator` publishes `TradingStatus.Auction` while the session is open and
  its book is crossed (`OnSessionChanged` replaces the timetable's direct `OnTradingStatus` calls; `UpdateTradingStatus`
  runs on every market-data update before the book is published). Why: on the Globex-outage day the real 6E/6A/6C books
  were crossed in CME's pre-open, which live CME reports as PreOpen; the simulator said Open (61% of the alerts).
  Status is now published only on a change.
- `Data/MarketByPrice.cs` — `SideByPrice64.TrySetQuantity`: a delete of a price better than the best is a no-op. It used
  to clear the worse level sharing that ring slot, without a broadcast, so the server's and clients' books diverged
  (replica fuzz: 616 of 3,000 runs before, 0 after). The old far-better-delete special case folds into it. Hot path:
  about +1 ns per update measured (JIT), no allocation.
- Not fixed by these two: a rerun the same day still shows the 6B alerts (13:11, bid 13239 against ask 13224). That 39%
  (6B 13:04 to 13:33, 6C after the reopen) comes from a stale level the real data never had, not, or not only, the ring
  defect; the remaining candidate is Make's own moved order left in the simulator's published book. Open.
- Breaks: nothing on the wire. The C++ server needs the ring fix (cpp_alignment_report_2026-10-12.md P23).

---

## 2026-10-13 — the Mirror takes its config path on the command line

- `Mirror/Program.cs`, `Mirror/MirrorConfig.cs` — `Mirror <absolute config path>` reads that file; with no argument,
  `MirrorConfig.json` in the working directory (was: always beside the executable). `LoadOrCreate` and `ThrowIfInvalid`
  take the path, and every refusal names its full path. Why: one build serves both roles from separate files, and the C++
  server's main can start the Local Mirror with its config wherever its working directory is. `DirectoriesRoot` still
  resolves a relative folder beside the executable, as the Workspace's `-d` does.
- Breaks: a Mirror started from another folder with no argument no longer finds the file beside the executable; start it
  from that folder or pass the path.

---

## 2026-10-13 — Logging polls every 100 ms live

- `Logging/LoggingServer.cs` — `PollerLoop` sleeps 100 ms after each pass in `Realtime` (was a continuous spin). Why:
  each poll reads the line a writer writes next, so nearly every message write on the trading server paid about +330 ns
  to take that line back from Logging's cores; now at most the first one or two writes per ring per 100 ms do.
  `Simulation` keeps the continuous poll (a backtest at Max speed could lap a 2 MB ring in 100 ms). The redundant
  empty-set branch is folded into the pass (same behaviour). The `WatermarkMargin` comment is corrected: the margin covers
  one pass's drain, not the poll interval, so it stays 10 ms.
- Costs: alerts and strategy logs land up to 100 ms later; a client's audit file up to about 200 ms later (records within
  the 10 ms ordering margin of the newest wait for the next poll); a crash can lose up to about 200 ms of unappended
  lines. Tested on the dev PC (scratchpad loggingpollverify: 0 lost of about 100,000, 0 inversions, typically 80 ms,
  worst 167 ms in normal running); not measured on the CME box. See Spec.md "Logging polls every 100 ms live".

---

## 2026-10-13 — `ServerHeader.ServerProcessId`; the Local Mirror exits with the server; a restart names why it cannot start

- `Provider/Allocate.cs` — `ServerHeader` gains `int ServerProcessId` at offset 173, after `Persistance`; every earlier
  offset is unchanged and the size check is now 177 bytes. Why: nothing in shared memory said which server process wrote
  the header, so nothing could tell a dead server from a live one.
- `Provider/Context.cs` — `ServerContext.Connect` stamps it with `Environment.ProcessId` beside the house-book reservation,
  so every host (Server, ServerSimulator, tests) gets it without setting it. When the post is refused (the previous run's
  header is still there because some process maps it) it now says which case: "already running as process N" or
  "previous server (process N) has exited, but other processes still map its shared memory; stop its clients…" (was
  "Failed to write ServerHeader to shared memory"), and disposes its handle first, so an in-process retry is not blocked
  by it. Overwriting a dead run's header was declined: the server would start on that run's rows and rings (Spec.md).
- `Mirror/Program.cs` — `RunLocal` runs while the server's process is alive (`ProcessId.IsAlive(ServerProcessId)`, every
  pass) and then exits. Why: a dead server's stand-ins are read by no one, and the server cannot restart while the Local
  Mirror maps its header. It checks once right after attaching, before its `AlertManager`: started on a dead run's header,
  it exits at once instead of waiting forever in the `AlertManager`'s connect to that server's Logging, which had exited
  with it (the test's one failure). Started before any server, it waits for one. Each server run needs its own Local
  Mirror (the C++ server's main will start it). Residuals in Spec.md: a server dying during that connect, and a reused
  process id.
- `Mirror/RemoteMirror.cs` — `GetLatency` returns -1 ns as soon as the link is down, not only after 10 s without a pong,
  so a remote Workspace shows "Connection broken" within one network trip of the Local end exiting.
- `Mirror/RemoteMirror.cs` — when a restarted Local end makes the Remote exit, it first writes -1 ns into `Latency[0]`.
  Why: the test showed it froze a positive value there (2,687 ms, a link up but no pong yet), so the desk's Workspaces
  showed a climbing number for up to 10 s instead of "Connection broken".
- `Workspace/Workspace.axaml.cs` — a Workspace beside the server (its manual client is not a `RemoteClient`) shows
  "Disconnected – server stopped, close this Workspace" beside the clock once the server's process has exited (checked
  each refresh). Why: its clock just froze before, and while it stays open it blocks the server's restart.
- Breaks: the header layout. The C++ `Provider/Allocate.hpp` must add the field and stamp `getpid()` in the same deploy
  (cpp_alignment_report_2026-10-12.md P22, L9); without it a Local Mirror reads process 0 and exits at once. Both Mirror
  ends and every Workspace deploy with it (the server-header packet grew 4 bytes).

---

## 2026-10-12 — remote desks: a `RemoteClient` passes every call to the Local end's ManualClient; the remote copy has one writer

- `Provider/RemoteClient.cs` (new) and `IManualClient` — a remote Workspace's manual client is a pass-through: each call
  (`OnOrderTarget`, `OnAllocateInstrument`, `OnControlAlgoStatus`, `OnControlRiskLimit`) goes up its ring to the Remote
  Mirror and on to the stand-in ManualClient the Local end runs under the same name and client id, which does the slot,
  risk check, target row and send. It holds no order state and writes no row, so the remote copy's only writer is the
  Mirror, applying the Local end's rows: an order appears once the Local end has it, never at the click. Why: the remote
  side guessing order state caused every link-loss defect of the last week (L, O, P/M, N, J, the half-sent race).
- `Mirror/` — `MirrorPublisher` is renamed `LocalMirror` and `MirrorSubscriber` `RemoteMirror` (files too), after the two
  roles; the `GuiReply` packet is `AllocatedClientId` (it carries what the Remote's `AllocateClientId` hook returns: a
  client id or a negative `ConnectRefusal`), with `ApplyAllocatedClientId` and `SendAllocatedClientId`. Entries below this
  one use the old names.
- `Mirror/RemoteMirror.cs` — deleted `ReconcileGui`, `CloseOutGui`, the forwarded bits, readiness bits, the `ApplyRow`
  skip rules and order states pushed into GUI sockets (333 → 227 lines). Only a RemoteClient attaches: a plain
  ManualClient is refused `ConnectRefusal.RemoteDesk` (new, -7) and `ManualClient.Open` opens a RemoteClient instead, so
  only a live Remote Mirror makes a Workspace remote. Remote GUI names end `@<machine>`, so no CME-box Workspace can take
  one during an outage.
- Latency display: one link per remote machine, one number. `Context.GetLatency(coreGroupId)` is a new shared array
  (`<server>/Latency`, a `Tools.Duration` per CoreGroup, below 0 = disconnected), appended after `WorkingRisks` so
  earlier array ids keep their mirror order. The Remote Mirror pings the Local end every 100 ms, the Local end echoes each
  ping straight back, and the Remote writes `Latency[0]` in the copy every pass: the worst of the last 10 round trips, or
  the age of the oldest unanswered ping when longer, -1 ns after 10 s without a pong. All Mirror timing (ping, pong, the Workspace display) uses `Timestamp.StopwatchNow` (new in `Tools/Timestamp.cs`: the machine's monotonic `Stopwatch` as a `Timestamp`) and `Duration`. A remote Workspace shows it beside the
  clock (or how long since the row last changed, so a dead Remote Mirror climbs too) and "Connection broken –
  reconnecting…" when below 0 or unchanged for 10 s. Clicks while the link is down are dropped; the number is the signal.
- `LocalMirror.IsPublishDue` (was `HasQueuedPackets`) — the Local end also publishes on the same pass when any stand-in
  ManualClient reads an order state from the server (its `OrderState` event sets a flag that `Publish`
  clears). Before, nothing was queued for an order once rejects stopped travelling, so its rows waited for the 100 ms
  tick (smoke run: shown 139 ms after the click while the server had it at 39 ms). Still one publish per pass.
- `Mirror/TcpLink.cs` — the constructor closes its socket if it throws (production review S13); the accept path leaked one
  socket handle per connection that failed while its link was built. Also fixes the truncated "gives up as soon" comment.
- `Provider/Context.cs`, `Mirror/MirrorPacket.cs`, `Mirror/LocalMirror.cs`, `Mirror/Program.cs` — layout check (production
  review S8): `Context.GetLayoutHash()` fingerprints the array layout (FNV-1a over each array's row size and capacity);
  the Local end sends it in every `MirrorServerHeader`, and `RunRemote` throws at startup when the desk's own differs,
  naming both values. Before, a desk on a different build silently put rows into the wrong arrays (adding `Latency` this
  week shifted every later array's number). Breaks: the server-header packet is 4 bytes longer; deploy both ends together.
- `Mirror/RemoteMirror.cs`, `Mirror/LocalMirror.cs` — the GuiList timer, `AllocateClientId`'s 10 s deadline and the 3 s
  refused-start cache time elapsed with `Timestamp.StopwatchNow`, not the wall clock (production review S9). Before, a
  backward wall-clock step (Windows after sleep, a time-sync correction, a manual change) stopped the GuiList for the size
  of the step, so a Workspace closed meanwhile kept its stand-in and its orders working at the exchange. The Local end's
  start time stays on the wall clock: it identifies a run, it does not measure time.
- `Mirror/TcpLink.cs` — `DialTcpLink` cancels a connect after 2 s (`CancellationTokenSource`, awaited) instead of
  abandoning it with `.Wait(2000)` (production review S2). An abandoned connect task faulted unobserved and the Local
  Mirror's AlertManager alerted `UnobservedTaskException`: with the home PC off, about one Telegram alert every 3 s all
  night. The dial log now shows the real cause (refused, canceled) instead of "One or more errors occurred".
- `Mirror/LocalMirror.cs`, `Mirror/Program.cs` — the Local end queues each pong instead of writing it straight back, so it
  goes out after the publish it triggers on the same pass, and the 100 ms publish timer is deleted (each remote's ping,
  every 100 ms, now drives the publishes; with no remote connected nothing is published). Why: a pong written straight
  back proved only the link, so a Local end that answered pings but no longer published (a throw in `Publish`) showed a
  healthy latency over a frozen screen; now it reads as a stall. The latency is still a round trip, now including one
  Local pass and publish (loopback smoke: 32 ms before and after; a `Publish` made to throw now climbs to "Connection
  broken" instead of showing 32 ms). `IsPublishDue` also covers a remote not yet synced: without that, a freshly started
  Remote (which pings only once it has the server header) and the Local end each waited for the other, and the link
  dropped and redialled every 10 s forever. About −3 lines.
- `Provider/Context.cs` — `LeggedHeader.GetLegHeader` is set only by the first `ServerContext` (`??=`), never by a
  `ClientContext`, and cleared by `ServerContext.Dispose` when it is its own (production review B2). Before, every context set it to itself; in the Local Mirror a closed
  stand-in left it pointing at its disposed context: the next spread lookup threw `ObjectDisposedException`, so a remote
  spread allocation was silently dropped and a spread reject alert named `UnknownSymbol_<id>` (a lookup racing that
  `Dispose` on the AlertManager thread could read unmapped memory). Verified on the loopback harness: both fail before,
  both pass after. +3 lines, cold path.
- `Mirror/MirrorConfig.cs`, `Mirror/TcpLink.cs` — `IPWhitelist`: a listening end accepts only these IP
  addresses and closes any other connection at once; `ThrowIfInvalid` refuses a listener with the list empty or holding a
  non-IP. Why: on a Remote any new link replaced the live one, so an internet scanner touching the port made the Local end
  close that desk's stand-ins and the server cancel every remote order (production review B3). Breaks: a Remote's
  MirrorConfig.json must now list the CME box's egress address.
- `LocalMirror.ApplyMessage` — the 1 s age check is deleted, with what only it used: `MirrorMessageHeader.CopyTimestamp`,
  `MirrorHeartbeat.PublishTimestamp`, the Remote's `_copyTimestamp` and `s_maximumMessageAge`. Every call that reaches the
  Local end is now made on its stand-in, however late: a call clicked during a stall under 10 s reaches the server when
  the stall ends (Spec.md "Calls are not aged"). Breaks: the message and heartbeat packets are 8 bytes shorter, so both
  Mirror ends must be deployed together.
- No Logging on a remote desk: the Remote Mirror starts none; a RemoteClient Workspace creates no AlertManager. The Local
  Mirror's `Program.RunLocal` runs one `AlertManager` on the server's context: its loop's exceptions and every stand-in's
  rejects reach Telegram from the CME box.
- `Provider/Client.cs` — `OnOrderRejected` and `OnOrderState` back to their committed shape (the id-only client-side
  Create rule and `DeallocateOrder` existed only for remote GUIs). `InstrumentHeadersWidget` follows the server's
  allocations, so the remote `Instrument` event is gone.
- Code: net −44 lines (.cs/.axaml). Smoke run against a simulation server: 14 of 16 pass; the 2 failures are by design:
  a reject does not reach the remote Workspace itself (it reaches Telegram, as for a Workspace on the CME box).
- Breaks: both Mirror ends and the Workspaces must be deployed together (wire and ring shape changed).

## 2026-10-08 — review fixes: `PendingNew` renamed `InFlight`, an order shows in the GUI from the click, a full client disposes (`cpp_alignment_report_2026-10-07.md` §4.4, §4c)

- `Execution/Order.cs` — `PendingNew = 1` is now `InFlight = 1` (same value). New `OrderState.NewInFlight(in OrderTarget)`
  builds the state of a Create the exchange has not acked (Active, InFlight, nothing filled, Seq 0).
- `Provider/Server.cs` — `OnOrderTarget`'s Create branch builds its state with `NewInFlight` (was an inline initialiser).
- `Provider/Context.cs` — `OrderEnumerator` finds a slot by its target row and shows an order the server has not answered
  as `NewInFlight(target)`. Before: a fresh slot's order was hidden until the server answered, a reused slot's showed as
  an empty state with id 0 (two such rows merged in the grid), and a refused Create in a reused slot showed as a phantom.
- `Widget/OrdersWidget` — a `StateReason` column.
- `Execution/Order.cs` — builders replace hand-copied initialisers (behaviour-preserving): `OrderTarget.NewOrderRejected(reasons,
  source)` in `Client.Reject`, `Server.OnOrderTarget`, `ServerSimulator` and `MirrorPacket.NewConnectionBrokenReject` (was
  `CreateConnectionBrokenReject`); `OrderState.NewRejected(in OrderRejected, out OrderState)` (out, not a return: a return cost an extra 64-byte copy, +4.7 ns per refused Create, measured) in `Server.OnOrderRejected` (on the live row)
  and `MirrorSubscriber.WriteRejectToGui` (on `new OrderState()`, as before). `GetInFlight` is now `NewInFlight`.
- `Execution/Order.cs`, `Data/Instrument.cs`, `Data/Tick.cs` — hot row constructors inline (behaviour- and layout-neutral:
  sizes, offsets, constructor bytes and JSON verified identical): `[method: MethodImpl(AggressiveInlining)]` on the
  parameterless constructors of `OrderRejected`, `OrderState`, `OrderTarget`, `Fill` (the compiler zeroes every field, so
  the JIT judged them too big); `fixed byte _reserved[]` became plain `byte` fields (`Fill`: `Size = 64`; `Header<T>`: its
  `Size = 4` already reserves them), removing a stack-cookie check from every frame holding one; `OrderRejected`'s static
  constructor became a constant mask (`OrderDiscarded`), so `IsDiscarded` inlines; `Header<T>`'s static constructor (the
  one-byte enum check) moved into its constructor as a JIT-time constant, free for a byte enum.
- `Execution/Order.cs` — `OrderState.NewInFlight(in OrderTarget, out OrderState)` and `OrderTarget.NewOrderRejected(reasons,
  source, out OrderRejected)` build as constructor, then field stores, into the caller's variable (the server's InFlight
  state straight into its row); `new() { ... }` built a temporary and copied it (measured about +7 ns per Create and per
  server reject). Same shape as `NewRejected`. Output verified byte-identical on populated orders.
- `Mirror/`, `Provider/Client.cs` — link recovery is exact, and the Mirror never makes up an OrderState (design A with
  DeallocateOrder; design B, a server-side age check, was built and measured, and rejected: a re-sent Create could overwrite a live
  order's state):
  - `Client.DeallocateOrder` (target done, position no longer counts it, slot free) is used by a Done state and by the refusal of
    a Create the server never saw (source `Client`), so a `Client` ends that order itself. Before, the Remote wrote a made-up
    Done/Rejected into the copy and the GUI's channel (`WriteRejectToGui`, deleted), which needed the phantom guard in
    `ApplyRow` (deleted).
  - `MirrorSubscriber.ReconcileGui` (issue J): on each GuiReply, every order the GUI still waits on that the server has no
    row about is refused `ConnectionBroken` as a Create. Before, an order lost in a dying link stayed InFlight with its slot
    taken until the Workspace restarted.
  - `ApplyRow` skips a row whose bytes the copy already holds, so a snapshot no longer re-delivers every state to a GUI.
  - `MirrorPublisher.PublishRows`: a row skipped mid-write while a remote takes its snapshot counts as changed next pass;
    before, it never reached that remote unless it changed again.
  - The Remote closes a dead link at once, so the Local end redials about 10 s sooner.
- `Mirror/MirrorPublisher.cs` — each GUI command is sent (`ManualClient.WriteSocket`) at the end of `ApplyMessage`, where
  it was aged, instead of in `Read` step 4 (which now only reads): nothing slow in the rest of the pass can push an order
  that passed the 1 s check past it, and a GUI detached later in the same batch no longer takes its last commands with it.
- `Mirror/MirrorPublisher.cs` — the 1 s age check refuses every order target alike (`ConnectionBroken`), cancels included,
  for one simple rule (it exempted cancels since the second rerun). A late cancel is now refused like any other: its order
  keeps working, and the operator sees the reject and cancels again.
- `Provider/Context.cs` — a strategy workspace lists every order booked to its strategy, whichever client sent it
  (`Context.EnumerateOrders(instrumentId, strategyId)` walks every slot with a `StrategyId` filter;
  `WorkspaceContext.EnumerateOrders` passes `-1` for a server workspace, else the ManualClient's strategy id). Before, it
  listed the algo's slots plus this GUI's, so a sibling GUI's manual orders for the same strategy (another Workspace, a
  remote desk) were invisible and could not be cancelled. `CompositeOrderEnumerable`/`Enumerator` and the per-context
  `EnumerateOrders` overrides are deleted (about -135 lines).
- `Mirror/MirrorSubscriber.cs` — `CloseOutGui` (defect N): on the GuiReply that admits a new Workspace process, before it
  is released from `AllocateClientId`, every target row its dead predecessor left Active with no server row about it is
  marked Done in the copy, as `Server.TryCloseOutOrders` settles a dead client's rows. Before, a Workspace restarted under
  the same name showed such an order (lost with the link, or refused before the server saw it) as InFlight forever: its
  refusal was in the queue the new process skips (`Client.Recover`), and a cancel of it was dropped by the Local end.
  About +19 lines, one file.
- `Provider/Client.cs` — `OnOrderRejected` ends an order on a client-side Create refusal matched by id alone, not id and
  seq. Before, an amend or cancel sent before the refusal arrived moved the target's seq on, so the refusal looked stale and
  the order hung InFlight with its slot taken: a Create and its cancel both refused as too old by the Local end (M, slot
  leak), a cancel clicked just after `ReconcileGui` refused a lost order, or a cancel arriving fresh after its Create was
  refused as too old (both dropped silently by the Local end's `ManualClient.Amend`). Safe: such a Create never reached the
  server, so nothing sent after it can be live; ids carry a generation, so a reused slot never matches. Every other refusal
  still needs the same seq. Measured (wf10 BenchmarkDotNet): +0.37 ns per server refusal, +0.5 ns per client-side Create
  refusal, 0 allocated; a refusal only, never a send.
- `Mirror/MirrorSubscriber.cs` — `ReconcileGui` refuses only orders the Remote sent and nobody answered: a per-GUI
  `Bitset64` (`_forwardedLocalIndicesByClientId`) is set when step 4 sends an own order, cleared by its Done row
  (`ApplyRow`) or a refused Create coming down (`ApplyPackets`), and cleared by `ReconcileGui`, which no longer requires the
  target to be Active. Fixes, found by the wf10 harness and its design review: a Create clicked just before a GuiReply
  (or whose row was written but not yet its message) was refused and then sent, so the GUI showed refused while the order
  was live (L, high); a refusal a frozen Workspace had not read (Remote not-ready, Local age check or ManualClient refusal,
  or a previous `ReconcileGui`) was repeated (O); a lost Create whose cancel was refused during the outage leaked its slot
  (P). Designs without the bit were checked and rejected: nothing the Remote can read at the GuiReply separates a lost order
  from one already refused (Spec.md "Only orders sent and never answered are reconciled"). About +12 lines.
- `Provider/Context.cs` — the walk shows an order the server has not answered only for the workspace's own GUI client
  (`EnumerateOrders(..., inFlightClientId)`; `WorkspaceContext` passes `Manual.ClientId()`). Another client's order is
  listed once the server's state row is about it. Before, a GUI could cancel another client's in-flight order; that cancel
  travels a different channel, could reach the server ahead of the Create, and was dropped while the order went on working.
  Hiding it replaces a per-action guard in every widget (about +6 lines instead of +12).
- `Socket/Socket.cs` — `ReadOnlySocket` no longer latches closed: `TryRead` returns `Closed` for a close message and reads
  on after it; its `_isClosed`, `IsClosed`, `Close()` and the clears in `Recover`/`Reset` are deleted, and `Socket.Close`
  closes only the writers. Fixes Logging's audit tap going deaf to a client's orders after its first close: after any
  reconnect under the same name the audit had the server's answers but none of the operator's orders, amends or cancels.
  The `ServerSocket` already ended the session through its Open gate, so nothing else changes (see Spec.md "A reader
  never latches closed").
- `Provider/Server.cs` — `CancelAllOrders` is now `TryCloseOutOrders`, true once nothing waits on the exchange;
  `IsClientReady` is just that call, and `HasActiveOrders` / `IsOrderActive` are folded in (one walk, one read per slot).
  It closes out a Create the server never read (target Active, state row still the slot's previous order): stamped Done, its
  cancel still sent; before, that cancel was always refused and the name stayed `NotReady` for the week. It also repairs a
  target row the dead client left mid-write when the slot is not working (before, only working slots were repaired).
  A Create counts as unread only once the client is drained: a request/acknowledge handshake (`OnClientClosed` bumps a
  request with `Interlocked.Increment`; each CoreGroup thread acknowledges it at the end of a `ReadExecution` pass, writing
  only when it moved) proves no Create read just before the close is still being handled; until then its row keeps
  Active + Cancel, so the vendor's replay-on-ack can cancel it. The state row is read with `TryRead` (a torn read of a row
  the CoreGroup thread is writing could skip a live order). `Provider/Client.cs` — `ManualClient.Open`
  asks a `NotReady` name again once a second, for up to 5 s, before moving on to the next number (which took another client id for the
  week and could not see the stuck orders); only `AlreadyConnected` moves on.
- `Provider/Server.cs` — each cancel `CancelAllOrders` injects (a client closed, or a refused reconnect) is written to the
  client's server-to-client channel before it is sent, so the client's audit shows the cancel before its Done/Canceled (it
  was in no audit file). `Provider/Client.cs` ignores an `OrderTarget` on that channel instead of throwing.
- `Provider/Client.cs` — `Dispose` no longer throws when all 64 order slots are active (an unexplained guard from the first
  commit): the Mirror's `DetachGuis` threw there on every pass for a remote GUI with 64 working orders, so the Local mirror
  stopped for every remote and those orders were never cancelled (the socket never closed). Any client now closes when full.
- Breaks: audit files store the enum by name. Lines written before the rename say `"PendingNew"`, fail to parse, and are
  skipped by the Audit Trail; text searches must use the new name. Deploy with the C++ rename (it writes the name too).

## 2026-10-07 — a connect is always answered; the previous-orders check moves to the server; remote-mirror groundwork (`cpp_alignment_report_2026-10-07.md`)

Complete in C# and run (see Evidence). Deploy the server and every client together: an old client
takes a negative refusal code as its id.

- `Socket/Socket.cs` — the server answers every connect request exactly once. The reply's
  `SocketHeader.ClientId` is the id when 0 or more, otherwise a new `ConnectRefusal`
  (`NoClientIdAvailable -1, ServerError -2, AlreadyConnected -3, Closing -4, ReservedClientName -5,
  NotReady -6`); the layout is unchanged. Today's three silent returns (no id, already Open,
  Closing/Closed) left the client in `Connect` forever. `PollLetterBox` takes the request,
  `DecideConnect` decides, and the new `ReplyToClient` stores the answer (also used by `OpenClient`).
  A throw while deciding is answered `ServerError`; the listen loop's pass is wrapped in try/catch,
  so a bad request or a `ClientOpened`/`ClientClosed` handler no longer ends the thread that notices
  dead clients and cancels their orders. A request's own `ClientId` is forced to -1 before
  allocating. A first attach builds the socket before firing `ClientAllocated` (was the reverse; as
  `LoadClients` does). `ReplyToClient` empties a reply left by a client that died before reading it
  (Linux keeps the region), which used to spin the listen thread forever. New hook
  `IsClientReady(clientId)`, asked only when no live process holds the name (Detached/Disposed);
  false is answered `NotReady`. Client side: `ClientSocket.Connect` returns the id or throws with the
  refusal (`Closing` is re-asked every 100 ms under a fresh timestamp; the reply is matched on name,
  timestamp and process id). A refused socket is disposed, never closed: a second process maps the
  same rings as the live one, and `Close` would end the live session and cancel its orders. The exit
  action is kept and cancelled on `Dispose` (it closed regardless at shutdown, the same danger, and
  threw `ObjectDisposedException` after every `Client.Dispose`). `IsClosed` throws instead of
  returning -1.
- `Provider/Server.cs` — `IsClientReady`: a client whose previous process left an Active order is
  refused `NotReady`, and `CancelAllOrders` runs again (the cancel on close can fail). A name that is
  Open (connected and trading) is answered `AlreadyConnected` and its orders are never touched.
  `HasActiveOrders`/`IsOrderActive` define "still working" once, for both: the server's state row
  decides once it is about the slot's order (same `OrderId`), otherwise the client's target row
  (a Create not processed yet). Not "state Active or target Active", `CancelAllOrders`'s old test:
  `CancelAllOrders` stamps the target row Active and nothing sets it back, so a process that died
  with a working order was refused `NotReady` forever after the exchange cancelled it (found by the
  runtime harness). `CancelAllOrders` no longer re-cancels an order whose state row is Done. Behaviour change: `CancelAllOrders` now also runs on
  every refused connect attempt (each lifts the target's `Seq` by 1,000,000; accepted, not capped).
- `Provider/Client.cs` — `ThrowIfPreviousOrdersActive`, its `OrderRisk` clear and the 100 ms sleep
  are gone: the server admits a process only once every previous order is Done, and their last
  messages are in the ring long before `Recover` runs (milliseconds after admission); `Recover` stays.
  The clear was redundant: a validated Create resets its row (`RiskLayer.cs`, now commented), and
  nothing reads a slot's row before the process's own Create on it. A constructor that
  throws after `Connect` now calls `Dispose()` (base constructor and `ManualClient`'s body), so the
  server releases the name; it used to stay Open for the life of the process, and a retry in the
  same process hung. `Dispose` tolerates a half-built client (`_instrumentData ?? []`,
  `Context?.Dispose()`).
- `Provider/Context.cs` — `ServerContext.AllocateClientId` returns `ReservedClientName` (a client named
  after the server) and `NoClientIdAvailable` (no free id) instead of throwing, so the client hears the
  reason; `Server.LoadClients` throws on a negative result, keeping a broken clients file loud at
  startup. `TryGetClientId(in String128, out int)`: one name lookup for
  `ClientContext.GetClientIdFromMap` (same exception as before when absent),
  `ServerContext.AllocateClientId` (same outcome) and the mirror. `GetSharedArray(arrayId)` replaces
  `Mirror()`/`EnumerateSharedArray()`; `SharedArrayEnumerable` deleted (its seq test missed every
  field written in place: Timestamp, TradingStatus, QuantityAhead, RateLimits).
- `Socket/SharedArray.cs` — byte-level `RecoveryWrite(int, ReadOnlySpan<byte>)` beside `Write`;
  both go through `AsValue` (length check).
- `Provider/TCPServer.cs`, `Provider/Program.cs`, `Provider/Properties/launchSettings.json` deleted;
  `Provider.csproj` is a library. New `Mirror` project (in `HFT.slnx` and `Build.csproj`), a stub
  until the remote-workspace mirror lands. `Widget/SendOrderWidget.axaml.cs` comment no longer cites
  TCPServer.
- `Provider/Context.cs` — `Context.DirectoriesRoot` (static, default `S:\`, i.e. `/mnt/S/` on Linux)
  replaces the hard-coded `S:\` in `ClientContext`/`ServerContext.DirectoriesPath`; a program may set
  it at startup before any path is built. Paths and shared-memory names are unchanged without an
  override (checked: all four Realtime/Simulation directory paths equal the old strings). For a remote
  risk desk with no S: drive. `Workspace/Program.cs` — `-d <DirectoriesRoot>`, relative to the
  executable. `Mirror/MirrorConfig.cs` — `MirrorConfig.json` beside the executable (role, server name,
  clock mode, listen port, dial addresses, DirectoriesRoot); a missing file is written with defaults and
  the program stops naming it. Telegram keeps `S:\Telegram.json` (Tools cannot see Provider), so a
  remote desk sends no Telegram alerts.
- `Mirror/` — the remote-workspace mirror (Spec.md "Remote workspaces"): `TcpLink`/`TcpLinkConnector`
  (plain TCP, length-prefixed packets, reader and writer threads, 10 s silence timeout, redial every
  second; loopback-tested), `MirrorPacket` (Row, Message, Heartbeat, GuiList, GuiReply; names travel
  relative to `Context.DirectoriesRoot`), `MirrorPublisher` (Local: byte-compared rows every 100 ms,
  one ManualClient per listed remote GUI, closed when the GUI goes so the server cancels its orders;
  GUI commands replayed through it), `MirrorSubscriber` (Remote: the copy, a `ServerSocket` under the
  server's name, GUI ids from the Local end's `GuiReply`, refusals and `ConnectionBroken` for a GUI no
  ManualClient serves, Logging told about each GUI so its Audit Trail works), `Program` (config,
  startup, the two loops). `Socket.cs`: `ConnectRefusedException` carries the refusal reason.
- `Socket/Protocol.cs`, `Socket/SharedArray.cs`, `Provider/Context.cs` — every `SharedArray` row is framed at
  construction (`Protocol.Stamp`: `Magic` and `Length` where missing, never `Sequence`). The end-to-end test found the
  mirror shipped no order state: rows edited under the lock never get a frame from `Write`, and `TryRead` read them as
  Empty (the house-book allocation row too, so a remote server workspace refused its own orders). The server-header
  alias (a one-row array over the header `LetterBox`, "array 0") is deleted: the `LetterBox` reads `Length` as "posted",
  so a stamp would fake a post. The Mirror sends the header as its own packet (`MirrorPacketType.ServerHeader`) first in
  every pass; the remote posts the first into its `LetterBox` (`TryStore`) and writes later ones into that entry. The
  publisher skips a row being written (odd seq) instead of letting `TryRead` spin, forever on a row a dead client left
  mid-write (it stalled the Local Mirror in the test, and a GUI order then reached the exchange 13 s late).
- `Mirror/` — a Create refused before the server saw it (by the GUI's stand-in ManualClient, which checks 100 ms after the
  GUI did, or by the mirror) left the remote GUI's slot busy for good: no Done row follows a refusal the server never
  made. The Remote now writes the Done/Rejected state before any source-`Client` Create reject
  (`MirrorSubscriber.WriteRejectToGui`), which also replaces its own `ConnectionBroken` path; `ConnectionBroken` rejects
  are source `Client`. The Local end refuses an order that waited more than 1 s after arriving (`TcpLink` stamps arrival)
  instead of sending it late.
- `Mirror/` — order age is now measured on the Local end's own clock: each heartbeat carries its `Stopwatch`, the remote
  echoes the latest in every GUI message (`MirrorMessageHeader.CopyStopwatchTimestamp`), and an order echoing more than
  1 s ago is refused. The arrival stamp it replaces (removed from `TcpLink`) missed a stall of the whole process: the
  rerun froze the Local process 3 s and an order was acked 2.6 s after the click. `PublishRows` reads with the new
  `SharedArray.TryCopy` (never waits on a row mid-write; the odd-seq check before `TryRead` left a window). A Create the
  Remote refuses also gets its Done/Rejected written into the copy's state row, so the GUI's grid shows the refusal.
- `Mirror/` — from the second rerun: a cancel is never refused for age (a stall threw cancels away); the Remote takes the
  echoed Local clock before applying its pass (a stalled Remote stamped an old click with the newest heartbeat, and the
  order reached the server 2.5 s late); an attached GUI's state row from a snapshot no longer replaces the Remote's
  Done/Rejected for the slot's current order (a phantom order after a link drop); `MirrorPacket.RewriteName` throws a
  clear message when a name exceeds 128 characters (a long remote install folder crashed it with `FixedString<128>`).
- `Provider/Client.cs` — in simulation only an `AlgoClient` clears its audit files at startup; a GUI never touches them, so
  GUIs (and the Mirror's stand-ins after a link drop) restart within a run. `Workspace/Program.cs` — the simulation clock
  holds while the copy's header reads 1970 (a remote link down) instead of running unpaced.
- `Provider/Context.cs` — `OrderEnumerator.MoveNext` skips a slot mid-write instead of retrying: it spun forever on a row
  a dead client left mid-write, hanging the GUI's ladder, orders and send-order widgets.
- `Provider/Client.cs`, `Workspace/Workspace.axaml.cs`, `Mirror/MirrorPublisher.cs` — several workspaces per principal
  (strategy or server), on any machines. GUI names are `<principal>_GUI<n>` from 0 (was `<principal>_GUI`, so the first
  GUI's folder moves to `_GUI0`; its earlier audit stays under `_GUI`, and a mid-week restart's `LoadClients` holds the
  old name's id until Monday). `ManualClient.Open(principal, server)` takes the lowest free number, skipping
  `AlreadyConnected`/`NotReady`, at most 8; the constructor takes the GUI name and the principal explicitly instead of
  adding or stripping "_GUI". The Local Mirror opens exactly the name the remote lists (`TryGetPrincipalName`) and, within
  3 s of refusing a name, repeats that refusal at once instead of staying silent (the remote used to wait 10 s).
- `Spec.md` — new "Connect: every request is answered" and "Remote workspaces (the Mirror)"; "Client
  restart" rewritten for the server-side check; server workspaces are one at a time (the old "any
  number may be open" was wrong); the obsolete TCP-mirror item and the startup-window residual are
  marked resolved.
- Evidence: the solution builds (0 errors). Scratchpad harness (`handshake_harness`): a real
  `ServerSimulator` plus each client as a separate OS process. Second process under a live name,
  restart, Closing retry, previous order Active / Done (seeded and through the real close path),
  reserved name, throwing close handler, constructor failure in the same process: all PASS after the
  `IsOrderActive` fix (S3b failed before it). Linux leftover reply not reproducible on Windows.
  Report §6.

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

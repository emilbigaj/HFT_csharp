# Patch log

Newest first. Each entry says what changed, why, and what it broke or unblocked.

---

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

- **`RiskLayer` reservation throws client-side.** `Client.cs` builds it on the read-only
  `ContextManager.ServerContext` while `ValidateOrder` takes `GetRiskLimit(...).GetRef()`. Every
  client order is rejected with `ExceptionThrownByRiskLayer`. Gating the reservation block on
  `_orderRejectedSource == Server` fixes it and stops the client double-counting exposure.
- **Acked amend-down releases nothing.** `OnOrderState` computes `before` and `after` from the same
  new acked quantity, so `acked 10, amend to 3` yields a delta of 0 instead of −7. Needs the hook to
  run before `Server.OnOrderState` overwrites the stored state.
- **Rate limits are enforced nowhere.** `MaxOrdersPerSecond`/`MaxOrdersPerSession` are gone from
  `RiskLimit` and `RiskLayer` has no rate-limit members in either language. Deferred deliberately.
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

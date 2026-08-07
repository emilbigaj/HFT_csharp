# Patch log

Newest first. Each entry says what changed, why, and what it broke or unblocked.

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
- **`OrderRisk` caps order quantity at 55** (`TryAdd` refuses `qty >= 56`). Needs its own reject
  reason and a provisioning-time check that `MaxOrderQuantity <= 55`.
- **Order.hpp is not yet mirrored** for `RiskLimit`'s new fields. C++ `RiskLimit` is 40 bytes; C# has
  moved on. See `RiskLayerRefactorPlan.md` §5 Step 0.
- **Simulation defaults limits to `int.MaxValue`** (`GetMaxLimits`), so no backtest has ever exercised
  a quantity limit. `Scenario.SetRiskLimit(instrument, maxOrderQuantity, maxPositionQuantity)` is the
  hook for the certification harness.
- **Unknown message types are dropped silently** — `default: break` in `Server.ReadAdmin`/
  `ReadExecution`, `default: return ""` in `AuditWriter.SerializeToLine`. The `Header<T>` bug above
  was invisible for exactly this reason; counting or logging them would have surfaced it immediately.

See `RiskLayerRefactorPlan.md` for the full risk-layer design and the certification evidence plan.

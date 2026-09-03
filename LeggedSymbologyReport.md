# Legged Symbology & Header Rework — Change Report

Date: 2026-08-25. Build: clean (0 errors, 0 new warnings).

Files in this change set (other working-tree modifications predate it):

```
Data/Symbology.cs                        Data/Instrument.cs
Data/InstrumentDetails.cs                Provider/Context.cs
Simulator/ServerSimulator.cs             Widget/InstrumentHeadersWidget.axaml(.cs)
Widget/AuditTrailWidget.axaml(.cs)
```

---

## 1. Motivation

Spreads were a hard-coded long/short pair at every layer (symbology, shared-memory header,
allocation, GUI). Butterflies, condors, and strips need N weighted legs. This rework makes
"a structure with legs" the general representation and reduces the calendar spread to the
2-leg case. **No behavioral change for outright futures or forex.**

## 2. The canonical ticker format

The ticker string IS the structure — one formatter, one parser, stored verbatim as `Ticker`:

```
Future:  "ES M2025-12-19"
Spread:  "ES +M2025-12-19 -M2026-03-20"            (calendar)
Fly:     "ES +M2025-12-19 -2M2026-03-20 +M2026-06-19"
Symbol:  "Spread XCME ES +M2025-12-19 -M2026-03-20"   (unique; used as filename)
Short:   "ES +Dec2025 -Mar2026"                     (display only, not unique)
```

Rules:
- **Root appears exactly once**, up front. Leg tokens are `±[n]<MaturityTypeChar><yyyy-MM-dd>`.
- **The sign carries the weight**: `+` = +1, `-` = −1, `+2`/`-2` = magnitude folded in.
- **Legs are always maturity-ascending** — never long-then-short. This is the ordering
  invariant everywhere: ticker, `LeggedSymbology.Symbologies`, `LeggedHeader` leg slots.
- Long/short is derived from sign where a consumer needs it, never from position.

**Breaking**: the legacy spread format `"ES M2025-12-19 - M2026-03-20"` is no longer parsed
(support was added, then removed by decision — old files are to be deleted). Any surviving
old-format file name under `Z:\InstrumentDetails\...` or `Z:\TickHistory\...` will throw in
`Symbology.FromString` and **abort the whole search** (both search loops rethrow on a bad
file: `TickHistory.cs:105`, `InstrumentDetails.cs:108`).

## 3. Symbology layer — `Data/Symbology.cs`

- **New `LeggedSymbology : Symbology`** — general N-leg symbology:
  - `Symbologies` (`List<Symbology>`, one full symbology per leg) + parallel `Weights`.
  - Ticker built by `GetLegsTicker(root, legs)` from each leg's `Ticker` with the leg's own
    root stripped; `ShortSymbol` likewise from leg `ShortSymbol`s. Build and parse cannot
    drift: the ctor formats exactly what `FromString` consumes.
- **`SpreadSymbology : LeggedSymbology`** — one-line subclass fixing
  `InstrumentType.Spread`. `LongSymbology`/`ShortSymbology` are gone (nothing used them).
- **`Symbology.FromString`** spread branch rewritten: splits the remainder into signed leg
  tokens, parses sign + optional magnitude digits + maturity token, builds
  `FutureSymbology` legs + weights. Accepts any leg count — "spreads only" is not enforced
  here (see §7).
- `FutureSymbology.ShortSymbol` compacted to `"ES Dec2025"` (month-year concatenated).

## 4. Shared-memory header — `Data/Instrument.cs`

`SpreadHeader` (fixed long/short maturity pair) → **`LeggedHeader`**, inside the 128-byte
`InstrumentHeader128` overlay:

```
offset   0  InstrumentHeader   64 B
offset  64  Multiplier          8 B
offset  72  LegCount            4 B
offset  76  _reserved           4 B
offset  80  Leg0..Leg5       6 × 8 B   LegHeader { int InstrumentHeaderId; int Weight; }
total      128 B (exactly)
```

- **Legs reference sibling headers by `InstrumentHeaderId`**, not by copied maturities —
  the leg's own header is the source of truth; ids are stable and exist before the leg is
  ever subscribed (unlike `InstrumentId`, which is −1 until allocation).
- `Legs` exposes the live prefix as `Span<LegHeader>` (`[UnscopedRef]`), **clamped to
  [0, 6]** because `LegCount` is read from shared memory and `MemoryMarshal.CreateSpan`
  does no validation.
- A **static ctor guard** throws at startup if `sizeof(LeggedHeader) > 128` — a new field
  can never silently corrupt the shared array.
- `Symbology` resolves each leg through a static hook
  `LeggedHeader.GetLegHeader : Func<int, InstrumentHeader128>` (same pattern as
  `InstrumentDetails.GetLeg`), then constructs `SpreadSymbology` from leg symbologies +
  weights.
- `InstrumentHeader128.AsSpread()` renamed **`AsLegged()`**.
- Capacity: **6 legs** (spread 2, fly 3, condor 4, strips ≤ 6).

Note: the old layout deliberately overlaid `LongMaturityDate` on `FutureHeader.MaturityDate`;
the new layout does not. Irrelevant in practice — `Future`'s maturity accessors go through
`AsFuture()`, which type-checks and throws for spreads, exactly as before.

## 5. Allocation — `Simulator/ServerSimulator.cs`

`OnInstrumentDetails` now owns leg stamping (it is where header ids are minted):

- New `_instrumentHeaderIdBySymbol` map, populated at id assignment.
- Spread branch: resolve each `Leg` via `InstrumentDetails.GetLeg`, **sort
  maturity-ascending**, write `LegCount` + `LegHeader { headerId, weight }` slots.
  Throws explicitly past 6 legs.
- Ordering is guaranteed without recursion: building the spread's `Symbology.Symbol` (first
  line of the method) already resolves legs via `GetLeg`, so legs must have registered
  before any spread — the same alphabetical guarantee (`"Future …"` < `"Spread …"`) the
  symbol lookup always relied on.
- `InstrumentDetails.SpreadHeader` getter **deleted** — it could not know header ids, and
  the simulator was its only caller.

## 6. Contexts — `Provider/Context.cs`

- `CreateInstrument` spread branch: iterate `Legs`, map
  `InstrumentHeaderId → InstrumentId → Future`, long = positive-weight leg, short =
  negative. (The old code resolved `LongInstrumentId`/`ShortInstrumentId`, which the
  simulator stamped as −1 unconditionally — spread instrument creation could never have
  worked; it now works whenever the legs are allocated.)
- Ctor installs the resolver: `LeggedHeader.GetLegHeader = id => GetInstrumentHeader(id).Read();`
  — every context maps the same server header array, so repeated installs are equivalent.

`Data/InstrumentDetails.cs` `BuildSymbology` sorts legs by maturity before constructing
`SpreadSymbology` (leg file order is not trusted), keeping details-side and header-side
`Symbol` strings byte-identical — required, since `_instrumentDetailsBySymbol` and all file
names key on `Symbol`.

## 7. GUI — `Widget/InstrumentHeadersWidget`

Spread columns (`LongMaturityDate/Type`, `ShortMaturityDate/Type`, `Long/ShortInstrumentId`)
replaced by **`Leg0`–`Leg5`**: one cell per leg, rendered sign + cached short symbol
(`+ES Dec2025`, `-2ES Mar2026`) via `SymbolCache` — no symbology construction in bindings.
All six are regex-filterable and hidden by default. Old saved workspaces degrade gracefully
(unknown column states and filters are skipped on load).

## 8. Invariants (the contract going forward)

1. Legs are **maturity-ascending** in every representation.
2. The **sign carries the weight**; long/short derives from sign, never position.
3. The **root appears once**; leg tokens are root-free.
4. `Symbol` is unique and parseable (exact dates, never month names) — it is the filename
   and the lookup key.
5. Leg identity in shared memory is **`InstrumentHeaderId`** (stable pre-allocation), never
   `InstrumentId`.

## 9. Known limitations / follow-ups

- **The 2-leg assumption survives in exactly one place**: `Context.CreateInstrument`'s
  long/short mapping (a butterfly's second positive leg would overwrite `longLeg`) — plus
  the `Spread : Future` instrument class it feeds. Adding butterflies = generalizing that
  one site + consumer; header, symbology, simulator, and widget are already N-leg.
- Creating a spread **instrument** still requires its legs to be allocated instruments
  (headerId → InstrumentId is −1 otherwise). Consider allocating legs automatically when a
  spread allocates.
- `SpreadSymbology` does not validate leg count/weights; `LeggedSymbology` has no
  `[RegisterJson]` (fine while never serialized directly) and no
  `Symbologies.Count == Weights.Count` guard (a mismatch throws an opaque
  `IndexOutOfRangeException` in the ctor).
- **Operational**: delete all old-format spread files before the next run (see §2).

---

## Appendix A — AuditTrailWidget changes (same session, unrelated to legs)

- **`Source` column**: `"Algo"` / `"Manual"` per row, from `OrderId.IsAlgoOrder()`
  (`ClientId == StrategyId`). For `Position` rows the header is stamped by the last fill.
- **Dual audit directories**: the widget now reads both the primary tap
  (`<Primary>/Audit`) and the manual client's own `<name>_GUI/Audit` — previously manual
  order targets/acks were invisible because the GUI client's traffic is tapped into its own
  directory. History paging is a k-way merge across readers (per-reader parsed queues,
  newest head first), so paging stays newest-to-oldest globally; live lines from both
  readers share one handler. Readers are skipped if the two paths coincide.
- **Filters**: body right-click gains a Source (Algo/Manual) checkbox section next to the
  OrderType filters; header right-click gains regex filters for `ShortSymbol`, `Symbol`,
  `ClientOrderId` (mirrored from InstrumentHeadersWidget: prompt dialog, header decorated
  with the active pattern, all filters AND together). One predicate (`IsRowVisible`) now
  gates history, live inserts, and rebuilds alike. Filters are not persisted in
  `SaveStateJson` (unlike InstrumentHeadersWidget).

## Appendix B — Designed this session, not yet implemented

- **`FutureChain`** (continuous backtest across contract rolls): chain object holding
  maturity-ascending contracts with `Active`/`Next`/`Roll()`/`Rolled` and follower
  `AdvanceTo`; roll *decision* left 100% to the strategy; roll *execution* = old algo
  flipped to exit-only (`IsExiting`/`IsDone` flags on `TestingAlgo`), fresh algo bound to
  the next contract; per-chain profit series. Full code sketches live in the conversation;
  nothing applied.
- **`TickHistory.Repair()`**: trust only the sealed prefix (back-patched
  `PositionOfTomorrow`), truncate the torn trailing day, rebuild footer + day-open snapshot
  dated to the last sealed day so a resume works for any later date **including the crashed
  date** (which today silently corrupts the file — data overwrites the unsealed header and
  gets absorbed into the previous day's block). Sketch in conversation; not applied.

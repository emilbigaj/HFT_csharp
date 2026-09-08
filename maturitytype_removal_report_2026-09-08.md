# MaturityType removal — downstream migration report (2026-09-08)

Audience: any program or library built on the HFT lib that creates, names, parses, or downloads
instrument data — the Databento downloader in particular. The HFT lib (C# `main`) has **deleted the
MaturityType concept entirely**. This report explains why, exactly what changed, and what a
downstream codebase must do to match. The C++ trading system has its own alignment doc
(`cpp_alignment_report_2026-09-08.md`, amendment section at the top).

---

## 1. Why it was a problem

Symbols and therefore file names embedded a maturity-type letter directly in front of the date:

```
Future XCBT 10Y M2026-08-31.json
Future XCBT 10Y Q2026-06-30.json
```

Directory enumeration is lexical, so files grouped by the LETTER first: every `M` contract sorted
before any `Q` contract regardless of date. Header registration order follows file order, and
first-match selectors (`Scenario.GetFuture`: "first header with MaturityDate >= target") walked
that order — returning an August monthly when a June quarterly existed. Every consumer that
assumed name order == maturity order was silently wrong.

## 2. Why removal (not sorting workarounds)

- A bare ISO date sorts lexically == chronologically **by construction**; every consumer is fixed
  at once, forever, with no sort shims.
- `MaturityDate` alone identifies a contract. Verified empirically before migrating: across all
  187,087 catalog files, ZERO name collisions after removing the letter (no product has two
  schedules sharing a maturity date).
- The type was derivable metadata nothing consumed for logic ("months" filters use
  `MaturityDate.Month`); removing the concept deletes complexity instead of managing it.

## 3. New formats (the contract downstream must emit and parse)

| thing | old | new |
|---|---|---|
| Future ticker | `10Y M2026-07-31` | `10Y 2026-07-31` |
| Future symbol / details filename | `Future XCBT 10Y M2026-07-31` | `Future XCBT 10Y 2026-07-31` |
| Spread ticker | `10Y +M2026-07-31 -M2026-08-31` | `10Y +2026-07-31 -2026-08-31` |
| Weighted leg token (butterfly) | `+2M2026-07-31` | `+22026-07-31` |
| Tick-history filename | `Future XCBT 10Y M2026-07-31.MarketByOrder.Tick.zstd` | `Future XCBT 10Y 2026-07-31.MarketByOrder.Tick.zstd` |
| details JSON | `"MaturityType": "Month",` present | key gone entirely |

**Leg-token grammar (IMPORTANT — a real bug lived here):** the ISO date is the fixed-width
**last 10 characters** of the token; any digits between the leading sign and the date are the
weight magnitude (absent = 1). Do NOT scan digits left-to-right after the sign — the year is
digits, and with the letter gone there is no delimiter: a left-to-right scan eats "2026" as the
weight and hands "-07-31" to the date parser. This shipped and was caught; parse by fixed width.

**No legacy tolerance — loud fail:** a leading letter on a maturity token throws
(`Invalid maturity date: "M2026-07-31" (legacy maturity-type letters are not accepted — migrate
the catalog)`). Unmigrated catalogs crash on load, by design. Migrate before pointing anything at
them; do not add tolerance downstream either.

## 4. Exactly what was removed from the HFT lib code

- `Data/Symbology.cs` — the `MaturityType` enum (D/W/M/Q/Y) DELETED. `FutureSymbology`: property
  and ctor parameter removed; ctor is now `FutureSymbology(exchange, root, maturityDate)`; ticker
  built as `$"{root} {maturityDate.ToDateString()}"`. `Symbology.FromString`: maturity tokens are
  bare dates (`ParseMaturityToken`, legacy-letter tolerant); spread leg tokens use the fixed-width
  grammar above.
- `Data/Instrument.cs` — `FutureHeader.MaturityType` field removed (it was the tail byte after
  MaturityDate; no other field offsets moved). `Future.MaturityType`, `Spread.LongMaturityType`,
  `Spread.ShortMaturityType` properties removed.
- `Data/InstrumentDetails.cs` — `MaturityType?` property removed; `BuildSymbology` and the
  `FutureHeader` getter no longer reference it.
- `Simulator/ServerSimulator.cs`, `Simulator/Program.cs` — assignments removed.
- `Widget/InstrumentHeadersWidget` (.cs + .axaml) — display property and column removed.

JSON compatibility: System.Text.Json ignores unknown keys, so *reading* an old details file with a
`"MaturityType"` key still works; the lib simply never emits it again. (Same on the C++ side —
glaze must drop the key from its schemas.)

## 5. The catalog migration (already executed on the shared drives)

Migrated 2026-09-08, rename-only (nothing deleted), idempotent, collision-checked first:

- `Z:\InstrumentDetails\Databento` — **131,139** JSON files: contents rewritten (all embedded
  symbols/tickers/FileName/Legs letterless, `"MaturityType"` lines removed, UTF-8 BOM preserved)
  and files renamed. 0 failures.
- `Z:\TickHistory\Databento` — **55,948** files renamed. 0 failures.

The two transformations, for reuse on any catalog a downstream program owns:

```
name & content token:  regex  [DWMQY](?=\d{4}-\d{2}-\d{2})   ->  ""      (safe: roots like "M2K"
                                                                          never precede a date)
details JSON line:     regex  [ \t]*"MaturityType":\s*"[^"]*",?\r?\n  ->  ""
```

Procedure: (1) dry-run collision check — apply the name regex to every path, `sort | uniq -d`,
abort on any duplicate; (2) rewrite JSON contents; (3) `File.Move` renames (throws rather than
overwrites). The migration tool source is a ~70-line C# console (scratchpad `migrate/Program.cs`);
copy it rather than reinventing.

**NOT yet migrated — these now FAIL LOUDLY if loaded**: `Z:\TickHistory\Refinitiv`,
`Z:\TickHistory\RefinitivNew`, loose `Future XCBT HRS *.json` files at the `Z:\InstrumentDetails`
root, `Z:\InstrumentDetails\CatalogTest`, and any symbol-named server state (positions/risklimits/
fills) on live machines. `S:\Servers\Simulation` and `S:\Strategies\Simulation` were migrated
(57 files renamed 2026-09-08).

## 6. What a downstream program must change

1. **Recompile against the new HFT lib and follow the compile errors** — every use of the deleted
   enum/properties surfaces as an error; there is no silent behavioral fallback.
2. **Stop emitting**: no `MaturityType` in generated details JSON; letterless file names, symbols,
   tickers. For the Databento downloader specifically: wherever it maps Databento definitions
   (`10YN6` etc.) into `InstrumentDetails` and builds `FileName`/`Symbol`/`Ticker`, drop the
   letter and the property — the maturity *date* mapping is unchanged.
3. **Parsing**: adopt the bare-date token and the fixed-width leg grammar; tolerate a legacy
   leading letter.
4. **Migrate any catalogs the program owns** with the §5 procedure (collision check first).
5. Do not re-add a schedule/type field "for information" — if schedule classification is ever
   needed again, it is derivable from the maturity date pattern and belongs in analysis code, not
   in identity strings.

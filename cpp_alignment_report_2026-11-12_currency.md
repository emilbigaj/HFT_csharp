# C++ alignment report — 2026-11-12: every profit figure is USD

Amends §5 (shared memory and rings) and §7 (Context) of `cpp_alignment_report_2026-10-12.md`, and is
listed there as P48. Scope: **one enum, one header byte, one shared-memory array, one multiplier**.
Everything else in the 2026-10-12 report stands. C# is the source of truth: `Data/Currency.cs`,
`Data/Instrument.cs`, `Provider/Context.cs`, `Execution/Order.cs`, and Spec.md § "Every profit figure
is USD (2026-11-12)". The C# commit that adds this file is the reference commit.

## 1. What was wrong

`Instrument::GetProfit` was `(sell − buy) × Multiplier × quantity`, and `Multiplier` is the
contract's own multiplier **in the currency the contract quotes in**. Nothing converted. The catalog
has known the currency all along (`InstrumentDetails::Currency`), but it was never carried into the
shared-memory header, so no running process could see it.

There are two catalogs and they disagree. The Databento one (`Z:\InstrumentDetails\Databento\<exchange> <root>\`) is the
one every Scenario reads; the flat Refinitiv one (`Z:\InstrumentDetails\`) is the older set.

| Currency | Roots | Effect before this change |
|---|---|---|
| JPY | NIY, MNI, TPY | P&L in **yen**, shown as dollars: ~159× too big |
| EUR | ECF (EHR in Refinitiv only) | P&L in euro |
| CNH | CNH | P&L in offshore renminbi |
| NOK | ENK | P&L in krone |
| SEK | ESK | P&L in krona |

**The grains are a catalog bug and this change does not fix them.** ZC, ZS, ZW, ZL, MZC, MZL, MZS, MZW and HRS are quoted
in cents a bushel (ZC's tick of 0.25 is a quarter of a cent), so their P&L has always been cents shown as dollars — 100×
too big. Refinitiv labels them `USc`; **Databento labels them `USD`**. Until the Databento import is corrected they take
rate 1.0 and compute exactly what they computed before: no regression, no fix. Whoever ports this should expect the same,
and should not "fix" it in C++ — the fix belongs in the import.

Worked example, Nikkei at 50 000, one tick = 5 index points. One tick of NIY is 5 × ¥500 = ¥2 500,
which at 158.86 ¥/$ is **$15.74**; it was displayed as **2 500**. Beside it one tick of the dollar
Nikkei MNK is 5 × $0.50 = **$2.50**. The positions widget added them to **2 502.50** where the true
total is **$18.24** — out by a factor of 137, and in the flattering direction for a loss.

## 2. The design

The header carries the **currency** (static, per contract); a separate shared array carries the
**rate** (volatile, per currency). One write to a rate row revalues every instrument in that
currency; the header never has to be restamped. Risk limits are quantity-only, so no rate can affect
an order decision — a wrong rate misprices a display or a record, never a trade.

## 3. Wire layout

### 3.1 `Currency` — a byte enum, values are the contract

```
USD = 0, USc = 1, JPY = 2, EUR = 3, CNH = 4, SEK = 5, NOK = 6
```

Rename freely, **never renumber**. `USD = 0` is deliberate: an unstamped header reads dollars, which
is exactly what every header meant before this change, so a C++ server that has not yet been ported
produces today's behaviour rather than something worse.

### 3.2 `InstrumentHeader` gains one byte

```
  @72  Currency Currency;   // 1 byte, appended after TradingStatusTimestamp
```

`InstrumentHeader` is `StructLayout(Sequential, Pack = 1, Size = 128)` and used 72 of its 128 bytes,
so the field is appended at offset 72 and **no existing offset moves**. `FutureHeader` stays 145
bytes, `LeggedHeader` 192, both inside the fixed 256-byte `InstrumentHeader256` overlay. No shared
array's `TypeSize` changes from this field.

### 3.3 New shared array `<server>/ExchangeRates`

```
  SharedArray<double>, capacity Currencies::s_capacity = 16, indexed by the Currency value
```

Created **last** in `Context`'s constructor, after `<server>/Latency`, so no existing array id moves.
The capacity is 16, not the enum's current 7, so that adding a currency later does not change the
layout hash.

**This changes `GetLayoutHash()`** (it folds in every array's row size and capacity). Both Mirror ends
must deploy together: the CME box, the Threadripper relay, each desk and the phone. See §10 of the
2026-10-12 report.

Nothing else is needed for the Mirror: `LocalMirror::PublishRows` walks every array by id and
byte-diffs it generically, and `MirrorPacket::RewriteNames` touches only `SharedArray<SocketHeader>`,
so a `SharedArray<double>` replicates to a remote desk with no new packet type and no dispatch case.

## 4. The conversion

```cpp
  // Instrument
  double UsdMultiplier() const { return Multiplier * *exchangeRateEntry_; }
  double GetValue(double price) const { return price * UsdMultiplier(); }
  double GetProfit(double buy, double sell, int quantity) const { return quantity == 0 ? 0 : GetValue(sell - buy) * quantity; }
```

The rate is read **live** from the row, never cached in the instrument: a GUI open for a week must
see a new rate. The instrument is handed its currency's entry at construction (in C# because `Data`
cannot reference `Provider`; keep it in C++ anyway — it is one load with no lookup and no bounds
check on a path that runs per fill).

`PositionHeader::OnFill(fill, usdMultiplier)` takes the converted multiplier, so `RealizedProfit`
banks in dollars at the rate that applied when it was earned and never moves again — which is what
the clearer does when it sweeps the day's yen. Floating P&L revalues at the current rate, because it
should. **Every call site that passed `instrument.Multiplier` to `OnFill` must pass
`instrument.UsdMultiplier`**: in C# that is `Server::OnFill` (two rows per fill) and the position
transfer (two rows).

## 5. Where the rate comes from

`<server>/ExchangeRates/<currency>.exchangerate`, one JSON line appended per day, read with
`ReadLastLine` exactly as `.risklimit` is:

```json
{"Currency":"JPY","Timestamp":"2026-11-12 00:00:00.000_000_000","UsdRate":0.00629485}
```

`UsdRate` is **dollars for one unit of the currency** — the reciprocal of a 158.86 ¥/$ quote, not the
quote. CME's own 6J future quotes exactly this number, if a live source is ever wanted.

At startup, in write mode only:

1. Seed `USD = 1.0` and `USc = 0.01` in code. They are conversions, not quotes — nothing fetches them,
   and a box that trades only USD products then needs no files at all. A file still wins if present.
2. Loop `<server>/ExchangeRates/*.exchangerate`, take each file's last line, and refuse a rate that is
   not positive. **Only the files present are loaded**: a box that trades no yen needs no yen file.
   The filename is the currency, through `Currencies::FromString`, which throws on an unknown name —
   so a typo'd `JYP.exchangerate` fails loudly instead of being skipped.

## 6. The guard, and where it must not go

```cpp
  // NOT on the read path. The rate is written once at startup and cannot change during the run.
  static void ThrowIfExchangeRateIsNotValid(Currency currency, double usdRate)
  {
      if (!(usdRate > 0))   // NOT <= 0: every comparison against NaN is false
          throw ...;
  }
```

Two call sites, both cold: the startup load above, and `ServerContext::AllocateInstrument`, which
refuses an instrument whose currency has no rate rather than pricing it at zero.

Three things the port must get right:

- **`!(usdRate > 0)`, not `usdRate <= 0`.** Measured on .NET 10: `NaN <= 0` is `false`, so the
  `<= 0` form lets a missing rate straight through. The `!(> 0)` form rejects NaN, zero and negative
  and passes a real rate. IEEE 754 binary64 comparison is identical in C++.
- **In `AllocateInstrument` the check sits above `serverHeaderEntry.AcquireLock()`**, beside the
  existing `InstrumentIds.IsFull` throw. Throwing past the lock would leave the `ServerHeader` row
  mid-write forever.
- **No check on the hot path.** In `Server::OnFill` the `instrument.UsdMultiplier` expression is
  evaluated inside the window between `AcquireLock()` on two position rows and their release; a throw
  there bricks both rows for every reader in the system.

NaN vs zero as the sentinel: NaN means the file exists but is unreadable or empty; zero means nothing
ever wrote the row (shared-memory pages are zero-filled). One guard catches both. Note that C#
`double.NaN` is `0xFFF8000000000000` (sign bit set, measured) while C++
`std::numeric_limits<double>::quiet_NaN()` is `0x7FF8000000000000`; both are quiet NaNs and behave
identically, but the Mirror byte-diffs rows, so if both ends ever wrote NaN to the same row with
different patterns that row would resend on every pass. Only the server writes these rows, so this
cannot arise today — keep it that way.

## 7. Port checklist

1. `Currency` enum with the seven explicit values above.
2. `InstrumentHeader::Currency` at offset 72, and **whoever builds an instrument header sets it from
   the catalog's `Currency` string**. `CmeServer` is that writer in production. A spread takes its
   legs' currency, not its own file's — the legs are the source of truth (the C# simulator does this).
3. `<server>/ExchangeRates` shared array, `double`, capacity 16, created last.
4. `Context::GetExchangeRate(Currency)` returning the row by reference.
5. The startup load and the two `ThrowIfExchangeRateIsNotValid` call sites.
6. `Instrument::UsdMultiplier`, `GetValue` through it, and every `OnFill` call site converted.
7. Redeploy every Mirror end together — the layout hash has changed.

Until step 2 is done on the C++ side, every instrument answers `USD` and prices exactly as it does
today. That is safe for the USD book but **means a yen or grain instrument is still wrong**, so do not
allocate NIY, MNI, TPY or a grain on a C++-fed server until `Currency` is stamped.

## 8. Verification (run on the C# side, 2026-11-12)

A harness drove the real `ServerSimulator` and `ServerContext` with three instruments taken unmodified from the Databento
catalog — MNK (USD, ×0.5), MNI (JPY, ×50), ZC (×5000). All assertions passed:

- `USD = 1.0` and `USc = 0.01` seed with no files present.
- With `JPY.exchangerate` holding two lines, the **last** line wins (0.0064516, not the earlier 0.007).
- MNK `UsdMultiplier` 0.5; ZC `UsdMultiplier` 5000 — unchanged, because the catalog calls corn `USD` (see §1).
- MNI `UsdMultiplier` 0.32258; one MNI tick is **$1.6129**, not 250; one MNK tick is **$2.50**
  unchanged; the two sum to **$4.1129** where the old code summed to 252.50.
- `PositionHeader::OnFill` round turn of 2 lots over 100 index points banks **$64.516**, not ¥10 000.
- With no `JPY.exchangerate`, allocating MNI is refused with
  `ThrowIfExchangeRateIsNotValid(JPY): rate is 0, must be greater than 0`, and the server stays usable
  afterwards (the next allocation succeeds — the `ServerHeader` row was not left locked).
- With an empty `JPY.exchangerate`, the server refuses to start:
  `rate is NaN, must be greater than 0`.

## 9. Open items

- **Replays use the file's newest rate**, not the rate of the replay's date. Accepted (2026-11-12):
  the load is `ReadLastLine`, matching `.risklimit`. A dated scan would make a 2022 Nikkei replay value
  its yen in 2022 dollars; it is not worth the code until a yen backtest matters.
- **No live rate source.** The file is written out of band (a script on the box, before start). The 6J
  front month's price *is* dollars per yen if that is ever wanted, at the cost of a subscription and a
  roll per backtest.
- **Nothing converts a currency a desk's screen shows**, because every figure is already USD. Prices
  stay in the contract's own quote (¥50 000 for the Nikkei), which is correct and untouched.

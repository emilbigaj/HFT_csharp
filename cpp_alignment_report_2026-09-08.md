# C++ alignment report — 2026-09-08 (supersedes cpp_alignment.md and cpp_alignment_report_2026-09.md)

> **Amended by `cpp_alignment_report_2026-09-10.md`** — read this report first, then that one; where
> they disagree the 2026-09-10 report wins (`OrderTarget` 52 bytes, `RiskLimit` 32 bytes,
> `ControlRiskLimit`, `OrderRisk` layout, threading model, NicTimestamp stamping, TradingStatus).
> Then `cpp_alignment_report_2026-09-14.md` (client-side update coalescing: `Instrument` /
> `Position` apply-then-raise, one callback per `ReadSocket` pass; no wire change).
> Then `cpp_alignment_report_2026-09-22.md` (two session contracts `RiskLayer` depends on:
> acceptance before trade, and In-Flight Mitigation always on; no wire change).

Compared **C++ github.com/emilbigaj/HFT_cpp `aeac53c`** (fresh clone, verified file-by-file
2026-09-08) against **C# github.com/emilbigaj/HFT_csharp `2e1ddfa`** (the spread trading vertical).
C# is the source of truth for every item. Both older alignment docs are folded in here; read only
this one.

**About the baseline:** `aeac53c` is a squash push that already contains *some* late-August work —
`RAIISpinLock` (Tools/SeqLock.hpp:234), `ExchangeOrderId` on OrderState, MBO comments — but not
others (ClientStatus is still 3-state, RiskLimit is still 40 bytes). Do NOT assume an item is
present or absent from its age; every section below states what the clone actually has.

**Deploy discipline:** Part A is shared-memory/wire bytes. C# `2e1ddfa` is already live in sim with
the new shapes. C++ must land ALL of Part A in one deploy, in lockstep with restarting anything
built against the old shapes. JSON fill/state lines written before the change do not parse after.

## Why these changes exist: spread trading

The C# side now trades CME calendar spreads end-to-end, and the guiding decision is that **a spread
is imaginary**: economically a spread position IS its outright legs, so risk limits, reservations,
positions and P&L all live on the legs — the spread instrument keeps only its own order book, its
order flow, and a volume-accounting row. That single decision forces most of this report: risk
validation decomposes an order across its legs by weight (an outright is just the 1-leg case), the
spread class stops pretending to be a Future (no multiplier, no maturity of its own), and
allocation must materialize the legs everywhere the spread goes.

The other driver is what a spread execution actually looks like on the wire. **One spread fill is
ONE atomic event carrying one OrderState plus a span of fills, in this order: the OrderState
(spread order id, spread units), then the spread instrument's own fill (spread price/quantity —
volume accounting only, no risk), then leg0's fill, leg1's fill, and so on** — each leg fill is the
order's OrderId with only `InstrumentId` rewritten to the leg, quantity = spread qty x weight
(signed), and the leg's assigned price. CME assigns those leg prices at increments finer than the
outright's trading tick, which is why `Fill` now carries a `double Price` instead of ticks; and
because state, positions and risk must never be observable half-applied, the whole span commits
inside one lock envelope (`Server::OnFill`, B3) and arrives from the vendor as one call (B4).
Everything in Part A/B is in service of those two facts.

---

# AMENDMENT 2026-09-08 (later the same day): MaturityType is DELETED

Supersedes every mention of MaturityType/ExpiryType below. C# removed the concept entirely because
the type letter in file names broke lexical-order == maturity-order (all M-files sorted before any
Q-file, so first-match instrument selection returned wrong contracts). MaturityDate alone
identifies a contract.

- **Delete the enum** (C++ still has `ExpiryType`) and the field from `FutureHeader` (it was the
  tail byte after MaturityDate — removal does not shift other fields; glaze drops the key).
  A7's rename instructions for MaturityType/ExpiryType are void; the MaturityDate rename stands.
- **Ticker format**: `"ES 2025-12-15"` (bare ISO date, no letter). Spread legs: `"+2025-12-15"`,
  weight magnitude between sign and date (`"+22025-12-15"` = weight 2). **Leg-token grammar: the
  date is the fixed-width LAST 10 chars of the token; digits between sign and date are the
  magnitude.** Do not scan digits left-to-right after the sign — the year is digits and the old
  letter was the only delimiter (this bug shipped in C# and is fixed at Symbology.FromString).
- **No parser tolerance — loud fail**: a leading legacy letter on a maturity token throws a
  FormatException naming the token. Unmigrated catalogs crash on load, by design; migrate them,
  mirror the same strictness in C++.
- **InstrumentDetails**: no MaturityType property; the JSON key is gone from all migrated files.
- **Catalogs migrated 2026-09-08**: `Z:\InstrumentDetails\Databento` (131,139 files renamed +
  contents rewritten) and `Z:\TickHistory\Databento` (55,948 files renamed). NOT yet migrated:
  Z:\TickHistory\Refinitiv, RefinitivNew, loose files at the InstrumentDetails root, CatalogTest —
  and any symbol-named files on the LIVE server (positions/risklimits/fills), which the C++ side
  must migrate the same way at its deploy.

# Part A — wire structs, byte-for-byte (BLOCKING, do these first)

## A1. `Fill` — price is a double now (Execution/Order.hpp:271)

The clone has `[Header][OrderHeader][FillType][Reserved3][FillId][OrderProfile]` (52 B — and note
its field order already disagreed with the old C#, which had FillId before OrderProfile; that
latent divergence dies here). New layout, 64 bytes, `Price` at offset 40 (8-aligned):

```cpp
#pragma pack(push, 1)
struct Fill
{
    // 64 bytes; Price sits at offset 40 (4+28+8), naturally 8-aligned. Price is a PRICE, not
    // ticks: spread leg fills are assigned at increments finer than the leg's trading grid (CME
    // leg pricing), so a fill is a terminal price fact — never quantize it back to a grid.
    Data::Header<OrderType> Header = Data::Header<OrderType>(OrderType::Fill); //  4
    Execution::OrderHeader  OrderHeader;                                       // 28
    uint64_t                FillId = 0;                                        //  8
    double                  Price = 0.0;                                       //  8  (offset 40)
    int32_t                 Quantity = 0;                                      //  4  signed: sells negative
    Execution::FillType     FillType = Execution::FillType::Maker;             //  1
    uint8_t                 Reserved[11] = { 0 };                              // 11  -> 64 total

    int32_t Sign() const { return (Quantity > 0) - (Quantity < 0); }
    Execution::Side Side() const { return static_cast<Execution::Side>(Sign()); }
};
#pragma pack(pop)
static_assert(sizeof(Fill) == 64);
static_assert(offsetof(Fill, Price) == 40);
```

- `OrderProfile` is GONE from Fill (orders/states keep it — orders genuinely live on the tick grid).
- Every consumer moves from `fill.OrderProfile.Quantity/.Ticks/.Sign()` to
  `fill.Quantity` / `fill.Price` / `fill.Sign()`.
- Glaze keys: `Header, OrderHeader, FillId, Price, Quantity, FillType`. Old fill JSON
  (`OrderProfile` shape) no longer parses — rotate live `Fills/` and audit files at deploy.
- Doctrine for Spec: **fill Price is terminal data — never re-quantized to any grid, never
  compared for equality.**

## A2. `OrderState` — reorder + TimeInForce + OrderStateReason (Order.hpp:363)

Clone has `Status + Reserved[3]` BEFORE `OrderProfile`, no TimeInForce, no reason. C# layout:

```cpp
struct OrderState
{
    Data::Header<OrderType>    Header;           //  4
    Execution::OrderHeader     OrderHeader;      // 28
    uint64_t                   ExchangeOrderId;  //  8  (already present in the clone — keep)
    Execution::OrderProfile    OrderProfile;     //  8
    Execution::TimeInForce     TimeInForce;      //  1  (Day=0, GTC=1, IOC=2, FOK=3, OpeningAuction=4, ClosingAuction=5)
    Execution::OrderStateStatus OrderStateStatus;//  1  (Done=0, Active=1 — unchanged)
    Execution::OrderStateReason OrderStateReason;//  1  (NEW — A3)
    uint8_t                    Reserved[1];      //  1
    int32_t                    QuantityFilled;   //  4  signed: sells negative
    int32_t                    QuantityAhead;    //  4
};
```

## A3. `OrderStateReason` replaces `OrderStateDoneReason` (Order.hpp:33)

```cpp
enum class OrderStateReason : uint8_t
{
    Unknown = 0,
    PendingNew = 1,
    Acked = 2,
    Fill = 3,        // partial vs complete lives in OrderStateStatus: Fill+Active / Fill+Done
    Canceled = 4,    // here onwards -> Done unconditionally
    Rejected = 5,    // create rejected, not amend/cancel rejected
    Eliminated = 6,
};
```

Semantics (FIX): Reason = ExecType (*why published*), Status = OrdStatus (*what the order is*). A
cancel preserves `OrderProfile.Quantity`. There is no PartialFill/Filled pair — one `Fill` reason,
set once per fill event. Delete `OrderStateDoneReason` and every use. (C# still *declares* the dead
`OrderStateDoneReason` enum at Order.cs:34 — vestigial, unused; safe to ignore or delete on both
sides.)

## A4. `OrderRejectedReason` — the 36–63 range has DIVERGED numerically (Order.hpp:47)

These values cross the wire as bit positions in `OrderRejected.OrderRejectedReasons` (Bitset64).
0–35 and 40–44 agree. Everything else must be renumbered to the C# column:

| value | C# (truth)                  | C++ clone today          |
|------:|-----------------------------|--------------------------|
| 23    | OrderTypeNotSupported       | — (absent)               |
| 36    | DuplicateOrderId            | — (absent)               |
| 45    | TooManyActiveTargets        | AlgoIsPaused             |
| 46    | AlgoIsPaused                | — (absent)               |
| 50    | NotInSession                | NotInSession ✓           |
| 51    | PositionIsSuspended         | PositionIsSuspended ✓    |
| 52    | QuantityExceedsRiskLimit    | QuantityTooLarge         |
| 53    | QuantityTooLarge            | PositionTooLarge         |
| 54    | PositionExceedsRiskLimit    | NotEnoughMargin          |
| 55    | NotEnoughMargin             | TooManyOrdersPerSecond   |
| 56    | TooManyOrdersPerSecond      | TooManyOrdersPerSession  |
| 57    | TooManyOrdersPerSession     | MessageEfficiencyViolated|
| 58    | MessageEfficiencyViolated   | — (absent)               |
| 59    | TooManyActiveOrders         | — (absent)               |
| 60    | NotAuthorizedToTrade        | ExceptionThrownByRiskLayer|
| 63    | ExceptionThrownByRiskLayer  | — (absent)               |

`PositionTooLarge` does not exist in C# — delete it. Copy the C# enum verbatim.

## A5. `RiskLimit` — 32 bytes, rate limits deleted (Order.hpp:144)

**Amended 2026-09-10: `StrategyId` is REMOVED — risk limits are server-wide, one row per
instrument.** (This section said 36 bytes with `StrategyId` at offset 16 until then.)

Clone still has the 40-byte struct with `RateLimit` members. C#:

```cpp
#pragma pack(push, 1)
struct RiskLimit
{
    Data::Header<OrderType> Header = Data::Header<OrderType>(OrderType::RiskLimit); // 4
    int32_t         InstrumentId = 0;
    Tools::Timestamp Timestamp = Tools::Timestamp::MinValue();   // 8
    int32_t         MaxOrderQuantity = 0;
    int32_t         MaxPositionQuantity = 0;
    int32_t         WorstLongWorkingQuantity = 0;                // aggregate, >= 0
    int32_t         WorstShortWorkingQuantity = 0;               // aggregate, <= 0
};
#pragma pack(pop)
static_assert(sizeof(RiskLimit) == 32);
```

Helpers: `GetLongQuantityAllowance(pos) = max(0, MaxPositionQuantity - pos - WorstLong)`,
`GetShortQuantityAllowance(pos) = min(0, -MaxPositionQuantity - pos - WorstShort)`. Update
`GetMaxLimits/GetMinLimits` (set `Timestamp = Clock::Now()`), and the glaze schema — `.risklimit`
files round-trip between processes. Restore/allocate paths zero both `Worst*` fields
(C# `Context.AllocateInstrument`, reservations never survive a restart).

## A6. `AllocateInstrument` — add ExchangeInstrumentId (Provider/Allocate.hpp:34)

Insert `int32_t ExchangeInstrumentId = -1;` between `InstrumentId` and `Symbol`. C# order:
`Header, ClientId(-1), InstrumentHeaderId(-1), InstrumentId(-1), ExchangeInstrumentId(-1),
Symbol(String64)`.

## A7. Instrument headers: Maturity renames + `SpreadHeader` → `LeggedHeader` (Data/Instrument.hpp)

Renames (layout-neutral, JSON-visible; the clone still has Expiry*): `ExpiryType` →
`MaturityType`, `FutureHeader.ExpiryDate` → `MaturityDate`; update glaze keys. The catalog on
`Z:\InstrumentDetails` already uses the new keys (`FirstTradeTimestamp`, `MaturityType`,
`MaturityDate`, and `Legs` with Weight/Symbol/ExchangeInstrumentId per leg).

The clone's `SpreadHeader` (fixed Long/Short expiry pairs, ~:146) is REPLACED by `LeggedHeader`
inside the 128-byte overlay:

```cpp
struct LegHeader                       // 8 bytes
{
    int32_t InstrumentHeaderId;        // sibling header id, NOT instrument id
    int32_t Weight;                    // signed: calendar = +1 front, -1 back
};

struct LeggedHeader
{
    // 128-byte overlay budget: InstrumentHeader 64 + Multiplier 8 + LegCount 4 + reserved 4 + 6*8 legs = 124.
    Data::InstrumentHeader InstrumentHeader;
    double   Multiplier;               // VESTIGIAL: spreads have no multiplier (legs carry them);
                                       // field kept for byte parity until a coordinated layout trim
    int32_t  LegCount;
    uint8_t  Reserved[4];
    LegHeader Leg0, Leg1, Leg2, Leg3, Leg4, Leg5;

    std::span<LegHeader> Legs() { return { &Leg0, static_cast<size_t>(std::clamp(LegCount, 0, 6)) }; }
};
static_assert(sizeof(LeggedHeader) <= sizeof(InstrumentHeader128));
```

`InstrumentHeader128::AsLegged()` gets the same type-guard as `AsFuture()` (throw unless
`InstrumentType == Spread`). Symbology for spreads is built from the legs
(maturity-ascending, signed-weight prefix: `"10Y +M2026-07-31 -M2026-08-31"`), resolved through a
`GetLegHeader(headerId)` hook the context installs.

## A8. Enum/type-byte parity (verify numerically, no changes expected)

`OrderType`: OrderState=10, OrderTarget=11, OrderRejected=12, Fill=13, Position=14,
AheadOfOrder=15, RiskLimit=16. `OrderStateStatus`: Done=0/Active=1. `FillType`,
`OrderRejectedSource` (Client=0, Server=1, Rival=2, Exchange=3), `AllocateType`, `ControlType`,
`TimeInForce` — diff value-for-value against C#.

## A9. `ServerHeader.Persistance` — still absent (Socket/Socket.hpp)

One byte at offset 172, `sizeof(ServerHeader) == 173`. Part of C1.

---

# Part B — the spread vertical (new behavior; C# `2e1ddfa`)

The model in one sentence: **a spread is imaginary** — risk, positions and P&L live on the outright
legs; the spread instrument keeps only its book, its order flow, and a volume-accounting row.

## B1. `Instrument::Legs` — the cached leg view (Data/Instrument.cs)

```cpp
struct InstrumentLeg { int32_t InstrumentId; int32_t Weight; };   // 8 B, resolved instrument ids

// Instrument (base): every instrument is its own single leg.
// ctor: _legs = { { InstrumentId(), 1 } };
std::span<const InstrumentLeg> Legs() const { return _legs; }
bool IsLegged() const { return _legs.size() > 1; }   // > 1, NOT > 0 — base seeds one self-leg
```

- Frozen at construction (header identity is immutable — same invariant as SymbolCache).
- **`Spread` derives from `Instrument`, NOT from `Future`** (the clone has `Spread final : public
  Future` at Instrument.hpp:491 — change it). Spreads have no multiplier, no maturity of their
  own; `Long()/Short()` remain as `Future` references for quoting, `LongMaturityDate()` etc.
  forward to the legs.
- Spread ctor resolves `_legs`; current runtime scope is 2-leg ±1 calendars:
  `_legs = { { long.InstrumentId(), +1 }, { short.InstrumentId(), -1 } }`; header weights beyond
  that (butterfly ±2, N-leg) are out of scope and should throw loudly at construction.

## B2. RiskLayer — per-leg validation and release (Provider/RiskLayer.cs)

The clone's RiskLayer has header/seq checks + MaxOrderQuantity only. Port the whole block; the
outright is the 1-leg degenerate case, so this replaces (not wraps) single-instrument logic.

The single home of aggregate arithmetic — every hook and the validator commit go through it:

```cpp
// Aggregates are per LEG (an outright is its own single leg, weight +1). Applies an ORDER-unit
// magnitude delta (negative = release) to each leg's side of exposure. legSide = orderSide *
// sign(weight) — the sign is applied exactly ONCE, here; signing anywhere else squares it away
// and drives the short aggregate positive (see Spec.md).
void ApplyWorstWorkingQuantityDelta(OrderId orderId, int32_t orderSideSign, int32_t magnitudeDelta)
{
    if (magnitudeDelta == 0) return;
    for (const InstrumentLeg& leg : _serverContext.GetInstrument(orderId.InstrumentId()).Legs())
    {
        int32_t legSide = orderSideSign * ((leg.Weight > 0) - (leg.Weight < 0));
        int32_t legMagnitudeDelta = magnitudeDelta * std::abs(leg.Weight);
        RiskLimit& riskLimit = _serverContext.GetRiskLimit(leg.InstrumentId).GetRef();
        riskLimit.WorstLongWorkingQuantity  += legSide > 0 ? legMagnitudeDelta : 0;
        riskLimit.WorstShortWorkingQuantity -= legSide < 0 ? legMagnitudeDelta : 0;
    }
}
```

`ValidateOrder` risk block (server-source only), in this exact order:

```cpp
if (!isCancel)
{
    // 1. Max order quantity per leg, in LEG units — BEFORE TryAdd, so rejects need no back-out.
    //    (weight-2 legs consume double; reject reason QuantityExceedsRiskLimit)
    for (leg : instrument.Legs())
        if (std::abs(workingQuantity * leg.Weight) > GetRiskLimit(leg.InstrumentId).MaxOrderQuantity) reject;

    // 2. The ONE pre-verdict mutation, with its first-class inverse (Reject) on any breach:
    int32_t worstBefore = orderRisk.GetAbsWorstOrderQuantity(ackedOrderQuantity);
    if (!orderRisk.TryAdd(orderTarget.OrderProfile.Quantity, reason)) reject;
    int32_t worstMagnitudeDelta = orderRisk.GetAbsWorstOrderQuantity(ackedOrderQuantity) - worstBefore;

    // 3. Phase 1 — PURE: check every leg, write nothing. The magnitude delta is >= 0, so
    //    legDelta's own sign IS the leg's side — routing by the ORDER's sign corrupts every
    //    negative-weight leg (a buy calendar reserves the back leg SHORT, not long).
    for (leg : instrument.Legs())
    {
        int32_t legDelta = worstMagnitudeDelta * sign * leg.Weight;
        bool breach = legDelta >= 0
            ? legPosition + riskLimit.WorstLongWorkingQuantity  + legDelta >  riskLimit.MaxPositionQuantity
            : legPosition + riskLimit.WorstShortWorkingQuantity + legDelta < -riskLimit.MaxPositionQuantity;
        if (breach) { orderRisk.Reject(quantity); set PositionExceedsRiskLimit; return false; }
    }

    // 4. Phase 2 — commit through the SAME arithmetic the release hooks use. Single-writer:
    //    nothing can change between the phases, so check-then-apply is atomic by ownership.
    ApplyWorstWorkingQuantityDelta(orderTarget.OrderHeader.OrderId, sign, worstMagnitudeDelta);
}
```

The four hooks become one-liners (`sideSign = Buy ? 1 : -1`):
- **Acked**: magnitude delta around `orderRisk.Ack(qty)` → `Apply(orderId, sideSign, delta)`
- **Done**: `released = worst - abs(QuantityFilled)`, zero the OrderRisk →
  `Apply(orderId, sideSign, -released)`
- **OnFill**: `Apply(fill.OrderHeader.OrderId, fill.Sign(), -abs(fill.Quantity))` — raw fill
  quantity, NOT a state delta: per-fill releases + the Done remainder telescope to exactly the
  reserved worst, per leg. A leg fill IS an outright fill.
- **Exchange Rejected**: magnitude delta around `orderRisk.Reject(qty)` → `Apply(..., delta)`

Also port `OrderRisk` (64 bytes, TryAdd/Ack/Reject/GetAbsWorstOrderQuantity — see C#
Execution/Order.cs; **layout amended 2026-09-09**: count + cached max + 30 × uint16 compact array,
not the bucketed bitset — take the field list from cpp_alignment.md §3) and the client-side
`CancelIsActive` guard extension:

```cpp
bool existingWillCancel = existingTarget.OrderHeader.OrderId == orderState.OrderHeader.OrderId
    && existingTarget.OrderProfile.Sign() * (existingTarget.OrderProfile.Quantity - orderState.QuantityFilled) <= 0;
if (existingTarget.OrderTargetAction == OrderTargetAction::Cancel || existingWillCancel)
    reasons.Set(CancelIsActive);   // <= 0, and the generation guard on OrderId is essential
```

`_maxClientOrderId` monotonicity stays CLIENT-side only (a server-side per-client vector races
across CoreGroups — deliberately removed).

## B3. `Server::OnFill` — one atomic fill event, span of fills (Provider/Server.cs)

The clone's OnFill (Server.hpp:~520) locks the two position rows separately, applies one fill, and
never touches the state row. Replace with the unified transaction:

```cpp
void OnFill(OrderState& orderState, std::span<Fill> fills)
{
    // identity: every fill's GlobalIndex must equal the order's (leg fills differ ONLY in
    // OrderId.InstrumentId); stamp NicTimestamps
    // acquire, in fills order, server row THEN local row per fill instrument (mirror convention):
    for (fill : fills) { GetPositionHeader(instr).AcquireLock(); GetPositionHeader(strategy, instr).AcquireLock(); }

    WriteOrderState(orderState);          // row write + risk ledger ONLY — no forward, no callback

    for (fill : fills)
    {
        Instrument& instrument = GetInstrument(fill.OrderHeader.OrderId.InstrumentId());
        GetPositionHeader(instr).GetRef().OnFill(fill, instrument.Multiplier());
        GetPositionHeader(strategy, instr).GetRef().OnFill(fill, instrument.Multiplier());
        // A legged instrument's own fill is accounting only (volume/position view on the spread
        // row); risk lives on the legs — releasing here would double-release what the leg fills
        // already covered. Risk is an outright concept.
        if (!instrument.IsLegged())
            _riskLayer.OnFill(fill);
    }

    for (i = fills.size()-1; i >= 0; --i) { release local; release server; }   // reverse order

    // AFTER release: forward state once, then per fill: fill + local position; audit per fill:
    // fill + server position; then OrderState callback once, Fill callback per fill.
}
```

- `WriteOrderState` = the private row-write half of OnOrderState (write under the state entry's
  seqlock + `_riskLayer.OnOrderState`); public `OnOrderState` = WriteOrderState + forward +
  callback. OnFill must call the PRIVATE half or the state is sent twice from inside the lock.
- `PositionHeader::OnFill(const Fill&, double multiplier)` — the tickSize parameter is GONE
  (Order.hpp:468 has the old 3-arg form): `price = fill.Price` directly.
- Era-rule reader pairs with this: the Algo's position read validates the position row's seqlock
  around SnapshotActives (actives FIRST, then position, seq re-check; odd seq = spin) — see C#
  Strategy/Algo.cs GetPositionQuantity. The whole actives/zipper layer is absent from the clone;
  when it is ported, that loop and the implicit-cancel enumeration
  (`targetIsCancel = ... <= 0`, gated on !targetRejected) come with it.

## B4. Live delivery contract — one ER, one call

The vendor/iLink3 session hands `Server::OnFill` the resulting `OrderState` TOGETHER with all the
fills of that execution as one span:
- fills[0] = the order's own instrument (for a spread: spread id, spread-unit quantity, the traded
  spread price) — the volume-accounting fill;
- one fill per leg: copy the order's OrderId and overwrite ONLY `OrderId.InstrumentId` with the
  leg's id; quantity = spread qty x weight (signed); **Price = the leg price straight off the wire**
  (iLink3 sends decimals; they are often finer than the outright tick — that is why A1 exists).
- An outright ER is the same call with a 1-element span. Never route the state and its fills as
  two independent calls — that split caused the live PositionExceedsRiskLimit incident.

## B5. Allocation unions — legs exist before the spread, everywhere

**Server** (`OnAllocateInstrument(clientId, ...)`, Server.hpp:481): before allocating a
Spread-typed header, allocate each leg with the full client path but NO admin echo — the client's
GetInstrument handshake is strictly one-request-one-reply, and a per-leg reply desynchronizes it
(the client reads a leg's reply as the spread's — this bug shipped and was caught):

```cpp
void OnAllocateInstrument(int32_t clientId, AllocateInstrument& allocateInstrument)
{
    if (header is Spread)
        for (legHeader : legged.Legs())
        {   AllocateInstrument legMsg = allocateInstrument;
            legMsg.InstrumentHeaderId = legHeader.InstrumentHeaderId;
            OnAllocateInstrument(clientId, legMsg, /*writeAdminReply*/ false); }
    OnAllocateInstrument(clientId, allocateInstrument, /*writeAdminReply*/ true);
}
// private overload: allocate rows + client bit + strategy-0 union + coregroup poll bit +
// (conditional) WriteToAdmin + AllocateInstrument event. Idempotent throughout.
```

**Client** (`GetInstrument(headerId)`, Client.hpp:138): header-based leg recursion through the same
public entry, BEFORE the admin round-trip; the early-return makes it exactly-once (rolling with a
spread whose leg is already traded onboards nothing twice, no duplicate Instrument events):

```cpp
Instrument& GetInstrument(int32_t headerId)
{
    if (TryGetInstrumentId(headerId, id) && onboarded(id)) return context.GetInstrument(id);

    if (GetInstrumentHeader(headerId).InstrumentType == InstrumentType::Spread)
        for (legHeader : legged.Legs())
            GetInstrument(legHeader.InstrumentHeaderId);      // admin round-trip each, if needed

    if (!TryGetInstrumentId(headerId, id)) { /* one admin request, one reply */ }
    return OnInstrumentAllocated(id);
}
```

Note the clone's early-return is `TryGetInstrumentId` alone — C# additionally requires the
instrument's data ring to be open ("onboarded"); mirror that, or a server-allocated-but-never-
onboarded instrument skips onboarding.

**Context** (`CreateInstrument`): materialize a spread's legs BEFORE taking the non-reentrant
creation spinlock — the Spread branch resolves legs via GetInstrument, which must hit the cache,
not re-enter CreateInstrument (self-deadlock; shipped and caught). Header reads on the client must
be read-only (`GetReadonlyRef` + value copy — clients map headers read-only).

**Strategy-0 house book** (unchanged requirement, still absent in clone): reserve client id 0 at
header creation; union every client allocation into strategy 0; `ServerStrategyName` path from the
server leaf name; no admin echo, no poll bit for id 0.

## B6. What deliberately does NOT change

- Spread position rows update ONLY via the accounting fill (volume + a spread-units position view);
  the risk gate never reads them. Spread `.risklimit` rows are inert.
- `WriteToExecution` per-message spinlock (`_recvFromExchangeLocks[cg]`) and single-writer audit
  channels: unchanged pattern — but note the fan-out is now 1 state + N fills + N positions per
  event; size rings accordingly.
- The C# Simulator (leg-price synthesis, `[OrderState][Fill x N]` queue framing, the Take/IsCrossed
  matching fix) is C#-only. The C++ live path gets real leg prices from the exchange (B4).

---

# Part C — previously listed, verified against this clone

- **C1. Persistence / socket close protocol — ABSENT, port it.** ClientStatus is still
  `{Disposed=0, Open=1, Closed=2}` (Socket.hpp:169). Target: `{Disposed=0, Detached=1, Open=2,
  Closing=3, Closed=4}`; `Tools::AtomicEnum<T>` ({state, epoch} in one 64-bit word, snapshot-CAS
  `TryTransition`; the listen thread owns transitions; readers request Open->Closing); write gate
  `Open || Detached || (Persistance && Closing)`; `Protocol::SkipRing` + `Recover()` on both
  halves, nothing calls `Reset()` on shared memory; `ServerHeader.Persistance` (A9). Also remove
  `PollDisconnects` (Server.hpp:172 — C# deleted it; close handling lives in the socket ladder,
  `OnClientClosed` = CancelAllOrders + per-CoreGroup AtomicClear) and add the console prints on
  client/instrument allocation.
- **C2. `RAIISpinLock` — PRESENT (SeqLock.hpp:234) ✓.** Verify semantics match C#: bool flag,
  TTAS acquire, release = plain store; C# uses byte-wide exchange on the bool — never a 4-byte RMW.
- **C3. Single-writer discipline (was N7) — now THREE row families**: positions, order states, AND
  risk-limit rows (B2's check-then-commit is only sound on one owner thread). `OnQuantityAhead`
  (MDP3 thread) and `OnControlAlgoStatus` (admin thread) must route to the owner thread, never
  write rows directly. This is the slot-64 torn-seqlock root cause. No CAS — the decision is
  single-writer. *(Amended 2026-09-10: the C# routes `ControlAlgoStatus` and `ControlRiskLimit` by
  having the client send them on the instrument's CoreGroup channel, read by `ReadExecution` —
  not through the injection queue; see cpp_alignment.md §5.)*
- **C4. Behavior fixes**: type-guards on every `AsFuture()`/`AsLegged()` overlay access (a realtime
  context contains spreads and empty slots — and remember B1: `Spread` is not a `Future`, so the
  ctor chain must not read `FutureHeader`); risk-limit edits arrive as `ControlRiskLimit` requests
  on the execution channel and are applied field-wise by the CoreGroup thread — see
  cpp_alignment.md §5 (amended 2026-09-10; the earlier copy-the-live-`Worst*` workaround is
  superseded); count-and-expose unknown message types (the C#
  unknown-type flood came from ONE uninitialized `Header` type byte on in-place-constructed
  fills — in C++, any `Fill` built over raw ring memory must run its default member initializers).
- **C5. Json**: C# now serializes with the relaxed encoder ('+' writes literally, not `\u002B`).
  Glaze already does this; old escaped files still parse on both sides. No action, listed for
  awareness (spread symbols contain '+').
- **C6. Reject -> pause is POLICY.** A create reject pauses the algo deliberately — rejects fail
  loudly. Do not add discard/no-pause behavior on the C++ side either.

---

# Part D — verification checklist

1. `static_assert`: `sizeof(Fill)==64` + `offsetof(Fill,Price)==40`; `sizeof(RiskLimit)==32`
   (was 36 — `StrategyId` removed 2026-09-10, see cpp_alignment.md §5);
   `sizeof(OrderRisk)==64`; `sizeof(Header<T>)==4`; `ServerHeader` Persistance at 172, sizeof 173;
   `sizeof(LegHeader)==8`; `LeggedHeader` fits in 128; `OrderState` offsets per A2.
2. Numeric parity dumps for every enum in A3/A4/A8 against the C# source.
3. Cross-process JSON round-trip: `.risklimit` / `.position` / fill lines written by one side parse
   on the other (glaze <-> System.Text.Json, new Fill shape).
4. The spread cycle test (mirror of the C# scratchpad run): buy a calendar; assert leg positions
   move +q/-q at leg prices whose weighted sum reproduces the traded spread price exactly; spread
   row shows volume only; every leg `Worst*` telescopes to zero through partial fill + cancel; a
   breach on the SECOND leg leaves zero residue on the first.
5. Roll test: allocate an outright, then a roll spread containing it — exactly one onboarding of
   the shared leg, admin handshake stays in sync.

# Suggested order

1. Part A wire structs + enum renumbers (one commit, lockstep deploy with anything attached).
2. C1 socket/persistence ladder (self-contained, was C++-first work — may exist on a local branch;
   push it).
3. B1 legs view + B2 per-leg RiskLayer + B3 OnFill transaction (one coherent unit — do not land
   B2 without B3's release path or the ledger leaks).
4. B5 allocation unions + B4 vendor delivery contract.
5. C3 single-writer routing, C4 guards, Part D asserts/tests throughout.

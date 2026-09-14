# C++ alignment report — 2026-09-14: one strategy callback per `ReadSocket` pass

Scope: the client-side half of update coalescing — `Provider/Client.cs`, `Data/Instrument.cs`,
`Provider/Position.cs`. No wire or shared-memory shape changes. Read after the 2026-09-10 report.
C# is the source of truth; Spec.md § "One strategy run per pass" carries the rationale.

## Why

The server side already coalesces (book builder: fold every packet the NIC has queued, publish one
tick per touched instrument per batch). The client undid that one hop later: `ReadSocket()` drained
its rings to the tail but fired the strategy inside the drain, once per tick, so three deltas on a
ring meant three `QuoteChanged` and three `Execute` calls, the first two on states the next message
in the same buffer had already superseded. The client now applies everything queued first and
raises once per changed object on the final state.

## C1. `Client::ReadSocket()` — two phases

```
// phase 1: fold, mark, no strategy callbacks
for each CoreGroup channel this client uses:
    while TryRead(channel) == New: OnSocketMessage(bytes)          // fills/states/rejects/positions -> images
for each subscribed instrument:
    ReadInstrumentData(instrumentId)                                 // deltas/trades/status -> images
// phase 2: once per changed object, on the final state
for each instrumentId in _dirtyBooks:     Instrument(instrumentId).RaiseChanged()
_dirtyBooks.ClearAll()
for each instrumentId in _dirtyPositions: Position(instrumentId).RaiseChanged()
_dirtyPositions.ClearAll()
```

- `_dirtyBooks` / `_dirtyPositions` are `Bitset64` keyed by instrument id, set in phase 1 wherever a
  delta was folded or a position row applied. Phase 2 walks them in ascending instrument id,
  books first, then positions.
- **Bound:** `ReadInstrumentData` reads at most `MaxReadsPerInstrumentPerPass = 64` records per ring
  per pass. A saturated ring must not keep phase 1 busy forever and starve the strategy; what is
  left waits for the next pass, still coalesced. The execution channels are NOT bounded — they are
  low volume and are the truth for positions, and a fill left behind would mean acting on a stale
  position, which is worse than acting late.
- The client's `NicTimestamp` / `ExchangeTimestamp` end up as the last record applied, so
  `OrderTarget.TriggerTimestamp` on anything the strategy sends names the batch tail — the state it
  actually acted on.

## C2. `Instrument` — apply now, decide at the end of the pass

`OnMarketByPriceDelta(in delta, bytes)` is split in two; there is no method of the old name any more.

```cpp
// phase 1, per delta
void ApplyMarketByPriceDelta(const MarketByPrice& delta, std::span<const uint8_t> bytes)
{
    if (!_isDirty) { _quoteAtPassStart = _quote; _isDirty = true; }   // remember the quote as it was when this pass first touched the book
    const MarketByPrice64& mbp64 = MarketByPrice();
    _quote.Bid = mbp64.BidsCount > 0 ? mbp64.BestBid : Level{};
    _quote.Ask = mbp64.AsksCount > 0 ? mbp64.BestAsk : Level{};
    _isQuoteValid = mbp64.BidsCount > 0 && mbp64.AsksCount > 0;
    MarketByPriceDelta(delta, bytes);                                   // per-delta consumers keep every delta (ladder, queue tracker)
}

// phase 2, once per pass
void RaiseChanged()
{
    _isDirty = false;
    if (_quote.Bid != _quoteAtPassStart.Bid || _quote.Ask != _quoteAtPassStart.Ask)
        QuoteChanged();                                                 // net change against the START of the pass, not the previous delta
    MarketByPriceChanged();
}
```

Semantics to preserve exactly:
- `QuoteChanged` is a **net** change: a quote that moves on delta one and moves back on delta three
  within one pass is not a change and does not fire. Comparison is on the exposed `Quote` (best bid
  and ask `Level`, i.e. ticks and quantity), with an empty side compared as the default level.
- `MarketByPriceChanged` fires once per pass for any touched book, changed quote or not.
- `MarketByPriceDelta` still fires per delta, inside phase 1.
- `TradeChanged` (per print) and `SettlementChanged` are unchanged and fire inside phase 1;
  `TradingStatusUpdateEvent` is unchanged (per transition, T3 of the 2026-09-10 report).

## C3. `Position` — keep the latest row, raise once

`OnPositionHeader(in header)` is split the same way; there is no method of the old name any more.

```cpp
void ApplyPositionHeader(const PositionHeader& header) { _lastPositionHeader = header; }   // phase 1, per message
void RaiseChanged()               { PositionChanged(_lastPositionHeader); }                // phase 2, once per pass
```

Two position rows for one instrument in one pass (a fill's row and a status echo, say) raise
`PositionChanged` once, with the later row. `Position.Fill` (per fill) and the order-active bitset
updates are unchanged and happen in phase 1.

## C4. What the strategy sees

Strategies keep their `QuoteChanged` / `PositionChanged` / `MarketByPriceChanged` subscriptions
unchanged — nothing in the strategy layer moved. A pass with three deltas on one book runs the
handler once; a pass that changed both a quote and a position runs it once per event, i.e. twice.
The client-level `Fill`, `OrderState`, `PositionHeader`, `OrderRejected` events still fire per
message in phase 1 (alert manager, widgets).

## C5. Threading

Unchanged: `ReadSocket()` runs on the client's owner thread (the strategy's tick thread; the GUI's
UI-side manual client). Phase 2 raises on that same thread. The GUI's manual client opens no
instrument rings, so for it phase 2 only ever raises positions.

## C6. Verification

1. Write three deltas for one instrument to its ring before one `ReadSocket()`: exactly one
   `QuoteChanged` and one `MarketByPriceChanged`, three `MarketByPriceDelta`, and the quote equals
   the state after the third delta.
2. Deltas that move the best bid up then back within one pass: `MarketByPriceChanged` once,
   `QuoteChanged` never.
3. Two position rows for one instrument in one pass: one `PositionChanged`, carrying the second row.
4. 100 records queued on one ring: the first pass applies 64 and raises once; the second pass
   applies the remaining 36 and raises once; no record lost, no reorder.
5. Books before positions, ascending instrument id, within phase 2.

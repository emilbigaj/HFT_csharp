# OrderId Refactor — Design Report for the C++ Port

This report explains the reasoning behind the C# changes to `OrderIdAllocator` / `OrderHeader` /
the order-message structs, so the same design can be implemented in the C++ version of the library.
It covers the constraints, the decision chain, the invariants that must hold, and porting notes.
It deliberately does not repeat the C# code — the code listing exists separately.

---

## 1. The problem and the hard constraint

Goal: add a `uint64 ExchangeOrderId` to order state so client orders can be matched against the
MarketByOrder feed (queue position, own-order recognition in MBO).

Constraint: every order message lives in a shared-memory array where an entry is
`64-byte protocol header + value`, rounded up to a 64-byte cache line. A value of ≤ 64 bytes gives a
128-byte entry; 65 bytes jumps to 192 (three cache lines, +50 % shared memory). `OrderState` was
exactly 64 bytes with zero slack, so 8 new bytes had to displace 8 existing bytes.

## 2. The key insight: the header stored redundant data

`ClientOrderId` was already a packed 64-bit id embedding a global order index (which encodes
ClientId, since `globalIndex = clientId * OrdersPerClient + localIndex`) and the InstrumentId.
Yet `OrderHeader` also stored `ClientId`, `StrategyId`, `InstrumentId` as separate ints — 12 bytes of
duplication. Crucially, the client allocates the id *before* anything hits the wire, so every
transmitted message always has a fully-populated id. The stored ints were pure redundancy.

Decision: embed StrategyId into the packed id as well, delete all three stored ints (−12 bytes),
add `ExchangeOrderId` (+8 bytes). Net: OrderHeader 40 → 36 bytes, and every message shrank
(OrderState 64→60, Fill 64→60, OrderRejected 64→60, OrderTarget 56→52, PositionHeader 69→65).
The former three ids became read-only accessors that decode the packed id.

## 3. The bit budget and why 6 / 6 / 14 was chosen

Fixed requirements: generation stays **32 bits** (product decision), localIndex needs 6 bits
(64 orders per client, matching the per-client `Bitset64` allocator). That fixes 38 bits and leaves
26 to split across clientId / strategyId / instrumentId.

Final layout (low → high): `localIndex 6 | clientId 6 | strategyId 6 | instrumentId 14 | generation 32`.

Reasoning:
- **Client/strategy get 6 bits (max 63)** because the server allocates client ids out of a
  `Bitset64` in the server header — a hard cap of 64 concurrent clients that no amount of id-field
  width can exceed. Strategy ids live in the *same* id space as clients (a strategy id IS a client
  id — `IsAlgoOrder()` is `ClientId == StrategyId`, and the risk layer indexes per-client arrays
  with the strategy id), so the same bound applies.
- **Instrument gets the headroom (14 bits = 16 384)** because instrument ids are dense indices
  assigned by *registration order* — every instrument definition the server loads consumes an id,
  traded or not. The configured capacity is 4 096, so 14 bits gives 4× headroom. This is the axis
  that actually grows (options chains).
- Rejected alternative `8/8/8/8/32` (byte-aligned fields): instrument would cap at 256, far below
  the 4 096 capacity — a latent runtime failure the first time an order touches an instrument
  registered with id ≥ 256. Byte alignment buys nothing anyway once access goes through masked
  accessors (see §5).
- Generation keeps the **top 32 bits**. This is load-bearing: comparing two full ids as plain u64
  orders them by allocation time, which the server's `ClientOrderIdOutOfOrder` monotonicity check
  relies on. Do not move generation.

Consequences: MaxClientId 1023 → 63, MaxInstrumentId 65 535 → 16 383, global order slots
65 536 → 4 096 (shrinking every array sized by OrdersCapacity by 4×).

## 4. Generation semantics

- Seeded at process start with unix seconds (`~1.77e9` today — needs 31 bits, fits 32 until 2106),
  monotonically incremented per allocation.
- On re-activating an existing id, the allocator raises its floor:
  `generation = max(generation, id.generation + 1)` — preserves monotonicity across restarts and
  recovery replays.
- **Generation 0 is reserved as the "template" marker** (see §6). A real allocation can never
  produce generation 0 because the seed is always ≥ the unix-seconds epoch value.

## 5. The typed OrderId struct

The raw u64 grew a typed wrapper (`struct OrderId`, 8 bytes, identical wire layout) with a
**settable accessor per field** (mask-out / mask-in over the raw value) plus derived `GlobalIndex`
and `IsAllocated` (generation > 0), implicit conversions to/from u64, and value equality.

Why this shape:
- Settable per-field accessors give "just set the field" ergonomics *without* requiring byte-aligned
  fields — a masked setter compiles to the same shift/AND/OR a byte-field access would. This is what
  made the 8/8/8/8 layout unnecessary.
- Implicit u64 conversions meant the entire existing ecosystem (comparisons against stored u64s,
  dictionary keys, debug-id equality, the monotonic `<=` check, `Free(u64)`) kept compiling with
  zero edits. In C++ the equivalent is a conversion operator to `uint64_t` plus `operator==/!=`
  (or `<=>`) on the raw value.
- All masks/shifts are derived from the allocator's bit-width constants — one source of truth.
  Changing the split later means editing five constants, nothing else.
- Caution that C# enforces and C++ won't: the setters can rewrite an *allocated* id's identity,
  which changes which global slot it refers to. The design compensates by restricting who writes
  what (§7). Consider debug asserts on `!IsAllocated` in setters if you want guardrails.

## 6. The template-id pattern

Problem created by deleting the stored fields: a Create request needs to carry the instrument
before any id exists (the old code read `OrderHeader.InstrumentId`, which no longer exists as
storage). Passing it as a side parameter reintroduces two channels for the same data — the exact
redundancy this refactor removes.

Solution: a **template id** — an OrderId with the id fields populated but generation 0. Templates
flow through the normal `ClientOrderId` slot; `IsAllocated` distinguishes them from real ids.
The allocator's branch condition is `generation > 0`, not `id > 0` (a template is nonzero).

Templates are also used for order-less `OrderHeader`s: position rows initialized before any fill
carry a template with the row's client/strategy/instrument. Note: the old code used `ClientId = -1`
as a "server-wide row" sentinel; −1 is unrepresentable in 6 bits, so server rows now carry
client 0. Nothing reads the sentinel programmatically — it is a display/JSON-only change.

On allocation failure (no free slot) the template is left intact rather than zeroed, so the
rejection message can still report which instrument the failed create was for.

## 7. The ownership chain (the core design principle)

The refactor converged on: **every field of the id is written exactly once, by the party that owns
it.** This resolved a mid-refactor smell where the allocator took clientId/strategyId/instrumentId
as parameters while the template also carried them (two channels, unclear owner).

Final chain for a Create:
1. **Caller (strategy / GUI widget)** builds the template with `InstrumentId` — the only field only
   it knows. The GUI widget *additionally* sets `ClientId` in the template, purely because the TCP
   server routes an incoming remote target to the right manual client by `header.ClientId` *before*
   the client object processes it. That value is advisory; step 2 overwrites it.
2. **Client.Create** stamps `ClientId = own id` and `StrategyId = StrategyId()` **unconditionally**.
   This is the sender-authority rule: *anything created by this client carries this client's ids*,
   regardless of what the template claimed — spoof-proof by construction, no validation needed.
   For a legitimately recovered own id the stamp is a no-op.
3. **Allocator (TryAllocate)** is now pure slot management: signature is
   `(activeOrderBitset&, OrderId&)` — no id parameters at all. Template → assign lowest free
   localIndex + next generation, keep the caller's ids. Allocated id → re-activate its slot
   (fail if busy), bump the generation floor. It neither reads nor validates identity.

Why the stamp lives in `Create` and is unconditional (this was iterated on):
- `Create` is the *only* place Creates are born, and everything reaching it is either a template
  (stamp = intended) or the client's own recovered id (stamp = no-op).
- Amend/Cancel **never** pass through `Create`. Their ids are allocated ids that identify the
  *order*, and must not be restamped — a manual client canceling an algo's order sends the algo's
  id; rewriting its ClientId would repoint it at a different global slot. An earlier design stamped
  in the shared entry path guarded by `!IsAllocated`; moving it into `Create` made the guard
  unnecessary.
- Accepted behavior change: a Create carrying another client's *allocated* id is now silently
  adopted (rewritten to the sender's identity) instead of rejected. This matches the sender-authority
  rule; the server's `ClientOrderIdOutOfOrder` monotonicity check remains as a backstop.

## 8. Identity vs sender

The old header carried both an order-owner identity (inside `ClientOrderId`) and a stamped *sender*
field (`ClientId` int, always overwritten with the sending client's id). The stored sender field is
gone. Consequences:
- The order's ids always mean the **owner**. For amend/cancel of another client's order, the header
  shows the owner's ids, not the sender's. Sender identity is implicit in the transport (which
  socket/queue the message arrived on).
- Code that previously distinguished "my own order vs the algo's order" by comparing the packed
  owner-id against the stamped sender field now compares against the client object's own id
  directly (`embeddedClientId == myClientId`). Semantically identical, since the stamp was always
  the sender's own id.

## 9. Validation shifts

- The reuse-path identity validation that lived in the allocator (reject if embedded
  client/strategy/instrument ≠ parameters) is deleted; §7 step 2 makes it true by construction.
- Server-side per-field mismatch checks (ClientIdIsWrong / StrategyIdIsWrong / InstrumentIdIsWrong
  vs the existing order) are now *implied by* ClientOrderId equality — they can only fire together
  with ClientOrderIdIsWrong. They were left in place (harmless) but are effectively dead.
- "NotValid" range checks on derived ids are unreachable (a 6/14-bit field can't be out of range);
  the "NotAllocated" bitset checks still catch garbage ids.

## 10. Serialization

- The packed id serializes as the **raw u64** (custom converter bound to the type), so the JSON
  wire shape is byte-identical to when the field was a plain u64, and old persisted files parse
  unchanged.
- `OrderHeader` additionally exposes derived read-only `ClientId` / `StrategyId` / `InstrumentId`
  accessors. The C# JSON layer serializes readable properties, so logs stay human-readable
  (ids appear as plain fields), while deserialization ignores those keys (read-only) and
  reconstructs everything from `ClientOrderId`. Old files containing stale id fields load correctly
  — the packed id wins. The C++ port should mirror this: emit derived ids for readability if
  desired, but *never* consume them on read; ClientOrderId is the single source of truth.

## 11. C++ porting notes

- **Do not implement the layout with bit-fields or a union of bytes** — bit-field ordering is
  implementation-defined and a byte-overlay bakes in endianness. Use explicit shift/mask accessors
  over a single `uint64_t`, exactly like the C# struct. If you must overlay, `static_assert` the
  round-trips.
- **The bit constants are the wire contract** between the two implementations:
  `LocalIndexBits=6, ClientBits=6, StrategyBits=6, InstrumentBits=14, GenerationBits=32`,
  packed low→high in that order. `OrdersPerClient = 1 << LocalIndexBits`;
  `globalIndex = id & 0xFFF` (low 12 bits); `clientId = globalIndex >> 6`.
- Keep raw-u64 ordering for ids (default `operator<=>` on the value) — monotonicity checks depend
  on generation being the top 32 bits.
- The per-client active-order bitset (64 bits) is the true capacity bound; LocalIndexBits must stay
  equal to log2 of that bitset's size.
- The allocator is single-threaded by contract (one owner thread per client); the generation
  counter is a plain non-atomic u64 there. Preserve that contract or adjust.
- Struct sizes to assert after the change (Pack=1 equivalents):
  OrderId 8, OrderHeader 36, OrderState 60, Fill 60, OrderRejected 60, OrderTarget 52.
  PositionHeader lands at 65 — one byte over a cache line (its shared-array entry stays 192 B;
  it was already over before at 69). Folding its 1-byte status enum into the message header's
  reserved bytes would bring it to 64; not done yet on the C# side.
- **Recorded binary logs from before this change are layout-incompatible** — field offsets moved
  (and the C# side also reordered OrderHeader to put Seq first). Any replay tooling must be
  versioned or old logs migrated.
- Field order note: C# `OrderHeader` is `Seq(4) | ClientOrderId(8) | ExchangeOrderId(8) |
  ExchangeTimestamp(8) | NicTimestamp(8)` under Pack=1 — the u64s sit at offsets 4/12/20/28,
  i.e. unaligned. Fine on x86; if the C++ side ever runs on stricter targets, load via memcpy.

## 12. What was verified (mirror these as tests in C++)

- Size asserts for all structs listed above.
- Per-field set/get round-trips at corner values (0 and max per field, mixed).
- Setter isolation: writing one field never disturbs the others; a value wider than its field
  wraps within the field only.
- Template flow: build template with instrument → stamp identity → allocate → localIndex 0,1,…
  assigned, generation > 0, instrument preserved, monotonic u64 ordering across allocations.
- Reuse flow: free then re-activate the same id (identical value back); re-activating an id whose
  slot is busy fails.
- u64 interop: implicit conversion round-trip, equality, ordering.
- Derived accessors agree with the standalone decode functions.
- JSON: id emitted as a raw number; round-trip lossless; old-format payloads with stale id fields
  parse with the packed id winning.

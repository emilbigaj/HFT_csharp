# C++ alignment report — 2026-09-10: `OrderRisk` layout change

Amends the RiskLayer section (B2) of `cpp_alignment_report_2026-09-08.md` and §3 of
`cpp_alignment.md`. Scope: **one struct, one shared-memory array**. Everything else in the
2026-09-08 report stands. C# is the source of truth: `Execution/Order.cs` (`OrderRisk`) and Spec.md
§ "OrderRisk: in-flight quantities are a scanned array, not a bitset" (rationale). The differential
test that validated the C# struct is specified in §6 below (the C# test itself is not in the repo).
The C# commit that adds this file is the reference commit.

## 1. What changed and why

| | 2026-09-08 report | now |
|---|---|---|
| storage | `Bitset64` + 56 × uint8 bucket counters | uint16 count + uint16 cached max + 30 × uint16 entries |
| size | 64 bytes | 64 bytes (unchanged) |
| max order quantity | 55 | 65 535 |
| in-flight targets per order slot | 255 per distinct quantity | 30 total |
| API / reject reasons / hook wiring | | unchanged |

The 55 cap was never a risk decision; it was the byte budget of a 64-bit bitset plus 56 one-byte
counters. The replacement is a compact array with a cached max: add appends, remove linear-scans
for one matching entry and swap-removes it. The scan is cheap because the in-flight count on one
order is one to three in practice (the client refuses a same-profile amend while one is active and
sends nothing while a cancel is pending). Measured over 1M order lifecycles it is within 1 ns of the
bitset; every SIMD lane layout tried was 2× slower (a narrow lane store followed by the wide reload
`RiskLayer` makes right after every `TryAdd`/`Ack` defeats store forwarding), a two-level 1024-bucket
bitset is 1160 bytes and dominated by its own reset, and a pessimistic high-water mark is inexact.
**Do not "optimise" the port with SIMD.**

## 2. Wire layout — shared array `OrderRisks`, one entry per order slot, server-owned

`#pragma pack(1)` / `Pack = 1`, sequential. A zeroed struct is the empty state; the server still
does `orderRisk = default` on Create (before `TryAdd`) and on Done — unchanged.

| offset | type | field | meaning |
|---|---|---|---|
| 0 | `uint16` | `ActiveTargetsCount` | number of live entries, 0..30 |
| 2 | `uint16` | `WorstOrderQuantity` | max over the live entries, 0 when there are none |
| 4 | `uint16[30]` | `AbsOrderQuantities` | live entries at indices `[0, ActiveTargetsCount)`; every index at or past the count is 0 |

`sizeof == 64`. Entry order is **not** significant (swap-remove reorders); never assume FIFO.
Values are magnitudes: sells are stored as `abs(quantity)`, side is fixed per order.

## 3. Semantics — must match bit-for-bit

- **`Abs(v)`** — branchless: `m = (unsigned)(v >> 31); return (int)(((unsigned)v ^ m) - m)`.
  `INT_MIN` stays negative (no UB, no throw); every caller range-checks with an unsigned compare.
  Do not replace with `std::abs` (`std::abs(INT_MIN)` is UB).
- **`GetAbsWorstOrderQuantity(acked)`** = `max(Abs(acked), WorstOrderQuantity)`. Empty struct →
  `Abs(acked)`. (The old code returned `max(Abs(acked), -1)` on empty; identical since
  `Abs(acked) >= 0` for every acked quantity that exists.)
- **`TryAdd(q, reason&)`** — `a = Abs(q)`.
  1. `(unsigned)a > 65535 || a == 0` → `reason = QuantityNotValid (20)`, return false.
  2. `ActiveTargetsCount == 30` → `reason = TooManyActiveTargets (45)`, return false.
  3. `AbsOrderQuantities[count] = a; count += 1; WorstOrderQuantity = max(WorstOrderQuantity, a)`;
     `reason = None (0)`, return true.
- **`Ack(q)` and `Reject(q)`** both call **`Remove(q)`** — `a = Abs(q)`.
  1. `(unsigned)a > 65535` → return. (No zero check: 0 never matches a live entry, the scan just
     misses — mirrors the C# text exactly.)
  2. Scan `i` in `[0, count)` for the **first** `AbsOrderQuantities[i] == a`. None → return.
     **An ack/reject for a quantity that was never reserved is a no-op. This tolerance is
     deliberate and required** (the multiset was chosen over a high-water mark precisely because a
     stray ack must not collapse the reservation).
  3. Swap-remove: `entries[i] = entries[count-1]; entries[count-1] = 0; count -= 1`.
  4. If `a == WorstOrderQuantity`: `WorstOrderQuantity = max over entries[0, count)` (0 if empty).
- **Multiset:** the same quantity reserved twice (two in-flight amends at two prices) is two
  entries; one `Ack` retires exactly one of them.

## 4. Reference implementation (drop-in for Order.hpp)

```cpp
#pragma pack(push, 1)
struct OrderRisk
{
    static constexpr int MaxOrderQuantity = 65535;
    static constexpr int MaxActiveTargets = 30;

    uint16_t ActiveTargetsCount;            // live entries, 0..30
    uint16_t WorstOrderQuantity;            // max over the live entries, 0 when none
    uint16_t AbsOrderQuantities[30];        // live at [0, ActiveTargetsCount), zeros after

    static inline int Abs(int v)
    {
        unsigned m = (unsigned)(v >> 31);   // arithmetic shift assumed (all our targets); C++20 guarantees it
        return (int)(((unsigned)v ^ m) - m);
    }

    inline int GetAbsWorstOrderQuantity(int ackedOrderQuantity) const
    {
        return std::max(Abs(ackedOrderQuantity), (int)WorstOrderQuantity);
    }

    inline bool TryAdd(int orderQuantity, OrderRejectedReason& reason)
    {
        int absOrderQuantity = Abs(orderQuantity);
        if ((unsigned)absOrderQuantity > (unsigned)MaxOrderQuantity || absOrderQuantity == 0)
        {
            reason = OrderRejectedReason::QuantityNotValid;
            return false;
        }

        int activeTargetsCount = ActiveTargetsCount;
        if (activeTargetsCount == MaxActiveTargets)
        {
            reason = OrderRejectedReason::TooManyActiveTargets;
            return false;
        }

        AbsOrderQuantities[activeTargetsCount] = (uint16_t)absOrderQuantity;
        ActiveTargetsCount = (uint16_t)(activeTargetsCount + 1);
        WorstOrderQuantity = (uint16_t)std::max((int)WorstOrderQuantity, absOrderQuantity);

        reason = OrderRejectedReason::None;
        return true;
    }

    inline void Ack(int orderQuantity) { Remove(orderQuantity); }
    inline void Reject(int orderQuantity) { Remove(orderQuantity); }

private:
    inline void Remove(int orderQuantity)
    {
        int absOrderQuantity = Abs(orderQuantity);
        if ((unsigned)absOrderQuantity > (unsigned)MaxOrderQuantity)
            return;

        // Acks retire the oldest target, so the forward scan normally stops at index 0.
        int activeTargetsCount = ActiveTargetsCount;
        int targetIndex = 0;
        while (targetIndex < activeTargetsCount && AbsOrderQuantities[targetIndex] != absOrderQuantity)
            targetIndex++;
        if (targetIndex == activeTargetsCount)
            return;

        int lastTargetIndex = activeTargetsCount - 1;
        AbsOrderQuantities[targetIndex] = AbsOrderQuantities[lastTargetIndex];
        AbsOrderQuantities[lastTargetIndex] = 0;
        ActiveTargetsCount = (uint16_t)lastTargetIndex;

        if (absOrderQuantity != WorstOrderQuantity)
            return;

        int worstOrderQuantity = 0;
        for (int i = 0; i < lastTargetIndex; i++)
            worstOrderQuantity = std::max(worstOrderQuantity, (int)AbsOrderQuantities[i]);
        WorstOrderQuantity = (uint16_t)worstOrderQuantity;
    }
};
#pragma pack(pop)

static_assert(sizeof(OrderRisk) == 64);
static_assert(offsetof(OrderRisk, ActiveTargetsCount) == 0);
static_assert(offsetof(OrderRisk, WorstOrderQuantity) == 2);
static_assert(offsetof(OrderRisk, AbsOrderQuantities) == 4);
```

The C# marks all five methods `AggressiveInlining`; without it the .NET JIT declined to inline the
loop-bearing `Remove` and it cost 5 ns per order lifecycle. Mark the C++ `inline`/`always_inline`
the same way if your compiler leaves it out of line.

## 5. What does NOT change

- The four RiskLayer hooks and the reserve path in B2 of the 2026-09-08 report (before/after
  magnitude around `TryAdd`/`Ack`/`Reject`, `released = worst - abs(QuantityFilled)` at Done, zero
  the struct at Done). They call the same four methods with the same arguments.
- Reject reason numbers: `QuantityNotValid = 20`, `TooManyActiveTargets = 45` (A4 column).
  `TooManyActiveTargets` stays in `OrderDiscarded` (silently absorbed, the algo retries next tick);
  its meaning moves from "256th identical in-flight quantity" to "31st in-flight target on one
  order slot".
- `RiskLimit.MaxOrderQuantity` remains the operative per-order bound, checked per leg **before**
  `TryAdd`, so a limit reject still needs no back-out.
- Part D item 1: `sizeof(OrderRisk) == 64` — still true; add the three `offsetof` asserts above.
- Client side: C# `Algo.NewAmend` clamps the amend quantity to `OrderRisk.MaxOrderQuantity`, now
  65 535. If the C++ strategy layer mirrors that clamp, use 65 535.

## 6. Verification

1. The four `static_assert`s in §4.
2. Differential test against a plain list — this is the exact procedure the C# struct passed:
   xorshift64 (`x ^= x << 13; x ^= x >> 7; x ^= x << 17`)
   seeded `0x9E3779B97F4A7C15`, 400 000 iterations; per iteration
   `quantity = 1 + (r % 65535)`, sign from bit 20, add-vs-remove from bit 32 with the reference
   list capped at 40 entries, one in eight removes (bits 40..42 == 0) acks a quantity **not** in the
   list, otherwise removes `reference[(r >> 40) % count]` with sign from bit 50; after every op
   compare `GetAbsWorstOrderQuantity(±acked)` for `acked = (r >> 24) % 65536` against
   `max(acked, max(reference))`. Then `TryAdd` of `{0, 65536, INT_MAX, INT_MIN}` must each fail with
   `QuantityNotValid`, and a zeroed struct must report `abs(acked)`.
   With that seed the C# prints `adds=174,830 removes=174,807 rejects=25,366 absentRemoves=24,997`;
   matching those four counts proves the RNG and op mix were ported identically, and a clean run
   proves the struct.
3. Cross-process: the C# server tooling dumps `OrderRisks` through the C# struct (Context.cs order
   dump, `absWorstOrderQuantity=`). A C++ server and C# tooling on the same shared memory must
   agree on §2 byte-for-byte.

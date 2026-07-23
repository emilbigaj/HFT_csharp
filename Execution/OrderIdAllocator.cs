using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Tools;

namespace Execution
{
    // This class is not thread-safe. Only one thread should ever use it.
    public static class OrderIdAllocator
    {
        // --- Configuration ---
        // 64-bit id layout (low -> high): localIndex 6 | clientId 6 | strategyId 6 | instrumentId 14 | generation 32
        public const int LocalIndexBits = 6;
        public const int ClientBits = 6;
        public const int StrategyBits = 6;
        public const int InstrumentBits = 14;
        public const int GenerationBits = 32;
        public const int IndexBits = LocalIndexBits + ClientBits; // global order index
        public const int OrdersPerClient = 1 << LocalIndexBits;
        public const int OrdersPerClientBitShift = LocalIndexBits;
        public const int MaxClientId = (1 << ClientBits) - 1;         // 63
        public const int MaxStrategyId = (1 << StrategyBits) - 1;     // 63
        public const int MaxInstrumentId = (1 << InstrumentBits) - 1; // 16 383

        // --- Derived Masks & Shifts ---
        private const ulong s_indexMask = (1UL << IndexBits) - 1;
        private const ulong s_strategyMask = (1UL << StrategyBits) - 1;
        private const ulong s_instrumentMask = (1UL << InstrumentBits) - 1;

        private const int s_strategyShift = IndexBits;
        private const int s_instrumentShift = IndexBits + StrategyBits;
        private const int s_generationShift = IndexBits + StrategyBits + InstrumentBits;

        // --- State ---
        private static ulong s_generation;

        static OrderIdAllocator()
        {
            s_generation = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        // Pure slot/generation allocation: ClientId/StrategyId/InstrumentId are already inside
        // orderId (stamped by Client.Create / packed by the caller). This method only
        // assigns LocalIndex + Generation; identity validation is the caller's job.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryAllocate(ref Bitset64 isOrderActive, ref OrderId orderId)
        {
            if (orderId.IsAllocated) // reuse/recovery of a real id: re-activate its slot
            {
                int localIndex = orderId.LocalIndex;
                if (isOrderActive[localIndex])
                    return false;
                isOrderActive.Set(localIndex);
                s_generation = Math.Max(s_generation, orderId.Generation + 1UL);
                return true;
            }
            else // template id: allocate a slot, keep the ids
            {
                int localIndex = isOrderActive.LowestClear;

                if (localIndex < 0)
                    return false;

                isOrderActive.Set(localIndex);
                ulong currentGen = s_generation++;

                orderId.LocalIndex = localIndex;
                orderId.Generation = (uint)currentGen;
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfInstrumentIdOutOfRange(int instrumentId)
        {
            if ((uint)instrumentId > (uint)MaxInstrumentId)
                ThrowInstrumentIdOutOfRange(instrumentId);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfClientIdOutOfRange(int clientId)
        {
            if ((uint)clientId > (uint)MaxClientId)
                ThrowClientIdOutOfRange(clientId);
        }

        [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInstrumentIdOutOfRange(int instrumentId) =>
            throw new ArgumentOutOfRangeException(nameof(instrumentId), $"OrderIdAllocator::ThrowIfInstrumentIdOutOfRange({instrumentId}), instrumentId should not be greater than: {MaxInstrumentId}");

        [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowClientIdOutOfRange(int clientId) =>
            throw new ArgumentOutOfRangeException(nameof(clientId), $"OrderIdAllocator::ThrowIfClientIdOutOfRange({clientId}), clientId should not be greater than: {MaxClientId}");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfStrategyIdOutOfRange(int strategyId)
        {
            if ((uint)strategyId > (uint)MaxStrategyId)
                ThrowStrategyIdOutOfRange(strategyId);
        }

        [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowStrategyIdOutOfRange(int strategyId) =>
            throw new ArgumentOutOfRangeException(nameof(strategyId), $"OrderIdAllocator::ThrowIfStrategyIdOutOfRange({strategyId}), strategyId should not be greater than: {MaxStrategyId}");


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Free(ref Bitset64 activeOrders, ulong orderId)
        {
            // Resolve Local Index directly from ID
            int localIndex = GetLocalIndex(orderId);
            activeOrders.Clear(localIndex);
        }

        // --- Data Extraction ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetFirstGlobalIndex(int clientId)
        {
            return OrdersPerClient * clientId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetLastGlobalIndex(int clientId)
        {
            return GetFirstGlobalIndex(clientId) + OrdersPerClient - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetGlobalIndex(ulong orderId)
        {
            return (int)(orderId & s_indexMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetStrategyId(ulong orderId)
        {
            return (int)((orderId >> s_strategyShift) & s_strategyMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetInstrumentId(ulong orderId)
        {
            return (int)((orderId >> s_instrumentShift) & s_instrumentMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetGeneration(ulong orderId)
        {
            return orderId >> s_generationShift;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetLocalIndex(ulong orderId)
        {
            return GetGlobalIndex(orderId) & (OrdersPerClient - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetClientId(ulong orderId)
        {
            return GetGlobalIndex(orderId) >> OrdersPerClientBitShift;
        }

        // --- Helpers ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToGlobalIndex(int clientId, int localOrderIndex)
        {
            return (clientId * OrdersPerClient) + localOrderIndex;
        }

    }

    // Typed view over the packed 64-bit order id. ClientOrderId is the raw value; ClientId,
    // StrategyId, InstrumentId, LocalIndex, Generation are all decoded from it — reference them
    // through the OrderId (e.g. header.OrderId.InstrumentId) so it is clear they share one source.
    // Generation 0 marks a template: ids-only, not yet allocated (real ids have generation >= the
    // unix-seconds seed). Callers build Create templates with an initializer (set what you own —
    // typically InstrumentId); TryAllocate stamps LocalIndex + Generation. Serializes as a nested
    // object: ClientOrderId is the source of truth on read; the decoded fields are emitted for
    // readability and are effectively ignored (their setters just re-derive the same bits).
    [RegisterJson]
    [StructLayout(LayoutKind.Sequential, Pack = 1)] // 8 bytes
    public struct OrderId : IEquatable<OrderId>
    {
        public ulong ClientOrderId;

        // Masks/shifts derived from the OrderIdAllocator bit layout:
        // localIndex 6 | clientId 6 | strategyId 6 | instrumentId 14 | generation 32
        private const ulong s_localIndexMask = (1UL << OrderIdAllocator.LocalIndexBits) - 1;
        private const int s_clientShift = OrderIdAllocator.LocalIndexBits;
        private const ulong s_clientMask = ((1UL << OrderIdAllocator.ClientBits) - 1) << s_clientShift;
        private const int s_strategyShift = s_clientShift + OrderIdAllocator.ClientBits;
        private const ulong s_strategyMask = ((1UL << OrderIdAllocator.StrategyBits) - 1) << s_strategyShift;
        private const int s_instrumentShift = s_strategyShift + OrderIdAllocator.StrategyBits;
        private const ulong s_instrumentMask = ((1UL << OrderIdAllocator.InstrumentBits) - 1) << s_instrumentShift;
        private const int s_generationShift = s_instrumentShift + OrderIdAllocator.InstrumentBits;
        private const ulong s_generationMask = ((1UL << OrderIdAllocator.GenerationBits) - 1) << s_generationShift;
        private const ulong s_globalIndexMask = (1UL << OrderIdAllocator.IndexBits) - 1;

        public int LocalIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)(ClientOrderId & s_localIndexMask); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { ClientOrderId = (ClientOrderId & ~s_localIndexMask) | ((uint)value & s_localIndexMask); }
        }

        public int ClientId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)((ClientOrderId & s_clientMask) >> s_clientShift); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { ClientOrderId = (ClientOrderId & ~s_clientMask) | (((ulong)(uint)value << s_clientShift) & s_clientMask); }
        }

        public int StrategyId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)((ClientOrderId & s_strategyMask) >> s_strategyShift); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { ClientOrderId = (ClientOrderId & ~s_strategyMask) | (((ulong)(uint)value << s_strategyShift) & s_strategyMask); }
        }

        public int InstrumentId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)((ClientOrderId & s_instrumentMask) >> s_instrumentShift); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { ClientOrderId = (ClientOrderId & ~s_instrumentMask) | (((ulong)(uint)value << s_instrumentShift) & s_instrumentMask); }
        }

        public uint Generation
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (uint)(ClientOrderId >> s_generationShift); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { ClientOrderId = (ClientOrderId & ~s_generationMask) | ((ulong)value << s_generationShift); }
        }

        public int GlobalIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)(ClientOrderId & s_globalIndexMask); }
        }

        public bool IsAllocated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Generation > 0; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAlgoOrder() => ClientId == StrategyId;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator ulong(OrderId orderId) => orderId.ClientOrderId;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator OrderId(ulong value) => new OrderId { ClientOrderId = value };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(OrderId left, OrderId right) => left.ClientOrderId == right.ClientOrderId;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(OrderId left, OrderId right) => left.ClientOrderId != right.ClientOrderId;

        public bool Equals(OrderId other) => ClientOrderId == other.ClientOrderId;
        public override bool Equals(object? obj) => obj is OrderId other && ClientOrderId == other.ClientOrderId;
        public override int GetHashCode() => ClientOrderId.GetHashCode();

        public override string ToString() => Json.Serialize(this);
    }
}
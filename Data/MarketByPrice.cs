using System;
using System.Numerics;                       // BitOperations
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Tools; // Bitset64

namespace Data
{

    // ─────────────────────────────────────────────────────────────────────────
    // Compact MBP header (blittable) + fixed buffers (int[64]) — capacity ≤ 64
    // Internal engine for a single side; specialized by TSide.
    // ─────────────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct SideByPrice64
    {
        /// <summary>Active capacity (power of two: 2..64).</summary>
        public static readonly int Capacity = 64;   // keep public for blittability/debugger hover
        /// <summary>Mask = Capacity - 1.</summary>
        public static readonly int IndexMask = 63;


        /// <summary>Active levels bitset (1 = non-zero size at ring index).</summary>
        private Bitset64 _bitset;
        /// <summary>Unbounded logical head (best level’s logical ring position; may exceed 0..63).</summary>
        internal int _bestIndex;
        /// <summary>Best price in ticks.</summary>
        internal int _bestTicks;

        public readonly Side Side; // keep public for blittability/debugger hover
        private fixed byte _reserved[47]; // for alignment and future use]

        /// <summary>Quantities ring buffer (size 64; only first Capacity entries used).</summary>
        private fixed int _quantities[64];

        // ── Properties for debugger hover / diagnostics ──────────────────

        /// <summary>Active levels mask (returned by value for diagnostics; not a ref).</summary>
        public Bitset64 Bitset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bitset;
        }

        /// <summary>Number of active price levels (popcount).</summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bitset.Count;
        }

        public int WorstTicks
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_bitset.IsEmpty) return 0;
                int worstOffset = _bitset.RotateRight(_bestIndex).HighestSet;
                return MapBestOffsetToTicks(worstOffset);
            }
        }

        public int BestTicks => _bestTicks;

        /// <summary>True when no active levels.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bitset.IsEmpty;
        }

        /// <summary>Mask of valid indices for current capacity.</summary>
        private ulong ActiveMask
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Capacity >= 64) ? ~0UL : ((1UL << Capacity) - 1UL);
        }


        public override string ToString()
        {
            if (_bitset.Raw == 0UL) return "[]";

            int approxPairs = _bitset.Count;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(16 + approxPairs * 16);

            sb.Append('[');

            bool first = true;
            Enumerator e = GetEnumerator();
            while (e.MoveNext())
            {
                Level lvl = e.Current;

                if (!first) sb.Append(", ");
                sb.Append('(');
                sb.Append(lvl.Ticks);
                sb.Append(", ");
                sb.Append(lvl.Quantity);
                sb.Append(')');

                first = false;
            }

            sb.Append(']');
            return sb.ToString();
        }
        // ── Initialization ───────────────────────────────────────────────

        public SideByPrice64(Side side)
        {
            Side = side;
            _bestIndex = -1;
            _bestTicks = 0;
            _bitset = new Bitset64(0);

            fixed (int* q = _quantities)
            {
                for (int i = 0; i < 64; i++) q[i] = 0;
            }
        }

        // ── Mapping: price ticks -> ring index (O(1)) ────────────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int MapBestOffsetToRingIndex(int bestOffset)
        {
            return (_bestIndex + bestOffset) & IndexMask;                    // mask to ring
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetQuantity(int ticks)
        {
            int bestOffset = MapTicksToBestOffset(ticks);

            // out of window? (steps ∉ [-Capacity, Capacity-1])
            if ((uint)bestOffset > (uint)IndexMask) // the cast converts negative to large positive
                return 0;

            int ringIndex = MapBestOffsetToRingIndex(bestOffset);

            if (!_bitset[ringIndex])
                return 0;

            unsafe
            {
                fixed (int* q = _quantities) return q[ringIndex];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MapTicksToBestOffset(int ticks)
        {
            int deltaTicks = _bestTicks - ticks;
            int bestOffset = (int)Side * deltaTicks;
            return bestOffset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MapRingIndexToTicks(int ringIndex)
        {
            int bestOffset = (ringIndex - _bestIndex) & IndexMask;
            return _bestTicks - bestOffset * (int)Side;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MapBestOffsetToTicks(int bestOffset)
        {
            return _bestTicks - bestOffset * (int)Side;
        }

        /// <summary>
		/// Gets the i-th best level (0-based) from this side of the book.
		/// <para>O(index) complexity. For deep access, this walks the set bits.</para>
		/// </summary>
		public Level this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new IndexOutOfRangeException();
                }

                // 1. Rotate to align BestIndex to logical 0.
                // 2. Select the n-th set bit directly using PDEP.
                Bitset64 rotated = _bitset.RotateRight(_bestIndex);
                int bestOffset = rotated.GetNthSetBit(index);

                // 3. Map back to physical ring index and price.
                int ringIndex = MapBestOffsetToRingIndex(bestOffset);
                int ticks = MapBestOffsetToTicks(bestOffset);

                // 4. Retrieve quantity.
                int quantity;
                unsafe
                {
                    fixed (int* q = _quantities)
                    {
                        quantity = q[ringIndex];
                    }
                }

                return new Level(ticks, quantity);
            }
        }

        // ── Public Write: set quantity at price (hot path) ───────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetQuantity(int ticks, int quantity, out int delta)
        {
            // first touch: anchor best; keep logical head = 0 (simple math)
            if (_bitset.Raw == 0UL)
            {
                _bestIndex = 0;              // unbounded logical head
                _bestTicks = ticks;
            }

            

            int bestOffset = MapTicksToBestOffset(ticks);
            bool isNewZero = quantity == 0;
            bool isBetter = bestOffset < 0;

            // far-worse (>= capacity): ignore
            if (!isBetter && bestOffset >= Capacity)
            {
                delta = 0;
                return false;
            }


            // far-better (>= capacity): accept, re-anchor without aliasing
            // remeber negative bestOffset means the price is better
            if (isBetter && bestOffset <= -Capacity)
            {
                if (isNewZero) // ignore it
                {
                    delta = 0;
                    return false;
                }

                // clear active mask (O(1)); quantities can remain stale if reads gate on bitset
                _bitset.ClearAll();

                // keep head location; just re-anchor price
                _bestTicks = ticks;

                // write qty first, then set bit (consistent with hot path)
                unsafe { fixed (int* q = _quantities) q[_bestIndex] = quantity; }
                delta = quantity;
                _bitset.Set(_bestIndex);

                return true;
            }

            int ringIndex = MapBestOffsetToRingIndex(bestOffset);
            int isOldNonZero = (!isBetter && _bitset[ringIndex]) ? 1 : 0;

            unsafe
            {
                fixed (int* q = _quantities)
                {
                    delta = quantity - q[ringIndex] * isOldNonZero;
                    q[ringIndex] = quantity;
                }
            }


            if (isNewZero)
            {
                _bitset.Clear(ringIndex);
                if (ringIndex == _bestIndex)
                {
                    int nextBestIndex = _bitset.FirstSet(ringIndex);

                    if (nextBestIndex == -1)
                        nextBestIndex = _bitset.FirstSet(0);

                    if (nextBestIndex != -1)
                    {
                        _bestTicks = MapRingIndexToTicks(nextBestIndex);
                        _bestIndex = nextBestIndex;
                    }
                    else
                    {
                        _bestIndex = nextBestIndex;
                    }
                }
                return true;
            }

            // Non Zero


            _bitset.Set(ringIndex);
            
            
            if (isBetter)
            {
                int oldBestIndex = _bestIndex;
                _bestTicks = ticks;
                _bitset.ClearOutside(_bestIndex, ringIndex);
                _bestIndex = ringIndex;
            }

            return true;
        }

        //TODO use a pointer
        public ref struct Enumerator
        {
            private readonly SideByPrice64 _mbp;   // snapshot (includes fixed buffer)
            private Bitset64 _rotatedBitset;                 // rotated+windowed active bits
            private Level _current;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(scoped in SideByPrice64 src)
            {
                _mbp = src;                         // copy struct so Quantities lives inside enumerator
                _current = default;
                _rotatedBitset = src.Bitset.RotateRight(_mbp._bestIndex);

            }

            public Level Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _current;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                if (_rotatedBitset.Raw == 0UL)
                    return false;

                int bestOffset = BitOperations.TrailingZeroCount(_rotatedBitset.Raw);
                _rotatedBitset.Raw &= ~(1UL << bestOffset);   // consume this bit

                int ringIndex = _mbp.MapBestOffsetToRingIndex(bestOffset);

                int qty;
                unsafe
                {
                    fixed (int* q = _mbp._quantities)        // fixed buffer is inside copied struct → stable
                    {
                        qty = q[ringIndex];
                    }
                }

                int ticks = _mbp.MapBestOffsetToTicks(bestOffset);
                _current = new Level(ticks, qty);
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Enumerator GetEnumerator() => this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator() => new Enumerator(in this);

        public unsafe ref struct SideByPrice64PtrEnumerator
        {
            private readonly SideByPrice64* _src;
            private Bitset64 _rotatedBitset;
            private Level _current;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public SideByPrice64PtrEnumerator(SideByPrice64* src)
            {
                _src = src;
                _current = default;
                _rotatedBitset = src->Bitset.RotateRight(src->_bestIndex);
            }

            public Level Current => _current;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                if (_rotatedBitset.Raw == 0UL)
                    return false;

                int bestOffset = BitOperations.TrailingZeroCount(_rotatedBitset.Raw);
                _rotatedBitset.Raw &= ~(1UL << bestOffset);

                int ringIndex = _src->MapBestOffsetToRingIndex(bestOffset);

                // Now this is safe: _src is unmanaged/pinned by design
                int qty = _src->_quantities[ringIndex];

                int ticks = _src->MapBestOffsetToTicks(bestOffset);
                _current = new Level(ticks, qty);
                return true;
            }

            public SideByPrice64PtrEnumerator GetEnumerator() => this;
        }

    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public two-sided book wrapper with snapshots
    // ─────────────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MarketByPrice64
    {
        public SideByPrice64 Bids;
        public SideByPrice64 Asks;

        /// <summary>Exchange timestamp of last applied update/snapshot.</summary>
        public Timestamp ExchangeTimestamp;
        public Timestamp SendingTimestamp;

        /// <summary>NIC timestamp of the local capture.</summary>
        public Timestamp NicTimestamp;

        public bool IsCrossed => Bids.Count > 0 && Asks.Count > 0 && Bids.BestTicks >= Asks.BestTicks;

        public MarketByPrice64()
        {
            ExchangeTimestamp = default;
            SendingTimestamp = default;
            NicTimestamp = default;
            Bids = new SideByPrice64(Side.Buy);
            Asks = new SideByPrice64(Side.Sell);
        }

        /// <summary>Human-readable dump via a rented snapshot.</summary>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("MarketByPrice64 ");
            sb.Append(ExchangeTimestamp.ToString());
            sb.Append(' ');
            sb.Append(NicTimestamp.ToString());
            sb.Append(' ');
            sb.Append($"BidsCount: {BidsCount} AsksCount: {AsksCount}");
            sb.Append(Environment.NewLine);


                    // print asks (reverse order)
            StackList<Level> asks = new StackList<Level>(stackalloc Level[AsksCount]);
            foreach (Level ask in Asks)
                asks.Add(ask);

            for (int i = asks.Count - 1; i >= 0; i--)
            {
                ref readonly Level level = ref asks[i];
                sb.Append(level.Ticks.ToString());
                sb.Append(' ');
                sb.Append(level.Quantity.ToString());
                sb.Append(' ');
                sb.Append(Environment.NewLine);
            }

            sb.Append('-');
            sb.Append(Environment.NewLine);


            // print bids (normal order)
            int b = 0;
            int exit = BidsCount - 1;
            foreach (Level bid in Bids)
            {
                sb.Append(bid.Ticks.ToString());
                sb.Append(' ');
                sb.Append(bid.Quantity.ToString());
                if (b++ == exit)
                    break;
                sb.Append(Environment.NewLine);
            }
            
            return sb.ToString();
        }

        public string BidsAsString
        {
            get
            {
                return Bids.ToString();
            }
        }

        public string AsksAsString
        {
            get
            {
                return Asks.ToString();
            }
        }

        /// <summary>Returns a pooled snapshot buffer encoded as <see cref="MarketByPrice"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref MarketByPrice CopyToSnapshot(int instrumentId, Span<byte> dst)
        {
            int bidsCount = BidsCount;
            int asksCount = AsksCount;
            int len = MarketByPrice.SizeOf(bidsCount, asksCount);

            ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(dst);
            mbp.TickHeader = new TickHeader
            {
                InstrumentId = instrumentId,
                TickType = TickType.MarketByPriceSnapshot,
                ExchangeTimestamp = ExchangeTimestamp,
                SendingTimestamp = SendingTimestamp,
                NicTimestamp = NicTimestamp,
            };
            mbp.BidsCount = bidsCount;
            mbp.AsksCount = asksCount;

            int b = 0;
            Span<Level> bidsSpan = mbp.BidsAsSpan(dst);
            foreach (Level lvl in EnumerateBids()) bidsSpan[b++] = lvl;

            int a = 0;
            Span<Level> asksSpan = mbp.AsksAsSpan(dst);
            foreach (Level lvl in EnumerateAsks()) asksSpan[a++] = lvl;

            dst = dst.Slice(0, len); // advance caller's span past the written snapshot

            return ref mbp;
        }

        /// <summary>Applies an MBP snapshot/update into the book.</summary>
        public bool TrySet(ReadOnlySpan<byte> src)
        {
            ref readonly MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(src);

            if (mbp.TickHeader.ExchangeTimestamp < ExchangeTimestamp)
            {
                return false;
            }

            ExchangeTimestamp = mbp.TickHeader.ExchangeTimestamp;
            SendingTimestamp = mbp.TickHeader.SendingTimestamp;
            NicTimestamp = mbp.TickHeader.NicTimestamp;

            if (mbp.TickHeader.TickType == TickType.MarketByPriceSnapshot)
            {
                Clear();
            }

            if (mbp.TickHeader.TickType == TickType.MarketByPriceDelta)
            {
                foreach (ref readonly Level level in mbp.BidsAsSpan(src))
                {
                    TrySetBidQuantity(level.Ticks, level.Quantity + Bids.GetQuantity(level.Ticks), out _);
                }

                foreach (ref readonly Level level in mbp.AsksAsSpan(src))
                {
                    TrySetAskQuantity(level.Ticks, level.Quantity + Asks.GetQuantity(level.Ticks), out _);
                }
            }
            else
            {
                foreach (ref readonly Level level in mbp.BidsAsSpan(src))
                {
                    TrySetBidQuantity(level.Ticks, level.Quantity, out _);
                }

                foreach (ref readonly Level level in mbp.AsksAsSpan(src))
                {
                    TrySetAskQuantity(level.Ticks, level.Quantity, out _);
                }
            }

            return true;
        }

        /// <summary>Applies an MBP snapshot/update into the book.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetAsDeltas(Span<byte> src)
        {
            ref MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(src);
            if (mbp.TickHeader.ExchangeTimestamp < ExchangeTimestamp)
                return false;

            if (mbp.TickHeader.TickType == TickType.MarketByPriceSnapshot)
                throw new ArgumentException("Not allowed, Unsupported.");

            ExchangeTimestamp = mbp.TickHeader.ExchangeTimestamp;
            SendingTimestamp = mbp.TickHeader.SendingTimestamp;
            NicTimestamp = mbp.TickHeader.NicTimestamp;

            mbp.TickHeader.TickType = TickType.MarketByPriceDelta;
            bool any = false;

            foreach (ref Level level in mbp.BidsAsSpan(src))
            {
                TrySetBidQuantity(level.Ticks, level.Quantity, out int bidQuantityDelta);
                level.Quantity = bidQuantityDelta;
                any |= bidQuantityDelta != 0;
            }

            foreach (ref Level level in mbp.AsksAsSpan(src))
            {
                TrySetAskQuantity(level.Ticks, level.Quantity, out int askQuantityDelta);
                level.Quantity = askQuantityDelta;
                any |= askQuantityDelta != 0;
            }
            return any;
        }

        // ── Public API (hover-friendly) ──────────────────────────────────

        /// <summary>Best bid (ticks + qty).</summary>
        public Level BestBid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return new Level(Bids._bestTicks, Bids.GetQuantity(Bids._bestTicks)); }
        }

        /// <summary>Best ask (ticks + qty).</summary>
        public Level BestAsk
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return new Level(Asks._bestTicks, Asks.GetQuantity(Asks._bestTicks)); }
        }

        /// <summary>Active bid levels count (popcount).</summary>
        public int BidsCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Bids.Bitset.Count; }
        }

        /// <summary>Active ask levels count (popcount).</summary>
        public int AsksCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Asks.Bitset.Count; }
        }

        /// <summary>True if both sides are empty.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Bids.Bitset.IsEmpty && Asks.Bitset.IsEmpty; }
        }

        /// <summary>Active mask for bids (returned by value as <see cref="Bitset64"/>).</summary>
        public Bitset64 BidsBitset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Bids.Bitset;
        }

        /// <summary>Active mask for asks (returned by value as <see cref="Bitset64"/>).</summary>
        public Bitset64 AsksBitset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Asks.Bitset;
        }

        // ── Mutations / reads ────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetBidQuantity(int ticks, int quantity, out int delta) => Bids.TrySetQuantity(ticks, quantity, out delta);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetAskQuantity(int ticks, int quantity, out int delta) => Asks.TrySetQuantity(ticks, quantity, out delta);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetBidQuantity(int ticks) => Bids.GetQuantity(ticks);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetAskQuantity(int ticks) => Asks.GetQuantity(ticks);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            Bids = new SideByPrice64(Side.Buy);
            Asks = new SideByPrice64(Side.Sell);
        }

        // Zero-alloc enumerations (best → worse)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SideByPrice64.Enumerator EnumerateBids() => Bids.GetEnumerator();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SideByPrice64.Enumerator EnumerateAsks() => Asks.GetEnumerator();
    }
}

//BEGIN_FILE HFT/Data/TickQueue.cs
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Tools;

namespace Data
{
    public class TickQueueManager()
    {
        public LockedArrayList<TickQueue> _sleeping = new LockedArrayList<TickQueue>(isMultiWriter: true, initialCapacity: 16);

        private readonly SemaphoreSlim _wakeSignal = new SemaphoreSlim(0);

        private int _threadsCount = 0;
        private int _queueCount = 0;

        public void Add(TickQueue tickQueue)
        {
            Interlocked.Increment(ref _queueCount);
            _sleeping.Add(tickQueue);

            int threadLimit = Math.Min(16, Environment.ProcessorCount);

            // Reserve a worker slot atomically; only spawn if we actually got one.
            int newCount = Interlocked.Increment(ref _threadsCount);
            if (newCount <= threadLimit)
            {
                Thread thread = new Thread(AddThread)
                {
                    IsBackground = true,
                    Name = "TickQueueWorker"
                };
                thread.Start();
            }
            else
            {
                // Lost the race; surrender the reserved slot.
                Interlocked.Decrement(ref _threadsCount);
            }

            _wakeSignal.Release();
        }

        private void AddThread()
        {
            while (true)
            {
                _wakeSignal.Wait(); // consume one permit

                TickQueue tickQueue;
                using (ArrayList<TickQueue> copy = _sleeping.Copy())
                {
                    if (copy.Count == 0)
                        continue;

                    // FIX: Replaced unstable Sort() with linear search.
                    // TickQueue.Count is volatile and changes during sort, causing IComparer exceptions.
                    tickQueue = copy[0];
                    int bestCount = tickQueue.Count;

                    for (int i = 1; i < copy.Count; i++)
                    {
                        TickQueue candidate = copy[i];
                        int count = candidate.Count;
                        if (count < bestCount)
                        {
                            bestCount = count;
                            tickQueue = candidate;
                        }
                    }

                    if (!_sleeping.SwapRemove(tickQueue, out _))
                    {
                        _wakeSignal.Release(); // restore on race miss
                        continue;
                    }
                }

                if (tickQueue.IsFull)
                {
                    _sleeping.Add(tickQueue);
                    Thread.Sleep(1);
                    _wakeSignal.Release();     // queue returned -> restore permit
                    continue;
                }

                if (tickQueue.TryFill())
                {
                    _sleeping.Add(tickQueue);
                    _wakeSignal.Release();         // queue returned -> restore permit
                }

            }
        }

    }

    public class PriorityTickQueue : TickQueue
    {

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue(TickQueue tickQueue)
        {
            if (tickQueue.TryPeek(out ReadOnlySpan<byte> src))
            {
                Timestamp timestamp = MemoryMarshal.AsRef<TickHeader>(src).ExchangeTimestamp;
                _priorityQueue.Enqueue(timestamp, tickQueue);
            }
        }

        private readonly LockedPriorityQueue<Timestamp, TickQueue> _priorityQueue = new LockedPriorityQueue<Timestamp, TickQueue>(isMultiWriter: true, initialCapacity: 16);
        public PriorityTickQueue(int capacity) : base(capacity)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override int MoveNext(Span<byte> dst, out TickType tickType)
        {
            if (_priorityQueue.TryPeek(out Timestamp timestamp, out TickQueue queue))
            {
                if (queue.TryPeek(out ReadOnlySpan<byte> src))
                {
                    tickType = MemoryMarshal.AsRef<TickHeader>(src).TickType;
                    if (tickType == TickType.Trade || tickType == TickType.Settlement)
                        src.Slice(0, Unsafe.SizeOf<Tick>()).CopyTo(dst);
                    else if (tickType == TickType.MarketByPriceSnapshot || tickType == TickType.MarketByPriceUpdate)
                    {
                        ref readonly MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(src);
                        src.Slice(0, Unsafe.SizeOf<MarketByPrice>()).CopyTo(dst);
                    }
                    return src.Length;
                }
            }
            tickType = default!;
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void ReadTick(Span<byte> dst)
        {
            PopAndPush(dst);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void ReadMarketByPrice(Span<byte> dst)
        {
            PopAndPush(dst);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void PopAndPush(Span<byte> dst)
        {
            if (!_priorityQueue.TryDequeue(out Timestamp timestamp, out TickQueue tickQueue))
                throw new Exception();

            tickQueue.TryPeek(out ReadOnlySpan<byte> src);

            TickType tickType = MemoryMarshal.AsRef<TickHeader>(src).TickType;
            if (tickType == TickType.MarketByPriceSnapshot || tickType == TickType.MarketByPriceUpdate)
            {
                ref readonly MarketByPrice mbp = ref MemoryMarshal.AsRef<MarketByPrice>(src);

                int headerSize = Unsafe.SizeOf<MarketByPrice>();
                int levelsSize = mbp.SizeOfLevels();

                // dst already has the header at [0..headerSize)
                // copy just the levels right after the header:
                ReadOnlySpan<byte> levelBytes = src.Slice(headerSize, levelsSize);

                levelBytes.CopyTo(dst.Slice(headerSize));
            }
            tickQueue.TryDequeue();
            if (tickQueue.TryPeek(out src))
            {
                timestamp = MemoryMarshal.AsRef<TickHeader>(src).ExchangeTimestamp;
                _priorityQueue.Enqueue(timestamp, tickQueue);
            }
        }
    }

    public class TickHistoryTickQueue : TickQueue
    {
        private readonly TickHistoryReader _tickHistoryReader;
        public int InstrumentId { get; }
        public TickHistoryTickQueue(TickHistoryReader tickHistoryReader, int instrumentId, int capacity) : base(capacity)
        {
            InstrumentId = instrumentId;
            _tickHistoryReader = tickHistoryReader;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override int MoveNext(Span<byte> dst, out TickType tickType)
        {
            int bytesNeeded = _tickHistoryReader.MoveNext(dst, out tickType);
            if (bytesNeeded > 0)
            {
                ref TickHeader tickHeader = ref MemoryMarshal.AsRef<TickHeader>(dst);
                tickHeader.InstrumentId = InstrumentId;
            }
            return bytesNeeded;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void ReadTick(Span<byte> dst)
        {
            _tickHistoryReader.ReadTick(dst);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void ReadMarketByPrice(Span<byte> dst)
        {
            _tickHistoryReader.ReadMarketByPrice(dst);
        }

        public override void Dispose()
        {
            base.Dispose();
            _tickHistoryReader.Dispose();
        }
    }


    public abstract class TickQueue : IDisposable
    {
        public TickQueue(int capacity)
        {
            _capacity = Tools.Tools.NextPowerOfTwo(capacity);
            _mask = _capacity - 1;
            unsafe
            {
                _ring = (byte*)NativeMemory.Alloc((nuint)_capacity); // unmanaged allocation
                for (nuint i = 0; i < (nuint)_capacity; i++)
                    _ring[i] = 0;
            }
        }

        private ulong _writeCount = 0;  // producer only increments
        public ulong WriteCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Volatile.Read(ref _writeCount); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private set { Volatile.Write(ref _writeCount, value); }
        }

        private ulong _readCount = 0;   // consumer only increments
        public ulong ReadCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Volatile.Read(ref _readCount); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private set { Volatile.Write(ref _readCount, value); }
        }

        private bool _endOfStream = false;
        public bool EndOfStream
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return _endOfStream; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private set { Volatile.Write(ref _endOfStream, value); }
        }

        private int _bytesRead = 0;
        public int BytesRead
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Volatile.Read(ref _bytesRead); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private set { Volatile.Write(ref _bytesRead, value); }
        }

        private int _bytesNeeded = 0;
        public int BytesNeeded
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Volatile.Read(ref _bytesNeeded); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private set { Volatile.Write(ref _bytesNeeded, value); }
        }


        private readonly int _capacity;       // power of two
        private readonly int _mask;           // _capacity - 1
        private unsafe byte* _ring;
        private bool _disposed = false;
        private object _lock = new object();
        public virtual void Dispose()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    unsafe
                    {
                        NativeMemory.Free(_ring);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPeek(out ReadOnlySpan<byte> dst)
        {
            if (IsEmpty && EndOfStream)
            {
                dst = default;
                return false;
            }
            SpinWait spinner = default;
            while (true)
            {
                bool isEmpty = IsEmpty;

                if (isEmpty && EndOfStream)
                {
                    dst = default;
                    return false;
                }

                if (!isEmpty)
                    break;


                if (spinner.Count > 20)
                    Thread.Yield();
                spinner.SpinOnce();
            }

            int readIndex = ReadIndex;
            unsafe
            {
                byte* readPtr = &_ring[readIndex];
                int sizeOf = GetSizeOf(ref readPtr);
                dst = new ReadOnlySpan<byte>(readPtr + sizeof(int), sizeOf);
                BytesRead = dst.Length + sizeof(int);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue()
        {
            if (BytesRead != 0)
            {
                ReadCount += (ulong)BytesRead;
                BytesRead = 0;
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe int GetSizeOf(ref byte* readPtr)
        {
            int free = (int)(_ring + _capacity - readPtr);
            int sizeOf = free < sizeof(int) ? -1 : *(int*)readPtr;
            if (sizeOf == -1) //wrap
            {
                ReadCount += (ulong)free;
                readPtr = _ring;
                SpinWait spinner = default;
                while (IsEmpty)
                {
                    if (spinner.Count > 20)
                        Thread.Yield();
                    spinner.SpinOnce();
                }
                sizeOf = *(int*)readPtr;
            }
            return sizeOf;
        }



        protected int WriteIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)(_writeCount & (ulong)_mask); }
        }
        protected int ReadIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)(_readCount & (ulong)_mask); }
        }

        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Count == 0; }
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)(WriteCount - ReadCount); }
        }

        public bool IsFull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Count == _capacity; }
        }


        private byte[] _header = new byte[64];

        // return true if filled all that it can
        public bool TryFill()
        {
            while (true)
            {
                int bytesNeeded = BytesNeeded;

                if (bytesNeeded == 0)
                    BytesNeeded = bytesNeeded = MoveNext(_header, out TickType tickType);


                if (bytesNeeded > _capacity)
                    throw new InvalidOperationException($"{GetType()}.TryFill(): _bytesNeeded {bytesNeeded} > _capacity {_capacity}");

                if (bytesNeeded <= 0)
                {
                    EndOfStream = true;
                    return false;
                }

                if (IsFull)
                    return true;

                int writeIndex = WriteIndex;
                int readIndex = ReadIndex;
                int free = 0;
                if (writeIndex >= readIndex) //since we already exit on full this is the case where reader caught up
                {
                    free = _capacity - writeIndex;
                    if (free < bytesNeeded + sizeof(int)) // wrap
                    {
                        if (free >= sizeof(int))
                        {
                            unsafe
                            {
                                byte* writePtr = _ring + writeIndex;
                                *(int*)writePtr = -1;
                            }
                        }
                        WriteCount += (ulong)free;
                        writeIndex = WriteIndex;
                        free = readIndex - writeIndex;
                    }
                }
                else
                {
                    free = readIndex - writeIndex;
                }

                if (free < bytesNeeded + sizeof(int))
                    return true;



                Span<byte> dst;
                unsafe
                {
                    byte* writePtr = _ring + writeIndex;
                    *(int*)writePtr = bytesNeeded;
                    dst = new Span<byte>(writePtr + sizeof(int), bytesNeeded);
                    ref TickHeader tickHeader = ref MemoryMarshal.AsRef<TickHeader>(_header.AsSpan());

                    if (tickHeader.TickType == TickType.Trade || tickHeader.TickType == TickType.Settlement)
                    {
                        _header.AsSpan().Slice(0, Unsafe.SizeOf<Tick>()).CopyTo(dst);
                        ReadTick(dst);
                    }
                    else if (tickHeader.TickType == TickType.MarketByPriceUpdate || tickHeader.TickType == TickType.MarketByPriceSnapshot)
                    {
                        _header.AsSpan().Slice(0, Unsafe.SizeOf<MarketByPrice>()).CopyTo(dst);
                        ReadMarketByPrice(dst);
                    }
                    else
                        throw new NotImplementedException($"Unsupported TickType: {tickHeader.TickType}");

                    Volatile.Write(ref _writeCount, _writeCount + (ulong)(bytesNeeded + sizeof(int)));
                    BytesNeeded = 0;
                }
            }
        }

        protected abstract int MoveNext(Span<byte> dst, out TickType tickType);
        protected abstract void ReadTick(Span<byte> dst);
        protected abstract void ReadMarketByPrice(Span<byte> dst);

    }
}

//END_FILE HFT/Data/TickQueue.cs
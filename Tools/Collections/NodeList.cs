using System;
using System.Runtime.CompilerServices;

namespace Tools
{
    /// <summary>
    /// Intrusive, array-pooled, doubly-linked list for exchange / queue simulators.
    /// • All nodes live in one pooled array.
    /// • Free slots kept in a LIFO stack — no tombstones to skip.
    /// • O(1): add head/tail, remove by node, remove head/tail.
    /// • Ref iteration over Nodes (so you can insert/remove in the loop) AND
    ///   ref iteration over values (plain foreach, no editing list).
    /// 
    /// Single-threaded: caller must synchronize externally.
    /// Resize/Clear/Dispose invalidates refs/enumerators.
    /// </summary>
    public sealed class NodeList<T> : IDisposable
    {
        /// <summary>
        /// Stored node. You usually see this only when doing
        /// <c>foreach (ref LinkedList&lt;T&gt;.Node n in list.Nodes)</c>.
        /// </summary>
        public struct Node
        {
            internal int Next;
            internal int Prev;
            internal int Index;
            public T Item;
        }

        private const int s_defaultCapacity = 16;

        private Node[] _nodes;
        private int[] _freeStack;
        private int _freeTop;
        private int _head;
        private int _tail;
        private int _count;
        private bool _disposed;

        public NodeList(int initialCapacity = s_defaultCapacity)
        {
            if (initialCapacity <= 0)
            {
                initialCapacity = s_defaultCapacity;
            }

            _nodes = ThreadArrayPool<Node>.Rent(initialCapacity);
            _freeStack = ThreadArrayPool<int>.Rent(initialCapacity);

            _freeTop = 0;
            for (int i = initialCapacity - 1; i >= 0; i--)
            {
                _freeStack[_freeTop++] = i;
                _nodes[i].Next = -1;
                _nodes[i].Prev = -1;
                _nodes[i].Index = i;
                _nodes[i].Item = default!;
            }

            _head = -1;
            _tail = -1;
            _count = 0;
            _disposed = false;
        }

        /// <summary>Number of active nodes.</summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ThrowIfDisposed();
                return _count;
            }
        }

        /// <summary>Total rented capacity (active + free).</summary>
        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ThrowIfDisposed();
                return _nodes.Length;
            }
        }

        // ============================================================
        // ADD
        // ============================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddLast(T item)
        {
            ThrowIfDisposed();
            int index = AcquireSlot();
            ref Node node = ref _nodes[index];

            node.Item = item;
            node.Prev = _tail;
            node.Next = -1;

            if (_tail != -1)
            {
                _nodes[_tail].Next = index;
            }
            _tail = index;

            if (_head == -1)
            {
                _head = index;
            }

            _count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T EmplaceLast()
        {
            ThrowIfDisposed();
            int index = AcquireSlot();
            ref Node node = ref _nodes[index];

            node.Prev = _tail;
            node.Next = -1;

            if (_tail != -1)
            {
                _nodes[_tail].Next = index;
            }
            _tail = index;

            if (_head == -1)
            {
                _head = index;
            }

            _count++;
            return ref node.Item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddFirst(T item)
        {
            ThrowIfDisposed();
            int index = AcquireSlot();
            ref Node node = ref _nodes[index];

            node.Item = item;
            node.Prev = -1;
            node.Next = _head;

            if (_head != -1)
            {
                _nodes[_head].Prev = index;
            }
            _head = index;

            if (_tail == -1)
            {
                _tail = index;
            }

            _count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T EmplaceFirst()
        {
            ThrowIfDisposed();
            int index = AcquireSlot();
            ref Node node = ref _nodes[index];

            node.Prev = -1;
            node.Next = _head;

            if (_head != -1)
            {
                _nodes[_head].Prev = index;
            }
            _head = index;

            if (_tail == -1)
            {
                _tail = index;
            }

            _count++;
            return ref node.Item;
        }

        /// <summary>
        /// Insert a new element directly AFTER the given node (O(1)).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T EmplaceAfter(in Node node)
        {
            ThrowIfDisposed();

            int afterIndex = node.Index;
            int newIndex = AcquireSlot();
            ref Node newNode = ref _nodes[newIndex];

            int nextIndex = _nodes[afterIndex].Next;

            newNode.Prev = afterIndex;
            newNode.Next = nextIndex;

            _nodes[afterIndex].Next = newIndex;

            if (nextIndex != -1)
            {
                _nodes[nextIndex].Prev = newIndex;
            }
            else
            {
                // was tail, now new node is tail
                _tail = newIndex;
            }

            _count++;
            return ref newNode.Item;
        }

        /// <summary>
        /// Insert a new element directly BEFORE the given node (O(1)).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T EmplaceBefore(in Node node)
        {
            ThrowIfDisposed();

            int beforeIndex = node.Index;
            int newIndex = AcquireSlot();
            ref Node newNode = ref _nodes[newIndex];

            int prevIndex = _nodes[beforeIndex].Prev;

            newNode.Next = beforeIndex;
            newNode.Prev = prevIndex;

            _nodes[beforeIndex].Prev = newIndex;

            if (prevIndex != -1)
            {
                _nodes[prevIndex].Next = newIndex;
            }
            else
            {
                // was head, now new node is head
                _head = newIndex;
            }

            _count++;
            return ref newNode.Item;
        }

        // queue-style
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue(T item) => AddLast(item);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out T value) => TryRemoveFirst(out value);

        // ============================================================
        // REMOVE
        // ============================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemoveFirst(out T value)
        {
            ThrowIfDisposed();
            if (_head == -1)
            {
                value = default!;
                return false;
            }

            int index = _head;
            ref Node node = ref _nodes[index];
            value = node.Item;

            Unlink(index);
            FreeSlot(index);
            _count--;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemoveLast(out T value)
        {
            ThrowIfDisposed();
            if (_tail == -1)
            {
                value = default!;
                return false;
            }

            int index = _tail;
            ref Node node = ref _nodes[index];
            value = node.Item;

            Unlink(index);
            FreeSlot(index);
            _count--;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemoveAtHandle(int index)
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)_nodes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            Unlink(index);
            FreeSlot(index);
            _count--;
        }

        /// <summary>Remove a node you got from <c>foreach (ref Node ...)</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(in Node node)
        {
            RemoveAtHandle(node.Index);
        }

        // ============================================================
        // HEAD / TAIL refs
        // ============================================================

        public ref T FirstRef
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ThrowIfDisposed();
                if (_head == -1)
                {
                    return ref Unsafe.NullRef<T>();
                }
                return ref _nodes[_head].Item;
            }
        }

        public ref T LastRef
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ThrowIfDisposed();
                if (_tail == -1)
                {
                    return ref Unsafe.NullRef<T>();
                }
                return ref _nodes[_tail].Item;
            }
        }

        // ============================================================
        // ENUMERATION 1: over NODES (editable, can remove during loop)
        // ============================================================

        /// <summary>
        /// Use this when you want to edit the list during the loop.
        /// <code>
        /// foreach (ref LinkedList&lt;T&gt;.Node n in list.Nodes)
        /// {
        ///     if (ShouldRemove(n.Item)) list.Remove(in n);
        /// }
        /// </code>
        /// </summary>
        public NodeEnumerable Nodes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { ThrowIfDisposed(); return new NodeEnumerable(this); }
        }

        public readonly ref struct NodeEnumerable
        {
            private readonly NodeList<T> _list;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal NodeEnumerable(NodeList<T> list) { _list = list; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public NodeEnumerator GetEnumerator() { return new NodeEnumerator(_list); }
        }

        public ref struct NodeEnumerator
        {
            private readonly NodeList<T> _list;
            private int _current;
            private int _next;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal NodeEnumerator(NodeList<T> list)
            {
                _list = list;
                _current = -1;
                _next = list._head;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                if (_next == -1)
                {
                    _current = -1;
                    return false;
                }

                int index = _next;
                _current = index;
                // cache next BEFORE yielding so remove(current) is safe
                _next = _list._nodes[index].Next;
                return true;
            }

            public ref Node Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return ref _list._nodes[_current]; }
            }
        }

        // ============================================================
        // ENUMERATION 2: over VALUES (plain foreach(ref T ... in list))
        // ============================================================

        /// <summary>
        /// Simple ref-foreach over values:
        /// <code>
        /// foreach (ref T value in list)
        /// {
        ///     // read/write value
        /// }
        /// </code>
        /// Do NOT modify the list during this loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RefEnumerator GetEnumerator()
        {
            ThrowIfDisposed();
            return new RefEnumerator(this);
        }

        public ref struct RefEnumerator
        {
            private readonly NodeList<T> _list;
            private int _current;
            private int _next;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal RefEnumerator(NodeList<T> list)
            {
                _list = list;
                _current = -1;
                _next = list._head;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                if (_next == -1)
                {
                    _current = -1;
                    return false;
                }

                _current = _next;
                _next = _list._nodes[_current].Next;
                return true;
            }

            public ref T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return ref _list._nodes[_current].Item; }
            }
        }

        // ============================================================
        // BULK / CAPACITY / DISPOSE
        // ============================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            ThrowIfDisposed();

            int length = _nodes.Length;
            _freeTop = 0;
            for (int i = length - 1; i >= 0; i--)
            {
                _nodes[i].Next = -1;
                _nodes[i].Prev = -1;
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    _nodes[i].Item = default!;
                }
                _freeStack[_freeTop++] = i;
            }

            _head = -1;
            _tail = -1;
            _count = 0;
        }

        public void EnsureCapacity(int min)
        {
            ThrowIfDisposed();
            if (_nodes.Length < min)
            {
                Resize(ComputeNewSize(_nodes.Length, min));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Node[] nodes = _nodes;
            int[] free = _freeStack;

            _nodes = Array.Empty<Node>();
            _freeStack = Array.Empty<int>();
            _freeTop = 0;
            _head = -1;
            _tail = -1;
            _count = 0;

            if (nodes.Length != 0)
            {
                ThreadArrayPool<Node>.Return(nodes);
            }
            if (free.Length != 0)
            {
                ThreadArrayPool<int>.Return(free);
            }
        }

        // ============================================================
        // INTERNALS
        // ============================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int AcquireSlot()
        {
            if (_freeTop > 0)
            {
                return _freeStack[--_freeTop];
            }

            int oldSize = _nodes.Length;
            int newSize = ComputeNewSize(oldSize, oldSize + 1);
            Resize(newSize);

            return _freeStack[--_freeTop];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FreeSlot(int index)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _nodes[index].Item = default!;
            }

            _nodes[index].Next = -1;
            _nodes[index].Prev = -1;
            _freeStack[_freeTop++] = index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Unlink(int index)
        {
            int prev = _nodes[index].Prev;
            int next = _nodes[index].Next;

            if (prev != -1)
            {
                _nodes[prev].Next = next;
            }
            else
            {
                _head = next;
            }

            if (next != -1)
            {
                _nodes[next].Prev = prev;
            }
            else
            {
                _tail = prev;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NodeList<T>));
            }
        }

        private void Resize(int newSize)
        {
            Node[] oldNodes = _nodes;
            int[] oldFree = _freeStack;
            int oldLen = oldNodes.Length;

            Node[] newNodes = ThreadArrayPool<Node>.Rent(newSize);
            int[] newFree = ThreadArrayPool<int>.Rent(newSize);

            // copy existing
            for (int i = 0; i < oldLen; i++)
            {
                newNodes[i] = oldNodes[i];
            }

            // copy free indexes
            int newFreeTop = 0;
            for (int i = 0; i < _freeTop; i++)
            {
                newFree[newFreeTop++] = oldFree[i];
            }

            // init new slots
            for (int i = newSize - 1; i >= oldLen; i--)
            {
                newNodes[i].Next = -1;
                newNodes[i].Prev = -1;
                newNodes[i].Index = i;
                newNodes[i].Item = default!;
                newFree[newFreeTop++] = i;
            }

            _nodes = newNodes;
            _freeStack = newFree;
            _freeTop = newFreeTop;

            ThreadArrayPool<Node>.Return(oldNodes);
            ThreadArrayPool<int>.Return(oldFree);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeNewSize(int current, int needed)
        {
            int next;
            if (current <= 1024)
            {
                next = (current == 0) ? s_defaultCapacity : (current << 1);
            }
            else
            {
                next = current + (current >> 1);
            }

            if (next < needed)
            {
                next = needed;
            }

            const int MaxArrayLength = 0x7FEFFFFF;
            if (next > MaxArrayLength)
            {
                if (needed > MaxArrayLength)
                {
                    throw new OutOfMemoryException();
                }
                next = MaxArrayLength;
            }

            return next;
        }
    }
}

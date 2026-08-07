//BEGIN_FILE HFT/Socket/Socket.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using Tools;

namespace Socket;

public static class SocketChannel
{
    public static readonly int AdminChannelLength = Tools.Memory.HugePageLength;
    public static readonly int ExecutionChannelLength = Tools.Memory.HugePageLength;
    public static readonly int Admin = 0;

    // Build the per-direction channel-length array from a CoreGroups bitset (channel index ==
    // CoreGroupId). Size = highest set bit + 1, so unset indices below it become 0-length gaps.
    // Channel 0 = admin (AdminChannelLength); 1..7 = execution (ExecutionChannelLength). Both
    // directions use this same shape. CoreGroupIds must set bit 0 (admin) and stay within 0..7.
    public static int[] BuildChannelLengths(Bitset64 coreGroupIds)
    {
        coreGroupIds.Set(Admin); //force admin

        int highest = coreGroupIds.HighestSet;
        if (highest > 7)
            throw new ArgumentException("SocketChannel.BuildChannelLengths: CoreGroupId > 7 (max 8 channels).");

        int[] lengths = new int[highest + 1];
        foreach (int coreGroupId in coreGroupIds)
            lengths[coreGroupId] = coreGroupId == Admin ? AdminChannelLength : ExecutionChannelLength;

        return lengths;
    }

    // Per-instrument broadcast ring: one writer (the server), many readers (subscribed clients).
    // Named "{serverName}_{symbol}_data"; Tools.Memory sanitizes the name (spaces/dashes are fine).
    public static readonly int InstrumentDataChannelLength = Tools.Memory.HugePageLength;

    public static string GetInstrumentDataName(string serverName, string symbol) => $"{serverName}_{symbol}_data";
}

public static class SocketUtils
{
    public static string GetSocketName(string clientName, string serverName)
    {
        return $"Socket_{clientName}_{serverName}";
    }

    public static string GetChannelName(string socketName, int channelId, ChannelDirection direction)
    {
        return $"{socketName}_{direction}_Channel_{channelId}";
    }
}

[RegisterJson]
public enum ChannelDirection
{
    ClientToServer,
    ServerToClient,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[RegisterJson]
public struct SocketHeader
{
    public String128 ServerName;
    public String128 ClientName;
    public Timestamp Timestamp;
    public int ClientId = -1;
    public int ClientProcessId;
    public int ClientToServerChannelCount;
    public int ServerToClientChannelCount;
    public Array8<int> ClientToServerLengths;
    public Array8<int> ServerToClientLengths;

    public SocketHeader(string serverName, string clientName, int[] clientToServerLengths, int[] serverToClientLengths, int clientProcessId)
    {
        ServerName = new String128(serverName);
        ClientName = new String128(clientName);
        Timestamp = Timestamp.UtcNow;
        ClientProcessId = clientProcessId;

        ClientToServerChannelCount = Math.Min(8, clientToServerLengths.Length);
        for (int i = 0; i < ClientToServerChannelCount; i++)
        {
            ClientToServerLengths[i] = clientToServerLengths[i];
        }

        ServerToClientChannelCount = Math.Min(8, serverToClientLengths.Length);
        for (int i = 0; i < ServerToClientChannelCount; i++)
        {
            ServerToClientLengths[i] = serverToClientLengths[i];
        }
    }

    public string Name => SocketUtils.GetSocketName(ClientName.ToString(), ServerName.ToString());

    public SharedMemory CreateOrOpenSharedMemory()
    {
        long length = 0;
        unsafe
        {
            for (int i = 0; i < ClientToServerChannelCount; i++)
            {
                length += ClientToServerLengths[i];
            }
            for (int i = 0; i < ServerToClientChannelCount; i++)
            {
                length += ServerToClientLengths[i];
            }
        }
        return SharedMemory.CreateOrOpen(Name, length);
    }

    public override string ToString() => Json.Serialize(this);
}

// Renumbered to insert Detached. This lives only in ServerSocket's per-client bookkeeping — it is
// never written to shared memory, so the renumber is not a wire change.
// Detached means: the socket exists and is writable, but no client process is attached.
//
//   Persistance = true                     Persistance = false
//   Disposed --connect--> Open             Disposed --connect--> Open
//   Open --client dies--> Detached         Open --client dies--> Closed
//   Detached --reconnect--> Open           Closed --1s--> Disposed
//
// Under Persistance, Closed and DisposeClient are never reached — so ClientDeallocated never fires
// and the logging server's audit tap is never torn down (which is the point: the tap must survive
// a client restart to catch the fills an iLink3 retransmit delivers while the client is away).
public enum ClientStatus : byte
{
    Disposed = 0,
    Detached = 1,
    Open = 2,
    Closed = 3
}

public sealed class ReadOnlySocket : IDisposable
{
    public static readonly int BufferSize = 64 * 1024;
    public readonly string Name;

    private SharedMemoryView _view;
    private SharedMemory? _ownedMemory;
    private unsafe byte* _startPtr = null;
    private unsafe byte* _readPtr = null;
    private unsafe byte* _endPtr = null;
    private ulong _readSeq = 0;
    private byte[] _buffer = null!;
    private bool _isClosed = false;
    private bool _isDisposed = false;

    public ReadOnlySocket(string name, SharedMemoryView view)
    {
        Name = name;
        _view = view;
        unsafe
        {
            _startPtr = _view.GetPtr();
            _readPtr = _startPtr;
            _endPtr = _startPtr + _view.Length;
        }
        _buffer = new byte[BufferSize];
    }

    /// <summary>
    /// Takes ownership of <paramref name="memory"/>: maps the whole region read-only and disposes the
    /// SharedMemory when this socket is disposed. Use this when the socket owns its backing region
    /// (e.g. a per-instrument broadcast ring); use the view ctor when the region is owned elsewhere.
    /// </summary>
    public ReadOnlySocket(string name, SharedMemory memory)
        : this(name, memory.GetView(0, memory.Length, Access.Read))
    {
        _ownedMemory = memory;
    }

    public bool IsClosed { get { return _isClosed; } }
    public bool IsDisposed { get { return _isDisposed; } }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            unsafe { return (int)(_endPtr - _startPtr); }
        }
    }

    // Attaches to a ring already in use: park at the head rather than replay the backlog.
    // The backlog is deliberately discarded — the client's authoritative state was never in the
    // ring. Server.OnFill/OnOrderState write PositionHeader and OrderState straight into the
    // SharedArray, ungated by client status, so those stay current the whole time a client is
    // Detached; only the notification is gated. Replaying would double-count anything a strategy
    // accumulates from Fill callbacks.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Recover()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        unsafe
        {
            Protocol.SkipRing(ref _readPtr, _startPtr, _endPtr, ref _readSeq);
        }
        _isClosed = false;   // the previous session's close message is not ours
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        unsafe
        {
            _readPtr = _startPtr;
        }
        _readSeq = 0;
        _isClosed = false;
    }

    public void Close()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        _isClosed = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ReadStatus TryRead(out ReadOnlySpan<byte> rdst)
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        if (_isClosed)
        {
            rdst = ReadOnlySpan<byte>.Empty;
            return ReadStatus.Closed;
        }

        ReadStatus status = Protocol.TryReadFromRing(ref _readPtr, _startPtr, _endPtr, _buffer, out rdst, ref _readSeq);
        if (Socket.IsCloseMessage(rdst))
        {
            _isClosed = true;
            return ReadStatus.Closed;
        }

        return status;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ReadStatus TryRead<T>(ref T value) where T : unmanaged
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        if (_isClosed)
        {
            value = default!;
            return ReadStatus.Closed;
        }

        Span<byte> dst = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
        ReadStatus status = Protocol.TryReadFromRing(ref _readPtr, _startPtr, _endPtr, dst, out ReadOnlySpan<byte> rdst, ref _readSeq);

        if (Socket.IsCloseMessage(rdst))
        {
            _isClosed = true;
            value = default!;
            return ReadStatus.Closed;
        }

        if (status != ReadStatus.New)
        {
            return status;
        }

        if (rdst.Length != dst.Length)
        {
            throw new ArgumentException($"Payload length mismatch.");
        }

        return ReadStatus.New;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ReadStatus GetReadStatus()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        if (_isClosed) return ReadStatus.Closed;
        return Protocol.GetReadStatusFromRing((Protocol.Header64*)_readPtr, _startPtr, _endPtr, _readSeq);
    }

    public void Dispose()
    {
        unsafe
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _view.Dispose();
            _ownedMemory?.Dispose();
            _startPtr = null;
            _readPtr = null;
            _endPtr = null;
            _buffer = Array.Empty<byte>();
        }
    }
}

public sealed class WriteOnlySocket : IDisposable
{
    public readonly string Name;

    private SharedMemoryView _view;
    private SharedMemory? _ownedMemory;
    private unsafe byte* _startPtr = null;
    private unsafe byte* _endPtr = null;
    private unsafe byte* _writePtr = null;
    private ulong _writeSeq = 0;
    private bool _isClosed = false;
    private bool _isDisposed = false;

    public WriteOnlySocket(string name, SharedMemoryView view)
    {
        Name = name;
        _view = view;
        unsafe
        {
            _startPtr = _view.GetPtr();
            _writePtr = _startPtr;
            _endPtr = _startPtr + _view.Length;
        }
    }

    /// <summary>
    /// Takes ownership of <paramref name="memory"/>: maps the whole region writable and disposes the
    /// SharedMemory when this socket is disposed. Use this when the socket owns its backing region
    /// (e.g. a per-instrument broadcast ring); use the view ctor when the region is owned elsewhere.
    /// </summary>
    public WriteOnlySocket(string name, SharedMemory memory)
        : this(name, memory.GetView(0, memory.Length, Access.Write))
    {
        _ownedMemory = memory;
    }

    public bool IsClosed { get { return _isClosed; } }
    public bool IsDisposed { get { return _isDisposed; } }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            unsafe { return (int)(_endPtr - _startPtr); }
        }
    }

    // Attaches to a ring already in use: resume the existing sequence space. Restarting at 0 makes
    // everything this writer publishes read as stale to any reader parked higher — permanently.
    // Also clears the _isClosed latch: a latched writer emits close messages instead of payloads.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Recover()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        unsafe
        {
            Protocol.SkipRing(ref _writePtr, _startPtr, _endPtr, ref _writeSeq);
        }
        _isClosed = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        unsafe
        {
            _writePtr = _startPtr;
        }
        _writeSeq = 0;
        _isClosed = false;
    }

    public unsafe void Close()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        if (_isClosed) return;
        _isClosed = true;
        Protocol.WriteToRing(Socket.CloseMessage, ref _writePtr, _startPtr, _endPtr, ref _writeSeq);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write(ReadOnlySpan<byte> src)
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        if (_isClosed)
        {
            Protocol.WriteToRing(Socket.CloseMessage, ref _writePtr, _startPtr, _endPtr, ref _writeSeq);
            return;
        }
        Protocol.WriteToRing(src, ref _writePtr, _startPtr, _endPtr, ref _writeSeq);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write<T>(in T value) where T : unmanaged
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        if (_isClosed) return;
        Protocol.WriteToRing(in value, ref _writePtr, _startPtr, _endPtr, ref _writeSeq);
    }

    public void Dispose()
    {
        unsafe
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _view.Dispose();
            _ownedMemory?.Dispose();
            _startPtr = null;
            _writePtr = null;
            _endPtr = null;
        }
    }
}

public sealed class Socket : IDisposable
{
    public const byte CloseMessageByte = 0;
    public static readonly byte[] CloseMessage = [CloseMessageByte];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCloseMessage(ReadOnlySpan<byte> bytes) => bytes.Length == 1 && bytes[0] == CloseMessageByte;

    public readonly string Name;
    public readonly int ReadChannelCount;
    public readonly int WriteChannelCount;

    private readonly SharedMemory _sharedMemory;
    private readonly ReadOnlySocket[] _readOnlySockets;
    private readonly WriteOnlySocket[] _writeOnlySockets;
    private bool _isDisposed = false;
    private bool _isClosed = false;

    public Socket(string name, SharedMemory sharedMemory, SharedMemoryView[] writeViews, SharedMemoryView[] readViews)
    {
        Name = name;
        ReadChannelCount = readViews.Length;
        WriteChannelCount = writeViews.Length;
        _sharedMemory = sharedMemory;
        _writeOnlySockets = new WriteOnlySocket[writeViews.Length];
        _readOnlySockets = new ReadOnlySocket[readViews.Length];

        // Recover() each sub-socket rather than assuming a virgin region. Nothing clears shared
        // memory any more, so a socket may be attaching to a ring that is already in use — by a
        // previous incarnation of this process, or by the peer that never went away. Reset() cannot
        // serve as the synchronisation mechanism: it clears the region and *one* side's cursors,
        // but the other side's cursors live in another process and are unreachable.
        for (int i = 0; i < writeViews.Length; i++)
        {
            if (writeViews[i] != null)
            {
                _writeOnlySockets[i] = new WriteOnlySocket(SocketUtils.GetChannelName(Name, i, ChannelDirection.ServerToClient), writeViews[i]);
                _writeOnlySockets[i].Recover();
            }
        }
        for (int i = 0; i < readViews.Length; i++)
        {
            if (readViews[i] != null)
            {
                _readOnlySockets[i] = new ReadOnlySocket(SocketUtils.GetChannelName(Name, i, ChannelDirection.ClientToServer), readViews[i]);
                _readOnlySockets[i].Recover();
            }
        }
    }

    public bool IsDisposed { get { return _isDisposed; } }
    public bool IsClosed { get { return _isClosed; } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetReadChannelLength(int channelId)
    {
        if (HasReader(channelId)) return _readOnlySockets[channelId].Length;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetWriteChannelLength(int channelId)
    {
        if (HasWriter(channelId)) return _writeOnlySockets[channelId].Length;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        _isClosed = false;

        foreach (WriteOnlySocket writer in _writeOnlySockets)
        {
            if (writer != null) writer.Reset();
        }
        foreach (ReadOnlySocket reader in _readOnlySockets)
        {
            if (reader != null) reader.Reset();
        }
        _sharedMemory.Clear();
    }

    public void Close()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        _isClosed = true;
        foreach (WriteOnlySocket writer in _writeOnlySockets) writer?.Close();
        foreach (ReadOnlySocket reader in _readOnlySockets) reader?.Close();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        foreach (ReadOnlySocket reader in _readOnlySockets) reader?.Dispose();
        foreach (WriteOnlySocket writer in _writeOnlySockets) writer?.Dispose();
        _sharedMemory.Dispose();
    }

    public bool HasReader(int channelId) => channelId >= 0 && channelId < ReadChannelCount && _readOnlySockets[channelId] != null;
    public bool HasWriter(int channelId) => channelId >= 0 && channelId < WriteChannelCount && _writeOnlySockets[channelId] != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int channelId, ReadOnlySpan<byte> bytes)
    {
        if (HasWriter(channelId)) _writeOnlySockets[channelId].Write(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(int channelId, in T value) where T : unmanaged
    {
        if (HasWriter(channelId)) _writeOnlySockets[channelId].Write(in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead(int channelId, out ReadOnlySpan<byte> bytes)
    {
        if (!HasReader(channelId))
        {
            bytes = default;
            return ReadStatus.Empty;
        }
        return _readOnlySockets[channelId].TryRead(out bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead<T>(int channelId, ref T value) where T : unmanaged
    {
        if (!HasReader(channelId)) return ReadStatus.Empty;
        return _readOnlySockets[channelId].TryRead(ref value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus GetReadStatus(int channelId)
    {
        if (!HasReader(channelId)) return ReadStatus.Empty;
        return _readOnlySockets[channelId].GetReadStatus();
    }
}

public sealed class SocketListener : IDisposable
{
    public readonly SocketHeader SocketHeader;
    public readonly string ClientName;
    public readonly string ServerName;
    public readonly string Name;
    public readonly int ClientToServerChannelCount;
    public readonly int ServerToClientChannelCount;

    private readonly SharedMemory _sharedMemory;
    private readonly ReadOnlySocket[] _clientToServer;
    private readonly ReadOnlySocket[] _serverToClient;
    private bool _isDisposed = false;

    public SocketListener(SocketHeader header)
    {
        SocketHeader = header;
        ClientName = SocketHeader.ClientName.ToString();
        ServerName = SocketHeader.ServerName.ToString();
        Name = SocketHeader.Name;
        ClientToServerChannelCount = SocketHeader.ClientToServerChannelCount;
        ServerToClientChannelCount = SocketHeader.ServerToClientChannelCount;

        _sharedMemory = SocketHeader.CreateOrOpenSharedMemory();
        _clientToServer = new ReadOnlySocket[ClientToServerChannelCount];
        _serverToClient = new ReadOnlySocket[ServerToClientChannelCount];

        unsafe
        {
            int offset = 0;
            for (int i = 0; i < ClientToServerChannelCount; i++)
            {
                int len = SocketHeader.ClientToServerLengths[i];
                if (len > 0)
                {
                    _clientToServer[i] = new ReadOnlySocket(SocketUtils.GetChannelName(Name, i, ChannelDirection.ClientToServer), _sharedMemory.GetView(offset, len, Access.Read));
                    offset += len;
                }
            }
            for (int i = 0; i < ServerToClientChannelCount; i++)
            {
                int len = SocketHeader.ServerToClientLengths[i];
                if (len > 0)
                {
                    _serverToClient[i] = new ReadOnlySocket(SocketUtils.GetChannelName(Name, i, ChannelDirection.ServerToClient), _sharedMemory.GetView(offset, len, Access.Read));
                    offset += len;
                }
            }
        }
    }

    public bool IsDisposed { get { return _isDisposed; } }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        for (int i = 0; i < ClientToServerChannelCount; i++) _clientToServer[i]?.Dispose();
        for (int i = 0; i < ServerToClientChannelCount; i++) _serverToClient[i]?.Dispose();
        _sharedMemory.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasClientToServerChannel(int channelId) => channelId >= 0 && channelId < ClientToServerChannelCount && _clientToServer[channelId] != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasServerToClientChannel(int channelId) => channelId >= 0 && channelId < ServerToClientChannelCount && _serverToClient[channelId] != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus GetServerToClientReadStatus(int channelId) => HasServerToClientChannel(channelId) ? _serverToClient[channelId].GetReadStatus() : ReadStatus.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus GetClientToServerReadStatus(int channelId) => HasClientToServerChannel(channelId) ? _clientToServer[channelId].GetReadStatus() : ReadStatus.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryReadServerToClient(int channelId, out ReadOnlySpan<byte> bytes)
    {
        if (!HasServerToClientChannel(channelId))
        {
            bytes = default;
            return ReadStatus.Empty;
        }
        return _serverToClient[channelId].TryRead(out bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryReadClientToServer(int channelId, out ReadOnlySpan<byte> bytes)
    {
        if (!HasClientToServerChannel(channelId))
        {
            bytes = default;
            return ReadStatus.Empty;
        }
        return _clientToServer[channelId].TryRead(out bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryReadServerToClient<T>(int channelId, ref T value) where T : unmanaged
    {
        if (!HasServerToClientChannel(channelId)) return ReadStatus.Empty;
        return _serverToClient[channelId].TryRead(ref value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryReadClientToServer<T>(int channelId, ref T value) where T : unmanaged
    {
        if (!HasClientToServerChannel(channelId)) return ReadStatus.Empty;
        return _clientToServer[channelId].TryRead(ref value);
    }
}

public sealed class ClientSocket : IDisposable
{
    public readonly string ClientName;
    public readonly string ServerName;
    public readonly string Name;
    public readonly int ClientToServerChannelCount;
    public readonly int ServerToClientChannelCount;

    private SocketHeader _socketHeader;
    private readonly Socket _socket;

    public ClientSocket(string clientName, string serverName, int[] clientToServerLengths, int[] serverToClientLengths)
    {
        ClientName = clientName;
        ServerName = serverName;
        Name = SocketUtils.GetSocketName(clientName, serverName);
        ClientToServerChannelCount = clientToServerLengths.Length;
        ServerToClientChannelCount = serverToClientLengths.Length;

        for (int i = 0; i < clientToServerLengths.Length; i++)
        {
            if (clientToServerLengths[i] > 0 && (clientToServerLengths[i] & 63) != 0)
            {
                throw new ArgumentException($"ClientSocket ClientToServer length {clientToServerLengths[i]} must be multiple of 64");
            }
        }
        for (int i = 0; i < serverToClientLengths.Length; i++)
        {
            if (serverToClientLengths[i] > 0 && (serverToClientLengths[i] & 63) != 0)
            {
                throw new ArgumentException($"ClientSocket ServerToClient length {serverToClientLengths[i]} must be multiple of 64");
            }
        }

        _socketHeader = new SocketHeader(serverName, clientName, clientToServerLengths, serverToClientLengths, Environment.ProcessId);

        if (ClientToServerChannelCount > 8 || ServerToClientChannelCount > 8)
        {
            throw new ArgumentException("Max 8 channels");
        }

        SharedMemory sharedMemory = _socketHeader.CreateOrOpenSharedMemory();

        SharedMemoryView[] clientToServer = new SharedMemoryView[ClientToServerChannelCount];
        SharedMemoryView[] serverToClient = new SharedMemoryView[ServerToClientChannelCount];

        int offset = 0;
        for (int i = 0; i < ClientToServerChannelCount; i++)
        {
            if (clientToServerLengths[i] > 0)
            {
                clientToServer[i] = sharedMemory.GetView(offset, clientToServerLengths[i], Access.Write);
                offset += clientToServerLengths[i];
            }
        }
        for (int i = 0; i < ServerToClientChannelCount; i++)
        {
            if (serverToClientLengths[i] > 0)
            {
                serverToClient[i] = sharedMemory.GetView(offset, serverToClientLengths[i], Access.Read);
                offset += serverToClientLengths[i];
            }
        }

        _socket = new Socket(Name, sharedMemory, clientToServer, serverToClient);
        Application.AddExitAction($"Close ClientSocket {Name}", Close);
    }

    public bool IsDisposed { get { return _socket == null || _socket.IsDisposed; } }
    public bool IsClosed { get { return _socket == null || _socket.IsClosed; } }

    public int Connect()
    {
        if (_socket == null) throw new InvalidOperationException("No socket");
        if (IsClosed) return -1;

        using LetterBox<SocketHeader> clientBox = new LetterBox<SocketHeader>(ClientName, Access.Read);
        using LetterBox<SocketHeader> serverBox = new LetterBox<SocketHeader>(ServerName, Access.Write);

        while (!serverBox.TryStore(in _socketHeader))
        {
            Thread.Sleep(1);
        }

        Timestamp waitingSince = Timestamp.UtcNow;
        SocketHeader reply = default;

        while (true)
        {
            bool peek = clientBox.TryPeek(out reply);

            if (peek && reply.ClientName == _socketHeader.ClientName && reply.Timestamp == _socketHeader.Timestamp)
            {
                clientBox.TryEmpty(out reply);
                break;
            }

            Timestamp now = Timestamp.UtcNow;
            if ((now - waitingSince).TotalSeconds > 3)
            {
                waitingSince = now;
                Console.WriteLine($"{ClientName}: Waiting for server {ServerName}...");
            }

            Thread.Sleep(1);
        }
        Console.WriteLine($"{ClientName}: Connected.");
        return reply.ClientId;
    }

    public void Close() => _socket?.Close();
    public void Dispose() => _socket?.Dispose();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> src) => Write(0, src);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(in T value) where T : unmanaged => Write(0, in value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int channelId, ReadOnlySpan<byte> src) => _socket.Write(channelId, src);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(int channelId, in T value) where T : unmanaged => _socket.Write(channelId, in value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead(out ReadOnlySpan<byte> rsrc) => TryRead(0, out rsrc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead<T>(ref T value) where T : unmanaged => TryRead(0, ref value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead(int channelId, out ReadOnlySpan<byte> rsrc) => _socket.TryRead(channelId, out rsrc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead<T>(int channelId, ref T value) where T : unmanaged => _socket.TryRead(channelId, ref value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus GetReadStatus(int channelId = 0) => _socket.GetReadStatus(channelId);
}

public delegate int ClientIdAllocator(in SocketHeader socketHeader);
public delegate SocketHeader ClientDeallocator(int clientId);

public delegate void ClientAllocated(in SocketHeader socketHeader);

public sealed class ServerSocket : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ClientHeader()
    {
        public volatile ClientStatus Status = ClientStatus.Disposed;

        private long _closedTimestamp = Timestamp.MaxValue.NanosSinceEpoch;
        public Timestamp ClosedTimestamp
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new Timestamp(Volatile.Read(ref _closedTimestamp));
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Volatile.Write(ref _closedTimestamp, value.NanosSinceEpoch);
        }
        public int ClientProcessId;
        public Socket? ClientSocket;
    }

    // Client sockets outlive their client process. Set from ServerHeader.Persistance before Listen().
    public bool Persistance = false;

    public readonly int Capacity;
    public readonly string ServerName;

    // ClientAllocated means "a socket was created for this client" and fires once per socket.
    // ClientOpened/ClientClosed fire on every attach/detach. Anything that needs a live socket must
    // hang off ClientOpened: at ClientAllocated time the socket may not exist yet.
    public event ClientAllocated? ClientAllocated;
    public event ClientAllocated? ClientDeallocated;
    public event Action<int>? ClientOpened;
    public event Action<int>? ClientClosed;
    public event Action<Exception>? Exception;
    public ClientIdAllocator AllocateClientId;
    public ClientDeallocator DeallocateClient;


    private readonly LetterBox<SocketHeader> _letterBox;
    private SocketHeader[] _clientSocketHeaders;
    private readonly ClientHeader[] _clientHeaders;
    private Bitset64 _clientIds;  // replace with IBitset so it can handle any capacity
    private Thread? _listenThread;
    private volatile bool _isRunning;

    public ServerSocket(string name, int capacity)
    {
        ServerName = name;
        Capacity = capacity;
        _letterBox = new LetterBox<SocketHeader>(ServerName, Access.Write);
        _clientSocketHeaders = new SocketHeader[capacity];
        _clientHeaders = new ClientHeader[Capacity];
        AllocateClientId = DefaultClientIdAllocator;
        DeallocateClient = DefaultClientDeallocator;

        for (int i = 0; i < Capacity; ++i)
        {
            _clientHeaders[i] = new ClientHeader();  // runs field initializers
        }
        Application.AddExitAction($"Close ServerSocket {ServerName}", Dispose);
    }

    // Only availalbe if the capacity is 64 or less, otherwise returns garbage.
    public Bitset64 ClientIds() => new Bitset64(_clientIds.AtomicLoad());

    public void Listen()
    {
        if (_isRunning) return;
        _isRunning = true;
        _listenThread = LowLatency.StartBackgroundThread($"{ServerName}.Listen()", () =>
        {
            while (_isRunning)
            {
                PollPids();
                PollLetterBox();
                Thread.Sleep(1);
            }
        });
    }

    private int DefaultClientIdAllocator(in SocketHeader socketHeader)
    {
        for(int i = 0; i < _clientSocketHeaders.Length; i++)
        {
            if (_clientSocketHeaders[i].ClientName == socketHeader.ClientName)
                return i;
        }

        if (_clientIds.IsFull)
            return -1;

        SocketHeader socketHeaderCopy = socketHeader;
        socketHeaderCopy.ClientId = _clientIds.LowestClear;
        _clientSocketHeaders[socketHeaderCopy.ClientId] = socketHeaderCopy;

        return socketHeaderCopy.ClientId;
    }
    private SocketHeader DefaultClientDeallocator(int clientId)
    {
        SocketHeader socketHeader = _clientSocketHeaders[clientId];
        _clientSocketHeaders[clientId] = default;
        return socketHeader;
    }

    private void DisposeClient(int clientId)
    {
        ref ClientHeader clientHeader = ref _clientHeaders[clientId];
        if (clientHeader.Status != ClientStatus.Disposed)
        {
            SocketHeader socketHeader = DeallocateClient(clientId);
            if (ClientDeallocated != null)
            {
                ClientDeallocated(in socketHeader);
            }

            Console.WriteLine($"{ServerName}: {socketHeader.ClientName.ToString()} Disconnected id {clientId}");

            clientHeader.Status = ClientStatus.Disposed;
            clientHeader.ClientSocket?.Dispose();
            clientHeader.ClientSocket = null;
        }
    }

    // Rebuilds a persisted client's socket at startup without a connecting process: it comes up
    // Detached, so the server can already write into its ring and the audit tap can already read.
    public void CreateDetatchedClient(in SocketHeader socketHeader)
    {
        if (!Persistance)
            throw new InvalidOperationException($"{GetType().Name}.CreateDetatchedClient: Persistance is disabled.");

        if (socketHeader.ClientId < 0 || socketHeader.ClientId >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(socketHeader), $"{GetType().Name}.CreateDetatchedClient: invalid clientId {socketHeader.ClientId}.");

        CreateClient(in socketHeader);
        _clientHeaders[socketHeader.ClientId].Status = ClientStatus.Detached;
    }

    private void CreateClient(in SocketHeader socketHeader)
    {
        int clientId = socketHeader.ClientId;
        string socketName = socketHeader.Name;
        SharedMemory sharedMemory = socketHeader.CreateOrOpenSharedMemory();

        SharedMemoryView[] clientToServer = new SharedMemoryView[socketHeader.ClientToServerChannelCount];
        SharedMemoryView[] serverToClient = new SharedMemoryView[socketHeader.ServerToClientChannelCount];

        int offset = 0;
        for (int i = 0; i < socketHeader.ClientToServerChannelCount; i++)
        {
            int len = socketHeader.ClientToServerLengths[i];
            if (len > 0)
            {
                clientToServer[i] = sharedMemory.GetView(offset, len, Access.Read);
            }
            offset += len;
        }
        for (int i = 0; i < socketHeader.ServerToClientChannelCount; i++)
        {
            int len = socketHeader.ServerToClientLengths[i];
            if (len > 0)
            {
                serverToClient[i] = sharedMemory.GetView(offset, len, Access.Write);
            }
            offset += len;
        }

        ref ClientHeader clientHeader = ref _clientHeaders[clientId];
        clientHeader.ClientSocket = new Socket(socketName, sharedMemory, serverToClient, clientToServer);
    }

    private void OpenClient(in SocketHeader socketHeader)
    {
        int clientId = socketHeader.ClientId;
        ref ClientHeader clientHeader = ref _clientHeaders[clientId];

        if (!Persistance)
            clientHeader.ClientSocket?.Reset();
            
        clientHeader.ClosedTimestamp = Timestamp.MaxValue;
        clientHeader.ClientProcessId = socketHeader.ClientProcessId;

        using LetterBox<SocketHeader> clientBox = new LetterBox<SocketHeader>(socketHeader.ClientName.ToString(), Access.Write);
        while (!clientBox.TryStore(in socketHeader))
        {
            Thread.Sleep(1);
        }

        clientHeader.Status = ClientStatus.Open;
        _clientIds.AtomicSet(clientId);

        ClientOpened?.Invoke(clientId);

        Console.WriteLine($"{ServerName}: {socketHeader.ClientName.ToString()} Connected id {clientId}");

    }

    private void CloseClient(int clientId)
    {
        _clientIds.AtomicClear(clientId);
        ref ClientHeader clientHeader = ref _clientHeaders[clientId];
        if (Persistance)
        {
            // Socket outlives the client process: the server keeps writing into its ring, so fills
            // the exchange retransmits while the client is away still land somewhere, and the audit
            // tap on the same region still sees them.
            clientHeader.Status = ClientStatus.Detached;
        }
        else
        {
            clientHeader.ClosedTimestamp = Timestamp.UtcNow;
            clientHeader.Status = ClientStatus.Closed;
        }
        ClientClosed?.Invoke(clientId);
    }

    private void PollLetterBox()
    {
        if (_letterBox.TryEmpty(out SocketHeader socketHeader))
        {
            string clientName = socketHeader.ClientName.ToString();
            Console.WriteLine($"{GetType().Name}::{ServerName}: Received connection request from {clientName}");

            int clientId = AllocateClientId(in socketHeader);
            socketHeader.ClientId = clientId;
            if (clientId < 0)
            {
                Console.WriteLine($"{GetType().Name}::{ServerName}: Client {clientName} failed to allocate clientId.");
                return;
            }

            ClientStatus status = _clientHeaders[clientId].Status;

            if (status == ClientStatus.Open)
            {
                Console.WriteLine($"{GetType().Name}::{ServerName}: Client {clientName} is already connected.");
                return;
            }
            else if (status == ClientStatus.Closed)
            {
                Console.WriteLine($"{GetType().Name}::{ServerName}: Client {clientName} is in the process of disposing. Try again in a moment.");
                return;
            }
            else if (status == ClientStatus.Detached)
            {
                // Re-attach: deliberately skip CreateClient and reuse the existing Socket. That is
                // what preserves both the buffered ring and this side's read cursors.
            }
            else if (status == ClientStatus.Disposed)
            {
                if (ClientAllocated != null)
                {
                    ClientAllocated(in socketHeader);
                }
                CreateClient(in socketHeader);
            }

            OpenClient(in socketHeader);
        }
    }

    private void PollPids()
    {
        Timestamp now = Timestamp.UtcNow;
        for (int clientId = 0; clientId < Capacity; ++clientId)
        {
            ref ClientHeader clientHeader = ref _clientHeaders[clientId];
            ClientStatus status = clientHeader.Status;

            if (status == ClientStatus.Open)
            {
                if (!ProcessId.IsAlive(clientHeader.ClientProcessId))
                {
                    CloseClient(clientId);
                }
            }
            else if (status == ClientStatus.Closed)
            {
                if ((now - clientHeader.ClosedTimestamp).TotalSeconds > 1)
                {
                    DisposeClient(clientId);
                }
            }
        }
    }

    public void Stop()
    {
        if (_isRunning)
        {
            _isRunning = false;
            _listenThread?.Join(500);
        }
    }

    public void Dispose()
    {
        Stop();
        for (int clientId = 0; clientId < Capacity; ++clientId)
        {
            DisposeClient(clientId);
        }
        _letterBox.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus GetReadStatus(int clientId, int channelId)
    {
        if (clientId < 0 || clientId >= Capacity) return ReadStatus.Empty;
        ref ClientHeader client = ref _clientHeaders[clientId];
        if (client.Status != ClientStatus.Open) return ReadStatus.Closed;

        try
        {
            Socket clientSocket = client.ClientSocket!;
            if (clientSocket != null && clientSocket.HasReader(channelId))
            {
                ReadStatus result = clientSocket.GetReadStatus(channelId);
                if (result == ReadStatus.Closed)
                {
                    CloseClient(clientId);
                }
                return result;
            }
        }
        catch(Exception exception)
        {
            Exception?.Invoke(exception);
        }
        return ReadStatus.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead(int clientId, int channelId, out ReadOnlySpan<byte> rdst)
    {
        rdst = default;
        if (clientId < 0 || clientId >= Capacity) return ReadStatus.Empty;

        ref ClientHeader clientHeader = ref _clientHeaders[clientId];
        Socket clientSocket = clientHeader.ClientSocket!;

        if (clientHeader.Status != ClientStatus.Open || clientSocket == null) return ReadStatus.Closed;

        try
        {
            if (!clientSocket.HasReader(channelId)) return ReadStatus.Empty;

            ReadStatus result = clientSocket.TryRead(channelId, out rdst);
            if (result == ReadStatus.Closed)
            {
                if (clientHeader.Status == ClientStatus.Open)
                {
                    CloseClient(clientId);
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Exception?.Invoke(ex);
        }
        return ReadStatus.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadStatus TryRead(int clientId, out ReadOnlySpan<byte> rdst) => TryRead(clientId, 0, out rdst);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(int clientId, int channelId, in T value) where T : unmanaged
    {
        if (clientId < 0 || clientId >= Capacity) return;
        ref ClientHeader client = ref _clientHeaders[clientId];

        // Detached is writable: this is the change that lets the server buffer fills for a client
        // whose process is gone. Reads stay Open-only — nothing writes to a detached client's
        // inbound ring, and refusing to act on a dead client's last unread order target is
        // deliberate (CancelAllOrders is about to cancel it anyway).
        ClientStatus status = client.Status;
        if (status != ClientStatus.Open && status != ClientStatus.Detached) return;
        Socket clientSocket = client.ClientSocket!;

        try
        {
            if (clientSocket != null && clientSocket.HasWriter(channelId))
            {
                clientSocket.Write(channelId, in value);
            }
        }
        catch (Exception ex)
        {
            Exception?.Invoke(ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int clientId, int channelId, ReadOnlySpan<byte> src)
    {
        if (clientId < 0 || clientId >= Capacity) return;
        ref ClientHeader client = ref _clientHeaders[clientId];

        ClientStatus status = client.Status;
        if (status != ClientStatus.Open && status != ClientStatus.Detached) return;
        Socket clientSocket = client.ClientSocket!;

        try
        {
            if (clientSocket != null && clientSocket.HasWriter(channelId))
            {
                clientSocket.Write(channelId, src);
            }
        }
        catch (Exception ex)
        {
            Exception?.Invoke(ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(int clientId, in T value) where T : unmanaged => Write(clientId, 0, in value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int clientId, ReadOnlySpan<byte> src) => Write(clientId, 0, src);
}
//END_FILE HFT/Socket/Socket.cs
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Provider;
using Tools;

namespace Mirror;

/// One TCP connection carrying length-prefixed packets, the same whichever end dialled. A reader thread queues every
/// received packet, so TryRead never blocks; a writer thread sends what Write queues, so a slow peer never blocks the
/// owner. A failed read or write, 10 s of silence, or a send queue that overflows marks it disconnected for good, and
/// the connector makes a new one. Cold path: allocates per packet, in a process that holds no hot path.
public sealed class TcpLink : IDisposable
{
    private static readonly int s_silenceTimeoutMilliseconds = (int)RemoteClient.s_silenceTimeout.TotalMilliseconds;  // Why: both ends send at least once a second; 10 s rides out a Wi-Fi blip
    private const int s_maximumPacketLength = 1 << 20;
    private const int s_maximumQueuedPackets = 1 << 20;       // Why: a peer that cannot keep up is dropped, and re-snapshots on its next link

    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _networkStream;
    private readonly ConcurrentQueue<byte[]> _receivedPackets = new();
    private readonly BlockingCollection<byte[]> _sendPackets = new(s_maximumQueuedPackets);
    private volatile bool _isConnected = true;

    public string RemoteEndPoint { get; }
    public bool IsConnected => _isConnected;

    public TcpLink(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        // Why: the link owns the socket from here, so a throw below must close it, not leak it
        try
        {
            // Step 1: a read that waits 10 s means the peer is gone; a stuck write gives up just as soon
            tcpClient.NoDelay = true;
            tcpClient.ReceiveTimeout = s_silenceTimeoutMilliseconds;
            tcpClient.SendTimeout = s_silenceTimeoutMilliseconds;
            _networkStream = tcpClient.GetStream();
            RemoteEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "";

            // Step 2: one thread each way
            LowLatency.StartBackgroundThread($"TcpLink.Read({RemoteEndPoint})", ReadPackets);
            LowLatency.StartBackgroundThread($"TcpLink.Write({RemoteEndPoint})", WritePackets);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    /// Returns the next received packet, the caller's to read or modify; false when none is waiting.
    public bool TryRead(out Span<byte> packet)
    {
        bool isRead = _receivedPackets.TryDequeue(out byte[]? receivedPacket);
        packet = receivedPacket;
        return isRead;
    }

    /// Queues one packet for the writer thread; it is copied, so the caller may reuse its buffer. Dropped once disconnected.
    public void Write(ReadOnlySpan<byte> packet)
    {
        if (_isConnected && !_sendPackets.TryAdd(packet.ToArray()))
            Disconnect("send queue full");
    }

    public void Dispose()
    {
        Disconnect("disposed");
        _sendPackets.CompleteAdding();
        _networkStream.Dispose();
        _tcpClient.Dispose();
    }

    // Reader thread: [int32 length][packet] frames until the connection fails or falls silent.
    private void ReadPackets()
    {
        try
        {
            Span<byte> packetLength = stackalloc byte[sizeof(int)];
            while (_isConnected)
            {
                // Step 1: the length prefix
                _networkStream.ReadExactly(packetLength);
                int length = BinaryPrimitives.ReadInt32LittleEndian(packetLength);
                if (length <= 0 || length > s_maximumPacketLength)
                    throw new InvalidDataException($"TcpLink.ReadPackets: packet length {length}");

                // Step 2: the packet, for the owner's thread
                byte[] packet = new byte[length];
                _networkStream.ReadExactly(packet);
                _receivedPackets.Enqueue(packet);
            }
        }
        catch (Exception exception)
        {
            Disconnect(exception.Message);
        }
    }

    // Writer thread: sends queued packets, flushing whenever the queue runs empty so a pass goes out in few segments.
    private void WritePackets()
    {
        try
        {
            BufferedStream bufferedStream = new(_networkStream, 64 * 1024);
            Span<byte> packetLength = stackalloc byte[sizeof(int)];
            foreach (byte[] packet in _sendPackets.GetConsumingEnumerable())
            {
                BinaryPrimitives.WriteInt32LittleEndian(packetLength, packet.Length);
                bufferedStream.Write(packetLength);
                bufferedStream.Write(packet);
                if (_sendPackets.Count == 0)
                    bufferedStream.Flush();
            }
        }
        catch (Exception exception)
        {
            Disconnect(exception.Message);
        }
    }

    private void Disconnect(string reason)
    {
        if (_isConnected)
            Console.WriteLine($"TcpLink {RemoteEndPoint}: disconnected ({reason})");
        _isConnected = false;
    }
}

/// Makes TcpLinks in whichever directions it is given: listens on listenPort for a peer in ipWhitelist that dials in, and
/// dials each of connectAddresses every second until it has a live link to it. New links queue for the owner's thread.
/// Cold path: one listen thread and one dial thread.
public sealed class TcpLinkConnector(int listenPort, string[] ipWhitelist, string[] connectAddresses)
{
    private const int s_dialIntervalMilliseconds = 1_000;
    private const int s_connectTimeoutMilliseconds = 2_000;

    private readonly ConcurrentQueue<TcpLink> _newTcpLinks = new();

    /// Starts the listen thread when a port is configured, and the dial thread when addresses are.
    public void Start()
    {
        if (listenPort > 0)
            LowLatency.StartBackgroundThread($"TcpLinkConnector.Listen({listenPort})", AcceptTcpLinks);
        if (connectAddresses.Length > 0)
            LowLatency.StartBackgroundThread("TcpLinkConnector.Dial", DialTcpLinks);
    }

    /// Hands the owner a link that connected since the last call.
    public bool TryTakeNewLink([MaybeNullWhen(false)] out TcpLink tcpLink) => _newTcpLinks.TryDequeue(out tcpLink);

    // Listen thread: accepts connections from IPWhitelist for as long as the process runs, closes any other at once;
    // a failure is logged, never fatal.
    private void AcceptTcpLinks()
    {
        IPNetwork[] ipNetworks = Array.ConvertAll(ipWhitelist, entry => MirrorConfig.TryParseIPNetwork(entry, out IPNetwork ipNetwork) ? ipNetwork : throw new InvalidDataException($"TcpLinkConnector: IPWhitelist entry {entry} is no address or range"));
        TcpListener tcpListener = new(IPAddress.Any, listenPort);
        tcpListener.Start();
        while (true)
        {
            try
            {
                TcpClient tcpClient = tcpListener.AcceptTcpClient();
                IPAddress peerAddress = ((IPEndPoint)tcpClient.Client.RemoteEndPoint!).Address;
                if (!Array.Exists(ipNetworks, ipNetwork => ipNetwork.Contains(peerAddress)))
                {
                    Console.WriteLine($"TcpLinkConnector: refused {peerAddress}, not in IPWhitelist");
                    tcpClient.Dispose();
                    continue;
                }
                _newTcpLinks.Enqueue(new TcpLink(tcpClient));
            }
            catch (Exception exception)
            {
                Console.WriteLine($"TcpLinkConnector: accept failed ({exception.Message})");
            }
        }
    }

    // Dial thread: every second, redials each address with no live link; a failure is logged once until it recovers.
    private void DialTcpLinks()
    {
        TcpLink?[] _dialedTcpLinks = new TcpLink?[connectAddresses.Length];
        bool[] _isFailureLogged = new bool[connectAddresses.Length];
        while (true)
        {
            for (int addressIndex = 0; addressIndex < _dialedTcpLinks.Length; addressIndex++)
            {
                if (_dialedTcpLinks[addressIndex] is { IsConnected: true })
                    continue;
                string connectAddress = connectAddresses[addressIndex];
                try
                {
                    _dialedTcpLinks[addressIndex] = DialTcpLink(connectAddress);
                    _isFailureLogged[addressIndex] = false;
                }
                catch (Exception exception)
                {
                    if (!_isFailureLogged[addressIndex])
                        Console.WriteLine($"TcpLinkConnector: {connectAddress} unreachable ({exception.Message}); retrying every second");
                    _isFailureLogged[addressIndex] = true;
                }
            }
            Thread.Sleep(s_dialIntervalMilliseconds);
        }
    }

    // Connects to "host:port" and queues the link; throws when the peer is down, giving up after 2 s so one dead address cannot hold up the others.
    private TcpLink DialTcpLink(string connectAddress)
    {
        TcpClient tcpClient = new();
        try
        {
            int portSeparatorIndex = connectAddress.LastIndexOf(':');
            // Why a cancelled connect, not an abandoned one: an abandoned task faults unobserved, and the AlertManager would alert each one
            using CancellationTokenSource connectTimeout = new(s_connectTimeoutMilliseconds);
            tcpClient.ConnectAsync(connectAddress[..portSeparatorIndex], int.Parse(connectAddress[(portSeparatorIndex + 1)..]), connectTimeout.Token).GetAwaiter().GetResult();
            TcpLink tcpLink = new(tcpClient);
            _newTcpLinks.Enqueue(tcpLink);
            return tcpLink;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }
}

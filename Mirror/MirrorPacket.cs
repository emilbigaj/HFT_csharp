using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Data;
using Provider;
using Socket;
using Tools;

namespace Mirror;

// The packets of the mirror protocol, after the TcpLink's int32 length (see Spec.md "Remote workspaces").
public enum MirrorPacketType : byte
{
    Row = 1,         // Local → Remote: one shared-memory row whose bytes changed (every row, the first time)
    Message = 2,     // Remote → Local: a call one GUI wrote
    Heartbeat = 3,   // Local → Remote: closes each publish pass (the first completes a remote's copy)
    GuiList = 4,     // Remote → Local, every second: the GUIs attached on the remote, and whether it syncs their folders
    AllocatedClientId = 5,    // Local → Remote: a listed GUI's id (its ManualClient's), or a negative ConnectRefusal saying why not
    ServerHeader = 6,  // Local → Remote: opens each publish pass; a remote sizes its copy from the first and posts it in its LetterBox
    Ping = 7,        // Remote → Local, every 100 ms: the Remote's clock, echoed at once
    Pong = 8,        // Local → Remote: that ping, unchanged; the Remote times the round trip on its own clock
    FileAnnounce = 9,     // Local → Remote: one file's length and write time, sent when either changes; FileVersion.s_withdrawn: no longer offered
    FileRequest = 10,     // Remote → Local: one file's bytes from an offset (what the desk holds of it)
    FileChunk = 11,       // Local → Remote: the answer, up to one chunk from that offset (empty when the file cannot be read now)
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MirrorServerHeader()
{
    public Header<MirrorPacketType> Header = new(MirrorPacketType.ServerHeader);
    public ServerHeader ServerHeader;   // ServerName relative to the root, as names travel
    public uint LayoutHash;             // the Local end's Context.GetLayoutHash(): a Remote whose own differs refuses to start
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MirrorRowHeader()            // + the row's bytes
{
    public Header<MirrorPacketType> Header = new(MirrorPacketType.Row);
    public int ArrayId;
    public int Index;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MirrorMessageHeader()        // + the message's bytes
{
    public Header<MirrorPacketType> Header = new(MirrorPacketType.Message);
    public int ClientId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MirrorHeartbeat()
{
    public Header<MirrorPacketType> Header = new(MirrorPacketType.Heartbeat);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MirrorPing()
{
    public Header<MirrorPacketType> Header = new(MirrorPacketType.Ping);
    public Timestamp PingTimestamp;         // the Remote's clock when it sent the ping
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MirrorGuiList()                // + the GUIs (MirrorGui each)
{
    public Header<MirrorPacketType> Header = new(MirrorPacketType.GuiList);
    public bool IsFolderSyncOn;             // this desk copies the box's files of these GUIs' folders (its MirrorConfig.IsFolderSyncOn)
}

// One GUI attached on a remote: listed in GuiList (ClientId -1), answered by AllocatedClientId (its ManualClient's id, or a negative ConnectRefusal).
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MirrorGui()
{
    public int ClientId = -1;
    public int GuiProcessId;        // a new GUI process under a name starts clean: what the last one left is cancelled
    public String256 ClientName;    // relative to Context.DirectoriesRoot: each end puts its own root in front
}

/// Builds and reads the variable-length packets. Cold path: allocates per packet, in a process that holds no hot path.
public static class MirrorPacket
{
    /// [MirrorMessageHeader][message]: one socket message from one GUI, to the Local end.
    public static byte[] CreateMessage(int clientId, ReadOnlySpan<byte> message)
    {
        int headerLength = Unsafe.SizeOf<MirrorMessageHeader>();
        byte[] packet = new byte[headerLength + message.Length];
        MemoryMarshal.Write(packet, new MirrorMessageHeader() { ClientId = clientId });
        message.CopyTo(packet.AsSpan(headerLength));
        return packet;
    }

    /// [header][MirrorGui ...]: a GuiList (MirrorGuiList header) with any number of GUIs, an AllocatedClientId (a plain Header) with one.
    public static byte[] NewGuiPacket<THeader>(in THeader header, ReadOnlySpan<MirrorGui> mirrorGuis) where THeader : unmanaged
    {
        int headerLength = Unsafe.SizeOf<THeader>();
        byte[] packet = new byte[headerLength + mirrorGuis.Length * Unsafe.SizeOf<MirrorGui>()];
        MemoryMarshal.Write(packet, in header);
        MemoryMarshal.AsBytes(mirrorGuis).CopyTo(packet.AsSpan(headerLength));
        return packet;
    }

    /// The MirrorGui entries of a GuiList or AllocatedClientId packet, after its header.
    public static ReadOnlySpan<MirrorGui> ReadMirrorGuis<THeader>(ReadOnlySpan<byte> packet) where THeader : unmanaged
        => MemoryMarshal.Cast<byte, MirrorGui>(packet[Unsafe.SizeOf<THeader>()..]);

    /// Rewrites the paths in a SocketHeader row: relative to this end's root to travel, or back under it on arrival.
    public static void RewriteNames(SharedArray sharedArray, Span<byte> row, bool isTravelling)
    {
        if (sharedArray is SharedArray<SocketHeader>)
        {
            ref SocketHeader socketHeader = ref MemoryMarshal.AsRef<SocketHeader>(row);
            socketHeader.ServerName = RewriteName(socketHeader.ServerName, isTravelling);
            socketHeader.ClientName = RewriteName(socketHeader.ClientName, isTravelling);
        }
    }

    /// One path: made relative to Context.DirectoriesRoot to travel, or put back under it on arrival; an empty name stays empty.
    public static String256 RewriteName(in String256 name, bool isTravelling)
    {
        string path = name.ToString();
        if (path.Length == 0)
            return name;
        string rewrittenPath = isTravelling ? Path.GetRelativePath(Context.DirectoriesRoot, path) : Context.DirectoriesRoot / path;
        // Why: names are 256-character fields, and on a remote the install folder is part of every one
        if (rewrittenPath.Length > 256)
            throw new InvalidDataException($"MirrorPacket.RewriteName: {rewrittenPath} is {rewrittenPath.Length} characters; names must fit 256: move the Mirror's folder nearer the drive root");
        return new String256(rewrittenPath);
    }
}

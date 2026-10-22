using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Provider;
using Tools;

namespace Mirror;

// One file as the box has it: its length and write time; any change to either is announced.
public readonly record struct FileVersion(long Length, DateTime LastWriteTimeUtc)
{
    public static readonly FileVersion s_withdrawn = new(-1, default);   // the box no longer offers the file (not in this desk's folders now, or gone): stop asking
}

/// The folder sync's packets and the path rules both ends share (see Spec.md "Remote workspaces", "Folder sync"). Cold path:
/// allocates per packet, in a process that holds no hot path.
public static class FileSyncPacket
{
    public const int s_chunkLength = 64 * 1024;   // Why: a row waits behind at most the desk's requests in flight, one chunk each
    private const string s_workspacesFolderName = "Workspaces";   // each machine keeps its own layouts: never synced

    /// [FileAnnounce][path][length][write time]: a file's version on the box.
    public static byte[] NewAnnounce(string path, FileVersion fileVersion) => NewPacket(MirrorPacketType.FileAnnounce, binaryWriter =>
    {
        binaryWriter.Write(path);
        binaryWriter.Write(fileVersion.Length);
        binaryWriter.Write(fileVersion.LastWriteTimeUtc.Ticks);
    });

    /// The path and version of a FileAnnounce packet.
    public static (string Path, FileVersion FileVersion) ReadAnnounce(ReadOnlySpan<byte> packet)
    {
        using BinaryReader binaryReader = NewBodyReader(packet);
        return (binaryReader.ReadString(), new FileVersion(binaryReader.ReadInt64(), new DateTime(binaryReader.ReadInt64(), DateTimeKind.Utc)));
    }

    /// [FileRequest][path][offset][length]: a file's bytes from offset, length of them at most.
    public static byte[] NewRequest(string path, long offset, int length) => NewPacket(MirrorPacketType.FileRequest, binaryWriter =>
    {
        binaryWriter.Write(path);
        binaryWriter.Write(offset);
        binaryWriter.Write(length);
    });

    /// The path, offset and length of a FileRequest packet.
    public static (string Path, long Offset, int Length) ReadRequest(ReadOnlySpan<byte> packet)
    {
        using BinaryReader binaryReader = NewBodyReader(packet);
        return (binaryReader.ReadString(), binaryReader.ReadInt64(), binaryReader.ReadInt32());
    }

    /// [FileChunk][path][offset][length][bytes]: one chunk of the answer to a request; a short one (empty included) ends it.
    public static byte[] NewChunk(string path, long offset, byte[] bytes) => NewPacket(MirrorPacketType.FileChunk, binaryWriter =>
    {
        binaryWriter.Write(path);
        binaryWriter.Write(offset);
        binaryWriter.Write(bytes.Length);
        binaryWriter.Write(bytes);
    });

    /// The path, offset and bytes of a FileChunk packet.
    public static (string Path, long Offset, byte[] Bytes) ReadChunk(ReadOnlySpan<byte> packet)
    {
        using BinaryReader binaryReader = NewBodyReader(packet);
        return (binaryReader.ReadString(), binaryReader.ReadInt64(), binaryReader.ReadBytes(binaryReader.ReadInt32()));
    }

    /// An absolute path under directoriesRoot as it travels: relative, '/'-separated.
    // Why the separator, not '\': a Linux name may hold a '\', which must travel as itself (a Windows desk then refuses the name)
    public static string ToRelativePath(string directoriesRoot, string path) => Path.GetRelativePath(directoriesRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    /// A travelling path under this end's root; false for one that would leave it or name no file here (a "..", a ':' on Windows).
    public static bool TryToAbsolutePath(string directoriesRoot, string relativePath, out string absolutePath)
    {
        // Why every segment: on Windows a '\' leaves the root and a ':' names an NTFS stream
        bool isUnderRoot = !Array.Exists(relativePath.Split('/'), segment => segment is "" or "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
        absolutePath = isUnderRoot ? Path.Combine(directoriesRoot, relativePath) : "";
        return isUnderRoot;
    }

    /// Each file under the folders with its version, relative to directoriesRoot, without any Workspaces folder; a folder this end
    /// cannot read is skipped.
    public static IEnumerable<(string Path, FileVersion FileVersion)> EnumerateSyncedFiles(string directoriesRoot, IEnumerable<string> rootPaths)
    {
        EnumerationOptions enumerationOptions = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        DateOnly yesterday = DateOnly.FromDateTime(Clock.Now.ToDateTime).AddDays(-1);   // Why a day's margin: UTC, Chicago and local dates differ
        foreach (string rootPath in rootPaths)
        {
            if (!TryToAbsolutePath(directoriesRoot, rootPath, out string absoluteRootPath) || !Directory.Exists(absoluteRootPath))
                continue;
            foreach (FileInfo fileInfo in new DirectoryInfo(absoluteRootPath).EnumerateFiles("*", enumerationOptions))
            {
                // Why: NTFS keeps a held-open file's listed size stale (a writer appending all day); a file named for a day before
                // yesterday is finished and closed, so its listing is right; Linux reads the real size anyway
                bool isFinishedDay = DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(fileInfo.Name), "yyyy-MM-dd", out DateOnly fileDate) && fileDate < yesterday;
                if (OperatingSystem.IsWindows() && !isFinishedDay)
                    fileInfo.Refresh();
                string relativePath = ToRelativePath(directoriesRoot, fileInfo.FullName);
                if (!relativePath.Split('/').Contains(s_workspacesFolderName))
                    yield return (relativePath, new FileVersion(fileInfo.Length, fileInfo.LastWriteTimeUtc));
            }
        }
    }

    // [type][body]
    private static byte[] NewPacket(MirrorPacketType mirrorPacketType, Action<BinaryWriter> writeBody)
    {
        using MemoryStream memoryStream = new();
        using BinaryWriter binaryWriter = new(memoryStream);
        binaryWriter.Write((byte)mirrorPacketType);
        writeBody(binaryWriter);
        binaryWriter.Flush();
        return memoryStream.ToArray();
    }

    // A reader over a packet's body, after its type.
    private static BinaryReader NewBodyReader(ReadOnlySpan<byte> packet) => new(new MemoryStream(packet[1..].ToArray()));
}

/// The box's end of the folder sync for one desk: serves the folders of the GUIs that desk lists, when it syncs (each GUI's principal
/// and its own folder); every 100 ms announces each file under them whose length or write time changed, and answers each request with
/// chunks from the offset asked for, or withdraws a file it no longer offers. It keeps no picture of what the desk holds: the desk asks
/// for what it lacks (see Spec.md "Folder sync"). The Local Mirror's main thread. Cold path.
public sealed class FileSyncLocal
{
    private static readonly Duration s_scanInterval = Duration.FromMilliseconds(100);

    private readonly string _directoriesRoot;
    private string[] _rootPaths = [];
    private readonly Dictionary<string, FileVersion> _announcedVersionsByPath = new();   // what this desk was last told; only these may be requested
    private Timestamp _scanTimestamp;

    public FileSyncLocal(string directoriesRoot)
    {
        _directoriesRoot = directoriesRoot;
    }

    /// The GUIs a desk lists (names under this end's root) and whether it syncs: their folders replace the last, and a change
    /// announces every file afresh on the next scan.
    public void Serve(IEnumerable<string> guiClientNames, bool isFolderSyncOn)
    {
        string[] rootPaths = isFolderSyncOn ? NewRootPaths(guiClientNames) : [];
        if (rootPaths.SequenceEqual(_rootPaths))
            return;
        _rootPaths = rootPaths;
        _announcedVersionsByPath.Clear();
        _scanTimestamp = default;
    }

    // Each GUI's principal folder and its own (its stand-in's audit is under the GUI's name), relative to the root, sorted.
    private string[] NewRootPaths(IEnumerable<string> guiClientNames)
    {
        SortedSet<string> _servedRootPaths = new(StringComparer.Ordinal);
        foreach (string guiClientName in guiClientNames)
        {
            if (!ManualClient.TryParseRemoteGuiName(guiClientName, out string principalName, out _))
                continue;
            _servedRootPaths.Add(FileSyncPacket.ToRelativePath(_directoriesRoot, principalName));
            _servedRootPaths.Add(FileSyncPacket.ToRelativePath(_directoriesRoot, guiClientName));
        }
        return _servedRootPaths.ToArray();
    }

    /// Every 100 ms: announces each file whose version is not the one this desk was last told.
    public void Announce(TcpLink tcpLink)
    {
        Timestamp now = Timestamp.StopwatchNow;
        if (now - _scanTimestamp < s_scanInterval)
            return;
        _scanTimestamp = now;
        foreach ((string path, FileVersion fileVersion) in FileSyncPacket.EnumerateSyncedFiles(_directoriesRoot, _rootPaths))
        {
            if (_announcedVersionsByPath.TryGetValue(path, out FileVersion announcedVersion) && announcedVersion == fileVersion)
                continue;
            _announcedVersionsByPath[path] = fileVersion;
            tcpLink.Write(FileSyncPacket.NewAnnounce(path, fileVersion));
        }
    }

    /// Answers a request with consecutive chunks from its offset, up to its length; a short chunk (empty past the file's end, or for one
    /// not readable now) ends the answer before then. A file not offered now (not announced, or gone) is withdrawn instead.
    public void OnRequest(ReadOnlySpan<byte> packet, TcpLink tcpLink)
    {
        // Step 1: only an announced file: a desk reads nothing it was not offered (outside its folders, or a Workspaces folder)
        (string path, long offset, int length) = FileSyncPacket.ReadRequest(packet);
        long _chunkOffset = offset;
        if (!_announcedVersionsByPath.ContainsKey(path))
        {
            tcpLink.Write(FileSyncPacket.NewAnnounce(path, FileVersion.s_withdrawn));
            return;
        }

        // Step 2: the chunks
        try
        {
            using FileStream fileStream = new(Path.Combine(_directoriesRoot, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int chunkLength;
            do
            {
                byte[] chunk = new byte[Math.Clamp(Math.Min(fileStream.Length, offset + length) - _chunkOffset, 0, FileSyncPacket.s_chunkLength)];
                fileStream.Position = _chunkOffset;
                fileStream.ReadExactly(chunk);
                tcpLink.Write(FileSyncPacket.NewChunk(path, _chunkOffset, chunk));
                chunkLength = chunk.Length;
                _chunkOffset += chunkLength;
            }
            while (chunkLength == FileSyncPacket.s_chunkLength && _chunkOffset < offset + length);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Why: a file gone from the box is withdrawn, so the desk stops asking (its copy stays); one that comes back is announced again
            _announcedVersionsByPath.Remove(path);
            tcpLink.Write(FileSyncPacket.NewAnnounce(path, FileVersion.s_withdrawn));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Why: one file this end cannot read now must not stop the others; the empty chunk ends the answer and the desk asks again
            tcpLink.Write(FileSyncPacket.NewChunk(path, _chunkOffset, []));
        }
    }
}

/// A desk's end of the folder sync (built only when its MirrorConfig.IsFolderSyncOn; the box picks the folders from the GUI list): for
/// each announced file its copy does not match, asks for what the copy lacks: the rest from its length when it is a shorter start of
/// a file bigger than a chunk (big files only grow), else the whole file, one request per file and a few in all; a withdrawn file is
/// no longer asked for. A copy caught up takes the box's write time, so the copy itself is what this desk holds: nothing else has to
/// agree with it (see Spec.md "Folder sync"). The Remote's main thread. Cold path.
public sealed class FileSyncRemote
{
    private static readonly Duration s_checkInterval = Duration.FromMilliseconds(100);
    private const int s_requestLength = 8 * FileSyncPacket.s_chunkLength;   // Why: 512 KB a round trip streams a catch-up at about 17 MB/s over 30 ms
    private const int s_maximumRequests = 4;   // Why: at most 2 MB of answers ahead of a row on the link, and only while files catch up

    // A request in flight: the offset its next chunk must have, where it ends, and whether its chunks are still written.
    private readonly record struct FileRange(long NextOffset, long EndOffset, bool IsWritten);

    private readonly string _directoriesRoot;
    private TcpLink? _tcpLink;                 // the link the box's view of this desk lives on
    private readonly OrderedDictionary<string, FileVersion> _pendingVersionsByPath = new();   // the box's word on each file this desk may not hold yet, longest waiting first
    private readonly Dictionary<string, FileRange> _requestedRangesByPath = new();   // requests in flight, one per file at most
    private readonly HashSet<string> _unwritablePaths = new();                    // files another program holds: asked for once writable again
    private Timestamp _checkTimestamp;

    public FileSyncRemote(string directoriesRoot)
    {
        _directoriesRoot = directoriesRoot;
    }

    /// Each pass once the link serves this server: forgets what a new link makes void, and every 100 ms asks for what differs.
    public void Sync(TcpLink tcpLink)
    {
        // Step 1: a new link: the box announces every file again on it, and what was in flight died with the old one
        if (tcpLink != _tcpLink)
        {
            _tcpLink = tcpLink;
            _pendingVersionsByPath.Clear();
            _requestedRangesByPath.Clear();
        }

        // Step 2: every 100 ms, a request for each file that still differs (one a slot was not free for, refused, or left unread)
        Timestamp now = Timestamp.StopwatchNow;
        if (now - _checkTimestamp < s_checkInterval)
            return;
        _checkTimestamp = now;
        RequestPending();
    }

    // Asks for each pending file, longest waiting first, while a request slot is free.
    private void RequestPending()
    {
        foreach (string path in _pendingVersionsByPath.Keys.ToArray())
            TryRequest(path);
    }

    /// The box's latest version of a file, asked for at once when a request slot is free; a withdrawn file is no longer asked for (its
    /// copy stays); a name this machine cannot hold is left out.
    public void OnAnnounce(ReadOnlySpan<byte> packet)
    {
        (string path, FileVersion fileVersion) = FileSyncPacket.ReadAnnounce(packet);
        if (fileVersion == FileVersion.s_withdrawn)
        {
            _pendingVersionsByPath.Remove(path);
            _requestedRangesByPath.Remove(path);
            return;
        }
        if (!FileSyncPacket.TryToAbsolutePath(_directoriesRoot, path, out _))
        {
            Console.WriteLine($"FileSyncRemote: {path} cannot be held on this machine; not synced");
            return;
        }
        _pendingVersionsByPath[path] = fileVersion;
        TryRequest(path);
    }

    // Asks for a pending file unless a request for it is in flight, every slot is taken, or another program still holds its copy.
    private void TryRequest(string path)
    {
        bool isWaiting = _requestedRangesByPath.Count >= s_maximumRequests || _requestedRangesByPath.ContainsKey(path)
            || (_unwritablePaths.Contains(path) && !IsWritable(path));
        if (!isWaiting)
            RequestNext(path);
    }

    /// Writes one chunk of an answer this desk waits for (any other is dropped); the slot is held until the answer ends, then goes to
    /// the file waiting longest. An answer that ended empty (nothing to read now) or that the copy refused is asked for on the next check.
    public void OnChunk(ReadOnlySpan<byte> packet)
    {
        // Step 1: only the chunk the request waits for
        (string path, long offset, byte[] bytes) = FileSyncPacket.ReadChunk(packet);
        if (!_requestedRangesByPath.TryGetValue(path, out FileRange fileRange) || fileRange.NextOffset != offset)
            return;

        // Step 2: written (an empty chunk too: it makes an empty copy), and the rest of the answer awaited
        bool isWritten = fileRange.IsWritten && TryWriteChunk(path, offset, bytes);
        long nextOffset = offset + bytes.Length;
        bool isAnswerOver = bytes.Length < FileSyncPacket.s_chunkLength || nextOffset >= fileRange.EndOffset;
        if (!isAnswerOver)
        {
            _requestedRangesByPath[path] = fileRange with { NextOffset = nextOffset, IsWritten = isWritten };
            return;
        }

        // Step 3: the answer is over: a file still catching up goes to the back, so every file waiting behind it has the slot first
        _requestedRangesByPath.Remove(path);
        if (!isWritten || bytes.Length == 0)
            return;
        _pendingVersionsByPath.Remove(path, out FileVersion fileVersion);
        _pendingVersionsByPath.Add(path, fileVersion);
        RequestPending();
    }

    // Settles a file whose copy matches the box's version, or asks for what the copy lacks: the rest from its length when it is a
    // shorter start of a file bigger than a chunk (big files only grow), else the whole file.
    private void RequestNext(string path)
    {
        // Step 1: the copy against the box's version
        FileVersion announcedVersion = _pendingVersionsByPath[path];
        FileInfo fileInfo = new(NewAbsolutePath(path));
        long heldLength = fileInfo.Exists ? fileInfo.Length : -1;
        bool isAppend = announcedVersion.Length > FileSyncPacket.s_chunkLength && heldLength >= 0 && heldLength <= announcedVersion.Length;
        bool isHeld = heldLength == announcedVersion.Length && (isAppend || fileInfo.LastWriteTimeUtc == announcedVersion.LastWriteTimeUtc);
        if (isHeld)
        {
            _pendingVersionsByPath.Remove(path);
            return;
        }

        // Step 2: what it lacks, never past the version announced: the box would serve appends after it, which the copy would then outrun
        long offset = isAppend ? heldLength : 0;
        int length = (int)Math.Min(s_requestLength, announcedVersion.Length - offset);
        _requestedRangesByPath[path] = new FileRange(offset, offset + length, IsWritten: true);
        _tcpLink!.Write(FileSyncPacket.NewRequest(path, offset, length));
    }

    // Writes a chunk over the copy at its offset (one from 0 then cuts the copy to it), and gives a copy now whole the box's write
    // time; false when another program holds the file, logged once.
    private bool TryWriteChunk(string path, long offset, byte[] bytes)
    {
        string absolutePath = NewAbsolutePath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            using (FileStream fileStream = new(absolutePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                fileStream.Position = offset;
                fileStream.Write(bytes);
                // Why written over, then cut: a widget tailing the copy never sees it shrink when the bytes are the same
                if (offset == 0)
                    fileStream.SetLength(bytes.Length);
            }
            FileVersion announcedVersion = _pendingVersionsByPath[path];
            if (offset + bytes.Length == announcedVersion.Length)
                File.SetLastWriteTimeUtc(absolutePath, announcedVersion.LastWriteTimeUtc);
            if (_unwritablePaths.Remove(path))
                Console.WriteLine($"FileSyncRemote: {path} written again");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Why: a file another program holds without sharing (an editor) must not stop the others; it is asked for once writable
            if (_unwritablePaths.Add(path))
                Console.WriteLine($"FileSyncRemote: {path} not written ({exception.Message}); asked for again once it can be written");
            return false;
        }
    }

    // Whether this desk can write the copy now: a write open succeeds once the other program lets go.
    private bool IsWritable(string path)
    {
        try
        {
            using FileStream fileStream = new(NewAbsolutePath(path), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // An announced path under this desk's root (OnAnnounce admits only those that are).
    private string NewAbsolutePath(string path)
    {
        FileSyncPacket.TryToAbsolutePath(_directoriesRoot, path, out string absolutePath);
        return absolutePath;
    }
}

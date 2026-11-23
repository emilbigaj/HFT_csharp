# C++ alignment report — 2026-11-24: a dead client's rings are ended before it is closed; a connect never restarts a ring

This handoff is for the engineer or agent who maintains the C++ server and its socket library. It is self-contained: you
do not need the C# conversation to act on it. It amends the socket sections of `cpp_alignment_report_2026-10-12.md` and
is listed there as **P50** (must port), **P51** (port if the construct exists) and **C36** (C# only). Everything else in
that report stands. C# is the source of truth: `Socket/Socket.cs`, `Socket/SharedMemory.cs`, `Logging/LoggingServer.cs`,
and Spec.md "Socket close protocol" (the three parts dated 2026-11-24). The C# commit that adds this file is the
reference commit.

Scope: **two small methods and three lines** in the socket library (P50); **one branch collapsed and four members
deleted** (P51). No wire change, no layout change, `GetLayoutHash()` unchanged, no lockstep deploy.

## 1. What was wrong

### 1.1 The symptom: message-efficiency rows lost at every server restart (not a C++ fault)

Every restart of the production server lost the day's message-efficiency counts so far: the `<group>.messageefficiency`
files only ever gained a line at the 16:00 close. The C++ server was doing its part. `CmeServer::Stop` calls
`Server::Dispose`, which posts every claimed row on the audit link and releases it (seen in the C++ tree on the box,
2026-11-23), and the rows were not empty (sequence 2 on the nine claimed rows, read from `/dev/shm`). The C# Logging
process dropped them: its socket marks a link closed within a millisecond of the writer's process dying, its read then
refused the link, and it looks for new records only every 100 ms. The audit files show it: nine `MessageEfficiency` rows
at the close on 2026-11-19, none on the restart days (2026-11-12, -15, -20).

That is fixed in C# only (C36, §4). **Nothing to change in C++ for it.** After the C# Logging is deployed, a server
restart appends one line to each claimed group's file.

### 1.2 The hazard found on the way, which is in shared code: a reader waits for ever on a half-written record

A ring write is three steps (`Protocol::WriteToRing`):

1. mark the record as being written: `Sequence = writerSeq + 1` (odd);
2. stamp the frame and copy the payload;
3. publish it: `Sequence = writerSeq + 2` (even).

A process killed between 1 and 3 leaves the odd sequence for ever. `Protocol::TryReadFromRing` spins while the sequence
at the reader's cursor is odd, so **any reader that reaches that record never returns**:

- the server's CoreGroup thread reading a strategy's order-target channel (every strategy on that CoreGroup stops being
  served);
- Logging's tap on the same ring (one Logging worker lost, holding that tap's file);
- Logging's reader of its own links.

`SkipRing` already refuses to spin there ("a writer that died mid-write leaves an odd sequence nobody will ever
complete"); the live read had no such protection. The window is a few tens of nanoseconds per write, so it needs a kill
to land inside a write, but the result is a hung trading thread.

Reproduced in C# on Logging's own socket with a test writer that performs steps 1 and 2 on two channels and is then
killed: the worker was still waiting on the record when the ring was unmapped a second later, and Logging died of an
access violation in `Protocol.TryReadFromRing`.

### 1.3 A connect could leave the two ends of a ring in different places

`ServerSocket::OpenClient` called `Recover()` under `Persistance` and `Reset()` without it. `Reset` moved the server
end's cursors to the ring's start and cleared the ring. But a client parks its own cursors when it builds its socket,
before it connects. Over a ring that still held the last session's records, the client stood after them, the server read
from the start, and nothing the client wrote was read for that session, with no error. The socket constructor's own
comment already said a reset cannot synchronise two ends. Only Logging's socket runs without `Persistance`, so only
Logging showed it (a writer restarted within the second Logging keeps a closed link).

## 2. P50, MUST PORT: end a dead client's rings before closing it

When `ServerSocket::CloseClient` runs for a client whose process is dead, it first writes a close message at the end of
each ring that client wrote. The write lands on a half-written record and completes it.

C# (`Socket/Socket.cs`), new on `ReadOnlySocket`:

```csharp
    // Ends this ring for a writer whose process is gone: writes the close message it could not, over a record it left half-written,
    // so no reader waits on that record for ever. Only once the writer is dead: a ring has one writer.
    public unsafe void CloseForDeadWriter()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);

        // Step 1: find the ring's head as a restarting writer does; the walk stops at a record left half-written
        byte* writePtr = _startPtr;
        ulong writeSeq = 0;
        Protocol.SkipRing(ref writePtr, _startPtr, _endPtr, ref writeSeq);

        // Step 2: write the close message there
        Protocol.WriteToRing(Socket.CloseMessage, ref writePtr, _startPtr, _endPtr, ref writeSeq);
    }
```

New on `Socket`, beside `Close()`:

```csharp
    // Ends every ring this socket reads, for a peer whose process is gone (see ReadOnlySocket.CloseForDeadWriter).
    public void CloseForDeadPeer()
    {
        if (_isDisposed) throw new ObjectDisposedException(Name);
        foreach (ReadOnlySocket reader in _readOnlySockets) reader?.CloseForDeadWriter();
    }
```

In `ServerSocket::CloseClient`, before the status is stored:

```csharp
        _clientIds.AtomicClear(clientId);
        ref ClientHeader clientHeader = ref _clientHeaders[clientId];
        // Why: a dead process cannot end its own rings; ending them here completes a record it left half-written
        if (!ProcessId.IsAlive(clientHeader.ClientProcessId))
            clientHeader.ClientSocket?.CloseForDeadPeer();
        if (Persistance)
        {
            clientHeader.Status.Store(ClientStatus.Detached);
```

How it repairs the record: `SkipRing` from the ring's start stops at the first record that is odd or not newer, leaving
`writeSeq` at the last published sequence S. `WriteToRing` then writes `S + 1` (the same odd value the dead writer
left), the frame and the one-byte close message, and publishes `S + 2`. A reader spinning on that slot sees it turn
even and reads a close message.

Rules to keep:

- **Only when the process is dead.** A ring has one writer. A client that closed its own rings and lives on (a link
  closed by its owner) is never written to; `CloseClient` is reached for it through the `Closing` branch and the
  `IsAlive` check is true.
- **The close itself is unchanged.** The status is stored straight afterwards, as before. Nothing waits on a reader, so
  the cancel-on-close timing and the drain handshake are as they were, and the read gate still stops anything a dead
  strategy left unread from being processed.
- **No allocation**: two locals. It runs once per client death, on the listen thread.

What else it touches, all harmless:

- **A reader may read the new close message before the status is stored.** `TryRead` then requests `Open → Closing`
  with its snapshot; the listen thread's store of `Detached`/`Closed` follows on the same pass and wins either way (the
  request's CAS fails if the store came first). `CloseClient` is still called once.
- **A client that wrote its own close message and exited gets a second one after it.** No reader treats a close message
  as data, `Recover` on reconnect parks past both, and Logging's tap reads a close message as a point in the record.
- **Under `Persistance` the message stays in the ring.** The next process under that name parks after it when it builds
  its socket, and `OpenClient`'s `Recover` parks the server end after it too.

Precondition: no new process has attached to the ring between the old one's death and this write. The write happens on
the listen thread's next pass (about a millisecond after the death); a restart takes tens of milliseconds.

One edge, from reading the code, not tested: a writer killed while writing the first record after a wrap, before the
wrap marker is stamped, leaves the odd sequence at the ring's start. `SkipRing` stops there at once with sequence 0, and
the close message is written at the start with a sequence no reader parked at the old tail will take as new. Nothing
hangs (the reader's cursor is on the pre-zeroed header at the old tail and reads Empty) and the status store closes the
client as usual; the close message is simply never read.

## 3. P51, PORT-IF: a connect always recovers; the ring resets are deleted

Before:

```csharp
        if (Persistance)
            clientHeader.ClientSocket?.Recover();
        else
            clientHeader.ClientSocket?.Reset();
```

After:

```csharp
        clientHeader.ClientSocket?.Recover();
```

`Socket::Reset`, `ReadOnlySocket::Reset`, `WriteOnlySocket::Reset` and `SharedMemory::Clear` had no other caller and are
deleted in C#. Port if the C++ `OpenClient` has the `Reset` branch: collapse it and delete the four members. The C++
server runs with `Persistance`, so its behaviour does not change; this keeps the two files the same. Rule that follows:
a writer connects before it writes (anything written between building the socket and the connect being accepted is
skipped by the park; every writer already connects first).

## 4. C36, DO NOT PORT: Logging reads a closed link to its end

C# only, in `Logging/LoggingServer.cs` and one new method `ServerSocket.TryReadToEnd`:

- `TryReadToEnd` is `TryRead` without the status gate. A close message read while the client is `Open` still requests
  the close. It exists for Logging's reader of its own links and nothing else.
- `LoggingServer.OnDirectClosed` (the socket's `ClientClosed`, on the listen thread) reads the link once more the moment
  it closes. `LoggingSocketReader.Dispose` sets its flag under the lock every read holds, and the listen thread calls it
  before it unmaps the ring.

**Do not give the trading server an ungated read.** Its `TryRead` must keep refusing a client that is not `Open`:
nothing a dead strategy left unread may be processed, and the close-out's drain handshake depends on it.

## 5. Verification

C# results, for reference (a test writer connects to a live Logging, 100 ms poll, as `<server>.audit`, posts nine rows
on two channels as its last act, and goes; eight runs per case, rows saved out of 72):

| How the writer goes | Before | After |
|---|---|---|
| releases the link with no close message, as `Server::Dispose` does | 0 | 72 |
| writes a close message and exits | 0 | 72 |
| is killed between records | 0 | 72 |
| is killed half-way through a record | Logging hangs or crashes | 72 |
| any of the above with the next run started at once | every second run lost | 72 |

To check the C++ port of P50:

1. **Killed half-way through a record.** A test client connects, sends a few order targets, then performs steps 1 and 2
   of a ring write on its execution channel (odd sequence, frame, part of a payload) and is killed with SIGKILL. Expect:
   the listen thread closes it on its next pass; the CoreGroup thread keeps serving other clients (before the port it
   spins on that record for ever); the ring's last record is a one-byte close message with an even sequence; the
   client's working orders are cancelled as for any dead client.
2. **Clean exit.** A client closes its socket and exits. Expect a second close message after its own, and a reconnect
   under the same name whose first target is read.
3. **Closed but alive.** A client closes its socket and keeps running. Expect nothing written by the server: the ring's
   last record is the client's own close message.
4. **Message efficiency, end to end** (needs the C# Logging from the reference commit): stop the server. Expect one new
   line in each claimed group's `<server>/MessageEfficiency/<group>.messageefficiency` and the same rows in the day's
   audit file.

## 6. Deploy

Nothing here changes a layout or a wire value, so there is no lockstep:

- The C# Logging alone fixes the lost message-efficiency rows. Deploy it whenever Logging is next restarted.
- P50 is independent of it and of every C# process. Until it is live, a strategy killed half-way through a record can
  still leave the C++ server's CoreGroup thread and a Logging tap waiting on that record.
- P51 changes nothing a `Persistance` server does.

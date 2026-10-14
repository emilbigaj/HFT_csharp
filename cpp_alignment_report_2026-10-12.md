# C++ alignment report — 2026-10-12: every connect is answered, the server decides when a client may restart, a gone client's orders are closed out

This handoff is for the engineer or agent who maintains the C++ server and the C++ `Client`. It is self-contained: you
do not need the C# conversation, `patch_log.md` or `Spec.md` to act on it. C# is the source of truth. Every name below was
checked against the C# working tree on 2026-10-13.

- **Baseline:** C# commit `9d6f3ab` ("Align with C++ (csharp_alignment_report_2026-10-05)"), which the C++ branch
  `persist-client-sockets` was aligned with. This report covers everything C# changed after it, up to 2026-10-14 (P22, P23, P24 and C15 to C17 were added on 2026-10-13, C17 extended on 2026-10-14).
- **Supersedes** `cpp_alignment_report_2026-10-07.md`, which was written piecemeal from 2026-10-07 to 2026-10-12 and never
  handed over. Everything in it that still holds is folded in here; what later work made stale is listed in §9. Read this
  report, not that one. Earlier reports (`2026-10-04` and before) stay valid for their own items, except where §9 says
  otherwise.
- **Status:** complete in C#. It was run on Windows against a simulation server with every client as a separate OS process
  (§11).

Words used throughout:

- **Name**: a client's name (`SocketHeader.ClientName`, a path, at most 128 characters). Under `Persistance` a name keeps
  its client id for the whole week.
- **Slot**: one of a client's 64 order rows (`OrderId.LocalIndex` 0..63). Each slot has an `OrderTarget` row (written by the
  client), an `OrderState` row (written by the server) and an `OrderRisk` row.
- **Gone client**: a name whose process has exited or died. Its status is `Detached` (or `Disposed` if never attached
  since the server started).
- **Stand-in**: a C# `ManualClient` that the C# Local Mirror runs on the CME box for each Workspace on a remote desk. To the
  C++ server it is an ordinary client (§8).

---

## 0. Summary

| Id | Area | Kind | Lockstep | One line |
|---|---|---|---|---|
| P1 | Wire | PORT | **yes** | New `ConnectRefusal` (int32) values in the connect reply's `ClientId`: -1 .. -7 |
| P2 | `ServerSocket` | PORT | **yes** | Answer every connect request exactly once: `PollLetterBox` + `DecideConnect`; ignore a request's own `ClientId` |
| P3 | `ServerSocket` | PORT | no | `ReplyToClient` empties a reply left by a client that died before reading it |
| P4 | `ServerSocket` | PORT | no | The listen loop catches and logs; it never dies |
| P5 | `ServerSocket`, `Server` | PORT | **yes** (server first) | Hook `IsClientReady(clientId)`; false answers `NotReady`. The server's hook is `TryCloseOutOrders` |
| P6 | `Server` | PORT | no (needs P14 in the same C++ change) | `CancelAllOrders` becomes `TryCloseOutOrders`: one walk, new "working" rule, closes out an unread Create, repairs a torn idle row |
| P7 | `Server` | PORT | no | Drain handshake: proves no CoreGroup thread is still handling something it read from a closed client |
| P8 | `Server`, every client | PORT | **yes** (clients first) | The server writes each cancel it injects to the client's server-to-client channel; clients ignore `OrderType::OrderTarget` there |
| P9 | `ClientSocket` | PORT | **yes** | `Connect` returns the id or throws the refusal; retries `Closing`; matches the reply by name, timestamp and pid; a refusal disposes, never closes |
| P10 | `ServerContext` | PORT | **yes** | `AllocateClientId` returns `ReservedClientName` / `NoClientIdAvailable` instead of throwing; `LoadClients` throws on them |
| P11 | `Client` | PORT | **yes** (after P5) | The constructor drops its previous-orders check, the 100 ms sleep and the `OrderRisk` clear; keeps `Recover` |
| P12 | `Client` | PORT | no | A constructor that throws after `Connect` disposes; `Dispose` tolerates a half-built client and never refuses a full one |
| P13 | `Client` | PORT-IF | no | In simulation only an algo wipes its audit files; a manual client never does |
| P14 | `SharedArray`, `Protocol` | PORT | no (C++ P6 needs it, L8) | `Protocol::Stamp` frames every row at construction; the one-row alias over the server-header letterbox is deleted |
| P15 | `ReadOnlySocket` | PORT | no | A ring reader never latches closed: `Closed` for the close message only, then reads on |
| P16 | `OrderStateReason` | PORT | **yes** | `PendingNew = 1` renamed `InFlight = 1` (audit JSON writes the name) |
| P17 | Order rows | PORT | no | Builders `OrderState::NewInFlight`, `OrderTarget::NewOrderRejected`, `OrderState::NewRejected` replace hand-copied initialisers |
| P18 | `OrderRejected` | PORT-IF | no | `OrderDiscarded` is a constant mask `0x01007F0000000000`; check the C++ set has all 8 reasons |
| P19 | `AheadOfOrder` | PORT-IF | no | Field `Quantity` renamed `QuantityAhead` (layout unchanged, 20 bytes) |
| P20 | `LeggedHeader` | PORT-IF | no | `GetLegHeader` is set by the first `ServerContext` only and cleared by its `Dispose` |
| P21 | Client names | PORT-IF | no | GUI names are `<principal>_GUI<n>`, and on a remote desk `<principal>_GUI<n>@<machine>`; the C++ server must accept them |
| P22 | `ServerHeader` | PORT | **yes** | New field `int32 ServerProcessId` at offset 173 (size 173 → 177), stamped when the header is posted; a refused post names "already running" or "previous server exited"; never overwrite |
| P23 | `SideByPrice64` | PORT | no | `TrySetQuantity`: a delete of a price better than the best is a no-op; it used to wipe the worse level sharing its ring slot, unbroadcast |
| P24 | Message efficiency | PORT | **yes** (L10) | `InstrumentHeader` gains `ProductGroupId` (int8, offset 7) and `ProductGroupRoot` (tag 1151, offset 60); the `MessageEfficiency` row gains a type header (84 bytes); the server assigns rows, ends the day on `Closed` and alone appends `<group>.messageefficiency` (type 18 via Logging at a close, directly at shutdown) |
| C1-C17 | Mirror, Workspace, widgets, harness, Logging, simulator | C#-ONLY | — | §8. Shared values they introduced: `ConnectRefusal::RemoteDesk = -7`, region `<server>/Latency` |
| R1-R8 | — | REVERTED / SUPERSEDED | — | §9. Do not port: `DeallocateOrder`, `isCreateRejectedByClient`, `HasActiveOrders`/`IsOrderActive`, client-side restart check |

**Kinds.** PORT: change the C++ code. PORT-IF: change it only if C++ has the same construct (the item says what to look
for). C#-ONLY: nothing to port. REVERTED: an earlier instruction that must not be ported, or must be undone if it was.

**One layout changed: `ServerHeader`** grew 4 bytes, from 173 to 177, for `ServerProcessId` at offset 173 (P22). No row layout changed.
`SocketHeader` is still 344 bytes, `OrderState` 64, `OrderTarget` 52, `Fill` 64,
`AheadOfOrder` 20, with every offset the same. What changed is the **meaning** of some values, one enum **name**, one new
**message on an existing channel**, and server and client **behaviour**.

---

## 1. Wire

### P1. `ConnectRefusal` (int32, new; C# `Socket/Socket.cs`, beside `ClientStatus`)

The server's connect reply is the request's `SocketHeader`, echoed back with `ClientId` filled in. `ClientId >= 0` is the
client's id, as before. A negative value is now a refusal reason:

| Name | Value | Meaning | Returned by |
|---|---|---|---|
| `NoClientIdAvailable` | -1 | no free client id (the allocators' existing "no id" return) | `AllocateClientId` |
| `ServerError` | -2 | something threw while the server decided | `PollLetterBox` |
| `AlreadyConnected` | -3 | a live process holds the name (status `Open`) | `DecideConnect` |
| `Closing` | -4 | the name's previous session is still closing; the client asks again (P9) | `DecideConnect` |
| `ReservedClientName` | -5 | the name is the server's own leaf, reserved for the house book (strategy 0) | `ServerContext::AllocateClientId` |
| `NotReady` | -6 | the server's admission rule refused it for now: orders a previous process left are still working (P5) | `DecideConnect` via `IsClientReady` |
| `RemoteDesk` | -7 | the "server" is a C# Remote Mirror on a remote desk, not a trading server (§8, C2) | only the C# Remote Mirror. **A C++ server never returns it.** Add the value so a C++ `ClientSocket` can name it |

These are wire values: never renumber them. Name them in log and exception text (C# prints the enum name).

Also new, beside the enum:

```
class ConnectRefusedException : InvalidOperationException { ConnectRefusal ConnectRefusal; }   // thrown by Connect (P9)
delegate bool IsClientReady(int clientId);                                                      // the hook of P5
```

A C++ client may throw an equivalent exception type or return the reason; the caller must be able to tell the reasons
apart (the C# `ManualClient.Open` acts differently on `AlreadyConnected`, `NotReady` and `RemoteDesk`).

**Why:** before this, three cases got no reply at all (no free id; name already `Open`; name `Closing`/`Closed`), so the
client waited in `Connect` forever. A throw while allocating killed the listen thread.

**Verify:** §11 scenarios 1, 2, 4.

---

## 2. `ServerSocket` (C# `Socket/Socket.cs`)

### P2. `PollLetterBox` answers exactly once; `DecideConnect` decides

```
PollLetterBox():                                   // listen thread, once per pass
  1. if the server letterbox is empty: return; else take the request (header)
  2. try   DecideConnect(ref header)
     catch (any e): log; raise the Exception event with e; header.ClientId = ServerError
  3. if header.ClientId < 0: log the refusal by name; ReplyToClient(header); return
     else OpenClient(header)                       // unchanged, except it now sends the accepting reply through ReplyToClient (P3)

DecideConnect(ref header):
  1. header.ClientId = -1                          // a request never chooses its own id
     header.ClientId = AllocateClientId(header)
     if header.ClientId < 0: return                // the allocator's own reason (P10)
  2. status = clientHeaders[header.ClientId].Status
     if status == Open:                 header.ClientId = AlreadyConnected; return
     if status == Closing or Closed:    header.ClientId = Closing;          return
  3. if IsClientReady is set and !IsClientReady(header.ClientId):
                                        header.ClientId = NotReady;         return
  4. if status == Disposed: CreateClient(header); then fire ClientAllocated(header)
     // Detached: reuse the existing socket, keeping its ring and this side's read cursors, as before
```

- **Step 1** forces the request's `ClientId` to -1 before allocating. A request could otherwise ask for another client's id
  and take its slot. `Server::LoadClients` still passes a stored id, because it calls `AllocateClientId` directly, not
  through the letterbox.
- **Step 2:** `Closing` and `Closed` are refused, never opened: opening there would swallow the close in flight (the close
  is finished on this same thread by `PollPids`).
- **Step 4 order changed:** the socket is built **before** `ClientAllocated` fires (it was the reverse). A failed build no
  longer announces a socket that does not exist, and a retry does not announce twice (the C# logging server throws
  "already subscribed" on a second announce). It is the order `LoadClients` already used. The event's comment now reads:
  "at `ClientAllocated` time the socket exists but no process is attached to it yet".

**Why:** a client must always hear an answer, and the thread that notices dead clients must never die on one bad request.

**Verify:** §11 scenarios 1, 2, 4, 7.

### P3. `ReplyToClient` empties a dead client's leftover reply

Used by the refusal path in `PollLetterBox` and by `OpenClient` (which used to inline the same store loop).

```
ReplyToClient(header):
  open the client's letterbox (name = header.ClientName) for write
  while !TryStore(header):
     if TryPeek(out leftover) and !ProcessId::IsAlive(leftover.ClientProcessId): TryEmpty()
     sleep 1 ms
```

`ProcessId::IsAlive` treats a pid of 0 or below as dead (C# commit `8a8bffc`, already in the baseline).

**Why:** on Linux a client that died between the server storing its reply and reading it leaves the reply in its region.
The next reply to that name spun here forever and blocked the listen thread: no more connects, no dead-client detection,
no cancels on close. Only the listen thread stores into a client's letterbox, so the box holds either that dead reply or
nothing.

**Verify:** §11 scenario 5 (Linux only).

### P4. The listen loop never dies

Each pass of the listen thread is:

```
try { PollPids(); PollLetterBox(); }
catch (e) { log "<server>.Listen: e"; raise the Exception event with e }
sleep 1 ms
```

This also covers a throw from a `ClientOpened` / `ClientClosed` handler (the server's `OnClientClosed` runs
`TryCloseOutOrders`, P6). The outer catch never replies; only P2's inner catch does, so a request is never answered twice.
It extends the "read loops never die" rule of `cpp_alignment_report_2026-10-04.md` §2.7 to the listen loop.

**Verify:** §11 scenario 6.

### P5. Hook `IsClientReady`

```
ServerSocket field:  IsClientReady? IsClientReady;      // unset means always ready
```

- Asked only when no live process holds the name (status `Detached` or `Disposed`), never for an `Open` name.
- False answers `NotReady` (P2 step 3).
- The trading server sets it in its constructor, beside `AllocateClientId` / `DeallocateClient`:
  `_serverSocket.IsClientReady = IsClientReady;` with `Server::IsClientReady(clientId) => TryCloseOutOrders(clientId)` (P6).
  The C# logging server and the C# Remote Mirror do not set it.

Resulting table for a connect under a name:

| Name's status | Answer | The name's orders |
|---|---|---|
| `Open` (connected and trading) | `AlreadyConnected` | never touched |
| `Closing` / `Closed` | `Closing` | `OnClientClosed` is already closing them out |
| `Detached` / `Disposed`, `TryCloseOutOrders` returns true | accepted | nothing left waiting on the exchange |
| `Detached` / `Disposed`, `TryCloseOutOrders` returns false | `NotReady` | closed out again: the cancels are re-sent |

It runs on the listen thread, where `OnClientClosed` → `TryCloseOutOrders` already runs, and no live process writes the
client's rows at that point. So `TryCloseOutOrders`'s `RecoveryWrite` of the target row has a single writer, as it
assumes.

**Behaviour change, accepted (decided 2026-10-07):** every refused attempt re-sends the cancels for what is still working
and lifts each such target's `Seq` by 1,000,000 again. Not capped: the retry rate is set by whoever retries (a person, the
C# `ManualClient.Open` once a second for up to 5 s, the C# Local Mirror at most every 3 s).

**Why:** the rule "a client may not start while its previous process's order is still working" moves from the client
constructor to the server (P11). The server is the one place that sees both the client's target rows and its own state
rows, and it can re-send the cancel.

**Verify:** §11 scenario 3.

---

## 3. `Server` (C# `Provider/Server.cs`)

### P6. `TryCloseOutOrders` (was `CancelAllOrders`)

Renamed, because it now returns whether the client is closed out: true once nothing of the client's waits on the
exchange. `HasActiveOrders` and `IsOrderActive` from the 2026-10-07 drafts are folded into it (§9, R3): one walk, one read
of each row per slot. Called from two places: `OnClientClosed` (return value ignored) and `IsClientReady` (P5).

```
bool TryCloseOutOrders(clientId):                         // listen thread only
  isClientDrained = IsClientDrained(clientId)             // P7
  isClosedOut = true
  for localIndex in 0..63:
    // Step 1: is this slot's order working?
    orderId = OrderId{ ClientId = clientId, LocalIndex = localIndex }
    targetEntry = OrderTargets[orderId]
    if targetEntry is empty: continue                     // never written
    target = plain copy of the target row                 // no seqlock: its writer, the client, is gone and may have died mid-write
    state  = seqlock TryRead of the OrderStates row       // waits out a write in progress: the CoreGroup thread is alive and may be
                                                          // writing this order's ack or fill now; an unwritten row reads as zeros (OrderId 0)
    isSameOrder        = state.OrderHeader.OrderId == target.OrderHeader.OrderId
    isWorking          = isSameOrder ? state.OrderStateStatus == Active
                                     : target.OrderTargetStatus == Active
    isStuckInTornWrite = targetEntry.Seq is odd           // the client died mid-write
    if !isWorking and !isStuckInTornWrite: continue

    // Step 2: stamp the cancel in; keep it Active while the exchange may hold the order; send it if working
    isAwaitingExchange = isWorking and (isSameOrder or !isClientDrained)
    isClosedOut &= !isAwaitingExchange
    target.OrderTargetStatus = isAwaitingExchange ? Active : Done
    target.OrderTargetAction = Cancel
    target.OrderHeader.Seq  += 1_000_000
    target.OrderHeader.NicTimestamp = Clock.Now
    RecoveryWrite(targetEntry, target)                    // re-bases an odd seq (existing primitive)
    if isWorking: EnqueueOrderTarget(target)              // that instrument's CoreGroup injection queue, as before
  return isClosedOut
```

What changed, and why:

1. **"Working" is decided by the server's row once that row is about this order** (same `OrderId`), and by the client's
   target row only before that (a Create the server has not processed yet). The old test was "state Active OR target
   Active". But `CancelAllOrders` itself stamps the target `Active` and no server code ever sets it back (only a live
   client does, and that client is the one being refused). So a process that died with a working order was refused
   `NotReady` forever, even after the exchange had cancelled the order and the state row read `Done`; and each refusal
   queued another cancel for an already-closed order. Side effect: no cancel is sent any more for an order whose state row
   is already `Done`.
2. **A Create the server never read is closed out.** Its target row is `Active` while the state row still holds the slot's
   previous order (`isSameOrder` false). With the client gone, its ring is never read again (reads stop at `Detached`), so
   that Create can never reach the exchange. The cancel sent for it is refused (the state row is another order's), and
   before this change nothing marked the target back, so the name was refused `NotReady` for the rest of the week. Now its
   target is stamped `Done` once the client is drained (P7), and it no longer counts as working. The cancel is still sent:
   a CoreGroup thread may be half-way through that very Create at the close, and then the cancel finds its order.
3. **A target row left mid-write (odd seq) is repaired even when its slot is not working.** Before, only working slots
   were `RecoveryWrite`n, so a torn idle row stayed odd: readers skipped it forever, and the next process's seqlock on that
   slot ran inverted. It is stamped `Done` with no cancel sent: its torn `OrderId` may name no instrument, and the
   instrument lookup in `EnqueueOrderTarget` would throw.
4. **The state row is read through the seqlock** (`TryRead`), not plainly: a torn read of a row the CoreGroup thread is
   writing could skip a live order. Waiting is safe here because that row's writer is alive. (The target row is read
   plainly because its writer is the gone client: spinning on a row a dead process left mid-write would never end.)

**P6 needs P14 first (or in the same change).** The server never writes an `OrderState` row with a full `Write`: the
Create branch (`NewInFlight`) and `OnOrderState` both edit it under the lock (`AcquireLock`, field stores, `ReleaseLock`),
which never sets the row's `Magic`/`Length`. The C# slot `TryRead` returns `Empty` for a row without `Magic` and leaves the
output zeroed. In C# the frame is there only because the `SharedArray` constructor stamps every row (P14). Suppose a C++
`TryRead` works the same way and P14 is not ported. Then every state row reads as zeros, `isSameOrder` is always false and
"working" is decided by the target row alone. Once the client is drained, a still-working order's target (`Active`) is
stamped `Done` and the name is admitted while the exchange may still hold the order: the double-count P11 relies on the
server to prevent. Port P14 with P6, or read the state row in a way that does not depend on the frame.

The cancel stamping itself (`Cancel`, `Seq + 1_000_000`, `NicTimestamp = Clock.Now`, `RecoveryWrite`, enqueue) is
unchanged. `Active` + `Cancel` on the target row is what the vendor's replay-on-ack reads to cancel an order that is
still `InFlight` at the exchange.

**Do not wait for the drain on the listen thread.** With the simulation clock paused the CoreGroup threads do not run, and
every connect and dead-client check would stop. A name with an unread Create simply stays `NotReady` until they run.

**Verify:** §11 scenarios 3, 8, 9.

### P7. Drain handshake (makes "the Create was never read" a proof, not a timing margin)

At a client's close, a CoreGroup thread may be part-way through a Create it read just before the client was marked
`Detached`. Until it has finished, that Create's target must keep `Active` + `Cancel` (P6 step 2). The server proves it has
finished with a request/acknowledge counter:

```
// Fields of Server
int64  drainRequest;                                   // on its own 64-byte cache line
int64  drainAcknowledgedByCoreGroupId[HighestCoreGroupId + 1];   // each on its own 64-byte cache line
int64  drainRequestAtCloseByClientId[64];              // listen thread only; 0 = never closed in this run

OnClientClosed(clientId):                              // listen thread
  drainRequestAtCloseByClientId[clientId] = atomic increment of drainRequest, returning the new value (full fence)
  TryCloseOutOrders(clientId)
  ... (the rest of OnClientClosed unchanged)

end of ReadExecution(coreGroupId):                      // each execution CoreGroup thread, once per pass
  request = atomic load (acquire) of drainRequest
  if request != drainAcknowledgedByCoreGroupId[coreGroupId]:
     atomic store (release) drainAcknowledgedByCoreGroupId[coreGroupId] = request

IsClientDrained(clientId):                              // listen thread
  for each coreGroupId in ServerHeader.CoreGroupIds, except the admin channel (0):
     if atomic load of drainAcknowledgedByCoreGroupId[coreGroupId] < drainRequestAtCloseByClientId[clientId]: return false
  return true
```

C# uses `Interlocked.Increment` for the increment, `Volatile.Read` / `Volatile.Write` for the loads and stores, and a
`[StructLayout(Size = 64)] PaddedLong` for the padding. In C++: `std::atomic<int64_t>` with `fetch_add(1, seq_cst) + 1`,
`load(acquire)`, `store(release)`, each in an `alignas(64)` wrapper.

- **Why the full fence:** a CoreGroup thread that loads the new request also sees the client `Detached`, so no later pass
  reads from it. The acknowledgement at the end of a pass covers everything that pass read.
- **Keep the injection-queue drain at the start of each `ReadExecution` pass** (as it already is). The cancels
  `OnClientClosed` enqueues are then handled at the start of the next pass, after any Create the current pass read from
  the client before its close. So the close's cancel always follows that Create: it either cancels the order or is
  refused harmlessly. That is why the cancel itself needs no wait. The drain only decides when the target may be stamped
  `Done`.
- **Hot-path cost:** one load of a line that is normally shared-clean, and a predicted branch, per pass. A store only after
  a client closed (one cache miss and one write per CoreGroup thread per close). Allocation: none.

**Verify:** §11 scenario 8.

### P8. The server's own cancels are audited on the client's channel; clients ignore them

**Server.** In `Server::ReadExecution`, where the CoreGroup's injection queue is drained, write each injected cancel to
the client's server-to-client execution channel **before** handling it:

```
while the injection queue has an OrderTarget:
   orderTarget = dequeue
   WriteToExecution(orderTarget.OrderHeader, orderTarget)   // new
   OnOrderTarget(orderTarget)
```

Before, not after: a synchronous reject from `OnOrderTarget` would otherwise reach the channel ahead of the request that
caused it. Only server-originated cancels travel this queue (`TryCloseOutOrders`), so there is no per-order cost.

**Every client.** The execution-channel read switch must accept `OrderType::OrderTarget` (11) and do nothing with it.
The C# `Client` has:

```
case OrderType.OrderTarget:   // a cancel the server sent for this client's previous process: recorded by the audit, nothing to apply
    break;
```

Before this, the C# client threw `NotImplementedException("Unknown message type")` for any unknown type. No live client
reads one in practice (they are written for a gone client, and the next process's `Recover` skips them), but the switch
must not throw if one ever is read.

**Why:** the client never sent these cancels, so nothing tapped them, and the client's audit file showed a `Done` /
`Canceled` with no cancel before it. The C# logging server taps every client channel in both directions; this puts the
cancel into that record, in order.

**Verify:** §11 scenario 10.

---

## 4. Client side (C# `Socket/Socket.cs` `ClientSocket`, `Provider/Context.cs`, `Provider/Client.cs`)

### P9. `ClientSocket::Connect` returns the id or throws the refusal

```
int Connect():
  1. no socket: throw; IsClosed: throw "ClientSocket.Connect: <name> is closed."   // was: return -1, which no caller used
  2. id = RequestClientId()
     while id == Closing: sleep 100 ms; id = RequestClientId()
  3. if id < 0: Dispose()                                                         // NOT Close (see below)
                throw ConnectRefusedException("ClientSocket.Connect: server <S> refused <C>: <ConnectRefusal name>.", reason)
     log "<C>: Connected."; return id

int RequestClientId():
  1. header.Timestamp = now (a fresh value per request); store it in the server letterbox, sleeping 1 ms while it is full
  2. wait for a reply whose ClientName, Timestamp AND ClientProcessId all equal the request's;
     empty it; return its ClientId
     (logs "Waiting for server <S>..." every 3 s; waits forever while no server listens, as before)

void Dispose():
  cancel the exit action registered in the constructor (now kept in a field)
  dispose the socket
```

- **A refusal disposes, never closes.** A refused second process maps the same rings as the live process under that name.
  `Close` writes a close message into them, which would end the live session and make the server cancel its orders.
  `Dispose` writes nothing.
- **The exit action is cancelled on `Dispose`.** The constructor registers "close this socket at process exit". It used to
  run regardless, which was the same danger for a refused socket, and it threw `ObjectDisposedException` (logged) after
  every normal `Client.Dispose`. A C++ `ClientSocket` that registers an exit close must likewise unregister it on dispose.
- **A fresh `Timestamp` per request**, so a retry never takes the reply to an earlier request. The process id is matched
  too, because `Timestamp` has only 100 ns resolution on Windows. A C++ server satisfies this by echoing the request header
  unchanged except for `ClientId` (as `DecideConnect` does).
- Callers that ignore the return value (the server's own `.server` / `.audit` links, `AlertManager`, strategy logging) are
  unchanged when accepted; where they used to hang forever on a refusal they now throw.

**Verify:** §11 scenarios 1, 2, 11.

### P10. `ServerContext::AllocateClientId` returns refusals instead of throwing

- A client named after the server (`ClientName == ServerStrategyName`, the house book's reserved leaf): **return**
  `ReservedClientName` (was: throw).
- No free id (`ClientIds.LowestClear < 0`): **return** `NoClientIdAvailable` (was: throw). The `ServerSocket` default
  allocator and the logging server's already returned -1.
- The existing-name lookup is now `TryGetClientId(ClientName, out id)` (a behaviour-preserving helper, also used by
  `ClientContext::GetClientIdFromMap`; optional to mirror).
- Through the letterbox these reach the client as the reason (P2). A throw there used to kill the listen thread.
- **`Server::LoadClients`** (startup, from the weekly clients file) is the one direct caller. It now throws
  `InvalidOperationException("Server.LoadClients: <name> refused: <ConnectRefusal name>.")` on a negative result, so a broken
  clients file still stops startup loudly.

**Verify:** §11 scenario 4.

### P11. The client constructor no longer checks previous orders

The constructor now does, after `Connect` and building its context and `RiskLayer`:

```
_socket.Recover()        // park every read cursor at the head of the client's rings: skip what queued while away
```

and nothing else of the old startup check:

- **Delete `ThrowIfPreviousOrdersActive`.** The server refuses `NotReady` (P5) before the constructor runs, so it could never
  fire.
- **Delete the 100 ms sleep.** The server admits only once every previous order's state row is `Done`. It writes each row
  and its ring message microseconds apart on one thread, and `Recover` runs milliseconds after admission, so everything
  about those orders is already in the ring and is skipped.
- **Keep `Recover`.** Without it the new process would count queued fills twice into `WorkingRisk.Position`.
- **Delete the `OrderRisk` clear** (all 64 own rows set to `OrderRisk{}`). A validated Create already resets its slot's row
  (`RiskLayer::ValidateOrder`: `if action == Create: orderRisk = OrderRisk{}`, now commented "a slot's row may hold a
  previous order's, or a previous process's: a Create always starts it empty"), and nothing reads a slot's row before this
  process's own Create on it: amend/reduce validation and `TryClipToRiskLimit` act only on this process's working orders;
  the client's `OnOrderState` gate requires `_isOrderActive` (all false after a restart); `OnOrderRejected` requires a
  matching target; the previous orders' messages are skipped by `Recover`. **A C++ client must keep the reset-on-Create.**

Supersedes `cpp_alignment.md` "A client refuses to start while a previous process's order is still Active",
`cpp_alignment_report_2026-10-04.md` §4.6 step 1, and the "ctor-time startup check + 100 ms + Recover" item of
`csharp_alignment_report_2026-10-05.md` (§9, R4).

**Deploy order:** the server's P5/P6 must be live no later than any client drops its own check. A client without the
check, against a server without `IsClientReady`, would start while a previous order is still working and count its fills
twice.

**Verify:** §11 scenario 3.

### P12. A constructor that throws after `Connect` disposes; `Dispose` never refuses

- **Base `Client` constructor:** everything after `Connect` is inside `try { ... } catch { Dispose(); throw; }`. Same for
  the whole body of the C# `ManualClient` constructor.
- **`Client::Dispose` tolerates a half-built client:** it skips the instrument readers when that array was never built
  (`_instrumentData ?? []`) and calls `Context?.Dispose()`. It still closes the socket: here the process owns the session,
  so the close is right and it is what makes the server release the name.
- **`Client::Dispose` no longer throws when all 64 slots are active.** The guard `if (_isOrderActive.IsFull) throw` (there
  since the first commit, with no stated reason) is deleted. A client with every slot working is the one whose socket most
  needs to close: the close is what makes the server cancel its orders. In the C# Local Mirror a stand-in with 64 working
  orders threw there on every pass, stopping the Mirror for every remote desk and leaving those orders working.

**Why:** without the dispose, the server keeps the name `Open` for the life of the process and answers no second connect
under it, so a retry in the same process hung (with P2 it would instead be refused `AlreadyConnected` for the life of the
process).

**Verify:** §11 scenarios 12, 13.

### P13. PORT-IF (a C++ manual client exists): only an algo wipes its audit files in simulation

The simulation-only wipe of the client's audit folder before `Connect` now runs for an `AlgoClient` only
(`Clock.Mode == Simulation && this is AlgoClient`). A `ManualClient` (GUI) never touches audit files. Why: a GUI restarted
within one simulation run failed, because that run's logging server still holds today's audit file open. A C++ algo keeps
the wipe; a C++ manual client, if the port has one, must drop it.

---

## 5. Shared memory and rings (C# `Socket/Protocol.cs`, `Socket/SharedArray.cs`, `Socket/Socket.cs`, `Provider/Context.cs`)

### P14. `SharedArray` frames every row at construction; the server-header alias is deleted

**New `Protocol::Stamp(Header64* header, int length)`:**

```
Stamp(header, length):
  if atomic load of header->Magic == s_magic and header->Length == length: return
  header->Length = length
  atomic store header->Magic = s_magic          // s_magic = 0x4846542148465421
  // never touch header->Sequence
```

`Header64` is unchanged: `Sequence` u64 @0, `Magic` u64 @8, `Length` i32 @16, `ObjectType` i32 @20.

**The `SharedArray<T>` constructor stamps every row** with `Length = sizeof(T)` when the view is write-capable, as its
last step:

```
if access == Write:
   for index in 0 .. Capacity-1: Protocol::Stamp(rowHeader(index), sizeof(T))
```

- **Why:** rows the server edits under the lock (`AcquireLock`, field stores, `ReleaseLock`: order states, positions, the
  house-book allocation row) never get `Magic`/`Length` from `Write`, so `TryRead`/`Read` saw them as `Empty`. The C#
  mirror found it: it shipped no order state at all.
- **Safe for concurrent openers and live rows:** the values are constants, so concurrent stampers and writers cannot
  disagree; `Sequence` is never written, so an unwritten row (seq 0) still reads `Empty` and a live row's seqlock counter
  does not move. This holds across languages only if the C++ `Write` frames each row with the same `Magic` and with
  `Length = sizeof(T)`, as C# does. If a C++ row were framed with any other `Length`, every C# opener would rewrite it.
- **P6 depends on it:** the server's `OrderState` rows are only ever edited under the lock, so without the stamp their
  seqlock read returns `Empty` (see P6, "P6 needs P14").
- **On the CME box** the C++ server creates the regions. Every C# process that opens them (a Workspace, the Local Mirror, a
  stand-in) stamps them as it opens, because each C# context also opens every array write-capable. Port it anyway, so C++
  readers can use `TryRead` on lock-edited rows without depending on a C# process having opened the region first.

**The one-row alias over the server-header letterbox is deleted.** C# `Context` used to open a one-row
`SharedArray<ServerHeader>` over the region of the server header's `LetterBox` (as "array 0", for the old mirror). A
`LetterBox` reads `Length != 0` as "a value is posted", so stamping that region through the array view would fake a post:
clients would read a zero header, and the server's `TryStore` would fail. If the C++ port has such an alias, delete it
**before** C++ starts stamping.

**Verify:** §11 scenario 14.

### P15. A ring reader never latches closed

`ReadOnlySocket::TryRead` (both the span form and the typed form) returns `Closed` for a close message, for **that read
only**; the next read continues right after it. Delete from the reader:

- the `_isClosed` flag, `Close()` and `IsClosed`;
- the flag's clears in `Recover` and `Reset`;
- the flag checks at the top of both `TryRead`s and of `GetReadStatus`;
- the reader loop in `Socket::Close` (now only the writers close: a close is a message to the other side).

```
TryRead(out span):
  status = Protocol::TryReadFromRing(...)
  return IsCloseMessage(span) ? Closed : status

TryRead<T>(ref value):
  status = Protocol::TryReadFromRing(...)
  if IsCloseMessage(...): value = {}; return Closed
  return status
```

`WriteOnlySocket` is unchanged: `Close` writes the close message and from then on emits only close messages, until a
reconnect's `Recover` clears it.

Who decides what `Closed` means is the owner of the session, not the reader:

- **`ServerSocket`** (trading server, logging server): `Closed` requests `Open → Closing`, and every later read is gated on
  the client being `Open`. Nothing in the server changes.
- **The C# logging server's audit tap** (`SocketListener`, which reads every client channel with no `ServerSocket` in front,
  for the life of the socket): the close message is just a point in the record; it keeps reading, so a reconnect under the
  same name is audited too. If C++ has its own tap of client channels, this is what keeps it recording.

**Why:** the latch made a close permanent for the ring. The audit tap went deaf to the client-to-server direction at the
first close, so after any reconnect under the same name (a Workspace reopened, an algo restarted, a Mirror link drop) the
audit kept the server's answers but lost every order, amend and cancel the operator sent.

**Verify:** §11 scenario 15.

---

## 6. Order rows (C# `Execution/Order.cs`)

### P16. `OrderStateReason::PendingNew` is renamed `InFlight`

```
enum OrderStateReason : uint8 { Unknown = 0, InFlight = 1, Acked = 2, Fill = 3, Canceled = 4, Rejected = 5, Eliminated = 6 }
```

- **Rename only:** value 1, so shared memory and the wire are unchanged. Comment: "sent, not yet acked by the exchange (the
  server writes it; a walk shows it until the server answers)".
- **Deploy in lockstep:** the audit JSON writes enums **by name**. A C++ writer still emitting `"PendingNew"` makes the C#
  Audit Trail skip those lines silently. Audit lines written before the rename say `"PendingNew"`; text searches over them
  must use the old name.
- Rename comments too ("the server's PendingNew seed" is now "the server's InFlight seed").

**Why:** the state covers both waits (to the exchange and back); "PendingNew" named only the narrower one.

### P17. Builders on the row types (behaviour-preserving)

Three builders replace hand-copied initialisers. Port them so the call sites match; output is byte-identical.

```
// OrderState: the state of a Create the exchange has not acked yet
static void OrderState::NewInFlight(const OrderTarget& orderTarget, OrderState& orderState /*out*/):
  orderState = OrderState{}                     // header type OrderState, everything else zero
  orderState.OrderHeader       = orderTarget.OrderHeader
  orderState.OrderHeader.Seq   = 0              // Seq 0 marks a new order the exchange has not acked
  orderState.OrderProfile      = orderTarget.OrderProfile
  orderState.TimeInForce       = orderTarget.TimeInForce
  orderState.OrderStateStatus  = Active
  orderState.OrderStateReason  = InFlight

// OrderTarget: the reject that refuses this target, for these reasons, from this source
void OrderTarget::NewOrderRejected(Bitset64 reasons, OrderRejectedSource source, OrderRejected& orderRejected /*out*/) const:
  orderRejected = OrderRejected{}
  orderRejected.OrderHeader          = OrderHeader
  orderRejected.OrderTargetAction    = OrderTargetAction
  orderRejected.OrderRejectedSource  = source
  orderRejected.OrderProfile         = OrderProfile
  orderRejected.OrderRejectedReasons = reasons

// OrderState: this state ended by its Create's refusal (instance method: called on the live row, keeps its other fields)
void OrderState::NewRejected(const OrderRejected& orderRejected, OrderState& rejectedState /*out*/) const:
  rejectedState = *this
  rejectedState.OrderHeader      = orderRejected.OrderHeader
  rejectedState.OrderProfile     = orderRejected.OrderProfile
  rejectedState.OrderStateStatus = Done
  rejectedState.OrderStateReason = Rejected
```

Call sites:

| Builder | Site | Note |
|---|---|---|
| `NewInFlight` | `Server::OnOrderTarget`, Create branch, under the state row's lock | then sets `QuantityAhead` (book quantity at the price) and `OrderHeader.NicTimestamp = Clock.Now`, as before |
| `NewOrderRejected` | `Client::Reject` (source as passed), `Server::OnOrderTarget` (`Server`), the simulator's exchange (`Exchange`) | |
| `NewRejected` | `Server::OnOrderRejected`, refused-Create branch, on the live row | so the `Done` keeps `TimeInForce`, `QuantityAhead` and `ExchangeOrderId` exactly as before |

C# fills an out parameter because returning a 64-byte struct and then passing it on by reference cost an extra copy
(measured +4.7 ns for `NewRejected`, about +7 ns for `new() { ... }` in the other two). A C++ port may return by value if
its compiler elides the copy; measure if in doubt.

### P18. PORT-IF: `OrderRejected::OrderDiscarded` is a constant mask

C# replaced the static constructor that built `OrderDiscarded` with a compile-time constant (a .NET code-generation fix:
the static constructor cost every `OrderRejected` constructor a class-init check and kept `IsDiscarded` from inlining).
The set is unchanged:

| Reason | Value |
|---|---|
| `StateIsDone` | 40 |
| `CreateIsActive` | 41 |
| `CancelIsActive` | 42 |
| `TargetIsActive` | 43 |
| `TargetIsStale` | 44 |
| `TooManyActiveTargets` | 45 |
| `AlgoIsPaused` | 46 |
| `TooManyOrdersPerSecond` | 56 |

Mask: `0x01007F0000000000`. `IsDiscarded = reasons != 0 && (reasons & ~mask) == 0`.

**Check the C++ set has all eight.** `cpp_alignment_report_2026-10-04.md` found the C++ `OrderDiscarded` lacked
`TooManyActiveTargets`. If C++ builds the set at runtime, a constant is the natural C++ form too.

### P19. PORT-IF: `AheadOfOrder.Quantity` renamed `QuantityAhead`

```
struct AheadOfOrder (20 bytes, Pack = 1):
  Header<OrderType> Header   @0  (OrderType::AheadOfOrder = 15; 4 bytes)
  int32  QuantityAhead       @4  (was Quantity)
  uint64 ClientOrderId       @8
  int32  QuantityBehind      @16
```

Layout and values unchanged. In C# it travels only the simulator's exchange-to-server queue and is never written to an
audit file, so no lockstep is needed. Rename it in C++ (field and constructor parameter) for one name per concept; if C++
serialises it by field name anywhere, deploy the rename with that reader.

---

## 7. Context

### P20. PORT-IF (C++ has one process-wide spread-leg lookup): `LeggedHeader::GetLegHeader`

- It is set only by the **first** `ServerContext` created in the process (`GetLegHeader ??= id => GetInstrumentHeader(id).Read()`),
  never by a `ClientContext`.
- `ServerContext::Dispose` clears it when it is its own (`if GetLegHeader.Target == this: GetLegHeader = null`), so a test
  that starts another server in the same process takes it over.

**Why:** every context used to set it to itself, so it pointed at the newest one. In the C# Local Mirror that is the last
stand-in started; once that stand-in closed, the next spread lookup went through its disposed context: an
`ObjectDisposedException` (a remote spread allocation silently dropped, a spread reject alert naming `UnknownSymbol_<id>`),
and a lookup racing that `Dispose` could read unmapped memory and crash the process. All contexts of one server map the
same header array, so the answer is the same from any of them. A C++ process with one server context for its whole life
has nothing to change; one that creates and destroys client contexts while running does.

### P21. PORT-IF (the C++ server validates or parses client names): GUI names

Client names the C++ server now sees (all from C# Workspaces and stand-ins):

- `<principal>_GUI<n>` with `n` from 0 to 7, where the principal is a strategy's name or the server's (server workspace).
  Was `<principal>_GUI`. The first GUI's folder therefore moves from `_GUI` to `_GUI0`. A mid-week deploy's `LoadClients`
  still holds the old `_GUI` name's client id until the weekly clients file is emptied on Monday.
- On a remote desk: `<principal>_GUI<n>@<machine>` (Windows machine names, up to 15 characters). These connect to the C++
  server as stand-ins on the CME box (§8, C1).
- A name is still at most 128 characters including its folder path.

What the C++ server must do: accept these names. Region names are sanitised the same way on both sides: every character
outside `[A-Za-z0-9_.-]` becomes `_` (C# `Tools.Sanitize`, written to match C++ `isalnum` in the "C" locale), so `@` maps
to `_`. Consequence to know about (no action): `X_GUI0@DESK` and a client literally named `X_GUI0_DESK` would map to the
same shared-memory regions. The reserved-name check (P10) is unaffected: `<server>_GUI<n>` is not the server's leaf.

### P22. `ServerHeader.ServerProcessId` (C# `Provider/Allocate.cs`, `Provider/Context.cs`; C++ `Provider/Allocate.hpp`)

```
struct ServerHeader (Pack = 1, 177 bytes; was 173):
  ... every field up to Persistance unchanged (Persistance: uint8 @172)
  int32 ServerProcessId   @173   // new: the process id of the server that published this header
```

- **Who sets it:** the code that publishes the header into its `LetterBox` (C# `ServerContext::Connect`, which already
  reserves the house book's client id there), with the current process id (`getpid()`), so no host can forget it. A host
  that builds the header leaves it 0.
- **Update the size check** (C# throws unless `sizeof(ServerHeader) == 177`; a C++ `static_assert` should match).
- **Who reads it:** the C# Local Mirror reads it once at startup and exits as soon as that process has exited
  (`ProcessId::IsAlive`, every pass), and a C# Workspace on the CME box shows "Disconnected – server stopped" once it has
  exited. **A C++ header that leaves it 0 makes the Local Mirror exit the moment it starts, and every Workspace on the box
  show "Disconnected".**
  The C++ server's `main` is to start the Local Mirror **after it has loaded its clients and instruments** (`LoadClients`,
  `LoadInstruments`), not merely after posting the header, as `Mirror <absolute path of its MirrorConfig.json>` (with no
  argument it reads `MirrorConfig.json` in its working directory). Why: on a server restart the remote desks rebuild their
  Workspaces on the first snapshot the new Local Mirror publishes, and a snapshot taken before the instruments are loaded
  has none. One started before any header waits until a header is posted, and one started on a dead run's header exits at once. On a remote desk's copy the field holds the CME
  box's process id, so it must never be tested against local processes.
- **Accepted residual: a bare process id.** If a dead server's id has been given to another process by the time of the
  restart, the refusal below reads it as alive and names that process as "already running". C# accepts this (Linux ids
  wrap slowly: the CME box passed 3.4 million since its July boot). The fix, if ever needed, is the start time stamped
  beside the id, a further layout change.
- **The refused post names its case** (C# `ServerContext::Connect`; port it to wherever C++ posts the header). The post
  fails while the previous run's header is still in the `LetterBox` (nothing empties it, and it stays while any process maps
  the region). Read the posted `ServerProcessId` with a plain read (no seqlock wait), dispose the box, and throw:
  `"ServerContext.Connect: <server> is already running as process <pid>; stop it before starting another"` when that pid
  is alive, otherwise `"ServerContext.Connect: <server>'s previous server (process <pid>) has exited, but other processes
  still map its shared memory; stop its clients (Local Mirror, algos, Workspaces) and start again"`. **Never overwrite a
  dead run's header** (declined 2026-10-13): the server would start on that run's order rows, rings and allocations, and
  every client still running would be read by no one.
- **Why:** nothing in shared memory identified the server run, so nothing could tell a dead server from a live one: a
  Local Mirror outliving the server kept the regions mapped, which blocked the restart with an unexplained error.

**Verify:** §11 scenario 18.

### P23. `SideByPrice64::TrySetQuantity`: a delete of a price better than the best is a no-op (C# `Data/MarketByPrice.cs`)

The 64-slot ring is indexed from the best, so a price 1 to 63 ticks **better** than the best shares its slot with a price
1 to 63 ticks **worse**. A delete (quantity 0) for such a better price, which feeds do send, ran the normal delete on that
slot: it cleared the worse, real level and reported delta 0. A zero delta is not broadcast, so the server's book lost the
level while every client kept it. Example: best bid 13230, 13190 resting; a delete for 13254 wiped 13190.

```
TrySetQuantity(ticks, quantity, out delta):
  ... bestOffset, isNewZero, isBetter computed as before
  if isBetter and isNewZero: delta = 0; return false        // new, before the far-worse check
  ... far-worse check unchanged
  if isBetter and bestOffset <= -Capacity:                    // far-better: its own "if isNewZero: ignore" is deleted (covered above)
  ... rest unchanged
```

- **Live impact:** the C++ server applies exchange market data through this type, so it can lose a level on a stray
  better-price delete and its clients keep it: books that differ until that price is set again.
- **Cost (C#, JIT, dev PC):** about +1 ns per update, no allocation. Measure the C++ build.
- No layout or wire change; no lockstep.

**Verify:** §11 scenario 19.

### P24. The server owns message efficiency; groups come from CME's security group (C# `Data/Instrument.cs`, `Data/InstrumentDetails.cs`, `Provider/Context.cs`, `Provider/Server.cs`, `Execution/Order.cs`, `Execution/RateLimit.cs`)

Each product group has one shared row in `<server>/MessageEfficiency`; strategies count into it. Before, every client
scanned for its group's row, claimed an empty one (two clients could claim the same), loaded the file, flipped the day on
the first instrument it allocated, and appended the row at each close and at exit, so the file got one copy per process.
Now the server does all of it, and the group is CME's security group (MDP3 tag 1151), not the root.

```
InstrumentHeader (64 bytes, unchanged size):
  offset 7:  int8  ProductGroupId   // was reserved. -1 until the instrument is allocated, then its MessageEfficiency row
  offset 60: char[4] ProductGroupRoot  // was reserved. tag 1151 from the security definition: NQ for MNQ, RY for RTY/M2K,
                                    // NK for NKD/MNK, BF for BTC/MBT, EZ for ETH/MET, MS for MES. Zero-padded.

enum OrderType : uint8 { ..., WorkingRisk = 17, MessageEfficiency = 18 }
struct MessageEfficiency (Pack = 1) {                 // the shared row: 84 bytes, was 80
  Header<OrderType> Header = MessageEfficiency;       // new, offset 0; every other field moves 4 bytes on
  ...                                                 // ProductGroup renamed ProductGroupRoot; layout otherwise unchanged
}

building a header (feed):          header.ProductGroupRoot = security definition tag 1151 (must not be empty)
ServerContext::OnInstrumentHeader: header.ProductGroupId = -1            // beside InstrumentId = -1
ServerContext::AllocateInstrument: header.ProductGroupId = AllocateProductGroupId(header)   // beside the risk-limit/position loads
AllocateProductGroupId(header):
  if header.ProductGroupRoot empty: throw
  for each row from 0 while not empty: if row.ProductGroupRoot == header.ProductGroupRoot: return row id
  claim the next empty row (throw if none): load the last line of <server>/MessageEfficiency/<group>.messageefficiency
    (else Min limits live, Max in simulation), ProductGroupRoot/ProductGroupId set, Reset(local now), Write; return its id

Server::OnTradingStatusUpdate(update):            // after writing the status byte and broadcasting, as before
  if update.TradingStatus == Closed:
    row = MessageEfficiency[header.ProductGroupId]; copy = row
    if row.Reset(local exchange time of the update) and realtime: WriteToAudit(header.CoreGroupId, copy)

Server::Dispose():
  ... server socket and instrument data disposed, as before
  if realtime: for each non-empty row: WriteToAudit(Admin, row)    // new, just before the audit link is disposed
  ... audit link disposed, as before

Client: GetInstrument no longer allocates a product group; Instrument::ProductGroupId reads header.ProductGroupId.
  Delete the client's AllocateProductGroupId (scan, claim, load), its Closed handler and every append.
```

- **Why `Reset`'s result gates the post:** every instrument of the group closes; the first resets the row, the rest find
  the day already ended, so the group's file gets one line per close.
- **Why a plain read** (no seqlock) for both copies: a client that died mid-write must not hang the close or shutdown.
- **Why `Dispose` posts after the server socket:** the admin thread, the audit admin channel's other writer, has stopped.
  The C# Logging drains a link on close, so rows written before the close message reach the files.
- **A JSON load must run the row's field initialisers** (the header); C# gave `MessageEfficiency` a parameterless
  constructor for this. Lines written before the header existed have no `Header` key.
- **The array is written by both sides:** the server claims, loads and resets rows; strategies count. Map it writable in
  the server.
- **Hot-path cost:** at a `Closed` only, one row copy, one time-zone conversion and at most one audit write. A client's
  per-order count now reads `ProductGroupId` from the header instead of a field on its own object: one load.
- **File names follow the group:** a group whose code is not its old root (RY, NK, BF, EZ, MS) starts a new file.
- **`MessageEfficiency.ProductGroup` is renamed `ProductGroupRoot`** (layout unchanged), so the files' JSON key changes. A
  line written before the rename still loads: the server sets the field itself after reading the line.
- The C# Logging appends the posted row and sorts it at the high-water mark; nothing to port unless C++ has its own Logging.

**Verify:** two clients allocate MNQ and NQ: both headers carry `ProductGroup` NQ and the same `ProductGroupId`; one row
exists. In Realtime, close the NQ session: one line in `NQ.messageefficiency` with the closing day's counts, and the row
starts the next day; stop the server: one more line per group.

---

## 8. Do not port (C#-only)

Nothing below needs C++ code. Where a C#-only feature put a value on a shared wire or into shared memory, the value is
listed.

- **C1. The `Mirror` project** (`Mirror/LocalMirror.cs`, `RemoteMirror.cs`, `TcpLink.cs`, `MirrorPacket.cs`,
  `MirrorConfig.cs`, `Program.cs`): remote desks run the normal Workspace against a copy of the CME server. Its TCP protocol
  (`MirrorPacketType` 1..8: `Row`, `Message`, `Heartbeat`, `GuiList`, `AllocatedClientId`, `ServerHeader`, `Ping`, `Pong`),
  `MirrorConfig.json` (role, server name, clock mode, ports, `IPWhitelist`, `DirectoriesRoot`) and the layout hash are
  between two C# ends only. What touches the C++ server on the CME box:
  - The **Local Mirror** reads the C++ server's regions and publishes changed rows. It copies rows with
    `SharedArray.TryCopy`, which never waits and never writes. Like every C# process, it does stamp row frames when it
    opens the regions (P14), and it never writes a row's content. So C++ row layouts and region names must stay byte-identical to C#'s, as they
    already must. Measured cost to the server: a line the server wrote since the last pass costs its next write to that line
    about +330 ns (a cross-chiplet ownership recall on Zen 4), once per hot line per pass; the Local Mirror runs on the
    housekeeping cores.
  - Each remote Workspace gets a **stand-in**, a C# `ManualClient` under the same name, connected to the C++ server like any
    client. It relies on P1, P2, P5 and P9 (§10, L7).
  - The Local Mirror publishes when a stand-in reads an order state. It relies on the server writing a fill's state and
    position rows **before** it sends the client the state message (as C# `Server.OnFill` does).
  - A remote desk's clock is the copied `ServerHeader.Timestamp`, which C# relies on the server rewriting every pass.
- **C2. `Provider/RemoteClient.cs` and `IManualClient`.** On a remote desk the Workspace's manual client is a pass-through:
  each call (`OrderTarget`, `AllocateInstrument`, `ControlAlgoStatus`, `ControlRiskLimit`, all existing message types) goes
  up its admin channel to the Remote Mirror's `ServerSocket`, and the Local end makes the same call on the stand-in. It
  never reaches a C++ process. **Shared value: `ConnectRefusal::RemoteDesk = -7`** (P1), returned only by the Remote
  Mirror's `ServerSocket` to a plain `ManualClient`, which then opens a `RemoteClient` instead.
- **C3. New shared array `<server>/Latency`.** `SharedArray<Duration>` (one int64 of nanoseconds per row; below 0 =
  disconnected), capacity `ServerHeader.CoreGroupIds.Length` (64), region name `serverName / "Latency"`, opened by every C#
  `Context` right after `WorkingRisks` (`Context.GetLatency(coreGroupId)`). Only the C# Remote Mirror writes it, on the
  remote copy (row 0, the Mirror link). **The C++ server neither creates nor writes it**: on the CME box the first C#
  process to open it creates the region, and its rows stay unwritten, so they are never published over a remote copy's.
  Port the accessor only if a C++ context should read it.
  - **C# array ids** (creation order; used only between the two C# Mirror ends): 0 `ClientHeaders`, 1 `InstrumentHeaders`,
    2 `InstrumentHeaderIdByInstrumentId`, 3 `InstrumentIdsByClientId`, 4 `ClientIdsByInstrumentId`, 5 `MarketsByPrice`,
    6 `RiskLimits`, 7 `MessageEfficiency`, 8 `RateLimits`, 9 `CoreGroups`, 10 `OrderStates`, 11 `OrderRisks`,
    12 `OrderTargets`, 13 `LocalPositionHeaders`, 14 `WorkingRisks`, 15 `Latency`, 16 `ServerPositionHeaders` (a
    `ServerContext` only). Deleting the alias (P14) moved every id down by one, and `Latency` was appended. C++ has no
    array ids, so nothing changes there.
- **C4. `Context.GetLayoutHash()`**: FNV-1a over each C# array's row size and capacity, in id order. The Local end sends it
  in every server-header packet and the Remote refuses to start when its own differs. It changes whenever a row size or
  capacity changes, so a C++/C# layout change will also require redeploying both Mirror ends (they already deploy together).
- **C5. `Timestamp.StopwatchNow`** (`Tools/Timestamp.cs`): the machine's monotonic `Stopwatch` as a `Timestamp`, for Mirror
  timing (ping, GuiList timer, timeouts). Never on a message.
- **C6. `SharedArray`**: `TryCopy(index, bytes)` (copy unless unwritten or mid-write; never waits), byte-level
  `RecoveryWrite(index, bytes)` beside `Write`, both through `AsValue` (length check). Used by the Mirror only.
- **C7. `Context`**: `GetSharedArray(arrayId)` replaces `Mirror()` and `EnumerateSharedArray()`; `SharedArrayEnumerable`
  deleted. `TryGetClientId(name, out id)` (P10, optional). `EnumerateOrders(instrumentId, strategyId, inFlightClientId)` is
  now one method on `Context`, walking every slot by its **target** row: a strategy workspace lists every order booked to its
  strategy whichever client sent it, an order the server has not answered is shown as `NewInFlight(target)` only for the
  workspace's own GUI client, and a slot mid-write is skipped (it used to spin forever on a row a dead client left
  mid-write). `CompositeOrderEnumerable` and the per-context overrides are deleted. GUI only.
- **C8. `ManualClient`**: `Open(principal, server)` takes the lowest free `_GUI<n>` (skips `AlreadyConnected`; asks a
  `NotReady` name again once a second for up to 5 s; at most 8; on `RemoteDesk` reopens as a `RemoteClient`), `GetGuiName`,
  `TryParseRemoteGuiName`; the constructor takes the GUI name and the principal explicitly instead of adding or stripping
  `_GUI`.
- **C9. Row struct code generation** (`OrderRejected`, `OrderState`, `OrderTarget`, `Fill`, `TickHeader`, `Header<T>`):
  `fixed` padding became plain `byte` fields (`Fill` pads with `Size = 64`), static constructors became constants or moved
  into the instance constructor, parameterless constructors are `AggressiveInlining`. Layout byte-identical (sizes,
  offsets, constructor bytes and JSON checked). .NET-only fixes.
- **C10. `Context.DirectoriesRoot`** (static, default `S:\`, which is `/mnt/S/` on Linux) replaces the hard-coded root in
  `ClientContext`/`ServerContext.DirectoriesPath`; only a Windows remote desk overrides it (Workspace `-d`,
  `MirrorConfig.json`). Paths are unchanged without an override. The C++ server keeps `/mnt/S/`.
- **C11. Workspace and widgets**: the latency display beside the clock and "Connection broken – reconnecting…", a
  `StateReason` column in the orders grid, the instrument-headers grid following the server's allocated instruments, no
  `AlertManager` on a remote desk, the simulation clock holding while a remote copy's header reads 1970.
- **C12. Projects**: `Provider` is a library; `Provider/Program.cs`, `Provider/TCPServer.cs` and
  `Provider/Properties/launchSettings.json` are deleted; `Mirror` is in `HFT.slnx` and `Build.csproj`.
- **C13. Harness algo** (`Testing/Test.cs`): `Target` → `TryTarget`, test-only.
- **C14. Wording and local renames**: `Simulator/spec.txt` and comments in `Simulator/ServerSimulator.cs`,
  `Provider/Position.cs`, `Socket/Protocol.cs` and `Socket/SharedArray.cs` (`PendingNew` → `InFlight`, `CancelAllOrders` →
  `TryCloseOutOrders`). The local `sameOrder` is renamed `isSameOrder` in `Position` and `Client::OnOrderRejected`, with the
  same logic. Mirroring these renames is optional.
- **C15. 2026-10-13, Mirror, Workspace and Logging** (C# processes only; what the C++ side must do for them is in P22):
  the Local Mirror checks `ServerProcessId` right after attaching and every pass and exits with the server; it takes its
  config's absolute path as its only argument (`MirrorConfig.json` in the working directory without one); the Remote writes
  -1 into `Latency[0]` as soon as its link drops; a Local Mirror restart over the same server is a link drop, and a server
  restart (a new `ServerProcessId` in the copied header) makes the Remote wipe its copy and every remote Workspace rebuild
  itself in place (the heartbeat no longer carries a Local start time: both C# ends deploy together); a Workspace on the CME box shows
  "Disconnected – server stopped, close this Workspace" once the server's process has exited; the C# Logging server polls
  each ring once per 100 ms live (§12).
- **C16. 2026-10-13, simulator: a crossed book in session is `Auction`.** `InstrumentSimulator` publishes
  `TradingStatus::Auction` while its exchange's session is open and its book is crossed (CME's pre-open), Open/Closed
  otherwise. Port it only if the C++ side has its own simulator; live, the status comes from the exchange.
- **C17. 2026-10-13, Workspace, widgets and Mirror** (C# processes only, nothing to port): the search widget is deleted;
  Instrument Headers opens Create Order and a ladder from its right-click menu for an instrument allocated to the
  Workspace's principal; widgets still restore by `InstrumentId`; the Local Mirror's alerts connect to Logging as
  `<server>/LocalMirror.alert`, since the C++ server holds `<server>.alert` (on the box, the C++ server must keep that
  name); `SharedArray.Clear(index)` (sequence 0, payload zeroed) is used by the Remote to wipe its copy on a server
  restart; a remote Workspace's `RemoteClient` sends no close message once the server it connected to has restarted
  (2026-10-14).

---

## 9. Reverted / superseded (do not port these instructions)

- **R1. `cpp_alignment_report_2026-10-07.md` §4f, `DeallocateOrder` and `isCreateRejectedByClient`: REVERTED.** It was added
  for remote GUIs that ran their own `Client`; a remote Workspace now runs a `RemoteClient` (C2), so nothing refuses a
  Create on a client's behalf after it was sent. If it was ported, undo it:
  `Client::OnOrderRejected` is `if isTargetDone: target.OrderTargetStatus = Done` plus the unchanged `RiskLayer` line (C#
  now names the shared test `isSameOrder`; behaviour as at the baseline), and the deallocation is inline in the `Done`
  branch of `OnOrderState`, its only caller.
- **R2. The "client-side Create refusal matched by id alone, not id and seq" rule** in `Client::OnOrderRejected`
  (patch log 2026-10-08): reverted with R1. Every refusal again needs the same id **and** seq.
- **R3. `HasActiveOrders` / `IsOrderActive` as separate functions, and "`IsClientReady = !HasActiveOrders`, then
  `CancelAllOrders`"** (2026-10-07 drafts §3.1, §3.2): superseded by P6. `IsClientReady` is just `TryCloseOutOrders`; the
  name `CancelAllOrders` is gone.
- **R4. The client-side restart check**: `cpp_alignment.md` "A client refuses to start while a previous process's order is
  still Active", `cpp_alignment_report_2026-10-04.md` §4.6 step 1, and the "ctor-time startup check + 100 ms + Recover" item
  of `csharp_alignment_report_2026-10-05.md`: superseded by P5 + P11. Only `Recover` survives.
- **R5. `cpp_alignment_report_2026-10-04.md` §1.8 "TCP mirror"**: obsolete; `Provider/TCPServer.cs` is deleted (C12).
- **R6. Mirror mechanisms named in the 2026-10-07 drafts, all deleted or renamed** (none was ever for C++; listed so
  nobody ports them from an old draft): `ConnectionBroken` rejects sent by the Mirror (the enum value
  `OrderRejectedReason::ConnectionBroken = 30` stays, unused by the Mirror), `WriteRejectToGui`,
  `MirrorPacket.NewConnectionBrokenReject`, `ReconcileGui`, `CloseOutGui`, the per-GUI "forwarded" and readiness bits, the
  Local end's 1 s age check (with `MirrorMessageHeader.CopyTimestamp` / `CopyStopwatchTimestamp`,
  `MirrorHeartbeat.PublishTimestamp`), the 100 ms publish timer, `HasQueuedPackets` (now `IsPublishDue`).
  Renames: `MirrorPublisher` → `LocalMirror`, `MirrorSubscriber` → `RemoteMirror`, packet `GuiReply` →
  `AllocatedClientId`.
- **R7. 2026-10-07 drafts §5, "the Mirror reads the server's shared memory read-only; a C++ server needs nothing for it
  beyond what already exists"**: corrected. It still never writes the server's rows, but its stand-ins are ordinary clients
  of the C++ server and need the connect handshake (§10, L7).
- **R8. 2026-10-07 drafts §1.1, `RemoteDesk` described as "a live Remote Mirror … `ManualClient.Open` opens one on this
  answer"**: still true; it is now P1 and C2. Unchanged, listed only because the surrounding design changed.

---

## 10. Deploy in lockstep

- **L1. Connect handshake (P1, P2, P9, P10): the server and every client, C# and C++, in one deploy.** An old client takes
  the reply's `ClientId` as its own id. `OrderId.ClientId` keeps 6 bits, so a refusal becomes another client's slot
  (-1 → 63, -3 → 61, -6 → 58) and the old client carries on writing there. A new client against an old server behaves as
  today when accepted, and still hangs when it would be refused. "Every client" includes the client sockets a server
  process opens itself. In C# these are the trading server's own `.server` / `.audit` links to the C# Logging server, the
  `AlertManager` and strategy logging. The C# Logging server's `ServerSocket` now answers refusals too
  (`AlreadyConnected`, `Closing`, `NoClientIdAvailable`, `ServerError`).
- **L2. Restart admission (P5, P6 before P11).** The server's `IsClientReady` must be live no later than any client drops
  its own previous-orders check; otherwise a restarted client whose previous order is still working is admitted and counts
  its fills twice.
- **L3. Cancels on the execution channel (P8): clients first, or together.** Every client that reads its execution channel
  must ignore `OrderType::OrderTarget` (11) before the server starts writing one there. An old C# client throws
  `NotImplementedException("Unknown message type")` on its read thread.
- **L4. `InFlight` (P16): the C++ writer and the C# readers together.** Audit JSON writes enums by name; the C# Audit Trail
  silently skips lines it cannot parse.
- **L5. Shared-memory rows: no lockstep from this set** (the header is L9). No row layout changed. The new region `<server>/Latency` is
  created and written by C# only. The C# array-id shift (C3) matters only between the two C# Mirror ends, and the layout
  hash (C4) refuses a mismatch at startup.
- **L6. C#-only, for completeness:** both Mirror ends and the remote Workspaces deploy together (the server-header packet
  grew 4 bytes for the layout hash, the message and heartbeat packets shrank 8 bytes, and a remote Workspace's ring now
  carries only the admin channel).
- **L7. Remote desks need the handshake on the CME box.** Do not run the C# Local Mirror against a C++ server without P1,
  P2, P5 and P9 at least. A stand-in that such a server would refuse (its name already `Open`, still `Closing`, or an old
  order still working) waits forever in `Connect`, and the stand-in is started on the Local Mirror's only loop thread, so
  every remote desk freezes with it.
- **L8. Within the C++ change: P14 with or before P6.** This is not a cross-process lockstep, but `TryCloseOutOrders` reads
  the server's lock-edited `OrderState` rows through the seqlock, and those rows read `Empty` without the stamp (P6).
- **L9. `ServerHeader` grew to 177 bytes (P22): the C++ server and every C# process in one deploy.** The C# static check
  throws on any other size, and a 173-byte header posted by an old C++ server leaves the C# readers' last 4 bytes
  unwritten (or the reverse). Both Mirror ends deploy with it too: the server-header packet carries the whole header.
- **L10. Message efficiency (P24): the C++ server and the C# processes on the box together; the C# Logging no later.**
  C# clients read their row from `InstrumentHeader.ProductGroupId`: against an old C++ server those bytes are 0, so every
  instrument counts into row 0, and an empty `ProductGroupRoot` means no row was ever loaded for it. The shared
  `MessageEfficiency` row grew from 80 to 84 bytes (the header): both sides must agree on it, or each reads the other's
  counts 4 bytes off. An old C# Logging writes
  a type-18 record as an empty line and never appends the row. The new C# processes on the box (Mirror, Workspace) no
  longer append at all.

---

## 11. Verification

**C# evidence.** 2026-10-07 to 2026-10-09 (Windows, .NET 10): a scratchpad harness hosting a real `ServerSimulator`
(simulation clock) and spawning every client as a separate OS process; the server's state was read, and order rows
seeded, through shared memory. Scenarios 1, 2 (on a `Persistance = false` `ServerSocket`), 3 (seeded, and through the real
close path with the exchange's cancel fed through `Server::OnOrderState`: three restarts all admitted, no extra cancel),
4, 6, 12 passed; 5 skipped (Linux only). A negative control showed scenario 1's check catches the old bug (a process
running `Close` at exit on a live name ended that session within 10 ms). Scenario 3 failed before the "working" rule of
P6 item 1 and passed after it. 2026-10-12: a loopback smoke run of the remote-desk path against a simulation server
passed 14 of 16 checks; the two failures are by design (a reject does not reach a remote Workspace's screen; it reaches
Telegram from the CME box). Observed: a process that reconnects itself immediately is refused `AlreadyConnected` until a
server reader has seen its close message (answered, not hung).

**Scenarios for C++** (server and clients built with this report applied):

1. **Second process under a live name** → `AlreadyConnected`, thrown promptly. The live process keeps trading, its session
   is not closed, its orders untouched (P2, P9).
2. **Restart within the close window** → `Closing`, asked again every 100 ms, then connected (P2, P9).
3. **Restart while a previous order is still working** (its cancel rejected) → `NotReady`, the cancel re-sent and the
   target's `Seq` lifted by 1,000,000 again. Once the order's state row is `Done`, the next attempt connects, and the
   backlog is skipped: no fill counted twice (P5, P6, P11). Run it once with only C++ processes attached since the server
   created its regions, so no C# opener has stamped them. The attempt must still be `NotReady` after the drain (P6
   needs P14, L8).
4. **A client named after the server** (the reserved leaf) → `ReservedClientName`; the server keeps running. A clients file
   with such a name stops startup with `Server.LoadClients: <name> refused: ReservedClientName.` (P10).
5. **Linux: kill a client between the server's reply and its read**; the next connect under the name succeeds (P3).
6. **A throw in a `ClientClosed` handler** → logged and raised on `Exception`; dead-client detection and connects continue
   (P4).
7. **A request carrying another client's `ClientId`** → ignored; the name gets its own id (P2 step 1).
8. **Unread Create at a gone client.** Seed a target row `Active` + `Create` whose state row holds the slot's previous,
   `Done` order, then close the client. Before every execution CoreGroup thread has finished a pass, a reconnect is
   `NotReady` and the target stays `Active` + `Cancel`. After every execution CoreGroup thread has finished one pass, the
   next reconnect stamps the target `Done` and is accepted in that same attempt. Its cancel is still sent, and the server
   refuses it harmlessly because the state row is another order's (P6 item 2, P7). With the simulation clock paused the
   reconnect stays `NotReady` and the listen thread keeps answering.
9. **Torn idle target row.** Leave a target row's seq odd with its slot not working, close the client: the row is
   re-based to an even seq and stamped `Done`, and no cancel is enqueued (P6 item 3).
10. **The server's cancel is audited.** Kill a client with a working order: its server-to-client execution channel holds
    the injected `OrderTarget` cancel before the order's `Done`/`Canceled` (or the reject); a client reading that channel
    does not throw (P8).
11. **A refused connect never closes.** After scenario 1, the refused process's exit runs no close: the live session is
    still `Open` (P9).
12. **Constructor failure after `Connect`** (inject a throw after `Connect`, before the constructor ends) → the process can retry
    under the same name and connects; the server saw a close, not a stuck `Open` (P12).
13. **A client with all 64 slots working disposes**: its socket closes and the server cancels all 64 (P12).
14. **Stamp.** Open a region freshly created by the server, before any full `Write` to an order-state row the server then
    edits under the lock: a seqlock `TryRead` of that row returns `New`, not `Empty`; an unwritten row still reads `Empty`;
    a live row's `Sequence` is unchanged by a second opener (P14).
15. **Reader without latch.** A reader of a client-to-server ring reads `Closed` once at the close message, then the next
    session's first message after a reconnect under the same name (P15).
16. **`InFlight` in the audit.** A Create's first state, written to the audit by C++, reads `"OrderStateReason":"InFlight"`
    and parses in the C# Audit Trail (P16, L4).
17. **Remote-desk name.** A C# stand-in named `<strategy>_GUI0@<machine>` connects to the C++ server, trades, and is
    cancelled out on close like any client; its regions are named with `_` for `@` on both sides (P21, C1).
18. **Server process id.** A C# process reading the header of a running C++ server sees `ServerProcessId` equal to the
    server's pid. Kill the server with a C# Local Mirror attached: the Local Mirror exits within a pass and its remote shows
    "Connection broken" at once. Start a second server under a live one's name: it throws "already running as process
    <pid>". Kill the server while an algo still maps its regions and restart it: it throws "previous server … has exited";
    close the algo and it starts (P22, L9).
19. **Better-price delete.** On one side, set the best at 13230 and a level at 13190, then delete 13254: 13190 keeps its
    quantity and the call reports no change (false, delta 0). A fuzz that applies random updates, including such deletes,
    to a server book and forwards its deltas to a replica book ends with the two identical (P23).

---

## 12. Open items

- **Port this report in C++.** The C# Local Mirror must not run on the CME box until at least P1, P2, P5 and P9 are deployed
  there (L7).
- **Confirm on the C++ side** (C# relies on both, and does not know the C++ behaviour): the server rewrites
  `ServerHeader.Timestamp` every pass (a remote desk's clock); the server writes a fill's state and position rows before it
  sends the client the state message (the Local Mirror's publish trigger, C1).
- **Check the C++ `OrderDiscarded` set** has all eight reasons (P18).
- **Confirm on the C++ side** (C# processes already stamp the C++ server's regions whenever they open them): every row
  the C++ server writes is framed with the same `Magic` and `Length = sizeof(T)` (P14). Also confirm that no C++ region is
  already named `<server>/Latency` (C3).
- **Accepted, not capped:** each refused reconnect re-sends the cancels and lifts the target's `Seq` by 1,000,000 again
  (P5).
- **Measure first, then consider:** a server idle-loop prefetch of hot lines (would cut the Local Mirror's per-pass recall
  cost of about 330 ns per hot line, and the recall a writer pays when it comes round to a ring line the C# logging server
  read on the previous lap). The C# logging server now polls each ring once per 100 ms live instead of continuously
  (2026-10-13, C#-only, nothing to port), so the server pays that recall on at most one or two writes per ring per 100 ms
  instead of on nearly every message.
- **C#-only, for context:** Mirror links are not yet encrypted or authenticated (an `IPWhitelist` and a firewall rule
  only); a reject does not show on a remote Workspace's screen (it reaches Telegram from the CME box).
- **Not planned** (decided 2026-10-07): a workspace registering a strategy whose algo has not connected, or adding
  instruments to a strategy's book. Emergencies are covered by the server workspace, which trades any instrument at any time
  on the house book (strategy 0).

# C++ alignment report — 2026-10-07: a connect is always answered, the previous-orders check moves to the server

This handoff is for the engineer or agent aligning the C++ server and the C++ `Client` with the C#
change set started on 2026-10-07. It is self-contained. C# is the source of truth; every line
reference below was checked against the C# working tree on 2026-10-07.

**Status: complete in C#; exercised at runtime on Windows against a simulation server (§6).**

---

## 0. Scope, and deploy in lockstep

**What changed, in one paragraph.** The server now answers every connect request exactly once. The
reply's `SocketHeader.ClientId` is the client's id when it is 0 or more, otherwise a negative reason
(`ConnectRefusal`). Today three cases got no reply at all, so the client waited in `Connect` forever.
The rule "a client may not start while its previous process's order is still Active" moves from the
client constructor to the server: the server refuses such a connect (`NotReady`) and cancels the
leftover orders again. The client constructor drops its own previous-orders check, the 100 ms wait and the `OrderRisk`
clear, and disposes its socket if it throws after connecting.

**Wire semantics change, layout unchanged.** `SocketHeader` stays 344 bytes, every offset the same.
What changes is the meaning of a negative `ClientId` in the server's reply.

**Deploy the server and every client (C# and C++) together.** An old
client takes the reply's `ClientId` as its own id. `OrderId.ClientId` keeps 6 bits, so a refusal code
becomes another client's slot (-6 becomes 58, -3 becomes 61, -1 becomes 63), and the old client
would carry on writing there. A new client against an old server behaves exactly as today (an
accepted connect is unchanged; a refusal still hangs).

---

## 1. Wire

### 1.1 `ConnectRefusal` (int32, new; `Socket/Socket.cs:147-156`)

| Name | Value | Meaning |
|---|---|---|
| `NoClientIdAvailable` | -1 | the allocator's existing "no id" return |
| `ServerError` | -2 | something threw while the server decided |
| `AlreadyConnected` | -3 | the name is Open: a live process holds it |
| `Closing` | -4 | the name's previous session is still closing: ask again (§4.1) |
| `ReservedClientName` | -5 | the name is the server's leaf, reserved for the house book (§4.2) |
| `NotReady` | -6 | the server's admission rule refused it for now (§3) |
| `RemoteDesk` | -7 | a live Remote Mirror (C# only), not a server: only a `RemoteClient` attaches there, and `ManualClient.Open` opens one on this answer. A C++ server never returns it; add the value so a C++ `ClientSocket` names it |

Values are wire values. 0 or more is always a client id.

### 1.2 A request's `ClientId` is ignored

The server forces the request's `ClientId` to -1 before allocating (`Socket.cs:1134-1135`). A request
can no longer choose its own id and take another client's slot. `LoadClients` still passes a stored
id, because it calls `AllocateClientId` directly, not through the letterbox.

---

## 2. `ServerSocket` (`Socket/Socket.cs`)

### 2.1 `PollLetterBox` answers exactly once (`:1099-1128`)

```
PollLetterBox:
  1. if the server letterbox is empty, return; else take the request
  2. try DecideConnect(ref header)
     catch (any): report to Exception, header.ClientId = ServerError
  3. if header.ClientId < 0: ReplyToClient(header); return
     else OpenClient(header)            // OpenClient sends the accepting reply, as before
```

### 2.2 `DecideConnect` (`:1130-1165`)

```
DecideConnect(ref header):
  1. header.ClientId = -1; header.ClientId = AllocateClientId(header)
     if < 0: return                                           // the allocator's own code
  2. status = clientHeaders[id].Status
     Open              -> ClientId = AlreadyConnected; return
     Closing or Closed -> ClientId = Closing; return
  3. if IsClientReady is set and IsClientReady(id) == false -> ClientId = NotReady; return
  4. if status == Disposed: CreateClient(header); then fire ClientAllocated(header)
     (Detached: reuse the socket, keeping its ring and read cursors, as before)
```

Step 4 order changed: the socket is built **before** `ClientAllocated` fires (it was the reverse).
A failed build no longer announces a socket that does not exist, and a retry does not announce
twice (the logging server throws "already subscribed" on a second announce). It is the order
`LoadClients` already used. The comment on the event now reads "at ClientAllocated time the socket
exists but no process is attached to it yet".

### 2.3 `ReplyToClient` (`:1167-1179`), used by the refusal path and by `OpenClient` (`:1066`)

```
ReplyToClient(header):
  open the client's letterbox (name = header.ClientName) for write
  while TryStore(header) fails:
     if TryPeek(leftover) and !IsProcessAlive(leftover.ClientProcessId): TryEmpty()
     sleep 1 ms
```

Why: on Linux a client that died between the server storing its reply and reading it leaves the
reply in its region. The next reply to that name used to spin here forever, which blocked the
listen thread: no further connects, no dead-client detection, no `TryCloseOutOrders`. Only the listen
thread stores into a client's letterbox, so the box holds either that dead reply or nothing.

### 2.4 The listen loop never dies (`:933-955`)

Each pass is `try { PollPids(); PollLetterBox(); } catch (e) { log; Exception?.Invoke(e); }`, then
sleep 1 ms. It covers throws in `ClientOpened`/`ClientClosed` handlers too (the server's
`OnClientClosed` runs `TryCloseOutOrders`). The outer catch never replies; only §2.1's inner catch
does, so a request is never answered twice. This extends the "read loops never die" rule
(`cpp_alignment_report_2026-10-04.md` §2.7) to the listen loop.

### 2.5 Hook `IsClientReady` (`:159`, field `:903`)

```
delegate bool IsClientReady(int clientId);   // false: refuse NotReady
```

Asked only when no live process holds the name (status Detached or Disposed), never for an Open
name. Unset means always ready (the logging server does not set it).

---

## 3. `Server` (`Provider/Server.cs`)

### 3.1 `IsClientReady` (`:330-338`), wired at `:131`

```
bool IsClientReady(clientId) = TryCloseOutOrders(clientId)    // (2026-10-09) one walk

bool TryCloseOutOrders(clientId):                             // was CancelAllOrders; also run by OnClientClosed (return ignored)
  isClientDrained = IsClientDrained(clientId)
  isClosedOut = true
  for each of the client's 64 slots:
    if the OrderTarget row is empty: continue
    target = OrderTarget row (plain read: its writer is gone)
    state = OrderState row via the seqlock read (waits out a write: the CoreGroup thread may be writing it now)
    isSameOrder = state.OrderId == target.OrderId
    isWorking = isSameOrder ? state.Status == Active : target.Status == Active
    if !isWorking && !IsWriteInProgress(target seq): continue  // a row left mid-write is repaired even when not working
    isAwaitingExchange = isWorking && (isSameOrder || !isClientDrained)   // an unread Create may still be on its way out
    isClosedOut &= !isAwaitingExchange
    target.Status = isAwaitingExchange ? Active : Done        // an unread Create (or a torn idle row) is closed out
    target.Action = Cancel; target.Seq += 1_000_000; target.NicTimestamp = Clock.Now
    RecoveryWrite(target)
    if isWorking: EnqueueOrderTarget(target)                   // not for a torn idle row: its id may name no instrument
  return isClosedOut
```
Drain handshake (port it; it is what makes "unread" a proof, not a timing margin):
```
long drainRequest (own 64-byte line); long drainAcknowledged[coreGroupId] (each on its own line); long drainRequestAtClose[clientId]
OnClientClosed(clientId):   drainRequestAtClose[clientId] = atomic increment of drainRequest (full fence), then TryCloseOutOrders
end of ReadExecution(cg):   r = load drainRequest; if r != drainAcknowledged[cg]: store drainAcknowledged[cg] = r
IsClientDrained(clientId):  every execution CoreGroup's drainAcknowledged >= drainRequestAtClose[clientId]
```
The fence guarantees a CoreGroup thread that loads the new request also sees the client Detached, so no later pass reads it;
the acknowledgement at the end of a pass covers everything that pass read. Hot-path cost: one cached load and a predicted
branch per pass; a store only after a close.

`CancelAllOrders` is renamed `TryCloseOutOrders` (true once nothing waits on the exchange); `HasActiveOrders` and
`IsOrderActive` are folded in. A target row left mid-write (odd seq) by the dead client is now repaired even when its slot is
not working; before, only working slots were `RecoveryWrite`n, so a torn idle row stayed odd: readers skipped it forever and
the next process's seqlock on that slot ran inverted. Why the close-out: a Create the server never read (the client wrote the
target row and its ring message, then went away before the server read it) can never be read once the client is gone; the
cancel sent for it is refused (the state row is another order's), and nothing marked the target back, so the name was
refused `NotReady` forever. The cancel is still sent: at close the segment thread may be mid-way through a Create it read
just before, and then the cancel finds its order.

| Name's status | Answer | Orders |
|---|---|---|
| Open (connected and trading) | `AlreadyConnected` | never touched |
| Closing / Closed | `Closing` | `OnClientClosed` is already cancelling them |
| Detached / Disposed, nothing Active | accepted | — |
| Detached / Disposed, something Active | `NotReady` | `TryCloseOutOrders` again |

It runs on the listen thread, which is where `OnClientClosed` → `TryCloseOutOrders` already runs, and
no live process writes the client's rows at that point, so `TryCloseOutOrders`'s `RecoveryWrite` of the
target row has a single writer, as it assumes.

### 3.2 The "working" rule (was `HasActiveOrders` / `IsOrderActive`; since 2026-10-09 inside `TryCloseOutOrders`)

```
bool IsOrderActive(probe):
  if the client's OrderTarget row is empty: return false
  target = OrderTarget row, state = OrderState row        // plain reads, no lock: the client may have died mid-write
  if state.OrderId == target.OrderId: return state.OrderStateStatus == Active    // the server's row is about this order: it decides
  else:                               return target.OrderTargetStatus == Active  // a Create the server has not processed yet
```

`HasActiveOrders` scans the client's 64 slots with it. `TryCloseOutOrders` (`:306-328`) skips a slot
with `!IsOrderActive(probe)`; its cancel stamping is unchanged.

**Why not "state Active OR target Active"** (the test `CancelAllOrders` used before): `CancelAllOrders`
itself stamps the target row `OrderTargetStatus = Active` (`:318`), and no server code ever sets it
back; only a live client does, and that client is the one being refused. With the OR, a process that
died with a working order was refused `NotReady` forever after the exchange cancelled it (state row
Done), and each refusal queued another cancel for an already-closed order. Found by the runtime
harness (§6, S3b/S3r). Side effect: `TryCloseOutOrders` no longer sends a cancel for an order whose
state row is already Done.

### 3.3 Behaviour change: `TryCloseOutOrders` runs on every refused connect

Each refused attempt re-sends the cancels for the still-Active orders and lifts each target's `Seq`
by 1,000,000 again. Accepted as is (decided 2026-10-07): the retry cadence is set by whoever retries
(a person, a supervisor, the mirror's 3 s retry).

---

## 4. Client (`Provider/Client.cs`)

### 4.1 `ClientSocket.Connect` returns the id or throws the refusal (`Socket/Socket.cs:785-847`)

```
int Connect():
  1. no socket -> throw; IsClosed -> throw (was: return -1, which no caller used)
  2. id = RequestClientId()
     while id == Closing: sleep 100 ms; id = RequestClientId()
  3. if id < 0: Dispose(); throw ConnectRefusedException("ClientSocket.Connect: server S refused C: <ConnectRefusal>", reason)
     // ConnectRefusedException : InvalidOperationException, carrying ConnectRefusal (Socket.cs, beside the enum), so a
     // caller can act on the reason; a C++ client throws or returns the equivalent
     return id

int RequestClientId():
  1. _socketHeader.Timestamp = now; store it in the server letterbox (wait while full)
  2. wait for the reply whose ClientName, Timestamp AND ClientProcessId equal the request's; empty it; return its ClientId
     (prints "Waiting for server ..." every 3 s; waits forever while no server listens, as before)

void Dispose():
  _closeOnExit.Cancel()      // the exit action registered in the constructor (:779), now kept in a field (:723)
  _socket.Dispose()
```

- **A refusal disposes, never closes.** A refused second process maps the same rings as the live
  process under that name; `Close` writes a close message into them, which would end the live
  session and cancel its orders. `Dispose` writes nothing.
- **The exit action is cancelled on `Dispose`.** It called `Close` at shutdown regardless, which was
  the same danger for a refused socket, and threw `ObjectDisposedException` (logged) after every
  normal `Client.Dispose`. A C++ `ClientSocket` that registers an exit close must likewise
  unregister it on dispose.
- **A fresh `Timestamp` per request**, so a retry never takes the reply to an earlier request. The
  process id is matched too (the reply echoes the request; `Timestamp` has 100 ns resolution on
  Windows).
- Callers that ignore the return value (`Server`'s `.server`/`.audit` links, `AlertManager`, strategy
  logging) are unchanged when accepted; where they hung forever on a refusal they now throw.

### 4.2 `ServerContext::AllocateClientId` returns refusals instead of throwing (`Provider/Context.cs:940-966`)

- Client named after the server (`ClientName == ServerStrategyName`): **return** `ReservedClientName`
  (was: throw).
- No free id (`LowestClear < 0`): **return** `NoClientIdAvailable` (was: throw). The `ServerSocket`
  default allocator and the logging server's already returned -1.
- Through the letterbox these reach the client as the reason (§2.1-§2.2), where a throw used to kill
  the listen thread. `Server::LoadClients` (`Server.cs:806-813`) is the one direct caller: it runs at
  startup from the weekly clients file and throws `InvalidOperation("Server.LoadClients: <name>
  refused: <reason>")` on a negative result, so a broken file still stops startup loudly.

### 4.3 The client constructor no longer checks previous orders (`Provider/Client.cs:266-267`)

Supersedes `cpp_alignment.md` "A client refuses to start while a previous process's order is still
Active" and `cpp_alignment_report_2026-10-04.md` §4.6 step 1. The constructor now does, after
`Connect` and building its context and `RiskLayer`:

```
_socket.Recover()               // skip what queued while away
```

- `ThrowIfPreviousOrdersActive` is deleted: the server refuses (`NotReady`) before the constructor
  runs, so the check could never fire.
- The 100 ms sleep is deleted. The server admits only once every previous order's state row is Done;
  it writes each row and its ring message microseconds apart on one thread, and `Recover` runs
  milliseconds after admission, so everything for those orders is already in the ring and skipped.
- `Recover` stays: without it the new process would count queued fills twice.
- The `OrderRisk` clear (all 64 own rows set to `OrderRisk{}`) is deleted too: a validated Create
  already resets its row (`RiskLayer.cs:453-455`, now commented), and nothing reads a slot's row
  before this process's own Create on it. Amend/reduce validation and `TryClipToRiskLimit` act only
  on this process's working orders; the client's `OnOrderState` gate requires `_isOrderActive` (all
  false after a restart); `OnOrderRejected` requires a matching target; the previous orders' messages
  are skipped by `Recover`. A C++ client must keep the reset-on-Create in `ValidateOrder` and may
  drop its startup clear.

### 4.4 A constructor that throws after `Connect` disposes (`Client.cs:258-273`, `ManualClient` `:34-60`)

Everything after `Connect` in the base constructor, and the whole `ManualClient` body, is in
`try { ... } catch { Dispose(); throw; }`. Without it the server keeps the name Open for the life of
the process and answers no second connect under it, so a retry in the same process hung.
`Dispose` (`:785-800`) tolerates a half-built client: it skips `_instrumentData` when null and calls
`Context?.Dispose()`. For a C++ client: release the socket (close it: here the process owns the
session) and whatever was built, then rethrow.

`Dispose` no longer throws when all 64 order slots are active (`if (_isOrderActive.IsFull) throw`, there since the first
commit, deleted). A client with every slot working is the one whose socket most needs to close: the close is what makes
the server cancel its orders. The Mirror's stand-in for a remote GUI with 64 working orders threw there on every pass,
stopping the Local mirror for every remote and leaving the orders working. A C++ `Client::Dispose` must not refuse either.

### 4.5 Only an algo clears its audit files in simulation (`Client.cs`, constructor)

The simulation-only audit-file wipe before `Connect` now runs for `AlgoClient` only; a `ManualClient`
(GUI) never touches audit files. Why: a GUI restarted within one simulation run failed, because that
run's Logging still holds the file open. A C++ algo keeps the wipe; a C++ manual client, if the port has
one, must drop it.

---

## 4b. `SharedArray` frames every row at construction; the server-header alias is gone (PORT)

- **New `Protocol::Stamp(Header64*, int length)`** (`Socket/Protocol.cs`): if the slot's `Magic != s_magic` or
  `Length != length`, write `Length = length` then `Magic = s_magic`. Never write `Sequence`: an unwritten slot (seq 0)
  still reads Empty, and a live slot's lock counter must not move. The values are constants, so concurrent stampers and
  writers cannot disagree.
- **The `SharedArray<T>` constructor stamps every row** with `sizeof(T)` when the view is write-capable
  (`Socket/SharedArray.cs`, step 5). Why: rows the server edits under the lock (`AcquireLock`, field stores, `ReleaseLock`:
  order states, positions, the house-book allocation row) never get `Magic`/`Length` from `Write`, so `TryRead`/`Read` saw
  them as Empty; the C# mirror never shipped an order state.
- **The `ServerHeaderLetterBox` one-row array alias is deleted** (`Provider/Context.cs`, was `NewSharedArray<ServerHeader>
  (ServerName/"ServerHeader" + "LetterBox", 1, …)`). That region is the server header `LetterBox`, whose `Length != 0`
  means "posted": stamping it through the array view would fake a post (clients would read a zero header; the server's
  `TryStore` would fail). Shared-array ids shift down by one (ClientHeaders is now 0); only the C# mirror uses ids.
- On the CME box the C++ server creates the regions; the first C# process to open them (the Mirror) stamps them
  harmlessly. Port both changes anyway so C++ readers can use `TryRead`, and so a C++ alias, if the port has one, is
  removed before C++ stamps.

## 4c. `OrderStateReason::PendingNew` is renamed `InFlight` (PORT, deploy in lockstep)

- **Rename only:** `InFlight = 1`, the same value, so shared memory and the wire are unchanged. The audit JSON writes
  enums by name, so a C++ writer still emitting `"PendingNew"` makes the C# Audit Trail skip those lines: rename in the
  same deploy.
- **New `OrderState::NewInFlight(const OrderTarget&)`** (C# fills an out parameter, written straight into the row) (`Execution/Order.cs`): `OrderHeader`, `OrderProfile` and
  `TimeInForce` from the target, `Active`, `InFlight`, `QuantityFilled` 0, `OrderHeader.Seq = 0`. `Server::OnOrderTarget`'s
  Create branch builds its state with it, then sets `QuantityAhead` and `OrderHeader.NicTimestamp` as before
  (`Provider/Server.cs`). Behaviour is unchanged.
- **C#-only:** the order walk (`OrderEnumerator`, GUIs only) now shows an unanswered Create as `NewInFlight(target)`, and
  only for the workspace's own GUI client: another client's order is listed once the server's state row is about it.
- **Two more builders, behaviour-preserving (port them so the sites match):** `OrderTarget::NewOrderRejected(Bitset64
  reasons, OrderRejectedSource source)` (C# fills an out parameter: constructor, then field stores, avoiding a copied temporary; a C++ port may simply return it; `OrderHeader`, `OrderProfile`, `OrderTargetAction` from the target, plus the two
  arguments) replaces the hand-built reject in `Client::Reject`, `Server::OnOrderTarget` and the simulator.
  `OrderState::NewRejected(const OrderRejected&, OrderState& rejectedState)` is an instance method filling an out parameter (returning by value cost an extra 64-byte copy, +4.7 ns): a copy of this state with the reject's
  `OrderHeader` and `OrderProfile`, `Done`, `Rejected`. `Server::OnOrderRejected` calls it on the live row, so the Done keeps
  `TimeInForce`, `QuantityAhead` and `ExchangeOrderId` exactly as before.

---

## 4f. A client deallocates an order on its Done, or on the refusal of a Create the server never saw (REVERTED: do not port)

**Reverted (RemoteClient):** a remote Workspace no longer runs a Client, so nothing sends a Client a client-side refusal of a
Create it sent. `Client::OnOrderRejected` is back to `if isTargetDone: target.Status = Done` plus the unchanged RiskLayer
line; drop `isCreateRejectedByClient` if it was ported. `DeallocateOrder` is inlined back into the Done branch of
`OnOrderState`, its only caller, as it was before this section. Net: nothing to port here; the C# client is back to the
2026-10-05 code. The C# Mirror no longer sends any client a reject: a stale call is dropped, not refused `ConnectionBroken`.

## 4e. A ring reader never latches closed (PORT)

- `ReadOnlySocket::TryRead` (both forms) returns `Closed` for a close message, that read only, and the next read continues
  after it. Delete the reader's closed flag, its `Close()`/`IsClosed`, the clears in `Recover`/`Reset`, the checks in
  `TryRead`/`GetReadStatus`, and the reader loop in `Socket::Close` (only writers close). `WriteOnlySocket` is unchanged.
- Nothing in the server changes: `ServerSocket` reads are gated on the client being Open, which already ends the session.
  If the C++ side has its own audit tap of client channels (as C#'s Logging `SocketListener`), this is what keeps it
  recording a client's orders after a reconnect under the same name (Spec.md "A reader never latches closed").

## 4d. The server's own cancels are audited on the client's channel (PORT)

- `Server::ReadExecution`, the injection-queue drain: before `OnOrderTarget(orderTarget)`, write the cancel to the client's
  server-to-client channel: `WriteToExecution(orderTarget.OrderHeader, orderTarget)`. Before, not after: a synchronous
  reject from `OnOrderTarget` would otherwise reach the channel ahead of its request. Only server-originated cancels
  travel this queue (`TryCloseOutOrders`), so there is no per-order cost.
- Every client's execution-channel read switch must ignore `OrderType::OrderTarget` (the C# client threw on unknown types).
  No live client reads one in practice: they are written for a gone client, and the next process's `Recover` skips them.

---

## 5. C#-only: do NOT port

- Row structs (`OrderRejected`, `OrderState`, `OrderTarget`, `Fill`, `TickHeader`, `Header<T>`): `fixed` padding became
  plain `byte` fields, static constructors became constants, the parameterless constructors are `AggressiveInlining`.
  Layout is byte-identical; these are .NET code-generation fixes with no C++ meaning.
- `Socket/SharedArray.cs` byte-level `RecoveryWrite(int, ReadOnlySpan<byte>)` and `AsValue`: used by
  the remote-workspace mirror only.
- `Provider/Context.cs`: `GetSharedArray(arrayId)` replaces `Mirror()` and `EnumerateSharedArray()`;
  `SharedArrayEnumerable` is deleted. `TryGetClientId(in String128, out int)` is a behaviour-preserving
  refactor of the name lookup (optional to mirror).
- `Provider/TCPServer.cs` is deleted, so `cpp_alignment_report_2026-10-04.md` §1.8 ("TCP mirror") is
  obsolete. Its replacement (the `Mirror` project) is C#-only and reads the server's shared memory
  read-only; a C++ server needs nothing for it beyond what already exists.
- `Provider.csproj` is a library; the new `Mirror` project (remote workspaces, Spec.md "Remote workspaces") is C#-only.
  That includes `LocalMirror`, `RemoteMirror` and `Provider/RemoteClient.cs`: nothing for C++ to port except the
  `ConnectRefusal.RemoteDesk = -7` value (the C++ server never returns it).
- New shared array `<server>/Latency`: one `Duration` (int64 nanoseconds) per CoreGroup (below 0 = disconnected), opened in
  `Context` after `WorkingRisks` with `GetLatency(coreGroupId)`. Only a C# Remote Mirror writes it, on its copy (index 0,
  the Mirror link); the C++ server neither creates nor writes it (the C# Local Mirror's open creates the region on the CME
  box, and its unwritten rows are never published). Port the accessor only if a C++ context should read it.
- PORT if C++ has the same static: `LeggedHeader.GetLegHeader` (the spread-leg header lookup) is now set only by the first
  `ServerContext` created (`??=`), never by a `ClientContext`, and `ServerContext::Dispose` clears it when it is its own.
  Each context used to set it to itself, so after the newest context was disposed a spread lookup went through it: in the
  C# Local Mirror, once a stand-in closed, an `ObjectDisposedException` (dropped spread allocations, `UnknownSymbol_<id>`
  in alerts), and unmapped reads in a race with `Dispose`. Spec.md "Spread symbols resolve through the process's ServerContext".
- `Context.DirectoriesRoot` (static, default `S:\` = `/mnt/S/` on Linux) replaces the hard-coded root in
  `DirectoriesPath`; only a Windows remote desk overrides it. The C++ server keeps `/mnt/S/`.

---

## 6. Verification

Run on 2026-10-07 (Windows, .NET 10): a scratchpad harness hosting a real `ServerSimulator`
(simulation clock) and spawning every client as a separate OS process; the server's state was read,
and order rows seeded, through shared memory. Results: S1, S2, S2c (Closing retry, on a
Persistance=false `ServerSocket`), S3a, S3b, S3c, S3r (kill with a working order through the real
close path, exchange cancel fed through `Server::OnOrderState`, three restarts all admitted, no extra
cancel), S4, S6, S7 PASS; S5 skipped (Linux only). A negative control showed S1's check catches the old
bug (a process running `Close` at exit on a live name ends that session within 10 ms). S3b failed
before the §3.2 fix and passes after it. Observed: a process that reconnects itself immediately is
refused `AlreadyConnected` until a server reader has seen its close message (answered, not hung).

Scenarios to mirror:
1. Second process under a live name → `AlreadyConnected`, thrown promptly; the live process keeps
   trading, its orders untouched.
2. Restart within the close window → `Closing`, retried, then connected.
3. Restart while a previous order is still Active (cancel rejected) → `NotReady`, the cancel re-sent;
   a later attempt after the order is Done connects, and the backlog is skipped (no double-counted
   fill).
4. A client named after the server (reserved) → `ReservedClientName`; the server keeps running.
5. Linux: kill a client between the server's reply and its read; the next connect under the name
   succeeds (§2.3).
6. A throw in a `ClientClosed` handler → logged; dead-client detection continues.

---

## 7. Open items

- Runtime verification (§6).
- Not planned (decided 2026-10-07): a workspace registering a strategy whose algo has not connected,
  or adding instruments to a strategy's book. Emergencies are covered by the server workspace, which
  trades any instrument at any time on the house book (strategy 0).

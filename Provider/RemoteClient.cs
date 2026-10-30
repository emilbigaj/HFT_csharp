using System;
using Execution;
using Socket;
using Tools;

namespace Provider;

/// What a Workspace and its widgets ask of their manual client: a ManualClient beside the server, a RemoteClient on a remote desk.
public interface IManualClient : IDisposable
{
    ClientContext Context { get; }
    int ClientId();
    bool OnOrderTarget(ref OrderTarget orderTarget);
    void OnAllocateInstrument(int instrumentHeaderId);
    void OnControlAlgoStatus(in ControlAlgoStatus controlAlgoStatus);
    void OnControlRiskLimit(in ControlRiskLimit controlRiskLimit);
    void OnControlTransferPosition(in ControlTransferPosition controlTransferPosition);
    void WriteSocket();
    void ReadSocket();
}

/// A Workspace's manual client on a remote desk: a pass-through to the stand-in ManualClient the Local Mirror runs under the
/// same name (same client id) beside the server. Each call goes up unchanged through the Remote Mirror and nothing comes back:
/// what the server does shows in the copy, and the link's latency in its Latency[0]. Holds no order state and writes no row:
/// the copy's only writer is the Mirror (see Spec.md "Remote workspaces"). Cold path: calls on the UI thread.
public sealed class RemoteClient : IManualClient
{
    public static readonly Duration s_silenceTimeout = Duration.FromSeconds(10);   // a link silent this long is broken: the Mirrors' TcpLink and the latency display

    private readonly ClientSocket _clientSocket;   // admin channel only: calls up to the Remote Mirror
    private readonly int _clientId;                // the stand-in's id beside the server, which the Remote Mirror's answer to the connect carried
    private readonly int _serverProcessId;         // the server the name was opened on; after a restart the copy's header names another
    private readonly LetterBox<int> _remoteMirrorProcessIdBox = null!;   // the Remote Mirror serving this desk; a restarted one posts its own
    private readonly int _remoteMirrorProcessId;   // the Remote Mirror the name was opened on

    public ClientContext Context { get; }

    public int ClientId() => _clientId;

    /// The letterbox where the Remote Mirror posts its process id once it listens: <server>/RemoteMirrorProcessId.
    public static FileSystemPath GetRemoteMirrorProcessIdPath(FileSystemPath serverName) => serverName / "RemoteMirrorProcessId";

    /// True once the server or the Remote Mirror this name was opened on has been replaced: its session is gone, and the Workspace rebuilds (see Spec.md "Remote workspaces").
    public bool IsSessionEnded => Context.ServerHeader.GetReadonlyRef().ServerProcessId != _serverProcessId || _remoteMirrorProcessIdBox.GetReadonlyRef() != _remoteMirrorProcessId;

    /// True while the Remote Mirror this name was opened on is running; however it ended (closed, killed, crashed), false at once.
    public bool IsRemoteMirrorAlive => ProcessId.IsAlive(_remoteMirrorProcessId);

    public RemoteClient(string guiName, string serverName)
    {
        _clientSocket = new ClientSocket(guiName, serverName, [SocketChannel.AdminChannelLength], [SocketChannel.AdminChannelLength]);
        _clientId = _clientSocket.Connect();
        // Why: as in Client: once connected the name is held until the socket closes, so a throw below must close it
        try
        {
            Context = new ClientContext(guiName, serverName, Access.Read);
            _serverProcessId = Context.ServerHeader.GetReadonlyRef().ServerProcessId;
            _remoteMirrorProcessIdBox = new LetterBox<int>(GetRemoteMirrorProcessIdPath(Context.ServerName), Access.Read);
            _remoteMirrorProcessId = _remoteMirrorProcessIdBox.GetReadonlyRef();
            _clientSocket.Recover();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Each call is written as made; the Local end makes it on the stand-in ManualClient, whose rejects alert beside the server.
    public bool OnOrderTarget(ref OrderTarget orderTarget)
    {
        _clientSocket.Write(in orderTarget);
        return true;
    }

    public void OnAllocateInstrument(int instrumentHeaderId) => _clientSocket.Write(new AllocateInstrument() { InstrumentHeaderId = instrumentHeaderId });

    public void OnControlAlgoStatus(in ControlAlgoStatus controlAlgoStatus) => _clientSocket.Write(in controlAlgoStatus);

    public void OnControlRiskLimit(in ControlRiskLimit controlRiskLimit) => _clientSocket.Write(in controlRiskLimit);

    public void OnControlTransferPosition(in ControlTransferPosition controlTransferPosition) => _clientSocket.Write(in controlTransferPosition);

    // Nothing queued. Why: every call comes from the UI thread, the ring's only writer, so it is written as it is made.
    public void WriteSocket() { }

    // Nothing comes down: the copy shows what the server did.
    public void ReadSocket() { }

    public void Dispose()
    {
        if (_clientSocket.IsDisposed)
            return;
        // Why: after a server or Remote Mirror restart the name's old session is gone and another Workspace may hold it; a close would end that one (see Spec.md)
        bool isSessionEnded = _remoteMirrorProcessIdBox != null && IsSessionEnded;
        if (!isSessionEnded)
            _clientSocket.Close();
        _clientSocket.Dispose();
        _remoteMirrorProcessIdBox?.Dispose();
        // Why retire, not dispose: a widget's late callback (a dialog it awaited) may still read it after a rebuild; unmapped at exit
        if (Context != null)
            ContextManager.Retire(Context);
    }
}

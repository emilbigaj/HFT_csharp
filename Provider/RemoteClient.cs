using System;
using Execution;
using Socket;
using Tools;

namespace Provider;

/// What a Workspace and its widgets ask of their manual client: a ManualClient beside the server, a RemoteClient on a remote desk.
public interface IManualClient : IDisposable
{
    ClientContext Context { get; }
    bool OnOrderTarget(ref OrderTarget orderTarget);
    void OnAllocateInstrument(int instrumentHeaderId);
    void OnControlAlgoStatus(in ControlAlgoStatus controlAlgoStatus);
    void OnControlRiskLimit(in ControlRiskLimit controlRiskLimit);
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

    public ClientContext Context { get; }

    public RemoteClient(string guiName, string serverName)
    {
        _clientSocket = new ClientSocket(guiName, serverName, [SocketChannel.AdminChannelLength], [SocketChannel.AdminChannelLength]);
        _clientSocket.Connect();
        // Why: as in Client: once connected the name is held until the socket closes, so a throw below must close it
        try
        {
            Context = new ClientContext(guiName, serverName, Access.Read);
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

    // Nothing queued. Why: every call comes from the UI thread, the ring's only writer, so it is written as it is made.
    public void WriteSocket() { }

    // Nothing comes down: the copy shows what the server did.
    public void ReadSocket() { }

    public void Dispose()
    {
        if (_clientSocket.IsDisposed)
            return;
        _clientSocket.Close();
        _clientSocket.Dispose();
        Context?.Dispose();
    }
}

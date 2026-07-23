using System;
using Tools;
using Provider;
using Socket;
using System.Threading;

Console.WriteLine("'local' + for LocalServer Mode or 'remote' for RemoteServer Mode");
string? input = null;
while (input != "local" && input != "remote")
{
    input = Console.ReadLine();
}

bool isLocal = input == "local";

FileSystemPath ServerName = ServerContext.GetDirectoryPath("ROCServer") + (isLocal ? "" : "_Web");

if (isLocal)
{
    ContextManager.Initialize(ServerName);

    if (Clock.Mode == ClockMode.Simulation)
    {
        Clock.Begin = ContextManager.ServerContext.ServerHeader.GetReadonlyRef().Timestamp;
        Clock.End = Timestamp.MaxValue;
        Clock.Interject += timestamp =>
        {
            Clock.OnInterject(ContextManager.ServerContext.ServerHeader.GetReadonlyRef().Timestamp);
            Thread.Sleep(1);
        };
    }

    LowLatency.StartBackgroundThread("Clock", () =>
    {
        Clock.Start();
    });

    // Trading server side
    var local = new LocalTCPServerContext();   // starts listener inside ctor

    Timestamp last = Timestamp.MinValue;
    while (true)
    {
        local.Read();
        Timestamp now = Timestamp.UtcNow;
        if ((now - last).TotalMilliseconds >= 100)
        {
            local.Update();
            last = now;
        }
        Thread.Sleep(1);
    }
}
else
{
    // Remote box side
    WebSocket ws = WebSocketClient.Connect("localHost", 5000);

    ServerSocket serverSocket = new ServerSocket(ServerName, 64);
    ServerHeader serverHeader = new ServerHeader()
    {
        ServerName = new String128(ServerName),
        InstrumentsCapacity = 4096,
        InstrumentsCount = 0,
        OrdersPerClient = 64,
    };
    serverHeader.CoreGroupIds.Set(0); // admin / housekeeping channel
    serverHeader.CoreGroupIds.Set(1); // single trading CoreGroup
    LetterBox<ServerHeader> serverHeaderBox = ServerContext.Connect(in serverHeader);
    
    ContextManager.Initialize(ServerName);
    ServerContext serverContext = new ServerContext(ServerName, Access.Write);
    serverSocket.AllocateClientId = serverContext.AllocateClientId;
    serverSocket.DeallocateClient = serverContext.DeallocateClient;
    serverSocket.Listen();
    var proxy = new RemoteContextProxy(ws, serverContext, serverSocket);

    while (true)
    {
        proxy.ReadManualClients();
        proxy.ReadTCP();    // returns quickly when WebSocket has nothing
    }
}
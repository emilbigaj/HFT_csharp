using System;
using System.IO;
using System.Net;
using Tools;

namespace Mirror;

[RegisterJson]
public enum MirrorRole : byte
{
    Local,        // beside the real server: publishes its shared memory and runs the remote GUIs' ManualClients
    Remote,       // where people sit: keeps the copy a local Workspace attaches to
}

/// What one end of the mirror runs as, how it reaches its peer, and where it keeps its directories. Static JSON, read once at
/// startup from the file named on the command line, or MirrorConfig.json in the working directory (see Spec.md "Remote
/// workspaces"). Either direction can be used on either end: listen, dial, or both.
[RegisterJson]
public sealed class MirrorConfig
{
    public const string s_fileName = "MirrorConfig.json";

    public MirrorRole MirrorRole;
    public string ServerName = "";                      // the trading server's leaf name; the remote copies it under the same name
    public ClockMode ClockMode = ClockMode.Realtime;
    public int ListenPort;                              // > 0: accept a peer that dials in
    public string[] IPWhitelist = [];                   // the only IP addresses a listening end accepts: the peer's (a Remote: the CME box's; a Local: each risk desk's)
    public string[] ConnectAddresses = [];              // "host:port", each dialled every second until connected
    public string DirectoriesRoot = "";                 // "" keeps S:\ (/mnt/S/); a relative folder is beside the executable ("Data" on a risk desk)

    /// The MirrorConfig at this path; a missing one is written with the defaults, to be filled in.
    public static MirrorConfig LoadOrCreate(string mirrorConfigPath)
    {
        if (File.Exists(mirrorConfigPath))
            return Json.Deserialize<MirrorConfig>(File.ReadAllText(mirrorConfigPath));
        MirrorConfig mirrorConfig = new();
        File.WriteAllText(mirrorConfigPath, Json.Serialize(mirrorConfig));
        return mirrorConfig;
    }

    /// Throws on a config that cannot work, before anything starts; a freshly written default always does, naming the file.
    public void ThrowIfInvalid(string mirrorConfigPath)
    {
        string mirrorConfigFullPath = Path.GetFullPath(mirrorConfigPath);
        if (string.IsNullOrWhiteSpace(ServerName))
            throw new InvalidDataException($"MirrorConfig.ThrowIfInvalid: ServerName is empty; fill in {mirrorConfigFullPath}");
        if (ListenPort <= 0 && ConnectAddresses.Length == 0)
            throw new InvalidDataException($"MirrorConfig.ThrowIfInvalid: set ListenPort, ConnectAddresses or both in {mirrorConfigFullPath}");
        // Why: links are not authenticated, so an unknown peer must never get one: a listening Local end would give it order entry,
        // and on a Remote a new link replaces the live one, which cancels every remote order
        if (ListenPort > 0 && (IPWhitelist.Length == 0 || !Array.TrueForAll(IPWhitelist, address => IPAddress.TryParse(address, out _))))
            throw new InvalidDataException($"MirrorConfig.ThrowIfInvalid: IPWhitelist is [{string.Join(", ", IPWhitelist)}], must list the IP addresses allowed to connect (on a Remote the CME box's address, on a Local each risk desk's); fill it in {mirrorConfigFullPath}");
    }
}

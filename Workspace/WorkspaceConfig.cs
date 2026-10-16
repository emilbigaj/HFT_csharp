using System.IO;
using Tools;

namespace Workspace;

/// What a Workspace opens when started with no arguments (a risk desk's Start.bat): the server, where its directories are, and
/// one window per entry, always in Realtime. Static JSON beside the executable, read once at startup (see Spec.md "Risk desk
/// package"). Cold path.
[RegisterJson]
public sealed class WorkspaceConfig
{
    public const string s_fileName = "WorkspaceConfig.json";

    public string ServerName = "";                      // the trading server's leaf name, as in MirrorConfig.json
    public string DirectoriesRoot = "";                 // as in MirrorConfig.json: "" keeps S:\ (/mnt/S/); a relative folder is beside the executable
    public WorkspaceConfigEntry[] Workspaces = [];      // one window each, in this order

    /// The WorkspaceConfig at this path; a missing one is written with the defaults, to be filled in.
    public static WorkspaceConfig LoadOrCreate(string workspaceConfigPath)
    {
        if (File.Exists(workspaceConfigPath))
            return Json.Deserialize<WorkspaceConfig>(File.ReadAllText(workspaceConfigPath));
        WorkspaceConfig workspaceConfig = new();
        File.WriteAllText(workspaceConfigPath, Json.Serialize(workspaceConfig));
        return workspaceConfig;
    }

    /// Throws on a config that cannot work, before anything starts; a freshly written default always does, naming the file.
    public void ThrowIfInvalid(string workspaceConfigPath)
    {
        string workspaceConfigFullPath = Path.GetFullPath(workspaceConfigPath);
        if (string.IsNullOrWhiteSpace(ServerName))
            throw new InvalidDataException($"WorkspaceConfig.ThrowIfInvalid: ServerName is empty; fill in {workspaceConfigFullPath}");
        if (Workspaces.Length == 0)
            throw new InvalidDataException($"WorkspaceConfig.ThrowIfInvalid: Workspaces is empty, must list at least one window (StrategyName \"\" for a server workspace); fill it in {workspaceConfigFullPath}");
    }
}

/// One window a Workspace opens at startup.
[RegisterJson]
public sealed class WorkspaceConfigEntry
{
    public string StrategyName = "";                    // "" opens a server workspace, else that strategy's
    public string WorkspaceName = "";                   // a saved layout of that principal, "" a blank window
}

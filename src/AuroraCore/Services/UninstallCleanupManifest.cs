using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Round-102: the hand-off from the dying launcher to a short-lived cleanup
/// process. Windows keeps the running EXE mapped, so the ONLY reliable way to
/// make the launcher body disappear immediately after uninstall is a second
/// copy that waits for the parent to exit and then deletes the original.
///
/// The helper re-validates every path against the uninstall allowlist before
/// deleting anything; a tampered manifest cannot be used to wipe arbitrary
/// folders.
/// </summary>
public sealed class UninstallCleanupManifest
{
    public int ParentPid { get; set; }
    public string ExePath { get; set; } = "";
    public string Root { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public string HelperPath { get; set; } = "";
    public string ManifestPath { get; set; } = "";

    public string Save(string path)
    {
        ManifestPath = path;
        File.WriteAllText(path, JsonHelper.SerializeIndented(this));
        return path;
    }

    public static UninstallCleanupManifest? Load(string path)
    {
        try { return JsonHelper.Deserialize<UninstallCleanupManifest>(File.ReadAllText(path)); }
        catch { return null; }
    }
}

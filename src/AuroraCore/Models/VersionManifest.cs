namespace AuroraCore.Models;

/// <summary>
/// Mojang version_manifest_v2.json (and compatible mirrors). Lists every
/// playable id with a pointer to its full version JSON.
/// </summary>
public class VersionManifest
{
    public Latest Latest { get; set; } = new();
    public List<VersionManifestEntry> Versions { get; set; } = new();
}

public class Latest
{
    public string Release { get; set; } = "";
    public string Snapshot { get; set; } = "";
}

public class VersionManifestEntry
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = ""; // release / snapshot / old_alpha / old_beta
    public string Url { get; set; } = "";
    public string Time { get; set; } = "";
    public string ReleaseTime { get; set; } = "";
}

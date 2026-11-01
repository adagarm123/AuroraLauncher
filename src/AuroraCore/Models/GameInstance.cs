using AuroraCore.Services;
using AuroraCore.Utils;

namespace AuroraCore.Models;

/// <summary>
/// A game instance = an isolated .minecraft-style directory (saves, mods, config)
/// plus metadata. Versions/libraries/assets are shared centrally for efficiency.
/// </summary>
public class GameInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New Instance";
    public string MinecraftVersion { get; set; } = "";
    public ModLoaderType LoaderType { get; set; } = ModLoaderType.NeoForge;
    public string? LoaderVersion { get; set; }
    public string? ResolvedVersionId { get; set; } // version JSON id to launch
    public string IconColor { get; set; } = "#4F8CFF";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Notes { get; set; } = "";

    /// <summary>User-chosen category label ("自动"/null = derive automatically).</summary>
    public string? Category { get; set; }

    /// <summary>Absolute path of a custom instance icon copied into the instance dir.</summary>
    public string? IconPath { get; set; }

    /// <summary>Favorited (收藏) marker.</summary>
    public bool Favorite { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Directory => Path.Combine(LauncherConfig.Instance.InstancesDir, Id);
}

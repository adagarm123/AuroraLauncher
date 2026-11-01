using AuroraCore.Models;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Manages game instances: creation, listing, deletion and importing existing
/// .minecraft folders or other launchers' instances. Import is COPY-based and
/// never deletes the user's source data or other instances — saves are always
/// protected.
/// </summary>
public sealed class InstanceManager
{
    private readonly Logger _log = Logger.Instance;
    private readonly string _metaFile;

    public InstanceManager()
    {
        PathEx.EnsureDirectory(LauncherConfig.Instance.InstancesDir);
        _metaFile = Path.Combine(LauncherConfig.Instance.InstancesDir, "instances.json");
    }

    public List<GameInstance> LoadAll()
    {
        if (!File.Exists(_metaFile)) return new List<GameInstance>();
        try
        {
            var list = JsonHelper.Deserialize<List<GameInstance>>(File.ReadAllText(_metaFile));
            // Sanitize legacy data: some older builds stored a full version id
            // ("fabric-loader-0.20.1-1.21") in LoaderVersion, which poisons
            // every profile URL (400/404 on all routes).
            foreach (var inst in list) Sanitize(inst);
            return list;
        }
        catch { return new List<GameInstance>(); }
    }

    /// <summary>Field validation applied on BOTH read and write: LoaderVersion
    /// must be a BARE loader version or null — a full version id or a
    /// placeholder poisons every profile URL (round-31 bug class).</summary>
    private static void Sanitize(GameInstance inst)
    {
        var lv = inst.LoaderVersion;
        if (string.IsNullOrEmpty(lv)) return;
        var log = Logger.Instance;
        if (lv.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase) ||
            lv.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = lv.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase)
                ? "fabric-loader-" : "quilt-loader-";
            var rest = lv[prefix.Length..];
            var suffix = "-" + inst.MinecraftVersion;
            inst.LoaderVersion = rest.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? rest[..^suffix.Length]
                : rest;
            log.Warn("Instance", $"Sanitized LoaderVersion for '{inst.Id}': '{lv}' -> '{inst.LoaderVersion}'");
        }
        else if (lv.Contains('（') || lv.Contains("自动") || lv.Contains("暂无"))
        {
            inst.LoaderVersion = null;
            log.Warn("Instance", $"Cleared placeholder LoaderVersion for '{inst.Id}'.");
        }
    }

    public void SaveAll(List<GameInstance> instances)
    {
        // Write-path validation: nothing poisoned can ever reach disk.
        foreach (var inst in instances) Sanitize(inst);
        File.WriteAllText(_metaFile, JsonHelper.SerializeIndented(instances));
    }

    public GameInstance Create(string name, string mcVersion, ModLoaderType loader, string? loaderVersion = null)
    {
        var inst = new GameInstance
        {
            Name = name,
            MinecraftVersion = mcVersion,
            LoaderType = loader,
            LoaderVersion = loaderVersion
        };
        PathEx.EnsureDirectory(inst.Directory);
        // Pre-create standard folders.
        foreach (var f in new[] { "saves", "mods", "config", "resourcepacks", "logs", "screenshots" })
            PathEx.EnsureDirectory(Path.Combine(inst.Directory, f));
        var all = LoadAll();
        all.Add(inst);
        SaveAll(all);
        _log.Info("Instance", $"Created instance '{name}' ({inst.Id}).");
        return inst;
    }

    public GameInstance? Get(string id) => LoadAll().FirstOrDefault(i => i.Id == id);

    public void Update(GameInstance inst)
    {
        var all = LoadAll();
        var idx = all.FindIndex(i => i.Id == inst.Id);
        if (idx >= 0) { all[idx] = inst; SaveAll(all); }
    }

    /// <summary>Deletes the instance folder AND garbage-collects version
    /// directories under versions/ that are no longer referenced by any
    /// remaining instance (client + loader versions). Shared libraries/assets
    /// are kept. Returns false if the instance metadata is missing.</summary>
    public bool Delete(string id)
    {
        var inst = Get(id);
        if (inst == null) return false;
        var dir = inst.Directory;
        if (Directory.Exists(dir))
        {
            // Safety: only delete inside our InstancesDir.
            var root = Path.GetFullPath(LauncherConfig.Instance.InstancesDir);
            var target = Path.GetFullPath(dir);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Refusing to delete a path outside the instances directory.");
            Directory.Delete(dir, recursive: true);
        }
        var all = LoadAll();
        all.RemoveAll(i => i.Id == id);
        SaveAll(all);
        GcUnreferencedVersions(all);
        _log.Warn("Instance", $"Deleted instance {id}.");
        return true;
    }

    /// <summary>Removes version directories (e.g. 1.21.10, fabric-loader-...) that
    /// no remaining instance references. Safety: only folders that carry our own
    /// version structure (a &lt;name&gt;.json or &lt;name&gt;.jar inside) are touched.</summary>
    private void GcUnreferencedVersions(List<GameInstance> remaining)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in remaining)
        {
            if (!string.IsNullOrEmpty(i.MinecraftVersion)) referenced.Add(i.MinecraftVersion);
            if (!string.IsNullOrEmpty(i.ResolvedVersionId)) referenced.Add(i.ResolvedVersionId);
        }

        var versionsRoot = LauncherConfig.Instance.VersionsDir;
        if (!Directory.Exists(versionsRoot)) return;
        foreach (var dir in Directory.GetDirectories(versionsRoot))
        {
            var name = Path.GetFileName(dir);
            if (referenced.Contains(name)) continue;
            var looksOurs = File.Exists(Path.Combine(dir, name + ".json"))
                         || File.Exists(Path.Combine(dir, name + ".jar"));
            if (!looksOurs) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                _log.Info("Instance", $"GC: removed unreferenced version '{name}'.");
            }
            catch (Exception ex)
            {
                _log.Warn("Instance", $"GC: could not remove version '{name}': {ex.Message}");
            }
        }
    }

    /// <summary>Imports an existing .minecraft folder by COPYING it into a new
    /// instance. The original is never modified or deleted.</summary>
    public GameInstance ImportMinecraftFolder(string sourceFolder, string? name = null)
    {
        if (!Directory.Exists(sourceFolder)) throw new DirectoryNotFoundException(sourceFolder);
        var inst = Create(name ?? "导入的实例 " + Path.GetFileName(sourceFolder), "", ModLoaderType.NeoForge);
        CopyDirectory(sourceFolder, inst.Directory, exclude: new[] { "logs", "cache" });
        _log.Info("Instance", $"Imported .minecraft from {sourceFolder} -> instance {inst.Id}.");
        return inst;
    }

    /// <summary>Imports instances exported by PCL2 / HMCL / Prism. These launchers
    /// store per-instance .minecraft folders; we copy each into a new instance.</summary>
    public List<GameInstance> ImportFromLauncher(string launcherRoot, string source = "unknown")
    {
        var imported = new List<GameInstance>();
        if (!Directory.Exists(launcherRoot)) return imported;

        // Prism: <root>/instances/<Instance>/.minecraft
        var prism = Path.Combine(launcherRoot, "instances");
        if (Directory.Exists(prism))
        {
            foreach (var d in Directory.GetDirectories(prism))
            {
                var mc = Path.Combine(d, ".minecraft");
                var src = Directory.Exists(mc) ? mc : d;
                imported.Add(ImportMinecraftFolder(src, $"[{source}] {Path.GetFileName(d)}"));
            }
        }
        // HMCL: <root>/<versionId>/.minecraft  (each version folder has a .minecraft)
        // PCL2: <root>/PCL/.minecraft  or  <root>/versions/<id>
        foreach (var candidate in new[] { Path.Combine(launcherRoot, ".minecraft"), Path.Combine(launcherRoot, "PCL", ".minecraft") })
        {
            if (Directory.Exists(candidate))
                imported.Add(ImportMinecraftFolder(candidate, $"[{source}] 导入"));
        }
        return imported;
    }

    private static void CopyDirectory(string source, string dest, string[]? exclude = null)
    {
        PathEx.EnsureDirectory(dest);
        exclude ??= Array.Empty<string>();
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dest, name), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            CopyDirectory(dir, Path.Combine(dest, name), exclude);
        }
    }
}

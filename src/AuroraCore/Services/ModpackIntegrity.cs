using AuroraCore.Models;

namespace AuroraCore.Services;

/// <summary>
/// Round-92: what a modpack instance was supposed to contain, and what it
/// actually contains.
///
/// The trigger: a .mrpack import that failed halfway (loader download 404, user
/// cancelled, network dropped) left an instance whose <c>mods/</c> folder was
/// empty. Every existing check called it "complete", because "complete" only
/// ever meant *vanilla* files — so the launcher happily started a plain vanilla
/// client under a modpack's name and the user played the wrong game without a
/// single warning.
///
/// The expectation is therefore recorded at import time (before any byte is
/// downloaded) and verified at launch time.
/// </summary>
public static class ModpackIntegrity
{
    /// <summary>How far off the installed mods are from the pack's manifest.</summary>
    public enum State
    {
        /// <summary>Not a modpack instance — nothing to verify.</summary>
        NotModpack,
        /// <summary>Every declared file is on disk (or no manifest to compare).</summary>
        Complete,
        /// <summary>Some mods are gone but enough are there to be a real pack.</summary>
        Partial,
        /// <summary>Zero mod jars while the pack declares some — the pack never landed.</summary>
        Empty
    }

    public readonly record struct Scan(State State, int Expected, int Actual, int MissingFiles)
    {
        public bool IsBroken => State is State.Empty or State.Partial;
        public string Describe() => State switch
        {
            State.NotModpack => "普通实例",
            State.Complete => $"整合包文件完整（{Actual} 个模组）",
            State.Empty => $"整合包的模组一个都没有（应有 {Expected} 个）",
            _ => $"整合包缺少 {Math.Max(0, Expected - Actual)} 个模组（{Actual}/{Expected}）"
        };
    }

    /// <summary>Counts <c>mods/*.jar</c> (top level only — nested jars are not
    /// loaded by any loader, so counting them would produce false "complete").</summary>
    public static int CountModJars(string instanceDirectory)
    {
        try
        {
            var dir = Path.Combine(instanceDirectory, "mods");
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*.jar", SearchOption.TopDirectoryOnly).Count();
        }
        catch { return 0; }
    }

    /// <summary>
    /// Verifies the instance against its manifest. A missing manifest is not an
    /// error (packs imported by an older build have none) — in that case only
    /// the recorded <paramref name="expectedModCount"/> is used, which is enough
    /// to catch "the whole pack never downloaded".
    /// </summary>
    public static Scan Evaluate(GameInstance instance)
    {
        if (instance == null || !instance.IsModpack) return new Scan(State.NotModpack, 0, 0, 0);
        var actual = CountModJars(instance.Directory);
        var manifest = ModpackManifest.Load(instance.Directory);
        var expected = manifest?.ModFileCount ?? instance.ExpectedModCount;

        if (expected <= 0)
        {
            // The pack declared no mods at all (a config-only pack). Nothing to
            // verify — declaring it "empty" would block a legitimately valid pack.
            return new Scan(State.Complete, 0, actual, 0);
        }

        var missing = manifest == null ? Math.Max(0, expected - actual) : manifest.MissingCount(instance.Directory);
        if (actual <= 0) return new Scan(State.Empty, expected, 0, missing);
        // "Partial" means a real chunk is gone: half the pack or worse. A user
        // who removed two optional mods on purpose must not be nagged every launch.
        if (actual * 2 <= expected) return new Scan(State.Partial, expected, actual, missing);
        return new Scan(State.Complete, expected, actual, missing);
    }
}

/// <summary>
/// Round-92: the declared contents of an imported .mrpack, saved next to the
/// instance so the launcher can (a) verify what should be there and (b) re-fetch
/// the missing files later without the original archive.
/// </summary>
public sealed class ModpackManifest
{
    public const string FileName = "modpack-manifest.json";

    public string PackName { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string? Loader { get; set; }
    public string? LoaderVersion { get; set; }
    public string? SourcePath { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
    public List<ModpackFile> Files { get; set; } = new();

    public sealed class ModpackFile
    {
        /// <summary>Path relative to the instance directory, forward slashes.</summary>
        public string Path { get; set; } = "";
        public string? Url { get; set; }
        public string? Sha1 { get; set; }
        public long Size { get; set; }

        public bool IsModJar =>
            Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
            Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    public int ModFileCount => Files.Count(f => f.IsModJar);

    public int MissingCount(string instanceDirectory) => Files.Count(f => !Exists(instanceDirectory, f));

    public IReadOnlyList<ModpackFile> Missing(string instanceDirectory) =>
        Files.Where(f => !Exists(instanceDirectory, f)).ToList();

    private static bool Exists(string instanceDirectory, ModpackFile file)
    {
        try
        {
            var full = System.IO.Path.Combine(instanceDirectory, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return false;
            // Declared size is a cheap truncation detector; hash verification is
            // the download engine's job, not this one's.
            return file.Size <= 0 || new FileInfo(full).Length == file.Size;
        }
        catch { return false; }
    }

    public static string PathFor(string instanceDirectory) => System.IO.Path.Combine(instanceDirectory, FileName);

    public void Save(string instanceDirectory)
    {
        try
        {
            Directory.CreateDirectory(instanceDirectory);
            var json = System.Text.Json.JsonSerializer.Serialize(this,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathFor(instanceDirectory), json);
        }
        catch { /* a read-only instance folder must not break an import */ }
    }

    public static ModpackManifest? Load(string instanceDirectory)
    {
        try
        {
            var path = PathFor(instanceDirectory);
            if (!File.Exists(path)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<ModpackManifest>(File.ReadAllText(path));
        }
        catch { return null; }
    }
}

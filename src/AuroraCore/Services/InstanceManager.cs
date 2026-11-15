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
    /// <summary>Round-84: per-instance metadata copy INSIDE the instance folder.
    /// instances.json stays the single source of truth; this file exists so an
    /// orphaned folder (metadata lost, JSON corrupted, user copied a folder in)
    /// can be identified and restored with its original Id — not a fresh GUID
    /// that would break every save/mod association.</summary>
    public const string MetaFileName = ".aurora-instance.json";

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

    /// <summary>Round-84: keeps the in-folder copy in sync (create / update /
    /// import). Best-effort: a read-only folder must not break saving metadata.</summary>
    private void WriteMarker(GameInstance inst)
    {
        try
        {
            PathEx.EnsureDirectory(inst.Directory);
            File.WriteAllText(Path.Combine(inst.Directory, MetaFileName),
                JsonHelper.SerializeIndented(inst));
        }
        catch (Exception ex)
        {
            _log.Warn("Instance", $"无法写入实例元数据副本 {inst.Id}: {ex.Message}");
        }
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
        WriteMarker(inst);
        _log.Info("Instance", $"Created instance '{name}' ({inst.Id}).");
        return inst;
    }

    public GameInstance? Get(string id) => LoadAll().FirstOrDefault(i => i.Id == id);

    /// <summary>Round-77: counts a launch the moment the game starts, so a crash
    /// (no clean exit) still shows up in "启动 N 次".</summary>
    public GameInstance? RecordLaunchStart(string id)
    {
        var all = LoadAll();
        var inst = all.FirstOrDefault(i => i.Id == id);
        if (inst == null) return null;
        inst.LaunchCount++;
        inst.LastPlayedAt = DateTime.Now;
        SaveAll(all);
        return inst;
    }

    /// <summary>Round-77: adds observed play time. Clamped so a clock jump or a
    /// sleeping laptop cannot invent days of playtime.</summary>
    public GameInstance? AddPlaytime(string id, TimeSpan duration, DateTime? playedAt = null)
    {
        var all = LoadAll();
        var inst = all.FirstOrDefault(i => i.Id == id);
        if (inst == null) return null;
        var seconds = (long)Math.Clamp(duration.TotalSeconds, 0, PlaytimeTracker.MaxSession.TotalSeconds);
        if (seconds <= 0) return inst;
        inst.PlaySeconds += seconds;
        if (playedAt is { } when && (inst.LastPlayedAt == null || when > inst.LastPlayedAt))
            inst.LastPlayedAt = when;
        SaveAll(all);
        _log.Info("Instance", $"实例 {inst.Name} 游玩时长 +{GameInstance.FormatPlaytime(seconds)}（累计 {inst.PlaytimeText}）");
        return inst;
    }

    public void Update(GameInstance inst)
    {
        var all = LoadAll();
        var idx = all.FindIndex(i => i.Id == inst.Id);
        if (idx >= 0) { all[idx] = inst; SaveAll(all); WriteMarker(inst); }
    }

    /// <summary>Round-79: stores the last run's peak memory / FPS / duration.
    /// Same single-writer rule as playtime — the launcher never edits
    /// instances.json anywhere else.</summary>
    public GameInstance? RecordPerformance(string id, int peakMemoryMb, int averageFps, int fpsSamples,
        TimeSpan duration, DateTime? measuredAt = null, int exitCode = 0)
    {
        if (peakMemoryMb <= 0 && averageFps <= 0) return null;
        var all = LoadAll();
        var inst = all.FirstOrDefault(i => i.Id == id);
        if (inst == null) return null;
        inst.LastPeakMemoryMb = Math.Max(0, peakMemoryMb);
        inst.LastAverageFps = Math.Max(0, averageFps);
        inst.LastFpsSamples = Math.Max(0, fpsSamples);
        inst.LastRunSeconds = (long)Math.Clamp(duration.TotalSeconds, 0,
            PlaytimeTracker.MaxSession.TotalSeconds);
        // Round-92: without the exit code the dashboard could not tell a crash
        // from a healthy session (it only looked at memory).
        inst.LastExitCode = exitCode;
        inst.LastPerfAt = measuredAt ?? DateTime.Now;
        SaveAll(all);
        _log.Info("Instance", $"实例 {inst.Name} 性能快照：峰值 {peakMemoryMb} MB" +
                              (fpsSamples > 0 ? $"，平均 {averageFps} FPS（{fpsSamples} 次）" : "，日志无 FPS") +
                              $"，退出码 {exitCode}，运行 {inst.LastRunSeconds} 秒");
        return inst;
    }

    /// <summary>Result of a delete. The UI must show <see cref="Message"/> when
    /// the outcome is not <see cref="DeleteOutcome.Deleted"/> — a silent failure
    /// leaves a folder the user believes is gone.</summary>
    public enum DeleteOutcome { Deleted, NotFound, FilesLocked }

    public readonly record struct DeleteResult(DeleteOutcome Outcome, string? Message)
    {
        public bool Ok => Outcome == DeleteOutcome.Deleted;
        public string Describe(string instanceName) => Outcome switch
        {
            DeleteOutcome.Deleted => $"实例「{instanceName}」及其文件夹已删除",
            DeleteOutcome.NotFound => "实例元数据里已经找不到它了（可能被手动改过 instances.json）",
            _ => $"删除失败：{Message}。文件夹仍然存在，已保留列表记录，关掉游戏/资源管理器后重试"
        };
    }

    /// <summary>Round-84: delete = folder first, metadata second.
    ///
    /// The old version deleted the folder and the record in one straight line.
    /// On Windows a locked file (game still exiting, an open log, antivirus)
    /// throws halfway — and anything that swallowed that exception left a folder
    /// on disk that no longer appeared in the list: a "ghost" nobody could see
    /// or remove. Now: retry, verify the directory is really gone, and only then
    /// drop the metadata. If it is still there, the record stays and the caller
    /// reports why.</summary>
    public DeleteResult DeleteChecked(string id, int attempts = 3, int delayMs = 400)
    {
        var inst = Get(id);
        if (inst == null) return new DeleteResult(DeleteOutcome.NotFound, null);

        var dir = inst.Directory;
        if (Directory.Exists(dir))
        {
            // Safety: only delete inside our InstancesDir.
            var root = Path.GetFullPath(LauncherConfig.Instance.InstancesDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(dir);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Refusing to delete a path outside the instances directory.");

            if (!TryDeleteDirectory(dir, attempts, delayMs, out var error))
                return new DeleteResult(DeleteOutcome.FilesLocked, error);

            // Belt and braces: never trust a single return value.
            if (Directory.Exists(dir))
                return new DeleteResult(DeleteOutcome.FilesLocked, "目录在删除后仍然存在");
        }

        var all = LoadAll();
        all.RemoveAll(i => i.Id == id);
        SaveAll(all);
        GcUnreferencedVersions(all);
        _log.Warn("Instance", $"Deleted instance {id} (folder + metadata).");
        return new DeleteResult(DeleteOutcome.Deleted, null);
    }

    /// <summary>Convenience wrapper for tests / CLI: true only when the folder
    /// AND the record are both gone.</summary>
    public bool Delete(string id) => DeleteChecked(id).Ok;

    /// <summary>Three attempts, 400 ms apart, clearing read-only flags between
    /// them — that covers "the game just exited", "Explorer has it open" and
    /// "a scanner is holding a jar for a moment".</summary>
    private bool TryDeleteDirectory(string dir, int attempts, int delayMs, out string? error)
    {
        error = null;
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                ClearReadOnly(dir);
                Directory.Delete(dir, recursive: true);
                if (!Directory.Exists(dir)) return true;
                error = "目录仍然存在";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (attempt < attempts - 1) Thread.Sleep(delayMs);
        }
        return false;
    }

    /// <summary>Read-only files (typical after copying from a CD/USB or after a
    /// backup restore) make Directory.Delete fail with UnauthorizedAccessException
    /// even though nothing is holding them.</summary>
    private static void ClearReadOnly(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                catch { /* vanished mid-enumeration */ }
            }
        }
        catch { /* unreadable subfolder: let the delete try anyway */ }
    }

    /// <summary>Round-84 reconcile: compare instances.json with the folders on
    /// disk. Never deletes anything, never invents an Id — the most it does is
    /// READ a folder's own marker so the user can re-register it.
    ///
    /// <see cref="Recovered"/>: on disk, not in the list, marker parsed OK.
    /// <see cref="MissingIds"/>: in the list, folder gone (dead link).
    /// <see cref="UnrecognizedFolders"/>: no marker — reported, never touched
    /// (could be a user's hand-made folder, a backup, anything).
    /// <see cref="CorruptedFolders"/>: marker exists but does not parse.</summary>
    public sealed record ScanResult(
        List<GameInstance> Recovered,
        List<string> MissingIds,
        List<string> UnrecognizedFolders,
        List<string> CorruptedFolders);

    public ScanResult Scan()
    {
        var known = LoadAll();
        var knownIds = known.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recovered = new List<GameInstance>();
        var missing = new List<string>();
        var unrecognized = new List<string>();
        var corrupted = new List<string>();

        var root = LauncherConfig.Instance.InstancesDir;
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var folder = Path.GetFileName(dir);
                if (knownIds.Contains(folder)) continue;

                var marker = Path.Combine(dir, MetaFileName);
                if (!File.Exists(marker)) { unrecognized.Add(folder); continue; }

                try
                {
                    var inst = JsonHelper.DeserializeNullable<GameInstance>(File.ReadAllText(marker));
                    if (inst == null) { corrupted.Add(folder); continue; }
                    // The FOLDER NAME is the identity (Directory derives from Id).
                    // Keep the stored Id when it matches; otherwise trust the
                    // folder and say so, rather than creating a brand-new GUID
                    // that would orphan every save and mod inside.
                    if (!string.Equals(inst.Id, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Warn("Instance", $"实例文件夹 {folder} 的元数据 Id 是 {inst.Id}，以文件夹名为准");
                        inst.Id = folder;
                    }
                    if (string.IsNullOrWhiteSpace(inst.Name)) inst.Name = folder;
                    recovered.Add(inst);
                }
                catch
                {
                    corrupted.Add(folder);
                }
            }

            // Dead links: metadata without a folder.
            foreach (var inst in known)
                if (!Directory.Exists(Path.Combine(root, inst.Id))) missing.Add(inst.Id);

            // Backfill markers for instances created before this round, so the
            // next scan can recover them even if instances.json is lost.
            foreach (var inst in known)
                if (Directory.Exists(inst.Directory) &&
                    !File.Exists(Path.Combine(inst.Directory, MetaFileName)))
                    WriteMarker(inst);
        }

        return new ScanResult(recovered, missing, unrecognized, corrupted);
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

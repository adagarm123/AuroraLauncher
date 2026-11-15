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

    /// <summary>Per-instance Java override: absolute path to java.exe.
    /// null / empty = follow the launcher-wide setting and auto-select by the
    /// game version (round-62). Lets a 1.12 pack stay on Java 8 while a 1.21
    /// pack uses Java 21, with no interference between instances. Old metadata
    /// files simply deserialize this as null, so no migration is needed.</summary>
    public string? JavaPath { get; set; }

    // ---- round-77: playtime statistics ----
    /// <summary>Accumulated play time in seconds (only counting sessions the
    /// launcher actually observed, including ones recovered after a crash).</summary>
    public long PlaySeconds { get; set; }

    /// <summary>Number of times the game was launched from this instance.</summary>
    public int LaunchCount { get; set; }

    /// <summary>When the instance was last launched (null = never).</summary>
    public DateTime? LastPlayedAt { get; set; }

    // ---- round-79: last-run performance snapshot ----
    /// <summary>Peak working set of the game JVM during the last run (MB, 0 =
    /// never measured). Sampled by the launcher itself every few seconds, so it
    /// works even though vanilla Minecraft logs no memory statistics.</summary>
    public int LastPeakMemoryMb { get; set; }

    /// <summary>Average FPS parsed from the last run's output (0 = the log had
    /// no FPS lines — most clients never print them).</summary>
    public int LastAverageFps { get; set; }

    /// <summary>How many FPS samples backed <see cref="LastAverageFps"/>.</summary>
    public int LastFpsSamples { get; set; }

    /// <summary>Duration of the last run (seconds) — shown next to the peak
    /// memory so "42 分钟 / 3.2 GB" reads as one run, not a total.</summary>
    public long LastRunSeconds { get; set; }

    /// <summary>When the snapshot was taken (null = never).</summary>
    public DateTime? LastPerfAt { get; set; }

    /// <summary>Exit code of the last run (0 = clean exit). Round-92: without it
    /// the dashboard called a 2-second crash "健康".</summary>
    public int LastExitCode { get; set; }

    // ---- round-92: modpack expectation ----
    /// <summary>Created by importing a .mrpack (or a Modrinth pack). Used to
    /// verify the pack actually landed, not just the vanilla files.</summary>
    public bool IsModpack { get; set; }

    /// <summary>How many <c>mods/*.jar</c> the pack declares. Written at import
    /// time, BEFORE any download, so a failed import cannot hide itself.</summary>
    public int ExpectedModCount { get; set; }

    /// <summary>Every client file the pack declares (mods + configs + resources).</summary>
    public int ExpectedFileCount { get; set; }

    /// <summary>Display name of the pack (the .mrpack file name / project title).</summary>
    public string? ModpackName { get; set; }

    /// <summary>Where the .mrpack came from, so "补全" can re-run the import.
    /// May be a temp path that no longer exists — the manifest is the fallback.</summary>
    public string? ModpackSourcePath { get; set; }

    public DateTime? ModpackImportedAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string PlaytimeText => FormatPlaytime(PlaySeconds);

    /// <summary>Card line: "已游玩 42 小时 15 分钟 · 启动 128 次".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PlayStatsText => LaunchCount > 0
        ? $"已游玩 {PlaytimeText} · 启动 {LaunchCount} 次"
        : "还没有游玩记录";

    [System.Text.Json.Serialization.JsonIgnore]
    public string LastPlayedText => LastPlayedAt is { } t
        ? $"上次游玩：{t:yyyy-MM-dd HH:mm}"
        : "上次游玩：—";

    /// <summary>Card line for the performance dashboard: "上次运行 42 分钟 ·
    /// 峰值内存 3072 MB · 平均 58 FPS" (FPS omitted when the log had none).
    /// Round-92: a reading below 10 MB is not a real JVM — say "未采集到"
    /// instead of printing a number that cannot be true.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PerfSummaryText => LastPerfAt == null
        ? "还没有运行记录"
        : $"上次运行 {FormatPlaytime(LastRunSeconds)} · " +
          (LastPeakMemoryMb >= 10 ? $"峰值内存 {LastPeakMemoryMb} MB" : "峰值内存未采集到") +
          (LastFpsSamples > 0 ? $" · 平均 {LastAverageFps} FPS" : "");

    /// <summary>Human duration. Under a minute reads "不足 1 分钟" rather than
    /// "0 分钟" — the latter looks like a bug to a user who just played.</summary>
    public static string FormatPlaytime(long seconds)
    {
        if (seconds <= 0) return "0 分钟";
        if (seconds < 60) return "不足 1 分钟";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes} 分钟";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} 小时" : $"{hours} 小时 {rest} 分钟";
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Directory => Path.Combine(LauncherConfig.Instance.InstancesDir, Id);
}

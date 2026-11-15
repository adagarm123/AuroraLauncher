using System.Text.RegularExpressions;

namespace AuroraCore.Services;

/// <summary>Pure decision logic: which Java major a Minecraft version wants.
/// Kept independent of any version JSON / IO so the instance-level Java UI can
/// show a recommendation without launching a process (round-62).
///
/// Vanilla requirements: 1.16.5 and older → Java 8; 1.17–1.20.4 → Java 17
/// (1.17 technically needs 16, but 17 is the LTS that works everywhere);
/// 1.20.5 / 1.21+ → Java 21. Snapshots and unparseable ids fall back to 21,
/// the safe modern default.</summary>
public static class JavaRequirement
{
    private static readonly Regex McVersion = new(@"^1\.(\d+)(?:\.(\d+))?", RegexOptions.Compiled);

    public static int RecommendMajor(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return 21;
        var m = McVersion.Match(minecraftVersion.Trim());
        if (!m.Success) return 21;
        if (!int.TryParse(m.Groups[1].Value, out var minor)) return 21;
        var patch = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var p) ? p : 0;

        if (minor < 17) return 8;
        if (minor < 20) return 17;
        if (minor == 20 && patch < 5) return 17;
        return 21;
    }

    /// <summary>Short, human-facing hint for the instance settings card.</summary>
    public static string Describe(string? minecraftVersion)
    {
        var major = RecommendMajor(minecraftVersion);
        return string.IsNullOrWhiteSpace(minecraftVersion)
            ? "此实例未记录游戏版本，默认推荐 Java 21。"
            : $"{minecraftVersion} 通常推荐 Java {major}；选「自动选择」会按版本需求自动匹配。";
    }
}

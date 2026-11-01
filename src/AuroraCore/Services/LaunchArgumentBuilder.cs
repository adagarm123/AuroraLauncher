using System.Runtime.InteropServices;
using AuroraCore.Models;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Everything needed to translate a resolved version + account + settings into a
/// concrete process command line. Implements Mojang argument rules, OS/feature
/// gating, token substitution and classpath assembly.
/// </summary>
public class LaunchContext
{
    public VersionJson Version { get; set; } = null!;
    public string JavaExecutable { get; set; } = "";
    public string VersionName { get; set; } = "";
    public string VersionType { get; set; } = "release";
    public string GameDirectory { get; set; } = "";
    public string AssetsRoot { get; set; } = "";
    public string LibrariesRoot { get; set; } = "";
    public string NativesDirectory { get; set; } = "";
    public string ClientJarPath { get; set; } = "";
    public List<string> ExtraClasspath { get; set; } = new();
    public string AssetsIndexName { get; set; } = "";
    public string LoggingConfigPath { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string UserType { get; set; } = "mojang";
    public string Xuid { get; set; } = "";
    public string UserProperties { get; set; } = "{}";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Fullscreen { get; set; }
    public string ExtraJvmArgs { get; set; } = "";
    public string ExtraGameArgs { get; set; } = "";
    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;
}

public class LaunchSpec
{
    public string JavaPath { get; set; } = "";
    public List<string> Arguments { get; set; } = new();   // everything after the java executable
    public string WorkingDirectory { get; set; } = "";
    public string DisplayCommandLine { get; set; } = "";    // redacted for logs
    public string VersionName { get; set; } = "";
    public int MinMemoryMb { get; set; }
    public int MaxMemoryMb { get; set; }
}

internal class ArgumentNode
{
    public List<Rule>? Rules { get; set; }
    public JsonElement Value { get; set; }
}

public sealed class LaunchArgumentBuilder
{
    private static readonly Dictionary<string, bool> Features = new()
    {
        ["is_demo_user"] = false,
        ["has_custom_resolution"] = false,
        ["has_quick_plays_support"] = false,
        ["is_quick_play_singleplayer"] = false,
        ["is_quick_play_multiplayer"] = false,
        ["is_quick_play_realms"] = false,
        ["minecraft_embedded"] = false,
        ["quick_play_launch"] = false
    };

    private static readonly (string Name, string Version, string Arch) Os = DetectOs();

    public LaunchSpec Build(LaunchContext ctx)
    {
        var version = ctx.Version;
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["version_name"] = ctx.VersionName,
            ["version_type"] = ctx.VersionType,
            ["assets_root"] = ctx.AssetsRoot,
            ["game_directory"] = ctx.GameDirectory,
            ["assets_index_name"] = ctx.AssetsIndexName,
            ["auth_player_name"] = ctx.PlayerName,
            ["auth_uuid"] = ctx.Uuid,
            ["auth_access_token"] = ctx.AccessToken,
            ["auth_session"] = ctx.AccessToken,
            ["auth_xuid"] = ctx.Xuid,
            ["clientid"] = ctx.Xuid,
            ["user_type"] = ctx.UserType,
            ["user_properties"] = ctx.UserProperties,
            ["natives_directory"] = ctx.NativesDirectory,
            ["launcher_name"] = "AuroraLauncher",
            ["launcher_version"] = "1.0.0",
            ["primary_jar"] = ctx.ClientJarPath,
            ["resolution_width"] = ctx.Width.ToString(),
            ["resolution_height"] = ctx.Height.ToString(),
            ["path"] = ctx.LoggingConfigPath
        };

        // Classpath: client jar + all OS-allowed artifacts.
        var classpath = new List<string> { ctx.ClientJarPath };
        classpath.AddRange(ctx.ExtraClasspath);
        foreach (var lib in version.Libraries)
        {
            if (!IsAllowed(lib.Rules)) continue;
            var artifact = GetArtifactPath(lib, ctx.LibrariesRoot);
            if (artifact != null) classpath.Add(artifact);
        }
        tokens["classpath"] = string.Join(";", classpath);

        var features = new Dictionary<string, bool>(Features)
        {
            ["has_custom_resolution"] = ctx.Width > 0 && ctx.Height > 0
        };

        // ---- JVM args ----
        var jvm = new List<string>
        {
            $"-Xms{ctx.MinMemoryMb}M",
            $"-Xmx{ctx.MaxMemoryMb}M",
            "-Dfile.encoding=UTF-8"
        };
        if (version.Arguments?.Jvm != null)
            jvm.AddRange(Expand(version.Arguments.Jvm, features, tokens));
        else
            jvm.Add("-Djava.library.path=" + ctx.NativesDirectory);

        if (!string.IsNullOrEmpty(ctx.LoggingConfigPath) && version.Logging?.Client?.Argument != null)
            jvm.Add(Substitute(version.Logging.Client.Argument, tokens));

        if (!jvm.Any(a => a.Contains("java.library.path")))
            jvm.Add("-Djava.library.path=" + ctx.NativesDirectory);

        if (!string.IsNullOrWhiteSpace(ctx.ExtraJvmArgs))
            jvm.AddRange(SplitArgs(ctx.ExtraJvmArgs));

        // ---- Game args ----
        var game = new List<string>();
        if (version.Arguments?.Game != null)
            game.AddRange(Expand(version.Arguments.Game, features, tokens));
        else if (!string.IsNullOrEmpty(version.MinecraftArguments))
        {
            game.AddRange(version.MinecraftArguments.Split(' ').Where(s => s.Length > 0).Select(s => Substitute(s, tokens)));
            if (ctx.Width > 0)
            {
                game.Add("--width"); game.Add(ctx.Width.ToString());
                game.Add("--height"); game.Add(ctx.Height.ToString());
            }
        }
        if (!string.IsNullOrWhiteSpace(ctx.ExtraGameArgs))
            game.AddRange(SplitArgs(ctx.ExtraGameArgs));

        if (string.IsNullOrEmpty(version.MainClass))
            throw new InvalidOperationException("Version JSON has no mainClass; cannot launch.");

        var args = new List<string>(jvm) { version.MainClass };
        args.AddRange(game);

        var spec = new LaunchSpec
        {
            JavaPath = ctx.JavaExecutable,
            Arguments = args,
            WorkingDirectory = ctx.GameDirectory,
            VersionName = ctx.VersionName,
            MinMemoryMb = ctx.MinMemoryMb,
            MaxMemoryMb = ctx.MaxMemoryMb
        };
        spec.DisplayCommandLine = RedactCommandLine(ctx.JavaExecutable, args);
        return spec;
    }

    private static string RedactCommandLine(string java, List<string> args)
    {
        var parts = new List<string> { QuoteIfNeeded(java) };
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--accessToken" && i + 1 < args.Count)
            {
                parts.Add("--accessToken");
                parts.Add("<REDACTED>");
                i++;
            }
            else
            {
                parts.Add(QuoteIfNeeded(args[i]));
            }
        }
        return string.Join(" ", parts);
    }

    private static string QuoteIfNeeded(string s) => s.Contains(' ') || s.Contains(';') ? $"\"{s}\"" : s;

    // ---------- helpers ----------

    private static List<string> Expand(List<JsonElement> elements, Dictionary<string, bool> features, Dictionary<string, string> tokens)
    {
        var outList = new List<string>();
        foreach (var el in elements)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                outList.Add(Substitute(el.GetString()!, tokens));
                continue;
            }
            if (el.ValueKind == JsonValueKind.Object)
            {
                var node = JsonHelper.Deserialize<ArgumentNode>(el.GetRawText());
                if (node.Rules != null && !RuleAllows(node.Rules, features)) continue;
                var v = node.Value;
                if (v.ValueKind == JsonValueKind.String)
                    outList.Add(Substitute(v.GetString()!, tokens));
                else if (v.ValueKind == JsonValueKind.Array)
                    foreach (var item in v.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            outList.Add(Substitute(item.GetString()!, tokens));
            }
        }
        return outList;
    }

    private static bool RuleAllows(List<Rule> rules, Dictionary<string, bool> features)
    {
        bool? result = null;
        foreach (var rule in rules)
        {
            if (RuleMatches(rule, features))
                result = rule.Action == "allow";
        }
        // No matching rule means the gating condition is not satisfied -> exclude.
        return result ?? false;
    }

    public static bool IsAllowed(List<Rule>? rules)
    {
        if (rules == null || rules.Count == 0) return true;
        bool? result = null;
        foreach (var rule in rules)
        {
            if (RuleMatches(rule, Features))
                result = rule.Action == "allow";
        }
        return result ?? false;
    }

    private static bool RuleMatches(Rule rule, Dictionary<string, bool> features)
    {
        if (rule.Os != null)
        {
            if (!string.IsNullOrEmpty(rule.Os.Name) && !string.Equals(rule.Os.Name, Os.Name, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(rule.Os.Arch) && !string.Equals(rule.Os.Arch, Os.Arch, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(rule.Os.Version) && !System.Text.RegularExpressions.Regex.IsMatch(Os.Version, rule.Os.Version))
                return false;
        }
        if (rule.Features != null)
        {
            foreach (var kv in rule.Features)
            {
                var actual = features.TryGetValue(kv.Key, out var f) ? f : false;
                if (actual != kv.Value) return false;
            }
        }
        return true;
    }

    private static string? GetArtifactPath(Library lib, string librariesRoot)
    {
        if (lib.Downloads?.Artifact?.Path != null)
            return Path.Combine(librariesRoot, lib.Downloads.Artifact.Path);
        return Path.Combine(librariesRoot, PathEx.MavenNameToPath(lib.Name));
    }

    private static string Substitute(string s, Dictionary<string, string> tokens)
    {
        foreach (var kv in tokens)
            if (s.Contains("${" + kv.Key + "}"))
                s = s.Replace("${" + kv.Key + "}", kv.Value);
        return s;
    }

    private static List<string> SplitArgs(string s) =>
        s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static (string Name, string Version, string Arch) DetectOs()
    {
        var name = "windows";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };
        var ver = Environment.OSVersion.Version.ToString();
        return (name, ver, arch);
    }
}

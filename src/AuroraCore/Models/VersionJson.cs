using System.Text.Json;

namespace AuroraCore.Models;

/// <summary>
/// Complete Minecraft version JSON model (piston-meta schema), extended with
/// Forge/NeoForge fields (processors, data). After inheritance resolution the
/// object is fully populated and consumed directly by the launch builder.
/// </summary>
public class VersionJson
{
    public string? Id { get; set; }
    public string? InheritsFrom { get; set; }
    public string? Type { get; set; }
    public string? MainClass { get; set; }
    public string? Assets { get; set; }                 // legacy asset index id
    public AssetIndexInfo? AssetIndex { get; set; }
    public JavaVersion? JavaVersion { get; set; }
    public int MinimumLauncherVersion { get; set; }
    public int ComplianceLevel { get; set; }
    public string? MinecraftArguments { get; set; }      // legacy (pre-1.13) single string
    public ArgumentSection? Arguments { get; set; }
    public List<Library> Libraries { get; set; } = new();
    public DownloadInfo? Downloads { get; set; }
    public LoggingInfo? Logging { get; set; }
    public List<Processor>? Processors { get; set; }
    public Dictionary<string, JsonElement>? Data { get; set; }
    public string? Time { get; set; }
    public string? ReleaseTime { get; set; }
}

public class AssetIndexInfo
{
    public string? Id { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
    public string? Url { get; set; }
}

public class JavaVersion
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public class Library
{
    public string Name { get; set; } = "";
    public List<Rule>? Rules { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public ExtractRule? Extract { get; set; }
    public LibraryDownloads? Downloads { get; set; }
    public string? Url { get; set; }        // custom Maven repository base
    public string? Sha1 { get; set; }       // loader-profile libraries carry their own hash
    public string? McMaven { get; set; }    // legacy Forge field (ignored)
}

public class LibraryDownloads
{
    public ArtifactInfo? Artifact { get; set; }
    public Dictionary<string, ArtifactInfo>? Classifiers { get; set; }
}

public class ArtifactInfo
{
    public string? Path { get; set; }
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
}

public class ExtractRule
{
    public List<string>? Exclude { get; set; }
}

public class DownloadInfo
{
    public ArtifactInfo? Client { get; set; }
    public ArtifactInfo? Server { get; set; }
    public ArtifactInfo? ClientMappings { get; set; }
    public ArtifactInfo? ServerMappings { get; set; }
}

public class LoggingInfo
{
    public LoggingClient? Client { get; set; }
}

public class LoggingClient
{
    public string? Argument { get; set; }
    public ArtifactInfo? File { get; set; }
    public string? Type { get; set; }
}

public class Rule
{
    public string Action { get; set; } = "allow"; // allow | disallow
    public OsCondition? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public class OsCondition
{
    public string? Name { get; set; }   // windows | osx | linux
    public string? Version { get; set; } // regex over OS version
    public string? Arch { get; set; }   // x86 | x64 | arm64
}

public class Processor
{
    public string? Jar { get; set; }
    public List<string>? Classpath { get; set; }
    public string? MainClass { get; set; }
    public List<string>? Args { get; set; }
}

/// <summary>arguments.game / arguments.jvm — arrays of string | rule-object.</summary>
public class ArgumentSection
{
    public List<JsonElement>? Game { get; set; }
    public List<JsonElement>? Jvm { get; set; }
}

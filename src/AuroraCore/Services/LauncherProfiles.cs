using System.Text.Json;

namespace AuroraCore.Services;

/// <summary>
/// Round-94: the Forge / NeoForge official installers refuse to run unless the
/// target directory looks like a Minecraft installation — concretely, they want
/// a <c>launcher_profiles.json</c>:
///
/// <code>
/// There is no Minecraft launcher profile in "…\AuroraLauncher",
/// you need to run the launcher first!
/// </code>
///
/// Our data root never had one (we keep instances in our own metadata), so
/// every Forge/NeoForge install died with exit code 1 — reported to the user as
/// "加载器安装失败".
///
/// The file we write is deliberately minimal but structurally valid, and it is
/// only ever created when it is missing or unusable: a real Mojang launcher's
/// file (or one the user edited) is left alone.
/// </summary>
public static class LauncherProfiles
{
    public const string FileName = "launcher_profiles.json";

    public readonly record struct EnsureResult(bool Created, bool Repaired, string Path)
    {
        public bool Changed => Created || Repaired;
    }

    /// <summary>Creates (or repairs) the file if needed. Never throws: when the
    /// data directory is read-only the installer will report the problem itself,
    /// which is better than failing the whole install from here.</summary>
    public static EnsureResult Ensure(string gameRoot)
    {
        var path = System.IO.Path.Combine(gameRoot, FileName);
        try
        {
            Directory.CreateDirectory(gameRoot);
            var existed = File.Exists(path);
            if (existed)
            {
                string text;
                try { text = File.ReadAllText(path); } catch { text = ""; }
                if (HasProfiles(text)) return new EnsureResult(false, false, path);

                // Present but unusable (empty file, `{}`, truncated write):
                // keep a copy before replacing it — someone may have cared.
                try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
            }

            var tmp = path + ".aurora-tmp";
            File.WriteAllText(tmp, Minimal());
            File.Move(tmp, path, overwrite: true);
            return new EnsureResult(!existed, existed, path);
        }
        catch
        {
            return new EnsureResult(false, false, path);
        }
    }

    /// <summary>True when the JSON has a non-empty <c>profiles</c> object, which
    /// is the only part the installer actually checks.</summary>
    public static bool HasProfiles(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("profiles", out var p) &&
                   p.ValueKind == JsonValueKind.Object &&
                   p.EnumerateObject().Any();
        }
        catch { return false; }
    }

    /// <summary>A minimal but official-shaped profile file. The installer needs
    /// <c>profiles</c> + <c>selectedProfile</c>; <c>clientToken</c> /
    /// <c>launcherVersion</c> keep other tooling from rejecting the file.</summary>
    public static string Minimal()
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return
            "{\n" +
            "  \"profiles\": {\n" +
            "    \"(Default)\": {\n" +
            "      \"name\": \"(Default)\",\n" +
            "      \"type\": \"latest-release\",\n" +
            $"      \"created\": \"{now}\",\n" +
            $"      \"lastUsed\": \"{now}\",\n" +
            "      \"icon\": \"Furnace_18x18\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"selectedProfile\": \"(Default)\",\n" +
            "  \"clientToken\": \"AuroraLauncher\",\n" +
            "  \"authenticationDatabase\": {},\n" +
            "  \"launcherVersion\": { \"name\": \"2.1.1\", \"format\": 21 }\n" +
            "}\n";
    }

    /// <summary>Round-94: installer output is a wall of maven noise; when we can
    /// recognise the one line that matters, say what to do instead of pasting it.</summary>
    public static string? Explain(string installerTail)
    {
        if (string.IsNullOrEmpty(installerTail)) return null;
        if (installerTail.Contains("no Minecraft launcher profile", StringComparison.OrdinalIgnoreCase))
            return "安装器认为目标目录不是合法的 Minecraft 目录（缺少 launcher_profiles.json）。" +
                   "启动器会自动补这个文件；如果反复出现，请检查数据目录是否被安全软件限制了写入。";
        if (installerTail.Contains("requires Java", StringComparison.OrdinalIgnoreCase) ||
            installerTail.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase))
            return "安装器用的 Java 版本不对。请安装对应版本的 Java（1.16 及以下用 8，1.17–1.20.4 用 17，1.20.5+ 用 21），" +
                   "并在设置里指定它。";
        // Round-95: "These libraries failed to download. Try again. <coords>"
        // is the most common Forge failure on a flaky network. Name the exact
        // artifacts and say what the launcher already tried, instead of
        // dumping six lines of maven noise at the user.
        var dependencyFailure = InstallerDependencies.ExplainDependencyFailure(installerTail);
        if (dependencyFailure != null) return dependencyFailure;

        if (installerTail.Contains("Could not download", StringComparison.OrdinalIgnoreCase) ||
            installerTail.Contains("UnknownHostException", StringComparison.OrdinalIgnoreCase))
            return "安装器在下载它自己的依赖时断网了。检查网络/代理后重试（安装器不经过启动器的镜像线路）。";
        return null;
    }
}

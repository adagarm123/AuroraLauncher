using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Starts the built Java process, streams stdout/stderr line-by-line to the
/// caller (for live logs + crash analysis) and reports the exit code. The
/// process can be cancelled (killed) via the cancellation token.
/// </summary>
public sealed class GameLauncher
{
    private readonly Logger _log = Logger.Instance;

    public async Task<int> LaunchAsync(LaunchSpec spec, Action<string>? onLog = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.JavaPath,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);

        _log.Info("Launcher", $"Launching: {spec.DisplayCommandLine}");

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLog?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLog?.Invoke(e.Data); };
        p.Exited += (_, _) =>
        {
            try { tcs.TrySetResult(p.ExitCode); } catch { tcs.TrySetResult(-1); }
        };

        if (!p.Start())
            throw new InvalidOperationException("Failed to start the Java process.");

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }))
        {
            var exit = await tcs.Task.ConfigureAwait(false);
            _log.Info("Launcher", $"Game process exited with code {exit}.");
            return exit;
        }
    }
}

/// <summary>Best-effort crash / failure detection from captured game logs.
/// Rules are ordered: specific loader/mod errors first, generic JVM errors
/// last (first match wins).</summary>
public static class CrashAnalyzer
{
    /// <summary>One triage rule. Adding a rule = adding one entry here
    /// (structure ready for external rule packs if the list grows past ~15).
    /// Matching is CASE-INSENSITIVE — JVM/mods sometimes emit lowercase
    /// variants ("unsupported class version error").</summary>
    public sealed record CrashRule(string Pattern, string Title, string Suggestion);

    private static readonly CrashRule[] Rules =
    {
        // ---- loader / mod specific precise rules (before generic ones) ----
        new("UnsupportedClassVersionError", "Java 版本过低", "该版本需要更高版本的 Java（如 Java 17/21），请在设置中更换 Java"),
        new("Duplicate mods|Duplicate MODID|Duplicate mod id|重复的模组", "检测到重复 Mod", "请检查 mods 文件夹并删除重复项"),
        new("ModResolutionException|Could not find required mod|is missing, though it is required|Missing mods", "Mod 前置缺失或不兼容", "常见为缺少 Fabric API / Forge 前置，请检查 mods 文件夹"),
        // ---- JVM / environment ----
        new("Could not create the Java Virtual Machine", "JVM 无法创建", "内存或启动参数错误"),
        new("OutOfMemoryError", "Java 内存溢出", "建议在设置里调大最大内存"),
        new("Could not reserve enough space for object heap", "堆内存不足", "请降低分配内存"),
        new("The specified size exceeds the maximum representable size", "分配内存超过 JVM 上限", "请调低最大内存"),
        new("java\\.lang\\.NoClassDefFoundError", "缺少依赖类", "classpath 不完整"),
        new("java\\.lang\\.NoSuchMethodError", "依赖版本冲突", "库版本不匹配（NoSuchMethodError）"),
        new("Error: Could not find or load main class", "找不到主类", "客户端 JAR 或 classpath 错误"),
        new("java\\.net\\.UnknownHostException", "网络无法访问", "资源下载源不可达"),
        new("FileNotFoundException", "文件缺失", "资源/库未下载完整"),
        new("Permission denied", "文件权限被拒绝", "以管理员运行或检查杀毒软件拦截"),
        new("java\\.lang\\.UnsatisfiedLinkError", "原生库缺失或架构不匹配", "重新补全游戏文件后重试"),
        // ---- catch-alls (last) ----
        new("Fabric\\.loader\\.impl|fabric\\.mod\\.json", "Fabric 加载器报错", "详情见上方游戏输出"),
        new("Exception in thread \"main\"", "主线程抛出异常", "启动失败，详情见上方游戏输出"),
    };

    public static string? Analyze(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        foreach (var r in Rules)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, r.Pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return $"{r.Title}：{r.Suggestion}";
        }
        return null;
    }
}

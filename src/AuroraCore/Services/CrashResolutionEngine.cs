using System.Text.RegularExpressions;
using AuroraCore.Models;

namespace AuroraCore.Services;

/// <summary>How a solution reaches the user. <c>Auto</c> = the launcher can
/// apply it itself (config-only or a download); <c>Guided</c> = we open the
/// right place and let the user decide; <c>Info</c> = explanation only.</summary>
public enum FixKind { Auto, Guided, Info }

/// <summary>
/// Every repair the crash card can offer. Deliberately CLOSED and small:
/// there is no "delete mods", "delete saves", "edit environment variables" or
/// "run as administrator permanently" member, because those are exactly the
/// actions a crash-repair engine must never take on the user's behalf.
/// <see cref="CrashFixSafety"/> enforces the rest (config-key allow-list).
/// </summary>
public enum FixAction
{
    None,
    RaiseMemory,
    LowerMemory,
    InstallJava,
    UseInstalledJava,
    InstallFabricApi,
    RepairFiles,
    ScanMods,
    OpenModsFolder,
    OpenShaderFolder,
    OpenSavesFolder,
    OpenGameFolder,
    OpenSettings,
    OpenAccounts,
    OpenGpuDriver,
    OpenHelpPage,
    ExportBundle,
    MailAuthor
}

/// <summary>One actionable diagnosis: what happened, why, and what to do.
/// <see cref="Payload"/> carries the parameter of the action ("6144" MB,
/// "21" Java major) so the executor never has to re-parse the log.</summary>
public sealed record CrashSolution(
    string RuleId,
    string Title,
    string Diagnosis,
    string Suggestion,
    FixKind Kind,
    FixAction Action,
    string ActionText,
    string? Payload = null)
{
    public bool HasAction => Action != FixAction.None;

    public string KindLabel => Kind switch
    {
        FixKind.Auto => "可一键修复",
        FixKind.Guided => "需要你确认",
        _ => "仅供了解"
    };
}

/// <summary>Facts the diagnosis needs, all captured on the UI thread before
/// the (background) analysis runs. Pure data — no service handles — so the
/// whole engine stays unit-testable.</summary>
public sealed record CrashContext
{
    public string InstanceName { get; init; } = "当前实例";
    public int ExitCode { get; init; }
    public int MaxMemoryMb { get; init; } = 4096;
    public int TotalPhysicalMb { get; init; }
    public ModLoaderType Loader { get; init; } = ModLoaderType.Vanilla;
    public string MinecraftVersion { get; init; } = "";
    public IReadOnlyList<int> InstalledJavaMajors { get; init; } = Array.Empty<int>();
    public int? CurrentJavaMajor { get; init; }

    public bool IsModded => Loader != ModLoaderType.Vanilla;
}

/// <summary>Result of <see cref="CrashResolutionEngine.Diagnose"/>.
/// <see cref="Signature"/> is a short fingerprint of the first exception line,
/// used in the "rare crash → mail the author" subject so two reports about the
/// same crash collide on the same string.</summary>
public sealed record CrashDiagnosis(IReadOnlyList<CrashSolution> Solutions, string Signature)
{
    public bool HasSolutions => Solutions.Count > 0;
    public int AutoFixable => Solutions.Count(s => s.Kind == FixKind.Auto && s.HasAction);
    public string Headline => HasSolutions
        ? Solutions[0].Title
        : "未匹配到已知崩溃原因";
}

/// <summary>
/// Round-78: local crash diagnosis + repair engine. Replaces the old
/// "translate a regex to one sentence" table with solutions that carry an
/// ACTION, so the crash card can offer a one-click fix instead of advice.
///
/// Design rules kept from the rest of the codebase:
///  - pure logic (no IO, no network, no UI) → every rule is unit-testable;
///  - rules are ordered specific → generic, and EVERY match is reported (a
///    modpack crash usually has two causes);
///  - the table is the single source of truth for <see cref="CrashAnalyzer"/>
///    too, so the old log line and the new card can never disagree.
/// </summary>
public static class CrashResolutionEngine
{
    private sealed record Rule(
        string Id,
        string Pattern,
        string Title,
        string Suggestion,
        FixKind Kind,
        FixAction Action,
        string ActionText);

    // Order = priority. Loader/mod-specific evidence first, JVM/environment
    // next, catch-alls last. Keep CrashAnalyzer's historical titles verbatim:
    // the old tests (and the log line shipped to users) depend on them.
    private static readonly Rule[] Rules =
    {
        // ---- Java runtime ----
        new("java_version_old",
            @"UnsupportedClassVersionError|Unsupported class file major version|class file version \d+\.\d+.*(?:not supported|unsupported)",
            "Java 版本过低",
            "该版本需要更高版本的 Java（如 Java 17/21），请在设置中更换 Java",
            FixKind.Auto, FixAction.InstallJava, "一键安装匹配的 Java"),
        new("out_of_memory",
            @"OutOfMemoryError|Java heap space|GC overhead limit exceeded|Metaspace",
            "Java 内存溢出",
            "建议在设置里调大最大内存",
            FixKind.Auto, FixAction.RaiseMemory, "一键调大内存"),
        new("heap_reserve",
            @"Could not reserve enough space for object heap|insufficient memory for the Java Runtime Environment",
            "堆内存不足",
            "请降低分配内存",
            FixKind.Auto, FixAction.LowerMemory, "一键降低内存"),
        new("heap_over_max",
            @"The specified size exceeds the maximum representable size|Invalid maximum heap size|Invalid initial heap size",
            "分配内存超过 JVM 上限",
            "请调低最大内存",
            FixKind.Auto, FixAction.LowerMemory, "一键降低内存"),
        new("jvm_create",
            @"Could not create the Java Virtual Machine",
            "JVM 无法创建",
            "内存或启动参数错误",
            FixKind.Auto, FixAction.LowerMemory, "一键降低内存"),

        // ---- mods ----
        new("fabric_api_missing",
            @"Could not find required mod:?\s*fabric-api|fabric-api.{0,30}(?:is missing|required|not (?:found|present))|requires fabric-api",
            "缺少 Fabric API 前置",
            "常见为缺少 Fabric API，请检查 mods 文件夹",
            FixKind.Auto, FixAction.InstallFabricApi, "一键下载 Fabric API"),
        new("mod_missing_dep",
            @"ModResolutionException|Could not find required mod|is missing, though it is required|Missing mods|Missing or unsupported mandatory dependencies|Missing dependencies",
            "Mod 前置缺失或不兼容",
            "常见为缺少 Fabric API / Forge 前置，请检查 mods 文件夹",
            FixKind.Auto, FixAction.ScanMods, "扫描缺失的前置"),
        new("duplicate_mods",
            @"Duplicate mods|Duplicate MODID|Duplicate mod id|duplicate mod file|重复的模组",
            "检测到重复 Mod",
            "请检查 mods 文件夹并删除重复项",
            FixKind.Guided, FixAction.OpenModsFolder, "打开 mods 文件夹"),
        new("mod_conflict",
            @"Conflicting mods|is incompatible with|declared .{0,40}incompatible|ModResolutionException.*conflict",
            "Mod 之间相互冲突",
            "两个模组声明了互不兼容，需要删掉其中一个",
            FixKind.Guided, FixAction.OpenModsFolder, "打开 mods 文件夹"),
        new("mixin_failed",
            @"Mixin apply failed|MixinApplyError|MixinTransformerError|org\.spongepowered\.asm\.mixin\.(?:MixinException|transform)",
            "Mixin 注入失败（模组不兼容）",
            "通常是某个模组与当前游戏/加载器版本不匹配",
            FixKind.Guided, FixAction.OpenModsFolder, "打开 mods 文件夹"),
        new("wrong_loader_side",
            @"Attempted to load class .{0,60}for invalid side|client-only mod|dedicated server only",
            "客户端 / 服务端专用模组混装",
            "只装客户端模组，移除仅服务端可用的模组",
            FixKind.Guided, FixAction.OpenModsFolder, "打开 mods 文件夹"),
        new("no_class_def",
            @"NoClassDefFoundError|ClassNotFoundException",
            "缺少依赖类",
            "classpath 不完整，通常是模组前置没装全",
            FixKind.Auto, FixAction.ScanMods, "扫描缺失的前置"),
        new("no_such_method",
            @"NoSuchMethodError|NoSuchFieldError|AbstractMethodError|IncompatibleClassChangeError",
            "依赖版本冲突",
            "库版本不匹配（NoSuchMethodError），某个模组不是给这个版本编译的",
            FixKind.Guided, FixAction.OpenModsFolder, "打开 mods 文件夹"),

        // ---- graphics ----
        new("gl_error",
            @"GL_INVALID|OpenGL error|Failed to create window|GLFW error|glfwCreateWindow|Pixel format not accelerated",
            "显卡 / OpenGL 报错",
            "光影或图形驱动不兼容；先移除光影再试",
            FixKind.Guided, FixAction.OpenShaderFolder, "打开光影文件夹"),
        new("gpu_driver",
            @"The driver does not appear to support OpenGL|WGL_|nvd3dum|atio6axx|igdumdim|Display driver .{0,20}(?:stopped|crash)",
            "显卡驱动过旧或不支持",
            "请更新显卡驱动后重试",
            FixKind.Guided, FixAction.OpenGpuDriver, "打开显卡驱动下载页"),

        // ---- account / files / network ----
        new("invalid_session",
            @"Invalid session|Failed to verify username|AuthenticationException|Invalid credentials|401 Unauthorized|User not authenticated",
            "登录状态失效",
            "请重新登录微软账户（或改用离线账户）",
            FixKind.Guided, FixAction.OpenAccounts, "前往账户设置"),
        new("access_denied",
            @"AccessDeniedException|Permission denied|Access is denied|拒绝访问",
            "文件权限被拒绝",
            "以管理员运行或检查杀毒软件拦截",
            FixKind.Guided, FixAction.OpenHelpPage, "查看权限排查步骤"),
        new("native_link",
            @"UnsatisfiedLinkError|no .{0,40} in java\.library\.path|%1 is not a valid Win32 application",
            "原生库缺失或架构不匹配",
            "重新补全游戏文件后重试",
            FixKind.Auto, FixAction.RepairFiles, "一键补全游戏文件"),
        new("corrupt_jar",
            @"Invalid or corrupt jarfile|zip END header not found|ZipException|invalid LOC header|error in opening zip file",
            "游戏文件损坏",
            "下载不完整或文件被杀软改坏，补全文件即可",
            FixKind.Auto, FixAction.RepairFiles, "一键补全游戏文件"),
        new("file_missing",
            @"FileNotFoundException|NoSuchFileException|The system cannot find the file specified|找不到指定的文件",
            "文件缺失",
            "资源/库未下载完整",
            FixKind.Auto, FixAction.RepairFiles, "一键补全游戏文件"),
        new("file_locked",
            @"being used by another process|另一个程序正在使用此文件",
            "文件被占用",
            "关闭其他启动器 / 游戏进程后重试",
            FixKind.Guided, FixAction.OpenHelpPage, "查看排查步骤"),
        new("network_unreachable",
            @"UnknownHostException|ConnectException|SocketTimeoutException|Network is unreachable|Connection refused|SSLHandshakeException",
            "网络无法访问",
            "资源下载源不可达，检查网络或换下载源",
            FixKind.Guided, FixAction.OpenSettings, "前往网络设置"),
        new("world_corrupt",
            @"Failed to load (?:world|level)|Chunk file at|Region file .{0,40}(?:corrupt|invalid)|world .{0,20}corrupt",
            "存档 / 区块损坏",
            "先用备份恢复存档，或把该存档移出 saves 目录",
            FixKind.Guided, FixAction.OpenSavesFolder, "打开 saves 文件夹"),
        new("disk_full",
            @"not enough space on the disk|There is not enough space|磁盘空间不足",
            "磁盘空间不足",
            "清理磁盘后重试",
            FixKind.Guided, FixAction.OpenGameFolder, "打开实例目录"),
        new("main_class_missing",
            @"Could not find or load main class|Error: Main method not found",
            "找不到主类",
            "客户端 JAR 或 classpath 错误，补全文件即可",
            FixKind.Auto, FixAction.RepairFiles, "一键补全游戏文件"),

        // ---- loader catch-alls (last) ----
        new("loader_generic",
            @"Fabric\.loader\.impl|fabric\.mod\.json|net\.minecraftforge|cpw\.mods",
            "Fabric 加载器报错",
            "详情见上方游戏输出",
            FixKind.Guided, FixAction.OpenModsFolder, "打开 mods 文件夹"),
        new("exception_main",
            @"Exception in thread ""main""",
            "主线程抛出异常",
            "启动失败，详情见上方游戏输出",
            FixKind.Guided, FixAction.MailAuthor, "发送给作者")
    };

    /// <summary>Legacy projection for <see cref="CrashAnalyzer"/>
    /// (Pattern / Title / Suggestion only) — one table, two consumers.</summary>
    public static IReadOnlyList<(string Pattern, string Title, string Suggestion)> LegacyRules =>
        Rules.Select(r => (r.Pattern, r.Title, r.Suggestion)).ToList();

    public static int RuleCount => Rules.Length;

    /// <summary>
    /// Full diagnosis: every matching rule becomes a solution with a concrete
    /// action; when nothing matches we return a signature + the "send it to the
    /// author" path instead of a dead end.
    /// </summary>
    public static CrashDiagnosis Diagnose(string? logText, CrashContext ctx)
    {
        var text = logText ?? "";
        ctx ??= new CrashContext();
        var solutions = new List<CrashSolution>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in Rules)
        {
            Match m;
            try { m = Regex.Match(text, rule.Pattern, RegexOptions.IgnoreCase); }
            catch { continue; }
            if (!m.Success) continue;
            if (!seen.Add(rule.Id)) continue;
            solutions.Add(BuildSolution(rule, m, text, ctx));
        }

        return new CrashDiagnosis(solutions, BuildSignature(text));
    }

    /// <summary>Short fingerprint of the first interesting exception line —
    /// used in the mail subject so identical crashes sort together.</summary>
    public static string BuildSignature(string? logText)
    {
        var text = logText ?? "";
        var line = text.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("Caused by", StringComparison.OrdinalIgnoreCase));
        if (line == null) return "unknown";
        if (line.Length > 100) line = line[..100];
        // Stable 8-char FNV-ish hash (deterministic across runs, unlike GetHashCode).
        unchecked
        {
            ulong h = 1469598103934665603UL;
            foreach (var c in line) { h ^= c; h *= 1099511628211UL; }
            return $"{h:x8}";
        }
    }

    // ---- diagnostics text -------------------------------------------------

    private sealed record Refined(string Diagnosis, string? Payload, string ActionText, FixAction Action, FixKind Kind);

    private static CrashSolution BuildSolution(Rule rule, Match m, string text, CrashContext ctx)
    {
        var r = rule.Id switch
        {
            "java_version_old" => DescribeJavaVersion(m, text, ctx),
            "out_of_memory" => DescribeRaiseMemory(ctx),
            "heap_reserve" or "heap_over_max" or "jvm_create" => DescribeLowerMemory(ctx, rule.Id),
            _ => new Refined(rule.Suggestion, null, rule.ActionText, rule.Action, rule.Kind)
        };

        // A refinement may downgrade an auto-fix to "go do it yourself" when
        // the safe automatic action would not actually help.
        return new CrashSolution(rule.Id, rule.Title, r.Diagnosis, rule.Suggestion,
            r.Kind, r.Action, r.ActionText, r.Payload);
    }

    private static Refined DescribeJavaVersion(Match m, string text, CrashContext ctx)
    {
        var build = Regex.Match(text, @"class file version\s+(\d+)", RegexOptions.IgnoreCase);
        var majorVersion = Regex.Match(text, @"major version\s+(\d+)", RegexOptions.IgnoreCase);
        var raw = build.Success ? int.Parse(build.Groups[1].Value)
                : majorVersion.Success ? int.Parse(majorVersion.Groups[1].Value) : 0;
        var required = JavaMajorForClassFile(raw);
        if (required <= 0) required = 21;

        var current = ctx.CurrentJavaMajor is { } cur ? $"Java {cur}" : "未知版本的 Java";
        var best = ctx.InstalledJavaMajors.Where(j => j >= required).OrderBy(j => j).FirstOrDefault();
        var target = best > 0 ? $"Java {best}" : $"Java {required}";

        var diagnosis = $"这段日志说明游戏要求更高的 Java：当前用的是{current}，" +
                        $"而游戏（或某个模组）需要 Java {required} 或更高。";

        if (best > 0)
            return new Refined(diagnosis + $"你本机已经装有 Java {best}，直接切过去就行，不用重新下载。",
                best.ToString(), $"改用已安装的 Java {best}", FixAction.UseInstalledJava, FixKind.Auto);

        return new Refined(diagnosis +
                           $"本机没有找到 Java {required}+，可以一键安装 Temurin JDK {required}（装在启动器自己的 java 目录里，不动系统环境变量）。",
            required.ToString(), $"一键安装 Java {required}", FixAction.InstallJava, FixKind.Auto);
    }

    private static Refined DescribeRaiseMemory(CrashContext ctx)
    {
        var modded = ctx.IsModded;
        var (target, useful) = SuggestHigherMemory(ctx.TotalPhysicalMb, ctx.MaxMemoryMb, modded);
        var physical = ctx.TotalPhysicalMb > 0 ? $"本机物理内存约 {ctx.TotalPhysicalMb} MB" : "未能读到物理内存";

        if (!useful)
            return new Refined(
                $"内存确实不够用了，但当前已经是 {ctx.MaxMemoryMb} MB，再往上加会挤爆系统内存（{physical}）。" +
                "建议关掉浏览器等占用内存的程序，或把渲染距离、视距调低一些。",
                null, "去设置调整内存", FixAction.OpenSettings, FixKind.Guided);

        var extra = target - ctx.MaxMemoryMb;
        return new Refined(
            $"游戏运行中把 Java 堆用完了（当前上限 {ctx.MaxMemoryMb} MB）。{physical}，" +
            $"把上限提到 {target} MB 一般就能撑住（增加 {extra} MB，仍在安全范围内）。",
            target.ToString(), $"把内存提到 {target} MB", FixAction.RaiseMemory, FixKind.Auto);
    }

    private static Refined DescribeLowerMemory(CrashContext ctx, string ruleId)
    {
        var (target, useful) = SuggestLowerMemory(ctx.TotalPhysicalMb, ctx.MaxMemoryMb);
        var reason = ruleId switch
        {
            "heap_over_max" => $"分配的内存（{ctx.MaxMemoryMb} MB）超过了这台机器/JVM 能表示的上限。",
            "jvm_create" => "JVM 根本没起来，最常见的原因是分配的内存超过了系统可给的空间，或启动参数里的 -Xmx 写错了。",
            _ => $"系统无法为 Java 预留 {ctx.MaxMemoryMb} MB 连续内存，说明分配得太大了。"
        };

        if (!useful)
            return new Refined(reason + "请把内存调到 1024–2048 MB 后重试。",
                "2048", "把内存降到 2048 MB", FixAction.LowerMemory, FixKind.Auto);

        return new Refined(reason + $"降到 {target} MB 通常就能启动（这是配置修改，随时可以在设置里改回去）。",
            target.ToString(), $"把内存降到 {target} MB", FixAction.LowerMemory, FixKind.Auto);
    }

    // ---- pure memory math -------------------------------------------------

    /// <summary>Class-file version → Java major (65 → 21). 0 when unusable.</summary>
    public static int JavaMajorForClassFile(int classFileVersion)
        => classFileVersion >= 45 ? classFileVersion - 44 : 0;

    /// <summary>How much headroom we allow the game: 55% of physical RAM,
    /// capped at 16 GB. Never suggests eating the OS alive.
    ///
    /// Round-83: the old lower clamp of 1536 MB could exceed 55% on a 2 GB
    /// machine (1536/2048 = 75%) — i.e. the "repair" could push the system into
    /// swap, which users then experience as the whole PC freezing. The floor is
    /// now 512 MB so this function can never break the 60% rule the guard test
    /// sweeps for.</summary>
    public static int MemoryCeiling(int totalPhysicalMb)
    {
        if (totalPhysicalMb <= 0) return 8192;
        var cap = (int)(totalPhysicalMb * MemoryAllocationPolicy.PhysicalShare);
        return Math.Clamp(cap, 512, MemoryAllocationPolicy.HardCapMb);
    }

    /// <summary>Next sensible step up from the current -Xmx. `Useful=false`
    /// means the machine is already at its ceiling — raising it would only
    /// move the crash to the OS.</summary>
    public static (int Target, bool Useful) SuggestHigherMemory(int totalPhysicalMb, int currentMaxMb, bool modded)
    {
        var ladder = modded
            ? new[] { 3072, 4096, 6144, 8192, 12288, 16384 }
            : new[] { 2048, 3072, 4096, 6144, 8192, 12288 };
        var cap = MemoryCeiling(totalPhysicalMb);
        foreach (var mb in ladder)
            if (mb > currentMaxMb && mb <= cap) return (mb, true);
        return (currentMaxMb, false);
    }

    /// <summary>Step down for "heap too big" errors — never below 1024 MB and
    /// never below what the OS can plausibly give.</summary>
    public static (int Target, bool Useful) SuggestLowerMemory(int totalPhysicalMb, int currentMaxMb)
    {
        var target = Math.Max(1024, currentMaxMb / 2);
        if (totalPhysicalMb > 0)
        {
            var half = (int)(totalPhysicalMb * 0.5);
            if (half >= 1024) target = Math.Min(target, Math.Max(1024, half));
        }
        target = (target / 512) * 512;
        if (target >= currentMaxMb) target = Math.Max(1024, currentMaxMb - 512);
        return (target, target < currentMaxMb);
    }
}

/// <summary>
/// Round-78 guard rail: an automatic repair may ONLY touch the launcher's own
/// settings (memory / java path) or download a missing file into the instance.
/// Anything that would delete, move or edit USER data is not expressible —
/// and this table makes that property testable instead of a comment.
/// </summary>
public static class CrashFixSafety
{
    /// <summary>Repairs the launcher may apply on its own, no confirmation.</summary>
    public static readonly IReadOnlySet<FixAction> AutoAllowed = new HashSet<FixAction>
    {
        FixAction.RaiseMemory,
        FixAction.LowerMemory,
        FixAction.InstallJava,
        FixAction.UseInstalledJava,
        FixAction.InstallFabricApi,
        FixAction.RepairFiles,
        FixAction.ScanMods
    };

    private static readonly string[] MemoryKeys = { "MaxMemoryMb", "MinMemoryMb" };
    private static readonly string[] JavaKeys = { "JavaPath" };

    /// <summary>Config keys an action is allowed to write. Empty = writes no
    /// config at all. Guard test asserts this is a subset of the
    /// launch-settings keys — i.e. never accounts, never paths of user data.</summary>
    public static IReadOnlyList<string> AffectedConfigKeys(FixAction action) => action switch
    {
        FixAction.RaiseMemory or FixAction.LowerMemory => MemoryKeys,
        FixAction.InstallJava or FixAction.UseInstalledJava => JavaKeys,
        _ => Array.Empty<string>()
    };

    /// <summary>Read-only / download-only actions: no user file is modified.
    /// Listed explicitly (not derived) so adding a destructive action later
    /// forces a conscious decision here — and the self-test checks every
    /// member of the enum.</summary>
    public static bool IsNonDestructive(FixAction action) => action switch
    {
        FixAction.None => true,
        FixAction.OpenModsFolder or FixAction.OpenShaderFolder or FixAction.OpenSavesFolder
            or FixAction.OpenGameFolder or FixAction.OpenSettings or FixAction.OpenAccounts
            or FixAction.OpenGpuDriver or FixAction.OpenHelpPage => true,
        FixAction.ExportBundle or FixAction.MailAuthor or FixAction.ScanMods => true,
        FixAction.InstallFabricApi or FixAction.RepairFiles => true,
        _ => AffectedConfigKeys(action).Count > 0
    };

    public static bool CanAutoApply(FixAction action) => AutoAllowed.Contains(action);

    /// <summary>Human sentence for the log — what we are about to touch.</summary>
    public static string Describe(FixAction action) => action switch
    {
        FixAction.RaiseMemory => "修改启动器设置：最大堆内存（只改 config.json）",
        FixAction.LowerMemory => "修改启动器设置：最大堆内存（只改 config.json）",
        FixAction.InstallJava => "下载并解压 JDK 到启动器自己的 java 目录",
        FixAction.UseInstalledJava => "修改启动器设置：默认 Java 路径",
        FixAction.InstallFabricApi => "向当前实例的 mods 目录新增 Fabric API",
        FixAction.RepairFiles => "校验并补全缺失/损坏的游戏文件",
        FixAction.ScanMods => "只读扫描 mods 目录，不改任何文件",
        FixAction.OpenModsFolder or FixAction.OpenShaderFolder or FixAction.OpenSavesFolder
            or FixAction.OpenGameFolder => "只打开资源管理器，不改任何文件",
        FixAction.OpenSettings or FixAction.OpenAccounts => "只切换页面",
        FixAction.OpenGpuDriver or FixAction.OpenHelpPage => "只打开网页",
        FixAction.ExportBundle => "导出脱敏诊断包",
        FixAction.MailAuthor => "唤起系统邮件客户端（由你亲手发送）",
        _ => "仅提示，不改动任何内容"
    };
}

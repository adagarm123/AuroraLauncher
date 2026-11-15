using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AuroraCore.Services;

/// <summary>FPS readings found in the captured game output. Most vanilla /
/// Fabric clients never log FPS, so <see cref="Samples"/> is usually 0 — the
/// dashboard says "日志里没有 FPS 数据" instead of inventing a number.</summary>
public sealed record FpsSummary(int Average, int Peak, int Samples)
{
    public static readonly FpsSummary None = new(0, 0, 0);
    public bool HasData => Samples > 0;
}

/// <summary>
/// Round-79: the performance dashboard's decision layer. Three inputs only:
/// the peak working set WE sampled from the game process, the -Xmx the instance
/// ran with, and whatever FPS lines happened to be in the log. All pure —
/// no process handles, no IO — so the wording and thresholds are testable.
/// </summary>
public static class PerformanceReport
{
    // "fps: 60" / "FPS = 144" / "60 fps" — the shapes mods and crash reports use.
    private static readonly Regex[] FpsPatterns =
    {
        new(@"(?i)\bfps\s*[:=]\s*(\d{1,4})\b", RegexOptions.Compiled),
        new(@"(?i)\b(\d{1,4})\s*fps\b", RegexOptions.Compiled)
    };

    /// <summary>Average/peak across every FPS line in the log buffer. Values
    /// outside 1..1000 are ignored (a mod printing "fps: 0" at startup is not a
    /// measurement).</summary>
    public static FpsSummary ParseFps(IEnumerable<string>? lines)
    {
        if (lines == null) return FpsSummary.None;
        var values = new List<int>();
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.Length > 400) continue;
            // Prefer the explicit "fps: N" shape; only fall back to "N fps" when
            // the line has no explicit label (otherwise "60 fps" would be counted twice).
            var pattern = FpsPatterns[0];
            if (!pattern.IsMatch(line)) pattern = FpsPatterns[1];
            foreach (Match m in pattern.Matches(line))
            {
                if (!int.TryParse(m.Groups[1].Value, out var fps)) continue;
                if (fps is > 0 and <= 1000) values.Add(fps);
            }
        }
        if (values.Count == 0) return FpsSummary.None;
        return new FpsSummary((int)Math.Round(values.Average()), values.Max(), values.Count);
    }

    /// <summary>One-line verdict + severity ("健康" / "吃紧" / "偏低" / "无数据").
    /// The memory check wins: OOM is a hard failure, low FPS is a preference.
    /// Kept for callers that only know the memory number (tests, old data).</summary>
    public static (string Level, string Text) Verdict(int peakMemoryMb, int maxMemoryMb, FpsSummary fps)
        => RunVerdict(0, 0, peakMemoryMb, maxMemoryMb, fps);

    /// <summary>Round-92: the verdict now knows HOW the run ended. The old one
    /// only compared peak memory against -Xmx, so a process that died after two
    /// seconds with 2 MB of working set was proudly labelled「健康」— the single
    /// most misleading thing this dashboard could say.
    ///
    /// Order matters: a crash outranks every performance observation (there is
    /// nothing to judge), and a run too short to measure outranks "no data".</summary>
    public static (string Level, string Text) RunVerdict(int exitCode, long runSeconds,
        int peakMemoryMb, int maxMemoryMb, FpsSummary fps)
    {
        if (exitCode != 0)
        {
            var tail = runSeconds > 0
                ? $"（运行 {Models.GameInstance.FormatPlaytime(runSeconds)}后退出）"
                : "";
            return ("异常退出",
                $"上次运行异常结束{tail}，退出码 {exitCode}。这不是性能问题 —— " +
                "点「诊断」看报错原文，或打开「日志」页看最后几十行。");
        }

        // A JVM cannot finish loading in under ~3 s; anything shorter never got
        // to the main menu and must not be reported as a healthy session.
        if (runSeconds is > 0 and <= 3)
            return ("闪退",
                $"进程只活了 {runSeconds} 秒就退出了。多半是加载器或核心依赖文件损坏 —— " +
                "在「实例管理」里点「补全文件」，或换一个 Java 版本再试。");

        if (peakMemoryMb <= 0)
            return ("无数据", "还没有运行记录：启动一次游戏后，这里会显示那次运行的峰值内存。");

        // Under 10 MB is not a JVM's working set (the runtime alone needs more),
        // so the sampler missed the run. Saying "2 MB, 运行平稳" is a lie.
        if (peakMemoryMb < 10)
            return ("未采集到",
                $"这次运行 {Models.GameInstance.FormatPlaytime(runSeconds)}，但没采到内存读数" +
                "（进程太短，或采样被系统挂起）。这次运行的性能结论不可用。");

        if (maxMemoryMb > 0 && peakMemoryMb >= maxMemoryMb * 0.92)
            return ("吃紧",
                $"峰值内存 {peakMemoryMb} MB，已经贴到上限 {maxMemoryMb} MB。大场景或传送时很容易 OutOfMemoryError，" +
                "建议在设置里把最大内存调高一档。");

        if (fps.HasData && fps.Average < 30)
            return ("偏低",
                $"平均 {fps.Average} FPS（{fps.Samples} 次采样，最高 {fps.Peak}）。" +
                "可以试试降低渲染距离、换轻量光影，或给这个实例多分一点内存。");

        var fpsPart = fps.HasData ? $"，平均 {fps.Average} FPS" : "";
        return ("健康", $"峰值内存 {peakMemoryMb} MB（上限 {maxMemoryMb} MB）{fpsPart}，运行平稳。");
    }

    /// <summary>"42 分钟" style duration for the dashboard line.</summary>
    public static string FormatDuration(TimeSpan span)
        => AuroraCore.Models.GameInstance.FormatPlaytime((long)Math.Max(0, span.TotalSeconds));

    public static string FormatMb(long bytes) => (bytes / 1024 / 1024).ToString("N0") + " MB";
}

/// <summary>One CPU load calculation from two <c>GetSystemTimes</c> samples.
/// Pure math, unit-testable: Windows reports kernel time INCLUDING idle, which
/// is the classic way to get this wrong.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User)
{
    public static CpuTimes Zero => new(0, 0, 0);
    public bool IsUsable => Kernel > 0 || User > 0 || Idle > 0;
}

public static class CpuLoadMath
{
    /// <summary>Busy share in percent, 0 when the counters did not advance
    /// (first sample, or a machine that just resumed).</summary>
    public static double Percent(CpuTimes previous, CpuTimes current)
    {
        var idle = Delta(previous.Idle, current.Idle);
        var kernel = Delta(previous.Kernel, current.Kernel);
        var user = Delta(previous.User, current.User);
        var total = kernel + user;
        if (total == 0) return 0;
        var busy = total > idle ? total - idle : 0;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    /// <summary>FILETIME counters can wrap/reset; a negative delta means "no
    /// new data", never a negative load.</summary>
    private static ulong Delta(ulong prev, ulong cur) => cur >= prev ? cur - prev : 0;
}

/// <summary>
/// Round-79: system CPU + physical memory without any NuGet package
/// (<c>PerformanceCounter</c> would break the zero-dependency rule).
/// <c>GetSystemTimes</c> for CPU deltas, <c>GlobalMemoryStatusEx</c> for RAM.
/// Every P/Invoke is best-effort: on failure the dashboard simply shows
/// "无法读取" instead of throwing.
/// </summary>
public sealed class SystemLoadProbe
{
    private CpuTimes _last = CpuTimes.Zero;

    public readonly record struct Snapshot(double CpuPercent, ulong TotalBytes, ulong AvailableBytes)
    {
        public bool Ok => TotalBytes > 0;
        public int TotalMb => (int)(TotalBytes / 1024 / 1024);
        public int AvailableMb => (int)(AvailableBytes / 1024 / 1024);
        public int UsedPercent => TotalBytes == 0 ? 0
            : (int)Math.Round((TotalBytes - AvailableBytes) * 100.0 / TotalBytes);
    }

    /// <summary>Call at most every couple of seconds: the CPU number is a delta
    /// between two calls, so hammering it yields noise.</summary>
    public Snapshot Read()
    {
        double cpu = 0;
        try
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var cur = new CpuTimes(ToUlong(idle), ToUlong(kernel), ToUlong(user));
                if (_last.IsUsable) cpu = CpuLoadMath.Percent(_last, cur);
                _last = cur;
            }
        }
        catch { }

        ulong total = 0, available = 0;
        try
        {
            var status = new MemoryStatusEx();
            status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
            if (GlobalMemoryStatusEx(ref status))
            {
                total = status.ullTotalPhys;
                available = status.ullAvailPhys;
            }
        }
        catch { }

        return new Snapshot(cpu, total, available);
    }

    private static ulong ToUlong(FileTime ft) => ((ulong)ft.High << 32) | ft.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

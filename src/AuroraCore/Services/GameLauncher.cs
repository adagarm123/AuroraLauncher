using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>
/// Round-92: the peak-memory reading for one run, with the evidence needed to
/// tell "the game used 2 GB" from "we never got a reading".
///
/// The old sampler was a <c>Task.Run</c> loop that sampled immediately and then
/// every 5 s. In practice it recorded the java.exe stub (~2 MB) and nothing
/// else, because the first sample fired 40 ms in and the async continuation
/// never came back — the dashboard then printed「峰值内存 2 MB，运行平稳」for a
/// run that had actually worked. Both halves are fixed: a dedicated thread (it
/// cannot be starved by the thread pool) and a first sample that waits for the
/// JVM to exist.
/// </summary>
public sealed class MemorySampler
{
    private long _peakBytes;

    public int Samples { get; private set; }
    /// <summary>First reading of the run — the number that exposes a stub.</summary>
    public long FirstBytes { get; private set; }

    public long PeakBytes => Interlocked.Read(ref _peakBytes);
    public int PeakMb => (int)(PeakBytes / 1024 / 1024);
    public int FirstMb => (int)(FirstBytes / 1024 / 1024);

    public void Report(long bytes)
    {
        if (bytes <= 0) return;
        Samples++;
        if (FirstBytes == 0) FirstBytes = bytes;
        long current;
        while (bytes > (current = Interlocked.Read(ref _peakBytes)))
        {
            if (Interlocked.CompareExchange(ref _peakBytes, bytes, current) == current) break;
        }
    }
}

/// <summary>
/// Starts the built Java process, streams stdout/stderr line-by-line to the
/// caller (for live logs + crash analysis) and reports the exit code. The
/// process can be cancelled (killed) via the cancellation token.
/// </summary>
public sealed class GameLauncher
{
    private readonly Logger _log = Logger.Instance;

    /// <summary>How often the running JVM's working set is sampled. 2.5 s is far
    /// below the cost of the game itself and still catches a chunk-load spike.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(2500);

    /// <summary>Ignore the first ~1.2 s: that is the launcher stub before jvm.dll
    /// is loaded, and its 2 MB working set is exactly the bogus reading this
    /// class used to report.</summary>
    private static readonly TimeSpan FirstSampleDelay = TimeSpan.FromMilliseconds(1200);

    public async Task<int> LaunchAsync(LaunchSpec spec, Action<string>? onLog = null, CancellationToken ct = default,
        MemorySampler? sampler = null)
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

        var started = DateTime.Now;
        var stop = false;
        Thread? thread = null;
        if (sampler != null)
        {
            // A dedicated thread, not Task.Run: the loop must keep ticking even
            // while the thread pool is busy with logs / downloads / UI work.
            thread = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(FirstSampleDelay);
                    while (!Volatile.Read(ref stop))
                    {
                        if (p.HasExited) break;
                        long bytes;
                        try { bytes = p.WorkingSet64; }
                        catch { break; } // process gone or handle revoked
                        sampler.Report(bytes);
                        Thread.Sleep(SampleInterval);
                    }
                }
                catch { /* background measurement must never take the launcher down */ }
            })
            {
                IsBackground = true,
                Name = "aurora-mem-sampler"
            };
            thread.Start();
        }

        using (ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }))
        {
            var exit = await tcs.Task.ConfigureAwait(false);
            Volatile.Write(ref stop, true);
            if (thread != null) { try { thread.Join(3000); } catch { } }
            var ran = DateTime.Now - started;
            if (sampler != null)
            {
                if (sampler.Samples == 0 && ran.TotalSeconds > 10)
                    _log.Warn("Perf", $"内存采样一次都没成功（运行 {ran.TotalSeconds:0} 秒），本次性能结论不可用。");
                else
                    _log.Info("Perf", $"内存采样 {sampler.Samples} 次（首采样 {sampler.FirstMb} MB）" +
                                      $"→ 峰值 {sampler.PeakMb} MB，运行 {ran.TotalSeconds:0} 秒");
            }
            _log.Info("Launcher", $"Game process exited with code {exit}.");
            return exit;
        }
    }
}

/// <summary>Best-effort crash / failure detection from captured game logs.
/// Round-78: the rule table moved into <see cref="CrashResolutionEngine"/>,
/// which now owns the patterns AND the actions; this class stays as the thin
/// "technical log → one Chinese sentence" projection used by the log line and
/// by older call sites. Rules are ordered: specific loader/mod errors first,
/// generic JVM errors last (first match wins).</summary>
public static class CrashAnalyzer
{
    /// <summary>One triage rule, projected from the resolution engine's table
    /// so the card and the log line can never disagree.</summary>
    public sealed record CrashRule(string Pattern, string Title, string Suggestion);

    private static readonly CrashRule[] Rules = CrashResolutionEngine.LegacyRules
        .Select(r => new CrashRule(r.Pattern, r.Title, r.Suggestion))
        .ToArray();

    /// <summary>Structured triage result for the graphical crash card
    /// (round-60): ALL matching rules, not just the first one — a modpack
    /// crash often has both "missing dependency" and "duplicate mod".</summary>
    public sealed record CrashFinding(string Title, string Suggestion);

    public sealed record CrashReport(int ExitCode, string InstanceName, IReadOnlyList<CrashFinding> Causes)
    {
        public bool HasCauses => Causes.Count > 0;
        public string Headline => "检测到游戏崩溃";
    }

    /// <summary>Case-insensitive match of EVERY rule against the captured log.
    /// Pure logic (no IO) so the crash-card content is unit-testable.</summary>
    public static CrashReport BuildReport(int exitCode, string instanceName, IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        var causes = Rules
            .Where(r => System.Text.RegularExpressions.Regex.IsMatch(text, r.Pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(r => new CrashFinding(r.Title, r.Suggestion))
            .ToList();
        return new CrashReport(exitCode, instanceName, causes);
    }

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

    /// <summary>Round-92: the case no rule can match — the process died within
    /// seconds and the log stopped before saying anything useful. Silence is a
    /// symptom here (broken loader / wrong Java), not an absence of one.</summary>
    public static string? DescribeInstantExit(int exitCode, TimeSpan ran, int logLines)
    {
        if (exitCode == 0) return null;
        if (ran.TotalSeconds > 15) return null;
        if (logLines > 20) return null; // the log said enough; the rules had their chance
        return "游戏进程启动后几秒内就退出了，日志里也没有明确报错 —— 通常是加载器或核心依赖文件损坏，" +
               "也可能是 Java 版本不对。请先在「实例管理」里点「补全文件」，再换一个 Java 版本试试。";
    }
}

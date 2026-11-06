using System.Globalization;
using System.Security.Cryptography;

namespace AuroraCore.Services;

/// <summary>Everything the detached "apply update" host needs, handed over as
/// ONE command-line token. Pure data + pure parsing so the handoff is
/// unit-testable without launching a GUI. Round-61: the old hidden cmd script
/// left the screen blank for 10+ seconds while it copied a 63 MB exe; the
/// host now shows a visible progress window for the whole swap.</summary>
public sealed class UpdateApplyPlan
{
    /// <summary>PID of the launcher instance the user just closed / is about
    /// to exit. The host waits for it before touching the locked exe.</summary>
    public int OldPid { get; init; }
    public string TargetExe { get; init; } = "";
    public string StagedExe { get; init; } = "";
    public string StagedInfo { get; init; } = "";

    public bool IsValid => OldPid > 0
        && TargetExe.Length > 0 && StagedExe.Length > 0
        && Path.IsPathRooted(TargetExe) && Path.IsPathRooted(StagedExe);
}

public static class UpdateApplyArgs
{
    public const string Switch = "--apply-update";

    /// <summary>Single argv token: Windows paths may contain spaces but NEVER
    /// '|' (an illegal filename char), so a pipe-delimited token round-trips
    /// through ProcessStartInfo.ArgumentList without quoting headaches.</summary>
    public static string Build(UpdateApplyPlan plan) =>
        $"{Switch}|{plan.OldPid.ToString(CultureInfo.InvariantCulture)}|{plan.TargetExe}|{plan.StagedExe}|{plan.StagedInfo}";

    public static bool TryParse(IEnumerable<string> args, out UpdateApplyPlan? plan)
    {
        plan = null;
        var raw = args.FirstOrDefault(a => a.StartsWith(Switch + "|", StringComparison.Ordinal));
        if (raw == null) return false;
        var parts = raw.Split('|');
        if (parts.Length != 5) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return false;
        var candidate = new UpdateApplyPlan
        {
            OldPid = pid,
            TargetExe = parts[2],
            StagedExe = parts[3],
            StagedInfo = parts[4]
        };
        if (!candidate.IsValid) return false;
        plan = candidate;
        return true;
    }
}

/// <summary>Executes the visible swap. Lives in AuroraCore (no WPF dependency)
/// so the file/process decisions stay testable; the window in the Desktop
/// project only renders the reported stages. Every failure is an exception
/// with a user-facing message — never a silent black screen.</summary>
public static class UpdateApplier
{
    public const int MaxCopyAttempts = 10;
    public const int WaitTimeoutSeconds = 20;

    /// <summary>Reports (stage text, percent 0..100) as the swap advances.
    /// <paramref name="launch"/> is injectable so the whole swap (minus the
    /// GUI) can be exercised offline without spawning a real launcher.</summary>
    public static async Task ApplyAsync(UpdateApplyPlan plan, Action<string, int> report,
        CancellationToken ct = default, Func<string, bool>? launch = null)
    {
        report("正在等待旧版本退出…", 8);
        if (!await WaitForExitAsync(plan.OldPid, TimeSpan.FromSeconds(WaitTimeoutSeconds), ct).ConfigureAwait(false))
            throw new IOException("旧版本未能及时退出，更新已暂停。请重试；若反复失败可手动替换文件。");

        report("正在校验更新包…", 25);
        await Task.Run(() => VerifyStaged(plan), ct).ConfigureAwait(false);

        report("正在替换文件…", 40);
        await Task.Run(() => CopyWithRetry(plan, report, ct), ct).ConfigureAwait(false);

        report("正在写入版本信息…", 92);
        await Task.Run(() => CopyBuildInfo(plan), ct).ConfigureAwait(false);

        report("更新完成，正在重新启动启动器…", 100);
        if (!File.Exists(plan.TargetExe))
            throw new IOException("替换后的启动器文件不存在，更新未完成。");
        var start = launch ?? (path =>
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        });
        if (!start(plan.TargetExe))
            throw new IOException("无法启动新版本启动器，请手动打开它。");
        // The staged copy is no longer needed. Best effort: the next startup
        // sweeps it if deletion fails (e.g. antivirus still scanning).
        TryDelete(plan.StagedExe);
    }

    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException) { return true; }        // process already gone
            catch (InvalidOperationException) { return true; }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static void VerifyStaged(UpdateApplyPlan plan)
    {
        if (!File.Exists(plan.StagedExe))
            throw new IOException("更新包文件丢失，请重新下载更新。");
        if (!File.Exists(plan.StagedInfo)) return; // hash is optional for older staged builds
        var hashLine = File.ReadAllLines(plan.StagedInfo)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("exe-sha256", StringComparison.OrdinalIgnoreCase));
        if (hashLine == null) return;
        var colon = hashLine.IndexOf(':');
        if (colon < 0) return;
        var expected = hashLine[(colon + 1)..].Trim().Replace("-", "");
        if (expected.Length != 64) return;
        using var fs = File.OpenRead(plan.StagedExe);
        var actual = Convert.ToHexString(SHA256.HashData(fs));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("更新包校验失败（文件与发布者签名不一致），已停止安装。");
    }

    private static void CopyWithRetry(UpdateApplyPlan plan, Action<string, int> report, CancellationToken ct)
    {
        for (var i = 1; i <= MaxCopyAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Copy(plan.StagedExe, plan.TargetExe, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Old process handle / antivirus scan: visible, bounded retry.
                if (i >= MaxCopyAttempts)
                    throw new IOException($"无法替换启动器文件（已重试 {MaxCopyAttempts} 次）：{ex.Message}。请关闭可能占用它的程序后点「重试」。");
                report($"文件暂时被占用，正在重试（{i}/{MaxCopyAttempts}）…", 40 + i * 5);
                Thread.Sleep(700);
            }
        }
        throw new IOException($"无法替换启动器文件（已重试 {MaxCopyAttempts} 次）。请关闭可能占用它的程序后点「重试」。");
    }

    private static void CopyBuildInfo(UpdateApplyPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.StagedInfo) || !File.Exists(plan.StagedInfo)) return;
        var dst = Path.Combine(Path.GetDirectoryName(plan.TargetExe)!, "build-info.txt");
        try { File.Copy(plan.StagedInfo, dst, overwrite: true); } catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

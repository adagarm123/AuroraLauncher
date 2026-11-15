namespace AuroraCore.Services;

/// <summary>What the window's close button should do. Persisted in config.json.</summary>
public enum CloseAction
{
    /// <summary>Ask every time (tray available) — the default.</summary>
    Ask = 0,
    /// <summary>Always exit (still warns when downloads are running).</summary>
    Exit = 1,
    /// <summary>Always hide to the tray and keep downloading.</summary>
    MinimizeToTray = 2
}

public enum CloseDecisionKind { Exit, HideToTray, Prompt }

/// <summary>Outcome of the close policy for one click on the X.</summary>
public readonly record struct CloseDecision(
    CloseDecisionKind Kind,
    bool OfferTray,
    bool MentionDownloads,
    int Running,
    int Queued)
{
    public int Total => Running + Queued;
}

/// <summary>
/// Round-74: replaces round-73's "always ask, never offer tray".
///
/// Rules (each one is a test):
///  - No tray available  → exactly round-73 behaviour (ask only when work is in
///    flight, otherwise close instantly).
///  - Tray + Ask         → always ask, offering "最小化到托盘" (that IS the ask).
///  - Tray + Exit        → exit, but only after the download warning when jobs
///    are running: an explicit preference must not silently destroy an install.
///  - Tray + Minimize    → hide immediately, no dialog.
///
/// Round-73 deliberately refused to add a tray; the reason was "no tray
/// infrastructure". That infrastructure now exists, so the refusal moved into
/// <see cref="ShouldPrompt"/>, not into a policy that forbids hiding.
/// </summary>
public static class CloseBehaviorPolicy
{
    public static CloseDecision Decide(CloseAction action, bool trayAvailable, int runningJobs, int queuedJobs)
    {
        var running = Math.Max(0, runningJobs);
        var queued = Math.Max(0, queuedJobs);
        var inFlight = running + queued > 0;

        if (!trayAvailable)
        {
            // Fall back to round-73: no tray means "minimize" cannot be honoured.
            if (action == CloseAction.Exit)
                return inFlight
                    ? new CloseDecision(CloseDecisionKind.Prompt, false, true, running, queued)
                    : new CloseDecision(CloseDecisionKind.Exit, false, false, running, queued);

            return inFlight
                ? new CloseDecision(CloseDecisionKind.Prompt, false, true, running, queued)
                : new CloseDecision(CloseDecisionKind.Exit, false, false, running, queued);
        }

        return action switch
        {
            CloseAction.Exit => inFlight
                ? new CloseDecision(CloseDecisionKind.Prompt, false, true, running, queued)
                : new CloseDecision(CloseDecisionKind.Exit, false, false, running, queued),
            CloseAction.MinimizeToTray => new CloseDecision(CloseDecisionKind.HideToTray, false, false, running, queued),
            _ => new CloseDecision(CloseDecisionKind.Prompt, true, inFlight, running, queued)
        };
    }

    /// <summary>Wording for the prompt. Downloads are spelled out because the
    /// difference between "隐藏" and "退出" is exactly the download.</summary>
    public static string BuildMessage(CloseDecision decision)
    {
        var headline = decision.MentionDownloads
            ? $"还有 {decision.Total} 个下载任务未完成（{(decision.Queued > 0 ? $"{decision.Running} 个正在下载，{decision.Queued} 个排队中" : $"{decision.Running} 个正在下载")}）。"
            : "要退出启动器吗？";

        var options = decision.OfferTray
            ? "选择「最小化到托盘」：窗口收起，下载继续，下次从托盘图标点开即可。\n" +
              "选择「退出」：结束所有下载（已下载完成的文件不受影响，未完成的临时文件会被清理，下次可从这里继续）。"
            : "现在退出会中断这些任务：已下载完成的文件不受影响，未完成的临时文件（.part）会被清理。";

        return $"{headline}\n\n{options}";
    }

    /// <summary>Tray tooltip / balloon text after hiding.</summary>
    public static string BuildTrayHint(int runningJobs, int queuedJobs)
    {
        var total = Math.Max(0, runningJobs) + Math.Max(0, queuedJobs);
        return total > 0
            ? $"已最小化到托盘，{total} 个下载任务继续在后台进行。"
            : "已最小化到托盘，双击图标可重新打开。";
    }

    public static string DisplayText(CloseAction action) => action switch
    {
        // Round-90: the parenthetical IS the promise. Without it "直接退出" reads
        // as "always closes instantly", and the download guard looks like a bug.
        CloseAction.Exit => "直接退出（有下载时仍会确认）",
        CloseAction.MinimizeToTray => "最小化到托盘",
        _ => "每次询问我"
    };

    /// <summary>Parses the combo box text. Prefix matching, so both the current
    /// label (with its parenthetical promise) and the legacy short form work —
    /// a stale string must never silently fall back to "Ask".</summary>
    public static CloseAction FromDisplayText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return CloseAction.Ask;
        if (text.StartsWith("直接退出", StringComparison.Ordinal)) return CloseAction.Exit;
        if (text.StartsWith("最小化到托盘", StringComparison.Ordinal)) return CloseAction.MinimizeToTray;
        return CloseAction.Ask;
    }
}

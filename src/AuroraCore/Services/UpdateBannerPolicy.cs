namespace AuroraCore.Services;

/// <summary>Pure logic behind the 版本列表「一键更新」 banner (round-62).
/// The banner is always visible so the button is discoverable; when a newer
/// round was detected it becomes an explicit "you are on an old launcher"
/// call to action. Keeping the wording here makes it testable without WPF.</summary>
public static class UpdateBannerPolicy
{
    public static bool IsOutdated(int currentRound, int? availableRound) =>
        availableRound.HasValue && availableRound.Value > currentRound;

    public static string Text(int currentRound, int? availableRound, bool staged)
    {
        if (staged) return "新版本已下载好，点「一键更新」即可安装并重启。";
        if (IsOutdated(currentRound, availableRound))
            return $"检测到新版启动器（第 {availableRound} 轮），点「一键更新」直接安装。";
        if (availableRound.HasValue)
            return "已是最新版本，点「一键更新」可重新检查。";
        return "点「一键更新」检查启动器是否有新版本。";
    }
}

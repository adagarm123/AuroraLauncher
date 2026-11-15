using System.Windows;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraCore.Utils;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>
/// Round-92: the launch gate. Everything here exists because a modpack import
/// that died halfway produced an instance which every old check called "完整" —
/// the vanilla files were there, the loader and the mods were not — and the
/// launcher started a plain vanilla client without a word.
/// </summary>
public partial class MainViewModel
{
    public enum LaunchGateAnswer    {
        Cancel,
        /// <summary>"仍然启动" — only offered for modpack gaps, never for a
        /// missing loader (that launch would be a different game).</summary>
        LaunchAnyway
    }

    /// <summary>Test seam: when set, the modal is skipped (a real dialog blocks
    /// an unattended smoke test forever). Production default is null.</summary>
    public LaunchGateAnswer? LaunchGateAnswerForTests { get; set; }

    /// <summary>Asks the user what to do about a blocking launch state.
    /// Returns true when the launch may continue.</summary>
    private bool ConfirmLaunchGate(LaunchReadiness.Result gate, GameInstance instance)
    {
        Logger.Instance.Info("Launch", $"启动闸门：{gate.Blocker} — {gate.Headline}");

        if (LaunchGateAnswerForTests is { } forced)
            return forced == LaunchGateAnswer.LaunchAnyway;

        var repairHint = gate.CanRepair
            ? "可以先在「实例管理」里点「补全文件」自动修复。"
            : "原始整合包文件已不在本机，无法自动补全 —— 请重新下载该整合包再导入。";

        if (!gate.CanOverride)
        {
            var only = MessageBox.Show(Application.Current.MainWindow,
                $"{gate.Headline}\n\n{gate.Detail}\n\n{repairHint}",
                "无法启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            _ = only;
            Status = gate.Headline + "（未启动）";
            return false;
        }

        var answer = MessageBox.Show(Application.Current.MainWindow,
            $"{gate.Headline}\n\n{gate.Detail}\n\n{repairHint}\n\n" +
            "现在仍然启动吗？（可能进原版，或一进游戏就崩）",
            "整合包文件不完整", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { Status = "已取消启动"; return false; }

        AddLog($"[Launch] 用户选择忽略完整性检查继续启动（{gate.Blocker}）");
        Logger.Instance.Warn("Launch", $"用户忽略闸门继续启动：{gate.Blocker} / {instance.Name}");
        return true;
    }

    /// <summary>Friendly text for a launch exception, with the full stack logged
    /// so the next "Object reference not set…" is diagnosable.</summary>
    private string ReportLaunchFailure(Exception ex)
    {
        var text = LaunchFailureText.Describe(ex);
        Logger.Instance.Error("Launch", "启动失败：" + text + Environment.NewLine + ex);
        AddLog("[Launch] 失败：" + text);
        FailStages(text);
        return "启动失败: " + text;
    }

    /// <summary>Round-92: writes "this instance should contain N mods" plus the
    /// pack's file manifest into the instance metadata, BEFORE anything is
    /// downloaded. Called by both import paths (local .mrpack and content
    /// center).</summary>
    private void RecordModpackExpectation(GameInstance inst, string packPath, string packName, MrpackInfo? info)
    {
        var plan = new List<ModpackManifest.ModpackFile>();
        try { plan = ModManager.ReadMrpackFiles(packPath); }
        catch (Exception ex) { Logger.Instance.Warn("Import", "读取整合包清单失败: " + ex.Message); }

        inst.IsModpack = true;
        inst.ModpackName = packName;
        inst.ModpackSourcePath = packPath;
        inst.ModpackImportedAt = DateTime.Now;
        inst.ExpectedModCount = plan.Count(f => f.IsModJar);
        if (inst.ExpectedModCount == 0) inst.ExpectedModCount = info?.ModCount ?? 0;
        inst.ExpectedFileCount = plan.Count > 0 ? plan.Count : info?.ClientFiles ?? 0;
        _svc.Instances.Update(inst);

        new ModpackManifest
        {
            PackName = packName,
            GameVersion = inst.MinecraftVersion,
            Loader = inst.LoaderType.ToString(),
            LoaderVersion = inst.LoaderVersion,
            SourcePath = packPath,
            Files = plan
        }.Save(inst.Directory);

        Logger.Instance.Info("Import",
            $"整合包预期：{inst.ExpectedModCount} 个模组 / {inst.ExpectedFileCount} 个文件（{packName}）");
        AddLog($"[Modpack] 声明 {inst.ExpectedModCount} 个模组 / {inst.ExpectedFileCount} 个文件，安装后会逐个核对");
    }

    /// <summary>Round-92: what the instance's modpack situation is, for the
    /// instance card / status line. Empty for normal instances.</summary>
    public static string DescribeModpack(GameInstance? instance)
    {
        if (instance is not { IsModpack: true }) return "";
        var scan = ModpackIntegrity.Evaluate(instance);
        return scan.State switch
        {
            ModpackIntegrity.State.Empty => $"整合包未安装完成（0/{scan.Expected} 个模组）",
            ModpackIntegrity.State.Partial => $"整合包缺少模组（{scan.Actual}/{scan.Expected}）",
            _ => $"整合包 · {scan.Actual} 个模组"
        };
    }
}

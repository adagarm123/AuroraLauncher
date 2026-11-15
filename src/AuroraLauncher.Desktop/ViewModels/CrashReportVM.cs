using System.Collections.ObjectModel;
using AuroraCore.Services;
using AuroraLauncher.ViewModels;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>One diagnosis row on the crash card (round-78): what the launcher
/// thinks happened, and the single button that actually fixes it.</summary>
public class CrashSolutionVM : ViewModelBase
{
    private bool _applied;
    private string _resultText = "";

    public CrashSolutionVM(CrashSolution solution, int index)
    {
        Solution = solution;
        Index = index.ToString();
    }

    public CrashSolution Solution { get; }
    public string Index { get; }
    public string Title => Solution.Title;
    public string Diagnosis => Solution.Diagnosis;
    public string ActionText => Solution.ActionText;
    public string KindLabel => Solution.KindLabel;
    public bool HasAction => Solution.HasAction;
    public bool IsAuto => Solution.Kind == FixKind.Auto;

    /// <summary>Text shown under the row after the fix ran ("已把内存提到
    /// 6144 MB，重启游戏生效").</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (SetProperty(ref _resultText, value))
            {
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    public bool HasResult => ResultText.Length > 0;

    /// <summary>Auto fixes are one-shot: once applied, the button greys out
    /// (re-applying "raise memory to 6144" after it is already 6144 is noise).
    /// Guided actions stay clickable — the user may reopen the folder.</summary>
    public bool CanApply => HasAction && !(_applied && IsAuto);

    public void MarkApplied(string text)
    {
        _applied = true;
        ResultText = text;
        OnPropertyChanged(nameof(CanApply));
    }
}

/// <summary>View model behind the graphical crash report card (round-60,
/// upgraded in round-78 with diagnosis + repair actions). Replaces "go dig
/// through the black console" with a friendly, actionable summary built by
/// <see cref="CrashResolutionEngine"/> on a background thread.</summary>
public class CrashReportVM : ViewModelBase
{
    public CrashReportVM(int exitCode, string instanceName, string signature = "")
    {
        ExitCode = exitCode;
        InstanceName = instanceName;
        Signature = signature;
        Timestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    public string Headline => "检测到游戏崩溃";
    public int ExitCode { get; }
    public string InstanceName { get; }
    public string Signature { get; }
    public string Timestamp { get; }
    public string Summary => $"游戏异常退出（代码 {ExitCode}）· 实例：{InstanceName} · {Timestamp}";
    public ObservableCollection<CrashSolutionVM> Solutions { get; } = new();

    public bool HasSolutions => Solutions.Count > 0;

    /// <summary>Legacy alias kept for the XAML card's visibility binding.</summary>
    public bool HasCauses => HasSolutions;

    public int AutoFixCount => Solutions.Count(s => s.IsAuto && s.HasAction);

    public string CountText => Solutions.Count == 0
        ? "没有命中预置规则"
        : $"共 {Solutions.Count} 条诊断，其中 {AutoFixCount} 条可一键修复";

    public string Fallback => HasSolutions
        ? ""
        : "这次崩溃没有命中预置规则，属于较罕见的情况。可以一键导出诊断包，再点「发送给作者」把包作为附件发过去。";
}

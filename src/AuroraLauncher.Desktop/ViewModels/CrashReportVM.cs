using System.Collections.ObjectModel;
using AuroraLauncher.ViewModels;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>One human-readable possible cause on the crash card.</summary>
public class CrashCauseVM
{
    public CrashCauseVM(int index, string title, string suggestion)
    {
        Index = index.ToString();
        Title = title;
        Suggestion = suggestion;
    }

    public string Index { get; }
    public string Title { get; }
    public string Suggestion { get; }
}

/// <summary>View model behind the graphical crash report card (round-60).
/// Replaces "go dig through the black console" with a friendly, actionable
/// summary. Data comes from the structured CrashAnalyzer.BuildReport.</summary>
public class CrashReportVM : ViewModelBase
{
    public CrashReportVM(int exitCode, string instanceName)
    {
        ExitCode = exitCode;
        InstanceName = instanceName;
        Timestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    public string Headline => "检测到游戏崩溃";
    public int ExitCode { get; }
    public string InstanceName { get; }
    public string Timestamp { get; }
    public string Summary => $"游戏异常退出（代码 {ExitCode}）· 实例：{InstanceName} · {Timestamp}";
    public ObservableCollection<CrashCauseVM> Causes { get; } = new();
    public bool HasCauses => Causes.Count > 0;
    public string Fallback => HasCauses
        ? ""
        : "暂未匹配到已知原因。请导出诊断包并附上日志，我们会帮你排查。";
}

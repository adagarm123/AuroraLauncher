using System.Collections.ObjectModel;
using System.ComponentModel;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraCore.Utils;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>One checkbox row in the "missing prerequisite" card.</summary>
public sealed class MissingDepVM : INotifyPropertyChanged
{
    private readonly Action _onSelectionChanged;
    private bool _isSelected;
    private string _statusKind = "";
    private string _statusText = "";

    public MissingDepVM(AutoFixCandidate candidate, bool selected, Action onSelectionChanged)
    {
        Candidate = candidate;
        _isSelected = selected;
        _onSelectionChanged = onSelectionChanged;
    }

    public AutoFixCandidate Candidate { get; }
    public string ModId => Candidate.ModId;
    public string RangeText => Candidate.RangeText;
    public string RequiredByText => "「" + Candidate.RequiredByText + "」需要它";
    public string SlugText => Candidate.Slug;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            _onSelectionChanged();
        }
    }

    /// <summary>"", "ok", "fail" — XAML colours via DataTrigger (no converter).</summary>
    public string StatusKind
    {
        get => _statusKind;
        private set { _statusKind = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusKind))); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText))); }
    }

    public bool HasStatus => _statusText.Length > 0;

    public void SetVerdict(AutoFixResult result)
    {
        StatusText = MissingDependencyFix.Describe(result) +
                     (result.Ok ? "" : "。" + MissingDependencyFix.ManualHint(Candidate));
        StatusKind = result.Ok ? "ok" : "fail";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatus)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainViewModel
{
    // ==================== 第 100 轮：一键补齐缺失前置 ====================
    public ObservableCollection<MissingDepVM> MissingDeps { get; } = new();

    private bool _isAutoFixing;
    public bool IsAutoFixing
    {
        get => _isAutoFixing;
        private set
        {
            if (SetProperty(ref _isAutoFixing, value))
            {
                OnPropertyChanged(nameof(AutoFixButtonText));
                OnPropertyChanged(nameof(CanAutoFix));
                AutoFixMissingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _missingDepsHint = "";
    public string MissingDepsHint
    {
        get => _missingDepsHint;
        private set => SetProperty(ref _missingDepsHint, value);
    }

    private string _autoFixSummary = "";
    public string AutoFixSummary
    {
        get => _autoFixSummary;
        private set
        {
            if (SetProperty(ref _autoFixSummary, value))
                OnPropertyChanged(nameof(HasAutoFixSummary));
        }
    }

    public bool HasAutoFixSummary => _autoFixSummary.Length > 0;
    public bool HasMissingDeps => MissingDeps.Count > 0;
    public int SelectedMissingCount => MissingDeps.Count(d => d.IsSelected);

    public string AutoFixButtonText => IsAutoFixing
        ? "正在补齐…"
        : $"一键补齐选中的 {SelectedMissingCount} 项";

    public bool CanAutoFix => !IsAutoFixing && SelectedMissingCount > 0;

    private void OnMissingSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedMissingCount));
        OnPropertyChanged(nameof(AutoFixButtonText));
        OnPropertyChanged(nameof(CanAutoFix));
        AutoFixMissingCommand.RaiseCanExecuteChanged();
    }

    private void ApplyMissing(IReadOnlyList<AutoFixCandidate> candidates,
        IReadOnlyList<AutoFixResult>? verdicts = null, string? hint = null)
    {
        var previous = MissingDeps.ToDictionary(d => d.ModId, d => d.IsSelected, StringComparer.OrdinalIgnoreCase);
        var byId = verdicts?.ToDictionary(r => r.ModId, r => r, StringComparer.OrdinalIgnoreCase);
        MissingDeps.Clear();
        foreach (var c in candidates)
        {
            var row = new MissingDepVM(c, previous.TryGetValue(c.ModId, out var was) && was, OnMissingSelectionChanged);
            if (byId != null && byId.TryGetValue(c.ModId, out var verdict)) row.SetVerdict(verdict);
            MissingDeps.Add(row);
        }
        MissingDepsHint = hint ?? (candidates.Count == 0
            ? "当前实例没有检测到缺失前置"
            : $"检测到 {candidates.Count} 个缺失前置，勾选后可从 Modrinth 自动补齐（可取消 / 逐个反馈结果）");
        OnPropertyChanged(nameof(HasMissingDeps));
        OnMissingSelectionChanged();
    }

    /// <summary>Used by the pre-launch dialog: its scan is the freshest one,
    /// so the checklist must be seeded from THAT report, not a background
    /// refresh that may not have finished yet.</summary>
    public void SetMissingFromReport(ModPreflight.Report report)
        => ApplyMissing(MissingDependencyFix.Plan(report));

    public void RefreshMissingDeps() => _ = RefreshMissingDepsCoreAsync();

    private async Task RefreshMissingDepsCoreAsync()
    {
        var inst = SelectedInstance;
        if (inst == null || inst.LoaderType == ModLoaderType.Vanilla)
        {
            Dispatch(() => ApplyMissing(Array.Empty<AutoFixCandidate>()));
            return;
        }
        ModPreflight.Report report;
        try
        {
            report = await Task.Run(() => ModPreflight.Scan(inst.Directory, inst.LoaderType)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("AutoFix", "补齐前扫描失败: " + ex.Message);
            Dispatch(() => ApplyMissing(Array.Empty<AutoFixCandidate>(), hint: "前置扫描失败：" + ex.Message));
            return;
        }
        var plans = MissingDependencyFix.Plan(report);
        Dispatch(() => ApplyMissing(plans));
    }

    public void SelectAllMissing()
    {
        foreach (var d in MissingDeps) d.IsSelected = true;
        OnMissingSelectionChanged();
    }

    public void ClearMissingSelection()
    {
        foreach (var d in MissingDeps) d.IsSelected = false;
        OnMissingSelectionChanged();
    }

    /// <summary>Uses the launcher's own search API, but accepts a project ONLY
    /// on an exact slug/title match — a fuzzy hit would install the wrong mod.
    /// Known slugs (e.g. kotlinforforge) go straight to the project endpoint.</summary>
    private async Task<ModrinthProject?> FindAutoFixProjectAsync(AutoFixCandidate c, GameInstance inst,
        CancellationToken ct)
    {
        if (c.KnownSlug)
        {
            try
            {
                var direct = await _svc.ModManager.GetProjectDetailAsync(c.Slug, ct).ConfigureAwait(false);
                if (direct != null && !string.IsNullOrEmpty(direct.ProjectId)) return direct;
            }
            catch { /* 404 / network — fall through to search */ }
        }
        var loader = PreflightAutoFixPolicy.LoaderTag(inst.LoaderType);
        foreach (var constrained in new[] { true, false })
        {
            try
            {
                var hits = await _svc.ModManager.SearchAsync(c.Slug, ContentType.Mod,
                    constrained ? inst.MinecraftVersion : null,
                    constrained ? loader : null, ct: ct).ConfigureAwait(false);
                var pick = MissingDependencyFix.PickProject(c, hits.Projects);
                if (pick != null) return pick;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* one route failed — try the looser query */ }
        }
        return null;
    }

    /// <summary>Runs one batch as a REAL DownloadJob (visible, cancellable),
    /// one candidate at a time. A failed item is recorded and the loop goes
    /// on — "尽力而为", never a spinner with no explanation. The verdict is
    /// finalised only after a fresh preflight scan sees the installed ids.</summary>
    public async Task RunAutoFixAsync(GameInstance? instance = null, IReadOnlyList<string>? modIds = null)
    {
        if (IsAutoFixing) return;
        var inst = instance ?? SelectedInstance;
        if (inst == null) return;
        var chosen = MissingDeps.Where(d => d.IsSelected &&
                (modIds == null || modIds.Contains(d.ModId, StringComparer.OrdinalIgnoreCase)))
            .Select(d => d.Candidate).ToList();
        if (chosen.Count == 0) return;

        IsAutoFixing = true;
        AutoFixSummary = "";
        var job = await BeginJobAsync("补齐", $"补齐 {chosen.Count} 个前置模组（{inst.Name}）", inst.Name,
            keepPreparing: true).ConfigureAwait(true);
        InitStages(job, chosen.Select(c => ($"fix-{c.ModId}", $"查找并安装 {c.ModId}")),
            $"{inst.Name} 缺失前置补齐");
        var session = new AutoFixSession(chosen);

        try
        {
            foreach (var c in chosen)
            {
                job.EnterStage($"fix-{c.ModId}");
                job.SetStageText($"正在查找 {c.ModId} …");
                Status = $"正在补齐前置 {c.ModId} …";
                AddLog($"[AutoFix] 开始处理 {c.ModId}（slug={c.Slug}，需要 {c.RangeText}）");

                AutoFixResult result;
                try
                {
                    var project = await FindAutoFixProjectAsync(c, inst, job.Token).ConfigureAwait(true);
                    if (project == null)
                    {
                        result = new AutoFixResult(c.ModId, c.Slug, AutoFixStatus.NotFound, "", c.Slug);
                    }
                    else
                    {
                        var versions = await _svc.ModManager.GetVersionsAsync(
                            project.ProjectId, ContentType.Mod, null, null, job.Token).ConfigureAwait(true);
                        var (pick, outcome) = MissingDependencyFix.PickVersion(
                            c, inst.LoaderType, inst.MinecraftVersion, versions);
                        if (pick == null)
                        {
                            result = new AutoFixResult(c.ModId, c.Slug,
                                outcome == MissingDependencyFix.PickOutcome.Unverifiable
                                    ? AutoFixStatus.Unverifiable : AutoFixStatus.NoMatchingVersion,
                                "", project.Title);
                        }
                        else
                        {
                            job.SetStageText($"正在下载 {c.ModId} → {pick.FileName}");
                            await _svc.ModManager.DownloadContentAsync(project, pick, ContentType.Mod,
                                inst.Directory, null, job.Report, job.Token).ConfigureAwait(true);
                            result = new AutoFixResult(c.ModId, c.Slug, AutoFixStatus.Installed,
                                pick.FileName, $"{project.Title} {pick.VersionNumber}");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result = new AutoFixResult(c.ModId, c.Slug, AutoFixStatus.DownloadFailed, "", ex.Message);
                }
                session.Record(result);
                AddLog($"[AutoFix] {c.ModId}: {result.Status} — {MissingDependencyFix.Describe(result)}");
            }

            ModPreflight.Report fresh;
            try
            {
                fresh = await Task.Run(() => ModPreflight.Scan(inst.Directory, inst.LoaderType),
                    job.Token).ConfigureAwait(true);
            }
            catch
            {
                // Fail closed: without a successful rescan nobody may be told
                // that their prerequisite is fixed.
                fresh = new ModPreflight.Report();
            }

            var final = session.Complete(fresh.PresentIds);
            var summary = session.Summary;
            var plans = MissingDependencyFix.Plan(fresh);
            Dispatch(() =>
            {
                ApplyMissing(plans, final,
                    plans.Count == 0 ? "补齐完成，重扫没有发现缺失前置" : $"重扫后仍有 {plans.Count} 个缺失前置");
                AutoFixSummary = summary + "。" + (session.FailedCount > 0
                    ? "未补齐的项目已写明原因，可打开 mods 文件夹手动处理。"
                    : "重新启动游戏即可生效。");
                Status = summary;
            });
            AddLog($"[AutoFix] 完成：{summary}（已验证 {session.InstalledCount} 项）");
            ShowToast(summary);
            var allFailed = session.InstalledCount == 0 && session.Candidates.Count > 0;
            FinishJob(job, allFailed ? new InvalidOperationException(summary) : null, false, summary);
        }
        catch (OperationCanceledException)
        {
            FinishJob(job, null, true);
            Dispatch(() => { Status = "已取消补齐前置"; AutoFixSummary = "已取消，未做重扫确认"; });
        }
        catch (Exception ex)
        {
            FinishJob(job, ex, false);
            Dispatch(() => { AutoFixSummary = "补齐过程出错：" + ex.Message; Status = "补齐前置失败：" + ex.Message; });
        }
        finally
        {
            IsAutoFixing = false;
        }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using AuroraCore.Download;

namespace AuroraCore.Services;

/// <summary>Round-72: one independent download job (an instance install, a
/// modpack import, a single mod, a loader, a Java runtime, a file repair).
///
/// Why this exists: until round-71 every flow shared ONE global
/// <c>CancellationTokenSource</c>, ONE <c>Download</c> status object and ONE
/// stage collection — so a second install silently hijacked the first one's
/// token and progress, and the download page could only ever describe one job.
///
/// Two properties matter for smoothness:
///  1. <b>Self-throttled UI push</b>: the engine reports per read chunk (dozens
///     per second); the job updates plain fields and only asks the dispatcher
///     for a refresh every <see cref="PushIntervalMs"/>, and at most one push is
///     in flight at a time. N parallel downloads therefore cost ~N×10 UI
///     notifications per second instead of thousands.
///  2. <b>Per-job token and stages</b>: cancelling one job cannot touch another,
///     and each row in the download manager owns its own flow chart.</summary>
public sealed class DownloadJob : INotifyPropertyChanged
{
    /// <summary>10 refreshes/sec per job is smooth to the eye and cheap for the
    /// dispatcher.</summary>
    public const int PushIntervalMs = 100;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<Action> _post;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DateTime _lastPushUtc = DateTime.MinValue;
    private volatile bool _pushInFlight;

    internal DownloadJob(string kind, string title, string instanceName, Action<Action> post)
    {
        Kind = kind;
        Title = title;
        InstanceName = instanceName;
        _post = post;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>实例 / 整合包 / 模组 / 加载器 / Java / 修复.</summary>
    public string Kind { get; }

    public string Title { get; }

    /// <summary>Which instance this job belongs to ("" for global jobs).</summary>
    public string InstanceName { get; }

    public CancellationToken Token => _cts.Token;

    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; private set; }

    /// <summary>Elapsed seconds, frozen once finished (used for "用时 12s").</summary>
    public double ElapsedSeconds => (FinishedAtUtc.HasValue ? (FinishedAtUtc.Value - StartedAtUtc) : DateTime.UtcNow - StartedAtUtc).TotalSeconds;

    // ---- stage flow chart (per job) ----
    public ObservableCollection<JobStage> Stages { get; } = new();

    private int _stageIndex = -1;
    private int _stageCount;

    public void SetStages(IEnumerable<(string Key, string Label)> stages)
    {
        var list = stages.ToList();
        _stageCount = list.Count;
        _stageIndex = -1;
        _post(() =>
        {
            Stages.Clear();
            foreach (var (key, label) in list) Stages.Add(new JobStage(key, label));
            Raise(nameof(Stages));
            PushNow();
        });
    }

    /// <summary>Called from the installer's stage callback (background thread).</summary>
    /// <summary>Round-96: which stage is currently running (watchdog chooses
    /// its timeout from this, not from a global busy flag).</summary>
    public string? CurrentStageKey { get; private set; }

    public void EnterStage(string key)
    {
        Touch();
        EnsureRunning();
        CurrentStageKey = key;
        var idx = -1;
        for (var i = 0; i < Stages.Count; i++)
        {
            if (Stages[i].Key == key) { idx = i; break; }
        }
        if (idx < 0) return;
        _stageIndex = idx;
        _post(() =>
        {
            for (var i = 0; i < Stages.Count; i++)
            {
                var st = Stages[i];
                if (i < idx) { st.State = "Done"; st.PercentText = "100%"; }
                else if (i == idx) { st.State = "Running"; st.PercentText = null; }
                else { st.State = "Pending"; st.PercentText = null; }
            }
            PushNow();
        });
    }

    // ---- live values (written from background threads) ----
    private JobState _state = JobState.Queued;
    public JobState State
    {
        get => _state;
        internal set { if (_state == value) return; _state = value; RaiseAll(); }
    }

    private int _percent;
    public int Percent { get => _percent; private set { if (_percent == value) return; _percent = value; Raise(nameof(Percent)); Raise(nameof(PercentText)); } }

    private double _speed;
    public double SpeedBytesPerSec { get => _speed; private set { if (Math.Abs(_speed - value) < 1) return; _speed = value; Raise(nameof(SpeedText)); } }

    private string _stageText = "等待开始";
    public string StageText { get => _stageText; private set { if (_stageText == value) return; _stageText = value; Raise(nameof(StageText)); } }

    private string _detail = "";
    public string Detail { get => _detail; private set { if (_detail == value) return; _detail = value; Raise(nameof(Detail)); } }

    private string? _message;
    /// <summary>Success or failure text shown on the row.</summary>
    public string? Message { get => _message; private set { if (_message == value) return; _message = value; Raise(nameof(Message)); } }

    public string PercentText => $"{_percent}%";

    public string SpeedText => JobProgressPolicy.FormatSpeed(_speed);

    public string StateText => _state switch
    {
        JobState.Queued => "排队中",
        JobState.Preparing => "准备中",
        JobState.Running => "下载中",
        JobState.Done => "已完成",
        JobState.Failed => "失败",
        _ => "已取消"
    };

    public string KindGlyph => Kind switch
    {
        "实例" => "\uE896",
        "整合包" => "\uE7B8",
        "模组" => "\uE8F1",
        "加载器" => "\uE90F",
        "Java" => "\uE8B8",
        _ => "\uE90F"
    };

    public bool CanCancel => _state is JobState.Queued or JobState.Preparing or JobState.Running;

    public bool IsFinished => _state is JobState.Done or JobState.Failed or JobState.Canceled;

    /// <summary>Round-96: last time ANY progress reached this job (bytes, a
    /// stage change or a human-readable log line). The watchdog uses it to
    /// distinguish "quiet but working" from "hung".</summary>
    public DateTime LastProgressUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Round-97: file-COMPLETION tracking. Bytes can keep trickling
    /// in (378 KB/s) while the "87/90" counter never moves for minutes — that
    /// is a stall too, and a speed-only watchdog cannot see it.</summary>
    public int LastCompletedFiles { get; private set; }
    public int TrackedTotalFiles { get; private set; }
    public DateTime LastFileCompletedUtc { get; private set; } = DateTime.UtcNow;

    private void Touch() => LastProgressUtc = DateTime.UtcNow;

    /// <summary>Round-96: first real stage / first byte ends the 准备中 phase.
    /// Until then the row is visible but honestly says it is preparing.</summary>
    private void EnsureRunning()
    {
        if (_state != JobState.Preparing) return;
        _state = JobState.Running;
        _post(() => { Raise(nameof(StateText)); Raise(nameof(RowSubtitle)); });
    }

    /// <summary>Shown under the title: "实例：xxx · 下载中 · 42%".</summary>
    public string RowSubtitle =>
        string.IsNullOrWhiteSpace(InstanceName) ? StateText : $"{InstanceName} · {StateText}";

    // ---- control ----
    public void Cancel()
    {
        try { _cts.Cancel(); } catch { }
        if (_state is JobState.Queued or JobState.Preparing) MarkCanceled();
    }

    internal void MarkCanceled()
    {
        if (IsFinished) return;
        MarkFinished(JobState.Canceled, "已取消");
    }

    /// <summary>Round-96: watchdog abort. Cancels the linked token so the
    /// underlying install really stops, while leaving the row in FAILED (not
    /// "已取消") so the user sees what happened and can retry.</summary>
    public void AbortAsFailure(string message)
    {
        try { _cts.Cancel(); } catch { }
        MarkFailed(message);
    }

    /// <summary>Round-96: called immediately after Create, so the row exists
    /// and reads 准备中 while metadata / installer jar / library prefill are
    /// still in flight but no byte has moved yet.</summary>
    public void MarkPreparing(string text = "准备中…")
    {
        if (IsFinished) return;
        _state = JobState.Preparing;
        _stageText = text;
        Touch();
        _post(RaiseAll);
    }

    internal void MarkRunning()
    {
        _state = JobState.Running;
        _stageText = "开始下载";
        Touch();
        _post(RaiseAll);
    }

    /// <summary>Round-96: a human-readable line from the install pipeline
    /// ("预填充安装器依赖 29/29") counts as progress for the watchdog, even
    /// though no bytes were reported.</summary>
    public void NoteProgress(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Touch();
        SetStageText(line.Length > 60 ? line[..60] + "…" : line);
    }

    /// <summary>Engine progress callback. Runs on a background thread; only the
    /// throttled push reaches the UI thread.</summary>
    public void Report(DownloadProgress p)
    {
        Touch();
        EnsureRunning();
        if (p.TotalFiles > 0)
        {
            TrackedTotalFiles = p.TotalFiles;
            if (p.CompletedFiles > LastCompletedFiles)
            {
                LastCompletedFiles = p.CompletedFiles;
                LastFileCompletedUtc = DateTime.UtcNow;
            }
        }
        var stageFrac = p.TotalFiles > 0
            ? Math.Clamp((double)p.CompletedFiles / p.TotalFiles, 0, 1)
            : 0;
        var percent = JobProgressPolicy.PercentFor(_stageIndex, _stageCount, stageFrac);

        _percent = ProgressDisplayPolicy.Display(percent, IsFinished);
        _speed = p.SpeedBytesPerSec;
        var detail = p.Message ?? p.Current?.DisplayName ?? "";
        _detail = detail;
        _stageText = _stageIndex >= 0 && _stageIndex < Stages.Count
            ? $"{Stages[_stageIndex].Label} {(p.TotalFiles > 0 ? $"{p.CompletedFiles}/{p.TotalFiles}" : "")}".Trim()
            : "下载中";

        RequestPush();
    }

    /// <summary>Stage text from the installer's human-readable callback, used
    /// when there is no flow chart (single-step jobs like one mod).</summary>
    public void SetStageText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _stageText = text.Trim();
        Touch();
        RequestPush();
    }

    internal void MarkDone(string? message = null) => MarkFinished(JobState.Done, message ?? "已完成");

    internal void MarkFailed(string? message = null) => MarkFinished(JobState.Failed, message ?? "失败");

    private void MarkFinished(JobState state, string message)
    {
        if (IsFinished) return;
        _state = state;
        _message = message;
        _speed = 0;
        FinishedAtUtc = DateTime.UtcNow;
        if (state == JobState.Done) _percent = 100;
        if (state == JobState.Done)
        {
            foreach (var st in Stages)
            {
                st.State = "Done";
                st.PercentText = "100%";
            }
        }
        else
        {
            var running = Stages.FirstOrDefault(s => s.State == "Running");
            if (running != null)
            {
                running.State = "Failed";
                running.PercentText = null;
            }
        }
        _post(RaiseAll);
    }

    private void RequestPush()
    {
        // At most one refresh in flight, and never more than ten per second.
        var now = DateTime.UtcNow;
        if (_pushInFlight) return;
        if ((now - _lastPushUtc).TotalMilliseconds < PushIntervalMs) return;
        _pushInFlight = true;
        _lastPushUtc = now;
        _post(() =>
        {
            _pushInFlight = false;
            Raise(nameof(Percent));
            Raise(nameof(PercentText));
            Raise(nameof(SpeedText));
            Raise(nameof(StageText));
            Raise(nameof(Detail));
        });
    }

    /// <summary>Immediate refresh (state changes, stage changes, completion).</summary>
    private void PushNow() => RaiseAll();

    private void RaiseAll()
    {
        Raise(nameof(State));
        Raise(nameof(StateText));
        Raise(nameof(RowSubtitle));
        Raise(nameof(Percent));
        Raise(nameof(PercentText));
        Raise(nameof(SpeedText));
        Raise(nameof(StageText));
        Raise(nameof(Detail));
        Raise(nameof(Message));
        Raise(nameof(CanCancel));
        Raise(nameof(IsFinished));
        Raise(nameof(ElapsedSeconds));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Snapshot for the aggregate counters (cheap, no UI).</summary>
    public DownloadJobSnapshot Snapshot() => new(_state, _percent, _speed);
}

/// <summary>Immutable per-job numbers used by the aggregate view.</summary>
public readonly record struct DownloadJobSnapshot(JobState State, int Percent, double SpeedBytesPerSec);

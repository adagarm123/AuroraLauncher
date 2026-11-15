using System.Collections.ObjectModel;

namespace AuroraCore.Services;

/// <summary>Round-72: pure rules behind the multi-download view.
///
/// Kept separate from <see cref="DownloadJob"/> and
/// <see cref="DownloadJobManager"/> so the arithmetic can be tested without a
/// dispatcher: stage-aware percent, aggregate across jobs, speed formatting and
/// the refresh throttle decision.</summary>
public static class JobProgressPolicy
{
    /// <summary>Overall percent of ONE job: finished stages plus the fraction of
    /// the running one. A single-step job (no stages) is just the fraction.</summary>
    public static int PercentFor(int stageIndex, int stageCount, double stageFraction)
    {
        var frac = double.IsNaN(stageFraction) || stageFraction < 0 ? 0 : Math.Min(1.0, stageFraction);
        if (stageCount <= 0) return (int)Math.Round(frac * 100);
        var index = Math.Clamp(stageIndex, 0, stageCount);
        var value = (index + frac) / stageCount;
        // AwayFromZero: 12.5% must read as 13%, not 12% (banker's rounding).
        return (int)Math.Round(Math.Clamp(value, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>Aggregate across jobs.
    ///
    /// Percent: average of the jobs that are still interesting (queued, running
    /// or failed/canceled in this session) — a finished job must not drag the
    /// summary back up while a new one is at 5%. Cancelled jobs are excluded
    /// entirely, because their "percent" says nothing about progress.
    /// Speed: sum of the running jobs (that is what the network is doing).</summary>
    public static (int Percent, double SpeedBytesPerSec, int Active, int Queued) Aggregate(
        IEnumerable<DownloadJobSnapshot> jobs)
    {
        var percentSum = 0;
        var counted = 0;
        var speed = 0.0;
        var active = 0;
        var queued = 0;
        foreach (var j in jobs)
        {
            switch (j.State)
            {
                case JobState.Canceled:
                    continue;
                case JobState.Queued:
                    queued++;
                    percentSum += 0;
                    counted++;
                    break;
                case JobState.Running:
                    active++;
                    speed += j.SpeedBytesPerSec;
                    percentSum += j.Percent;
                    counted++;
                    break;
                default: // Done / Failed
                    percentSum += j.Percent;
                    counted++;
                    break;
            }
        }
        var percent = counted == 0 ? 0 : (int)Math.Round((double)percentSum / counted);
        return (Math.Clamp(percent, 0, 100), speed, active, queued);
    }

    /// <summary>Round-74: byte sizes for "已下载 1.2 GB / 3.4 GB" style text.
    /// Shares the unit ladder with <see cref="FormatSpeed"/> but has no "/s".</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return i == 0 ? $"{(long)value} B" : $"{value:F1} {units[i]}";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        var value = bytesPerSecond;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return $"{value:F1} {units[i]}";
    }

    /// <summary>Throttle decision: a push every 100 ms per job is smooth and
    /// keeps a ten-way parallel download at ~100 dispatcher hops/sec total
    /// instead of thousands.</summary>
    public static bool ShouldPush(DateTime lastPushUtc, DateTime nowUtc, int intervalMs = DownloadJob.PushIntervalMs)
        => (nowUtc - lastPushUtc).TotalMilliseconds >= intervalMs;

    /// <summary>How many jobs may run at once. More than this saturates the
    /// network and the disk without making anything finish sooner.</summary>
    public static int DefaultMaxConcurrentJobs => 3;

    /// <summary>Queued jobs start in order; the UI shows them as 排队中.</summary>
    public static bool ShouldQueue(int runningJobs, int maxConcurrent)
        => runningJobs >= Math.Max(1, maxConcurrent);
}

/// <summary>Owns every visible download job. Created on the UI thread; the
/// dispatcher hook is injected so Core stays free of WPF.</summary>
public sealed class DownloadJobManager
{
    private readonly Action<Action> _post;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();

    public DownloadJobManager(Action<Action> post, int maxConcurrent = 0)
    {
        _post = post;
        MaxConcurrent = maxConcurrent > 0 ? maxConcurrent : JobProgressPolicy.DefaultMaxConcurrentJobs;
        _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
    }

    public int MaxConcurrent { get; }

    /// <summary>Newest first, so the job the user just started is on top.</summary>
    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    /// <summary>Bumped whenever the aggregate changes; the VM polls the snapshot
    /// on one timer instead of recomputing per progress callback.</summary>
    public int Version { get; private set; }

    public DownloadJob Create(string kind, string title, string instanceName)
    {
        var job = new DownloadJob(kind, title, instanceName ?? "", _post);
        lock (_gate)
        {
            Jobs.Insert(0, job);
            Version++;
        }
        return job;
    }

    /// <summary>Waits for a download slot (max <see cref="MaxConcurrent"/> jobs).
    /// The job stays 排队中 until a slot frees; cancelling it while queued
    /// aborts the wait instead of silently starting later.</summary>
    public async Task StartAsync(DownloadJob job)
    {
        if (job.IsFinished) return;
        try
        {
            await _slots.WaitAsync(job.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            job.MarkCanceled();
            throw;
        }

        if (job.Token.IsCancellationRequested)
        {
            _slots.Release();
            job.MarkCanceled();
            throw new OperationCanceledException(job.Token);
        }
        job.MarkRunning();
        Version++;
    }

    /// <summary>Called exactly once per started job (success, failure or cancel).</summary>
    public void Finish(DownloadJob job, Exception? error = null, bool canceled = false, string? message = null)
    {
        var wasStarted = !job.IsFinished && job.State == JobState.Running;
        if (canceled) job.MarkCanceled();
        else if (error != null) job.MarkFailed(Humanize(error));
        else job.MarkDone(message);

        if (wasStarted)
        {
            try { _slots.Release(); } catch { }
        }
        Version++;
    }

    /// <summary>Marks the job cancelled immediately (the UI must react on click,
    /// not when the engine's exception reaches the flow) and frees its slot.</summary>
    public void Cancel(DownloadJob job)
    {
        var wasRunning = job.State == JobState.Running;
        job.Cancel();
        if (!job.IsFinished) job.MarkCanceled();
        if (wasRunning)
        {
            try { _slots.Release(); } catch { }
        }
        Version++;
    }

    public void CancelAll()
    {
        foreach (var job in Jobs.ToList())
            if (job.CanCancel) Cancel(job);
    }

    /// <summary>Removes finished rows. Running/queued jobs are never touched.</summary>
    public int ClearFinished()
    {
        var removed = 0;
        lock (_gate)
        {
            for (var i = Jobs.Count - 1; i >= 0; i--)
            {
                if (!Jobs[i].IsFinished) continue;
                Jobs.RemoveAt(i);
                removed++;
            }
            if (removed > 0) Version++;
        }
        return removed;
    }

    public (int Percent, double SpeedBytesPerSec, int Active, int Queued) Snapshot()
        => JobProgressPolicy.Aggregate(Jobs.Select(j => j.Snapshot()).ToList());

    public int FinishedCount => Jobs.Count(j => j.IsFinished);

    /// <summary>A raw exception string is not user-facing text.</summary>
    private static string Humanize(Exception ex) => ex switch
    {
        OperationCanceledException => "已取消",
        IOException => "读写文件失败：" + ex.Message,
        HttpRequestException => "网络请求失败：" + ex.Message,
        _ => ex.Message
    };
}

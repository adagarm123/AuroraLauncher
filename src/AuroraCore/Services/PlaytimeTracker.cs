using System.IO;
using System.Text.Json;
using AuroraCore.Utils;

namespace AuroraCore.Services;

/// <summary>One "game is running" marker on disk.</summary>
public sealed class PlaySession
{
    public string InstanceId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    /// <summary>Last time the launcher saw the game alive. Updated every
    /// <see cref="PlaytimeTracker.HeartbeatSeconds"/> seconds, and used as the end
    /// time when the process was killed instead of exiting normally.</summary>
    public DateTime HeartbeatUtc { get; set; }
    public int ProcessId { get; set; }
}

/// <summary>
/// Round-77: playtime tracking that survives a crash or a forced kill.
///
/// The naive implementation (record start, subtract on exit) silently loses the
/// whole session when the launcher dies with the game. So the running session is
/// written to disk and heartbeated; if a session file is still there on the next
/// start, the game is gone (or the launcher was killed) and the elapsed time up
/// to the LAST heartbeat is credited. Worst-case loss is one heartbeat.
/// </summary>
public sealed class PlaytimeTracker : IDisposable
{
    public const int HeartbeatSeconds = 60;

    /// <summary>A single session longer than this is treated as clock skew /
    /// a machine that slept for days, never as real play time.</summary>
    public static readonly TimeSpan MaxSession = TimeSpan.FromHours(24);

    private readonly string _dir;
    private readonly Logger _log = Logger.Instance;
    private PlaySession? _session;
    private System.Threading.Timer? _heartbeat;
    private readonly object _sync = new();

    public PlaytimeTracker(string rootDirectory)
    {
        _dir = Path.Combine(rootDirectory, "playtime");
    }

    public static string SessionPathFor(string rootDirectory, string instanceId)
        => Path.Combine(rootDirectory, "playtime", instanceId + ".session.json");

    /// <summary>Clamped duration between two timestamps (negative / absurd → 0..24h).</summary>
    public static TimeSpan Clamp(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc) return TimeSpan.Zero;
        var span = endUtc - startUtc;
        return span > MaxSession ? MaxSession : span;
    }

    /// <summary>Marks the beginning of a session and starts heartbeating.</summary>
    public void Begin(string instanceId, int processId = 0)
    {
        lock (_sync)
        {
            EndInternal(credit: false); // never leave two sessions behind
            var now = DateTime.UtcNow;
            _session = new PlaySession
            {
                InstanceId = instanceId,
                StartedUtc = now,
                HeartbeatUtc = now,
                ProcessId = processId
            };
            Write(_session);
            _heartbeat = new System.Threading.Timer(_ => Beat(), null,
                TimeSpan.FromSeconds(HeartbeatSeconds), TimeSpan.FromSeconds(HeartbeatSeconds));
            _log.Trace("Playtime", $"会话开始：实例 {instanceId}");
        }
    }

    /// <summary>Ends the session and returns the elapsed play time.</summary>
    public TimeSpan End()
    {
        lock (_sync) { return EndInternal(credit: true); }
    }

    private TimeSpan EndInternal(bool credit)
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        var session = _session;
        _session = null;
        if (session == null) return TimeSpan.Zero;

        var elapsed = Clamp(session.StartedUtc, DateTime.UtcNow);
        TryDelete(PathOf(session.InstanceId));
        if (credit)
            _log.Info("Playtime", $"会话结束：实例 {session.InstanceId}，本次 {FormatSpan(elapsed)}");
        return elapsed;
    }

    private void Beat()
    {
        lock (_sync)
        {
            if (_session == null) return;
            _session.HeartbeatUtc = DateTime.UtcNow;
            Write(_session);
        }
    }

    /// <summary>
    /// Called once at startup. Returns the sessions that were left behind and the
    /// time each should be credited with (up to its last heartbeat). Sessions whose
    /// game process is STILL alive are left alone — two launcher windows can exist
    /// on different data roots, and a live game must keep counting.
    /// </summary>
    public IReadOnlyList<(string InstanceId, TimeSpan Duration, DateTime StartedAt)> RecoverOrphans(
        Func<int, bool>? isProcessAlive = null)
    {
        var recovered = new List<(string, TimeSpan, DateTime)>();
        try
        {
            if (!Directory.Exists(_dir)) return recovered;
            foreach (var file in Directory.EnumerateFiles(_dir, "*.session.json"))
            {
                PlaySession? session = null;
                try
                {
                    session = JsonSerializer.Deserialize<PlaySession>(File.ReadAllText(file));
                }
                catch { /* corrupt marker → drop it below */ }

                if (session == null || string.IsNullOrEmpty(session.InstanceId))
                {
                    TryDelete(file);
                    continue;
                }

                if (session.ProcessId > 0 && isProcessAlive != null && isProcessAlive(session.ProcessId))
                {
                    _log.Info("Playtime", $"实例 {session.InstanceId} 的游戏进程仍在运行，保留该会话。");
                    continue;
                }

                // Credit only up to the last heartbeat: the gap between the last
                // beat and now is time we cannot prove the game was running.
                var end = session.HeartbeatUtc > session.StartedUtc ? session.HeartbeatUtc : session.StartedUtc;
                var duration = Clamp(session.StartedUtc, end);
                if (duration > TimeSpan.Zero)
                    recovered.Add((session.InstanceId, duration, session.StartedUtc));
                _log.Info("Playtime",
                    $"恢复上次未结算的会话：实例 {session.InstanceId}，计入 {FormatSpan(duration)}（进程被强制结束）");
                TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Playtime", "扫描未结算的游玩会话失败（忽略）: " + ex.Message);
        }
        return recovered;
    }

    private string PathOf(string instanceId) => Path.Combine(_dir, instanceId + ".session.json");

    private void Write(PlaySession session)
    {
        try
        {
            PathEx.EnsureDirectory(_dir);
            File.WriteAllText(PathOf(session.InstanceId),
                JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.Trace("Playtime", "写入会话标记失败: " + ex.Message); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string FormatSpan(TimeSpan span)
        => AuroraCore.Models.GameInstance.FormatPlaytime((long)Math.Max(0, span.TotalSeconds));

    public void Dispose()
    {
        lock (_sync)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
        }
    }
}

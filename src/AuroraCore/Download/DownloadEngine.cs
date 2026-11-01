using AuroraCore.Utils;

namespace AuroraCore.Download;

/// <summary>One file to fetch. Destination is the final on-disk path.</summary>
public class DownloadEntry
{
    public string Url { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? ExpectedSha1 { get; set; }
    public long? ExpectedSize { get; set; }
    public bool Required { get; set; } = true;
    public string? DisplayName { get; set; }
    public string? Category { get; set; }
}

/// <summary>Aggregated and per-file progress snapshot reported to the UI.</summary>
public class DownloadProgress
{
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public int FailedFiles { get; set; }
    public int ActiveTasks { get; set; }
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public DownloadEntry? Current { get; set; }
    public string? Message { get; set; }

    public double Fraction => TotalBytes > 0 ? Math.Min(1.0, (double)DownloadedBytes / TotalBytes) : (TotalFiles > 0 ? (double)CompletedFiles / TotalFiles : 0);
}

/// <summary>
/// High-performance, dependency-free download engine:
///  - bounded concurrency (semaphore)
///  - HTTP Range resume (does NOT assume Range support; 200 vs 206 handled)
///  - exponential backoff retry with jitter
///  - SHA-1 verification and atomic commit (.part -> move)
///  - cancellation, de-duplication, aggregated progress + speed
/// </summary>
public sealed class DownloadEngine
{
    private readonly int _maxConcurrency;
    private readonly Logger _log = Logger.Instance;

    public DownloadEngine(int? maxConcurrency = null)
    {
        _maxConcurrency = maxConcurrency ?? LauncherConfig.Instance.MaxConcurrency;
    }

    public async Task DownloadAsync(IEnumerable<DownloadEntry> entries,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        // De-duplicate by destination (a path holds exactly one artifact) while
        // keeping required status.
        var map = new Dictionary<string, DownloadEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Url) || string.IsNullOrEmpty(e.Destination)) continue;
            if (map.TryGetValue(e.Destination, out var existing))
            {
                if (e.Required) existing.Required = true;
            }
            else map[e.Destination] = e;
        }
        var list = map.Values.ToList();
        var total = list.Count;
        if (total == 0)
        {
            onProgress?.Invoke(new DownloadProgress { TotalFiles = 0, CompletedFiles = 0 });
            return;
        }

        var completed = 0;
        var failed = 0;
        var downloadedBytes = 0L;
        var totalBytes = list.Sum(e => e.ExpectedSize ?? 0);
        var sem = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var progress = new DownloadProgress
        {
            TotalFiles = total,
            TotalBytes = totalBytes
        };

        var failures = new List<(DownloadEntry entry, string reason)>();
        var stopwatch = Stopwatch.StartNew();
        var lastSample = (bytes: 0L, time: stopwatch.Elapsed);

        void Report(DownloadEntry? cur, string? msg = null)
        {
            progress.CompletedFiles = completed;
            progress.FailedFiles = failed;
            progress.DownloadedBytes = downloadedBytes;
            progress.Current = cur;
            progress.Message = msg;
            var now = stopwatch.Elapsed;
            var dt = (now - lastSample.time).TotalSeconds;
            if (dt >= 0.25)
            {
                var db = (progress.DownloadedBytes) - lastSample.bytes;
                progress.SpeedBytesPerSec = db / dt;
                lastSample = (progress.DownloadedBytes, now);
            }
            onProgress?.Invoke(progress);
        }

        var tasks = new List<Task>();
        foreach (var entry in list)
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            progress.ActiveTasks++;
            var t = Task.Run(async () =>
            {
                try
                {
                    await DownloadFileAsync(entry, (deltaBytes) =>
                    {
                        Interlocked.Add(ref downloadedBytes, deltaBytes);
                        Report(entry);
                    }, msg => Report(entry, msg), ct).ConfigureAwait(false);
                    Interlocked.Increment(ref completed);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    // Optional files that exhausted retries count as processed
                    // so "remaining files" reaches zero instead of sticking.
                    Interlocked.Increment(ref completed);
                    lock (failures) failures.Add((entry, ex.Message));
                    _log.Warn("Download", $"Failed: {entry.DisplayName ?? entry.Url} -> {ex.Message}");
                }
                finally
                {
                    sem.Release();
                    progress.ActiveTasks--;
                    Report(null);
                }
            }, ct);
            tasks.Add(t);
        }
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Download", "Download cancelled by user; cleaning up incomplete .part files.");
            CleanupPartFiles(list);
            throw;
        }

        if (failed > 0)
        {
            var requiredFailures = failures.Where(f => f.entry.Required).ToList();
            if (requiredFailures.Count > 0)
            {
                var detail = string.Join("; ", requiredFailures.Select(f => $"{f.entry.DisplayName ?? f.entry.Url}: {f.reason}"));
                throw new DownloadException($"{requiredFailures.Count} required file(s) failed to download. {detail}");
            }
        }
        _log.Info("Download", $"Download batch complete: {completed} ok, {failed} skipped (optional).");
    }

    /// <summary>Deletes the .part temp files of a batch (called when the user
    /// cancels — incomplete files are removed instead of lingering for resume).
    /// Fully downloaded and hash-verified files are NOT touched.</summary>
    private static void CleanupPartFiles(List<DownloadEntry> entries)
    {
        int removed = 0;
        foreach (var e in entries)
        {
            try
            {
                var part = e.Destination + ".part";
                if (File.Exists(part))
                {
                    File.Delete(part);
                    removed++;
                }
            }
            catch { /* best effort */ }
        }
        if (removed > 0) Logger.Instance.Info("Download", $"Removed {removed} incomplete .part file(s).");
    }

    private async Task DownloadFileAsync(DownloadEntry entry, Action<long> onBytes, Action<string>? onStatus, CancellationToken ct)
    {
        var dest = entry.Destination;
        PathEx.EnsureDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";

        // Already complete?
        if (File.Exists(dest) && VerifyHash(dest, entry.ExpectedSha1))
        {
            _log.Trace("Download", $"Cached (skip): {entry.DisplayName ?? dest}");
            return;
        }

        // Resume point.
        long existing = 0;
        if (File.Exists(part)) { existing = new FileInfo(part).Length; }

        int hashFailures = 0;
        const int maxHashFailures = 3;

            // Resume invalidation: a .part that is already at/over the expected
            // size is stale or corrupt — no server will accept a Range beyond
            // EOF (416). Generalized "resume dead" rule.
            if (existing > 0 && entry.ExpectedSize is long es && existing >= es)
            {
                _log.Warn("Download", $"Resume invalidated for {entry.DisplayName ?? entry.Url}: .part size {existing} >= expected {es}; restarting from scratch.");
                TryDeletePart(part);
                existing = 0;
            }

        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Per-attempt IDLE timeout: if no bytes arrive for 25s (stalled
            // server / dead connection), abort THIS attempt and retry — a
            // hung connection previously blocked the whole batch for minutes.
            using var idleCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
            try
            {
                long startOffset = existing;
                using var req = new HttpRequestMessage(HttpMethod.Get, entry.Url);
                bool resume = startOffset > 0;
                if (resume) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startOffset, null);

                using var resp = await HttpClientFactory.Instance.Client.SendAsync(req,
                    HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);

                if ((int)resp.StatusCode == 429)
                {
                    // Rate limited: wait then retry.
                    await Task.Delay(2000 * attempt, ct).ConfigureAwait(false);
                    continue;
                }
                if ((int)resp.StatusCode == 416)
                {
                    // Range not satisfiable: the .part file is at odds with the
                    // server (fully-sized but corrupt, or stale). Restart from
                    // scratch instead of burning attempts on the same resume.
                    _log.Warn("Download", $"416 for {entry.DisplayName ?? entry.Url}; restarting from scratch.");
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                    existing = 0;
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode} for {entry.Url}");
                }

                bool append = resp.StatusCode == (HttpStatusCode)206;
                if (append && startOffset > 0 &&
                    !IsResumeOffsetAcceptable(resp.Content.Headers.ContentRange?.ToString(), startOffset))
                {
                    _log.Warn("Download", $"Resume invalidated for {entry.DisplayName ?? entry.Url}: Content-Range mismatch; restarting from scratch.");
                    TryDeletePart(part);
                    startOffset = 0;
                    continue;
                }
                if (!append && startOffset > 0)
                {
                    // Server ignored Range and sent the whole file; restart.
                    File.Delete(part);
                    startOffset = 0;
                }
                else if (append)
                {
                    _log.Trace("Download", $"Resuming {entry.DisplayName} at {startOffset} bytes (206).");
                }

                using (var outStream = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
                {
                    await using var inStream = await resp.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await inStream.ReadAsync(buffer, attemptCts.Token).ConfigureAwait(false)) > 0)
                    {
                        await outStream.WriteAsync(buffer.AsMemory(0, read), attemptCts.Token).ConfigureAwait(false);
                        onBytes(read);
                        idleCts.CancelAfter(TimeSpan.FromSeconds(25)); // reset idle window
                    }
                    await outStream.FlushAsync(attemptCts.Token).ConfigureAwait(false);
                } // outStream is closed here so the file can be read/renamed.

                // Verify hash.
                if (!VerifyHash(part, entry.ExpectedSha1))
                {
                    hashFailures++;
                    // Corrupt payload (resume gone stale or bad CDN node):
                    // invalidate the .part and retry from scratch — but if the
                    // server-side file itself changed, retrying forever is
                    // pointless. Cap consecutive hash failures per file.
                    if (hashFailures >= maxHashFailures)
                        throw new IOException($"Hash mismatch {hashFailures}x in a row for {entry.DisplayName ?? entry.Url} — server-side file may have changed; giving up on this file.");

                    if (attempt < maxAttempts)
                    {
                        _log.Warn("Download", $"Hash mismatch for {entry.DisplayName ?? entry.Url}; invalidating resume, retrying from scratch.");
                        onStatus?.Invoke($"校验失败，从头重下 ({attempt}/{maxAttempts})：{entry.DisplayName ?? entry.Url}");
                        TryDeletePart(part);
                        existing = 0;
                        continue;
                    }
                    throw new IOException($"Hash mismatch for {entry.DisplayName ?? entry.Url} (expected {entry.ExpectedSha1 ?? "n/a"})");
                }

                // Atomic commit.
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(part, dest, overwrite: true);
                _log.Trace("Download", $"Done: {entry.DisplayName ?? dest}");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _log.Warn("Download", $"Attempt {attempt} failed for {entry.DisplayName ?? entry.Url}: {ex.Message}. Retrying...");
                onStatus?.Invoke($"重试中 ({attempt}/{maxAttempts})：{entry.DisplayName ?? entry.Url}");
                int delay = (int)(Math.Pow(2, attempt) * 250) + Random.Shared.Next(0, 250);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                // Re-evaluate resume point in case partial grew.
                if (File.Exists(part)) existing = new FileInfo(part).Length;
            }
        }
        throw new IOException($"Download failed after {maxAttempts} attempts: {entry.DisplayName ?? entry.Url}");
    }

    private static void TryDeletePart(string part)
    {
        try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
    }

    /// <summary>Pure rule: does a 206 Content-Range header confirm resuming
    /// exactly at <paramref name="startOffset"/>? Tolerant of unusual formats
    /// ("bytes 0-99/*", missing unit, garbage) — only a *conflicting start
    /// offset* invalidates the resume; anything unparseable passes (the hash
    /// check is the final arbiter). Also accepts null (no header).</summary>
    public static bool IsResumeOffsetAcceptable(string? contentRange, long startOffset)
    {
        if (string.IsNullOrWhiteSpace(contentRange)) return true;
        // Expected shape: "bytes <from>-<to>/<total|*>"
        var spaceIdx = contentRange.IndexOf(' ');
        if (spaceIdx < 0) return true; // no unit separator → don't judge
        var rangePart = contentRange[(spaceIdx + 1)..];
        var dashIdx = rangePart.IndexOf('-');
        if (dashIdx <= 0) return true; // malformed → don't judge
        if (!long.TryParse(rangePart[..dashIdx].Trim(), out var from))
            return true; // unparseable start → don't judge (hash check arbitrates)
        return from == startOffset;
    }

    private static bool VerifyHash(string file, string? expectedSha1)
    {
        if (string.IsNullOrEmpty(expectedSha1)) return true;
        if (!File.Exists(file)) return false;
        try
        {
            var actual = CryptoHelper.Sha1(file);
            return CryptoHelper.Matches(expectedSha1, actual);
        }
        catch { return false; }
    }
}

public class DownloadException : Exception
{
    public DownloadException(string message) : base(message) { }
}

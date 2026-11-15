using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AuroraCore.Utils;

/// <summary>
/// Lightweight, thread-safe, zero-dependency structured logger that writes to
/// the console and (optionally) a rolling file. Used across all modules so the
/// launcher always leaves a diagnostic trail.
///
/// Round-69: the FILE write is asynchronous. Until round-68 every log line did
/// a synchronous <c>File.AppendAllText</c> ON THE CALLING THREAD — and the
/// caller is usually the UI thread (view-model actions, network continuations
/// resuming on the dispatcher). During a download four routing attempts log
/// several lines per file, so the UI thread was doing dozens of open/append/
/// close cycles per second, each of which can take milliseconds with a real
/// time antivirus filter. That is exactly the "switching pages still feels
/// sticky" symptom: the click is cheap, but the dispatcher is busy with disk
/// I/O it never needed to wait for.
///
/// Now lines are enqueued (lock-free) and a single background thread does the
/// appending in batches. <see cref="Flush"/> makes the file authoritative again
/// for the diagnostics bundle and for process shutdown.
/// </summary>
public sealed class Logger
{
    private static readonly Logger _instance = new();
    public static Logger Instance => _instance;

    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<string> _pendingWrites = new();
    private readonly AutoResetEvent _writeSignal = new(false);
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private const int MaxRecent = 500;

    private Thread? _writer;
    private long _enqueued;
    private long _written;

    private string? _logFilePath;

    public enum Level { Trace, Info, Warn, Error }

    public event Action<LogEntry>? OnLog;

    public void Configure(string? logDirectory = null)
    {
        if (logDirectory != null)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                _logFilePath = Path.Combine(logDirectory, $"aurora-{DateTime.Now:yyyy-MM-dd}.log");
            }
            catch
            {
                _logFilePath = null;
            }
        }
    }

    /// <summary>Where the rolling file lives (null = file logging disabled).</summary>
    public string? LogFilePath => _logFilePath;

    public void Log(Level level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);
        _recent.Enqueue(entry);
        while (_recent.Count > MaxRecent) _recent.TryDequeue(out _);

        var line = $"[{entry.Time:HH:mm:ss}] [{level}] [{source}] {message}";

        // Enqueue only — never touch the disk on this thread (round-69).
        if (_logFilePath != null)
        {
            _pendingWrites.Enqueue(line);
            Interlocked.Increment(ref _enqueued);
            EnsureWriter();
        }

        // Console colorization (no-op on non-interactive hosts).
        try
        {
            ConsoleColor? color = level switch
            {
                Level.Error => ConsoleColor.Red,
                Level.Warn => ConsoleColor.Yellow,
                Level.Trace => ConsoleColor.DarkGray,
                _ => null
            };
            if (color.HasValue)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color.Value;
                Console.WriteLine(line);
                Console.ForegroundColor = prev;
            }
            else
            {
                Console.WriteLine(line);
            }
        }
        catch { }

        OnLog?.Invoke(entry);
    }

    public void Info(string src, string msg) => Log(Level.Info, src, msg);
    public void Warn(string src, string msg) => Log(Level.Warn, src, msg);
    public void Error(string src, string msg) => Log(Level.Error, src, msg);
    public void Trace(string src, string msg) => Log(Level.Trace, src, msg);

    public IReadOnlyList<LogEntry> RecentLogs() => _recent.ToArray();

    /// <summary>Blocks until every queued line has hit the file (or the timeout
    /// expires). Called before exporting diagnostics and on shutdown; it is NOT
    /// part of the logging hot path.</summary>
    public bool Flush(int timeoutMs = 3000)
    {
        if (_logFilePath == null) return true;
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _written) < Volatile.Read(ref _enqueued))
        {
            _writeSignal.Set();
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            Thread.Sleep(4);
        }
        return true;
    }

    private void EnsureWriter()
    {
        if (_writer != null) return;
        lock (_fileLock)
        {
            if (_writer != null) return;
            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "aurora-log-writer"
            };
            _writer.Start();
        }
    }

    private void WriterLoop()
    {
        var batch = new StringBuilder(16 * 1024);
        while (true)
        {
            var lines = 0;
            try
            {
                _writeSignal.WaitOne(1000);
                if (_pendingWrites.IsEmpty) continue;

                // Coalesce: a burst (four routing attempts failing at once, an
                // install reporting progress) becomes ONE open/append/close
                // instead of one per line. 15 ms is invisible in a log file and
                // cuts disk churn on the writer thread by an order of magnitude.
                Thread.Sleep(15);

                batch.Clear();
                while (_pendingWrites.TryDequeue(out var line))
                {
                    batch.Append(line).Append(Environment.NewLine);
                    lines++;
                }

                var path = _logFilePath;
                if (path != null)
                {
                    lock (_fileLock)
                    {
                        // FileShare.ReadWrite: the single-instance guard closes the
                        // window before this file is locked out, but two processes
                        // (old + new during an update, portable copies) may briefly
                        // append at once — a sharing violation must not eat the line.
                        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                        var bytes = System.Text.Encoding.UTF8.GetBytes(batch.ToString());
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch
            {
                // A failed write must never kill the writer (nor the app).
            }
            finally
            {
                // Count the batch as handled either way, so Flush() can never
                // spin forever on a disk that keeps failing.
                if (lines > 0) Interlocked.Add(ref _written, lines);
            }
        }
    }
}

public readonly record struct LogEntry(DateTime Time, Logger.Level Level, string Source, string Message);

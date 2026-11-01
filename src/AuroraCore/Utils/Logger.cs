using System.Collections.Concurrent;

namespace AuroraCore.Utils;

/// <summary>
/// Lightweight, thread-safe, zero-dependency structured logger that writes to
/// the console and (optionally) a rolling file. Used across all modules so the
/// launcher always leaves a diagnostic trail.
/// </summary>
public sealed class Logger
{
    private static readonly Logger _instance = new();
    public static Logger Instance => _instance;

    private readonly object _fileLock = new();
    private string? _logFilePath;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private const int MaxRecent = 500;

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

    public void Log(Level level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);
        _recent.Enqueue(entry);
        while (_recent.Count > MaxRecent) _recent.TryDequeue(out _);

        var line = $"[{entry.Time:HH:mm:ss}] [{level}] [{source}] {message}";
        try
        {
            if (_logFilePath != null)
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
        }
        catch { /* never let logging break the app */ }

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
}

public readonly record struct LogEntry(DateTime Time, Logger.Level Level, string Source, string Message);

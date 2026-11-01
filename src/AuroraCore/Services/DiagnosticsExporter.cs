using AuroraCore.Models;
using AuroraCore.Download;
using AuroraCore.Utils;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace AuroraCore.Services;

/// <summary>One-click diagnostics bundle: zips recent logs (sanitized), a
/// system-info sheet, the Java census, instance metadata and recent
/// install/download records — everything a bug report needs, so maintainers
/// never have to ask "what OS / which Java / which mods" again.</summary>
public static class DiagnosticsExporter
{
    /// <summary>Per-file cap for copied logs: a Trace-complete file log can
    /// reach tens of MB; without a cap the bundle becomes unsendable.</summary>
    private const long MaxLogBytes = 2 * 1024 * 1024;

    public static async Task ExportAsync(string zipPath, string rootDir, IEnumerable<GameInstance> instances)
    {
        var logDir = Path.Combine(rootDir, "logs");
        var tmp = Path.Combine(Path.GetTempPath(), "aurora-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            // 1) Recent logs (up to 3 newest) — TAIL-truncated per file and
            //    fully sanitized (tokens, device codes, user paths, proxies).
            var logs = Directory.Exists(logDir)
                ? Directory.EnumerateFiles(logDir, "*.log").OrderByDescending(File.GetLastWriteTime).Take(3).ToList()
                : new List<string>();
            foreach (var log in logs)
            {
                var dst = Path.Combine(tmp, "logs", Path.GetFileName(log));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                WriteTextTail(log, dst, MaxLogBytes, rootDir);
            }

            // 2) config.json — sanitized.
            var cfg = Path.Combine(rootDir, "config.json");
            if (File.Exists(cfg))
            {
                Directory.CreateDirectory(Path.Combine(tmp));
                File.WriteAllText(Path.Combine(tmp, "config.json"), Sanitize(File.ReadAllText(cfg), rootDir));
            }

            // 3) System info.
            var si = new System.Text.StringBuilder();
            si.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            si.AppendLine($"Arch: {RuntimeInformation.ProcessArchitecture}");
            si.AppendLine($".NET: {Environment.Version}");
            si.AppendLine($"CPU cores: {Environment.ProcessorCount}");
            si.AppendLine($"Working set: {Environment.WorkingSet / 1024 / 1024} MB");
            try
            {
                var gc = GC.GetGCMemoryInfo();
                si.AppendLine($"Total physical memory: {gc.TotalAvailableMemoryBytes / 1024 / 1024} MB");
            }
            catch { }
            si.AppendLine($"Launcher root: {rootDir}");
            await File.WriteAllTextAsync(Path.Combine(tmp, "system-info.txt"), si.ToString());

            // 4) Java census.
            try
            {
                var finder = new JavaFinder();
                var javas = await finder.FindAllAsync(default);
                var jl = new System.Text.StringBuilder();
                foreach (var j in javas)
                    jl.AppendLine($"{j.Major} ({j.Version}) - {j.Vendor} @ {j.Home}");
                await File.WriteAllTextAsync(Path.Combine(tmp, "java-list.txt"), jl.ToString());
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(Path.Combine(tmp, "java-list.txt"), "探测失败: " + ex.Message);
            }

            // 5) Instance metadata (incl. per-instance mod census).
            var mods = new ModManager(new DownloadEngine(4));
            var im = new System.Text.StringBuilder();
            foreach (var inst in instances)
            {
                int modCount = 0;
                string? newestMod = null;
                try
                {
                    var dir = inst.Directory;
                    var modsDir = Path.Combine(dir, "mods");
                    if (Directory.Exists(modsDir))
                    {
                        var files = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                            .OrderByDescending(File.GetLastWriteTime).ToList();
                        modCount = files.Count;
                        newestMod = files.FirstOrDefault() is { } f ? Path.GetFileName(f) : null;
                    }
                }
                catch { }
                im.AppendLine($"{inst.Name} | MC {inst.MinecraftVersion} | {inst.LoaderType} {inst.LoaderVersion ?? "(自动)"} | Mods: {modCount}{(newestMod != null ? $" (最近: {newestMod})" : "")} | resolved: {inst.ResolvedVersionId ?? "-"}");
            }
            await File.WriteAllTextAsync(Path.Combine(tmp, "instances.txt"), im.ToString());

            // 6) Recent install/download records from the newest log.
            if (logs.Count > 0)
            {
                try
                {
                    var lines = File.ReadAllLines(logs[0]);
                    var records = lines
                        .Where(l => l.Contains("[Info] [Install]") || l.Contains("[Info] [Download]") || l.Contains("[Info] [ModLoader]"))
                        .TakeLast(20);
                    await File.WriteAllTextAsync(Path.Combine(tmp, "recent-records.txt"), string.Join(Environment.NewLine, records));
                }
                catch { }
            }

            // Final pass: sanitize EVERY text file in the bundle (defense in
            // depth — logs may contain device codes, tokens in raw responses,
            // user paths, authenticated proxy URLs). Every replacement is
            // recorded in a redaction report shipped INSIDE the bundle: users
            // see "what was touched" and trust the rest of the content.
            var report = new System.Text.StringBuilder();
            report.AppendLine("========== 脱敏报告 (REDACTION REPORT) ==========");
            report.AppendLine("以下替换仅作用于诊断包内的文本；原文件未改动。");
            report.AppendLine("脱敏为【替换】而非删除——保留字段结构，便于判断字段是否存在。");
            report.AppendLine();
            int totalHits = 0;
            foreach (var f in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith("redaction-report.txt", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var text = File.ReadAllText(f);
                    var (clean, findings) = SanitizeWithReport(text, rootDir);
                    if (findings.Count > 0)
                    {
                        var rel = Path.GetRelativePath(tmp, f);
                        report.AppendLine($"[{rel}]");
                        foreach (var line in findings) report.AppendLine("  " + line);
                        report.AppendLine();
                        totalHits += findings.Count;
                    }
                    File.WriteAllText(f, clean);
                }
                catch { /* binary or locked — skip */ }
            }
            if (totalHits == 0) report.AppendLine("(未发现任何敏感内容，未做替换)");
            report.AppendLine($"合计替换 {totalHits} 处。");
            report.AppendLine("=============================================");
            File.WriteAllText(Path.Combine(tmp, "redaction-report.txt"), report.ToString());

            // 7) Zip it.
            PathEx.EnsureDirectory(Path.GetDirectoryName(zipPath)!);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tmp, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    /// <summary>Copies the LAST <paramref name="maxBytes"/> of a log file
    /// (newline-aligned) so Trace-complete files stay sendable.</summary>
    private static void WriteTextTail(string src, string dst, long maxBytes, string rootDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
                var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                if (start > 0)
                {
                    // Drop the first (likely mid-line) fragment.
                    var nl = text.IndexOf('\n');
                    if (nl >= 0) text = text[(nl + 1)..];
                    text = TruncateTail(text, maxBytes); // ensures the tail marker + consistency
                }
                File.WriteAllText(dst, Sanitize(text, rootDir));
        }
        catch { /* best effort */ }
    }

    /// <summary>Field-level redaction (keeps structure: field exists, value
    /// replaced) plus general pattern scrubbing — applied to EVERY text file
    /// in the bundle so users can send it without fear. Returns the cleaned
    /// text plus a per-pattern hit list for the redaction report.</summary>
    public static (string Text, List<string> Findings) SanitizeWithReport(string text, string rootDir)
    {
        var findings = new List<string>();

        void Replace(string label, string pattern, string replacement)
        {
            try
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (matches.Count == 0) return;
                text = System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                findings.Add($"{matches.Count}× {label}");
            }
            catch { /* malformed pattern — never break the export */ }
        }

        // Local user name in paths.
        var userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName) && text.Contains(userName, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add($"{CountOccurrences(text, userName)}× 本地用户名 → <user>");
            text = text.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        }
        // Absolute launcher root path.
        if (!string.IsNullOrEmpty(rootDir) && text.Contains(rootDir, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add($"{CountOccurrences(text, rootDir)}× 启动器根路径 → <root>");
            text = text.Replace(rootDir, "<root>", StringComparison.OrdinalIgnoreCase);
        }
        // Any C:\Users\<name>\... style profile path.
        Replace("用户目录 (C:\\Users\\<名>)", @"[A-Za-z]:\\Users\\[^\\\r\n""]+", "<user-profile>");
        // Authenticated proxy URLs: user:pass@host.
        Replace("代理凭据 (user:pass@host)", @"(?i)\b(https?|socks5)://[^\s/""':]+:[^\s/""'@]+@", "$1://<credentials>@");
        // Token-shaped JSON/YAML fields (keep the field, redact the value).
        Replace("令牌形字段 (access/refresh/device_code/client_id/xuid)", @"(?i)(""?(access[_-]?token|refresh[_-]?token|device[_-]?code|client[_-]?id|xuid|user[_-]?hash|proxy[_-]?password)""?\s*[:=]\s*"")[^""]*""", "$1<REDACTED>\"");
        // Bearer tokens anywhere.
        Replace("Bearer 令牌", @"(?i)bearer\s+[A-Za-z0-9._\-]{16,}", "Bearer <REDACTED>");

        return (text, findings);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    /// <summary>Backwards-compatible single-shot sanitizer (no report).</summary>
    public static string Sanitize(string text, string rootDir) => SanitizeWithReport(text, rootDir).Text;

    /// <summary>PURE truncation rule for diagnostic logs: keep the TAIL
    /// (crash lines live at the END of a log), never the head. Marks the cut
    /// so readers know content was dropped.</summary>
    public static string TruncateTail(string text, long maxBytes)
    {
        if (maxBytes <= 0 || text.Length * 2 <= maxBytes) return text; // conservative: UTF-16 upper bound
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes) return text;
        var slice = bytes[^((int)maxBytes)..];
        var tail = System.Text.Encoding.UTF8.GetString(slice);
        // Drop the first (likely mid-character/mid-line) fragment.
        var nl = tail.IndexOf('\n');
        if (nl >= 0) tail = tail[(nl + 1)..];
        return "……（诊断包仅保留日志末尾部分，头部已截断）\n" + tail;
    }
}

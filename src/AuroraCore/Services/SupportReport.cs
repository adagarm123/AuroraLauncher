namespace AuroraCore.Services;

/// <summary>
/// Round-78: "rare crash → mail the author" without an SMTP client.
///
/// Sending mail from inside the program would mean shipping credentials (or
/// asking the user for theirs), and would let any crash spam the author. A
/// <c>mailto:</c> link handed to the user's own mail client is the opposite:
/// nothing leaves the machine until the user presses Send, the body is fully
/// visible, and the diagnostics bundle is attached by the user themselves.
/// Pure string building — unit-testable, no IO.
/// </summary>
public static class SupportReport
{
    public const string AuthorEmail = "adagarm0216@outlook.com";

    /// <summary>Most mail clients silently truncate very long mailto URLs
    /// (~2000 chars). We stay well below and shorten the cause list instead.</summary>
    public const int MaxUrlLength = 1600;

    public sealed record Context
    {
        public string InstanceName { get; init; } = "未知实例";
        public int ExitCode { get; init; }
        public string MinecraftVersion { get; init; } = "";
        public string Loader { get; init; } = "";
        public string? BundlePath { get; init; }
        public string Signature { get; init; } = "";
        public IReadOnlyList<string> AttemptedFixes { get; init; } = Array.Empty<string>();
        public string OsDescription { get; init; } = "";
        public string JavaText { get; init; } = "";
    }

    /// <summary>Builds the whole mailto URI: subject carries the build round +
    /// a stable crash signature, body carries the diagnosis the launcher
    /// already worked out, so the author does not have to re-read the log.</summary>
    public static string BuildMailto(IReadOnlyList<CrashSolution> solutions, Context ctx)
    {
        ctx ??= new Context();
        var subject = $"[Aurora 崩溃报告] {Short(ctx.InstanceName)} · 第 {BuildInfo.CurrentRound} 轮" +
                      (string.IsNullOrEmpty(ctx.Signature) ? "" : $" · {ctx.Signature}");

        var body = BuildBody(solutions, ctx);
        // Trim from the cause list until the URL fits a mail client.
        while (body.Length > 1200 && solutions.Count > 1)
        {
            solutions = solutions.Take(solutions.Count - 1).ToList();
            body = BuildBody(solutions, ctx);
        }
        if (body.Length > 1200) body = body[..1200] + "\n…（内容过长已截断）";

        return $"mailto:{AuthorEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
    }

    private static string BuildBody(IReadOnlyList<CrashSolution> solutions, Context ctx)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("作者你好，我遇到了一个启动器没能自动解决的问题，麻烦帮忙看看。");
        sb.AppendLine();
        sb.AppendLine("【环境】");
        sb.AppendLine($"实例：{ctx.InstanceName}");
        sb.AppendLine($"游戏版本：{(string.IsNullOrEmpty(ctx.MinecraftVersion) ? "未知" : ctx.MinecraftVersion)}" +
                      $"{(string.IsNullOrEmpty(ctx.Loader) ? "" : " · 加载器：" + ctx.Loader)}");
        sb.AppendLine($"退出代码：{ctx.ExitCode}");
        sb.AppendLine($"启动器：{BuildInfo.Version}（第 {BuildInfo.CurrentRound} 轮）");
        if (!string.IsNullOrEmpty(ctx.OsDescription)) sb.AppendLine($"系统：{ctx.OsDescription}");
        if (!string.IsNullOrEmpty(ctx.JavaText)) sb.AppendLine($"Java：{ctx.JavaText}");
        if (!string.IsNullOrEmpty(ctx.Signature)) sb.AppendLine($"崩溃指纹：{ctx.Signature}");
        sb.AppendLine();

        sb.AppendLine("【启动器的诊断】");
        if (solutions.Count == 0)
        {
            sb.AppendLine("（没有匹配到已知规则，属于罕见崩溃）");
        }
        else
        {
            var i = 1;
            foreach (var s in solutions)
            {
                sb.AppendLine($"{i++}. {s.Title}");
                sb.AppendLine("   " + OneLine(s.Diagnosis));
            }
        }
        sb.AppendLine();

        sb.AppendLine("【我已经试过的操作】");
        if (ctx.AttemptedFixes.Count == 0) sb.AppendLine("（还没有尝试自动修复）");
        else foreach (var f in ctx.AttemptedFixes) sb.AppendLine("- " + f);
        sb.AppendLine();

        sb.AppendLine("【诊断包】");
        sb.AppendLine(string.IsNullOrEmpty(ctx.BundlePath)
            ? "（未导出）请点击「一键导出诊断包」后，把 zip 拖到附件里。"
            : ctx.BundlePath + "\n（请把这个文件拖进附件再发送。）");
        sb.AppendLine();
        sb.AppendLine("【补充说明】");
        sb.AppendLine("（在这里补充崩溃前你在做什么，会很有帮助）");
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Short(string text) =>
        string.IsNullOrWhiteSpace(text) ? "未知实例" : (text.Length > 20 ? text[..20] : text);

    /// <summary>Round-trip parser, used by the self-test to prove the URL is
    /// well formed (right recipient, encoded subject/body, no raw newlines).</summary>
    public static bool TryParse(string? mailto, out string to, out string subject, out string body)
    {
        to = subject = body = "";
        if (string.IsNullOrWhiteSpace(mailto) || !mailto.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = mailto["mailto:".Length..];
        var q = rest.IndexOf('?');
        to = q < 0 ? rest : rest[..q];
        if (q < 0) return to.Length > 0;
        foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (key.Equals("subject", StringComparison.OrdinalIgnoreCase)) subject = value;
            else if (key.Equals("body", StringComparison.OrdinalIgnoreCase)) body = value;
        }
        return to.Length > 0;
    }
}

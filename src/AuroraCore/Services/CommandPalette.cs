namespace AuroraCore.Services;

/// <summary>Where a palette entry jumps to. Drives the small badge on the row.</summary>
public enum PaletteKind { Action, Page, Instance, Mod, Setting, Web }

/// <summary>
/// One row of the Ctrl+K palette. <see cref="Payload"/> is opaque to the core:
/// the view model decides what "page:Settings" or "instance:&lt;id&gt;" means.
/// </summary>
public sealed record PaletteItem(PaletteKind Kind, string Title, string Subtitle, string Action, string? Payload = null)
{
    public string KindText => Kind switch
    {
        PaletteKind.Action => "操作",
        PaletteKind.Page => "页面",
        PaletteKind.Instance => "实例",
        PaletteKind.Mod => "模组",
        PaletteKind.Setting => "设置",
        _ => "网页"
    };

    public string Glyph => Kind switch
    {
        PaletteKind.Action => "\uE945",   // lightning
        PaletteKind.Page => "\uE8A9",     // page
        PaletteKind.Instance => "\uE71D", // box
        PaletteKind.Mod => "\uE8F1",      // puzzle
        PaletteKind.Setting => "\uE713",  // gear
        _ => "\uE774"                     // globe
    };
}

/// <summary>
/// Round-79: Ctrl+K command palette — a VS Code style "type three letters, press
/// Enter" jump box over everything the launcher already knows: pages, instances,
/// installed mods, settings sections and one-shot actions.
///
/// Pure scoring so the ranking can be tested without a window: every query term
/// must appear somewhere (AND), title matches outrank subtitle matches, and a
/// prefix match outranks a mid-word match. No fuzzy magic — predictable beats
/// clever when the user is typing at speed.
/// </summary>
public static class CommandPalette
{
    public const int DefaultLimit = 12;

    // Action ids are the contract between this list and MainViewModel's
    // RunPalette switch; the self-test asserts every id is handled there.
    public const string ActionLaunch = "launch";
    public const string ActionBackupNow = "backup-now";
    public const string ActionCheckUpdate = "check-update";
    public const string ActionExportDiagnostics = "export-diagnostics";
    public const string ActionExportLogs = "export-logs";
    public const string ActionOpenRoot = "open-root";
    public const string ActionSwitchTheme = "switch-theme";
    public const string ActionOpenModsFolder = "open-mods";
    public const string ActionOpenSavesFolder = "open-saves";
    public const string ActionInstallJava = "install-java";

    /// <summary>Pages, settings and actions — the static half of the index.</summary>
    public static IReadOnlyList<PaletteItem> StaticItems { get; } = new List<PaletteItem>
    {
        new(PaletteKind.Action, "启动游戏", "用当前实例与账户立即启动", ActionLaunch),
        new(PaletteKind.Action, "立即备份存档", "把当前实例的 saves 打包到备份目录", ActionBackupNow),
        new(PaletteKind.Action, "检查更新", "查询更新源是否有新版本", ActionCheckUpdate),
        new(PaletteKind.Action, "导出诊断包", "日志 + 系统信息 + Java 清单（已脱敏）", ActionExportDiagnostics),
        new(PaletteKind.Action, "导出日志", "导出最近的脱敏日志文本", ActionExportLogs),
        new(PaletteKind.Action, "切换主题", "浅色 / 深色", ActionSwitchTheme),
        new(PaletteKind.Action, "打开 mods 文件夹", "当前实例的模组目录", ActionOpenModsFolder),
        new(PaletteKind.Action, "打开 saves 文件夹", "当前实例的存档目录", ActionOpenSavesFolder),
        new(PaletteKind.Action, "一键安装 Java 21", "Adoptium Temurin，装到启动器目录", ActionInstallJava),
        new(PaletteKind.Action, "打开存储目录", "配置、实例、缓存所在位置", ActionOpenRoot),

        new(PaletteKind.Page, "首页", "账户 / 实例 / 启动 / 性能概览", "page", "Home"),
        new(PaletteKind.Page, "游戏下载", "版本列表与安装", "page", "Download"),
        new(PaletteKind.Page, "实例管理", "个性化 / 游玩统计 / 存档备份", "page", "Instances"),
        new(PaletteKind.Page, "内容中心", "Modrinth / CurseForge / 本地模组", "page", "Mods"),
        new(PaletteKind.Page, "下载管理", "所有进行中与已完成的下载", "page", "Downloads"),
        new(PaletteKind.Page, "设置", "内存 / Java / 更新 / 网络 / 备份", "page", "Settings"),
        new(PaletteKind.Page, "日志", "运行日志与崩溃诊断", "page", "Logs"),
        new(PaletteKind.Page, "更新日志", "每个版本改了什么", "page", "Changelog"),
        new(PaletteKind.Page, "帮助中心", "内置离线帮助", "page", "Help"),

        new(PaletteKind.Setting, "最大内存", "设置 → 内存分配", "setting", "Settings"),
        new(PaletteKind.Setting, "Java 路径", "设置 → Java 环境", "setting", "Settings"),
        new(PaletteKind.Setting, "更新方式", "自动 / 提醒 / 不检查", "setting", "Settings"),
        new(PaletteKind.Setting, "下载源与代理", "官方 / BMCLAPI / 代理地址", "setting", "Settings"),
        new(PaletteKind.Setting, "存档自动备份", "启动前 / 退出后 / 保留份数", "setting", "Settings"),
        new(PaletteKind.Setting, "关闭行为与托盘", "关窗是退出还是收进托盘", "setting", "Settings"),
        new(PaletteKind.Setting, "启动前模组体检", "缺前置 / 冲突 / 重复", "setting", "Settings")
    };

    /// <summary>Adds the dynamic half (instances + installed mods) and always
    /// appends a "search Modrinth" entry for whatever was typed.</summary>
    public static IReadOnlyList<PaletteItem> BuildIndex(
        IEnumerable<(string Id, string Name, string Version, string Loader)> instances,
        IEnumerable<(string Name, string FileName)> mods,
        string? query = null)
    {
        var items = new List<PaletteItem>(StaticItems.Count + 64);
        items.AddRange(StaticItems);

        foreach (var inst in instances)
        {
            var sub = string.Join(" · ", new[] { inst.Version, inst.Loader }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            items.Add(new PaletteItem(PaletteKind.Instance, inst.Name,
                string.IsNullOrWhiteSpace(sub) ? "实例" : sub, "instance", inst.Id));
        }

        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Name) && string.IsNullOrWhiteSpace(mod.FileName)) continue;
            items.Add(new PaletteItem(PaletteKind.Mod,
                string.IsNullOrWhiteSpace(mod.Name) ? mod.FileName : mod.Name,
                mod.FileName, "mod", mod.FileName));
        }

        var q = (query ?? "").Trim();
        if (q.Length >= 2)
            items.Add(new PaletteItem(PaletteKind.Web, $"在 Modrinth 搜索「{q}」",
                "打开内容中心并按关键词搜索", "web", q));

        return items;
    }

    /// <summary>Ranked, capped result list. Empty query returns the head of the
    /// list (actions first) so opening the palette is instantly useful.</summary>
    public static IReadOnlyList<PaletteItem> Search(IEnumerable<PaletteItem>? items, string? query, int limit = DefaultLimit)
    {
        if (items == null) return Array.Empty<PaletteItem>();
        var list = items as IList<PaletteItem> ?? items.ToList();
        if (list.Count == 0) return Array.Empty<PaletteItem>();
        var cap = limit <= 0 ? DefaultLimit : limit;

        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return list.OrderBy(i => KindRank(i.Kind)).ThenBy(i => i.Title, StringComparer.CurrentCulture)
                       .Take(cap).ToList();

        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = new List<(PaletteItem Item, int Score)>();
        foreach (var item in list)
        {
            var score = Score(item, terms, q);
            if (score > 0) scored.Add((item, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => KindRank(s.Item.Kind))
            .ThenBy(s => s.Item.Title, StringComparer.CurrentCulture)
            .Take(cap)
            .Select(s => s.Item)
            .ToList();
    }

    /// <summary>0 = does not match (at least one term missing).</summary>
    public static int Score(PaletteItem item, IReadOnlyList<string> terms, string fullQuery)
    {
        if (terms.Count == 0) return 1;
        var title = item.Title ?? "";
        var subtitle = item.Subtitle ?? "";
        var score = 0;

        foreach (var term in terms)
        {
            var inTitle = title.IndexOf(term, StringComparison.CurrentCultureIgnoreCase);
            var inSubtitle = subtitle.IndexOf(term, StringComparison.CurrentCultureIgnoreCase);
            if (inTitle < 0 && inSubtitle < 0) return 0;
            if (inTitle == 0) score += 120;
            else if (inTitle > 0) score += 70;
            else score += 25;
        }

        if (string.Equals(title, fullQuery, StringComparison.CurrentCultureIgnoreCase)) score += 200;
        else if (title.StartsWith(fullQuery, StringComparison.CurrentCultureIgnoreCase)) score += 60;

        return score;
    }

    private static int KindRank(PaletteKind kind) => kind switch
    {
        PaletteKind.Action => 0,
        PaletteKind.Page => 1,
        PaletteKind.Instance => 2,
        PaletteKind.Setting => 3,
        PaletteKind.Mod => 4,
        _ => 5
    };
}

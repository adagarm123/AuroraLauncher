using AuroraCore.Services;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>One CurseForge search result for the browse panel.</summary>
public class CurseForgeItemVM
{
    public CurseForgeItemVM(CurseForgeMod mod)
    {
        Id = mod.Id;
        Slug = mod.Slug;
        WebsiteUrl = mod.WebsiteUrl;
        Name = mod.Name;
        Summary = string.IsNullOrWhiteSpace(mod.Summary) ? "暂无简介" : mod.Summary;
        DownloadsText = mod.Downloads >= 100_000_000
            ? $"{mod.Downloads / 100_000_000.0:0.0} 亿次下载"
            : mod.Downloads >= 10_000
                ? $"{mod.Downloads / 10_000.0:0.0} 万次下载"
                : $"{mod.Downloads:N0} 次下载";
    }

    public long Id { get; }
    public string Slug { get; }
    public string WebsiteUrl { get; }
    public string Name { get; }
    public string Summary { get; }
    public string DownloadsText { get; }
    public CurseForgeMod Model => new(Id, Name, Summary, Slug, 0, WebsiteUrl);
}

/// <summary>One OptiFine installer build listed on the official downloads page.</summary>
public class OptiFineItemVM
{
    public OptiFineItemVM(OptiFineBuild build)
    {
        FileName = build.FileName;
        Label = build.Label;
        McVersion = build.McVersion;
        PageUrl = build.PageUrl;
        Display = string.IsNullOrWhiteSpace(McVersion) ? Label : $"MC {McVersion} · {Label}";
    }

    public string FileName { get; }
    public string Label { get; }
    public string McVersion { get; }
    public string PageUrl { get; }
    public string Display { get; }
    public OptiFineBuild Build => new(FileName, Label, McVersion, PageUrl);
}

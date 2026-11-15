using AuroraCore.Services;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>One row of the settings cache inventory. Pure display model.</summary>
public sealed class CacheFolderVM
{
    public CacheFolderVM(CacheFolderInfo info)
    {
        Key = info.Key;
        DisplayName = info.DisplayName;
        Path = info.Path;
        Description = info.Description;
        SizeText = info.SizeText;
        FileCount = info.FileCount;
        Exists = info.Exists;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public string Path { get; }
    public string Description { get; }
    public string SizeText { get; }
    public int FileCount { get; }
    public bool Exists { get; }
    public string Line => $"{DisplayName} · {SizeText} · {FileCount} 个文件" + (Exists ? "" : "（尚未生成）");
}

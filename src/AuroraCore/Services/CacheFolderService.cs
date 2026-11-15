namespace AuroraCore.Services;

public sealed record CacheFolderInfo(
    string Key, string DisplayName, string Path, string Description,
    long SizeBytes, int FileCount, string SizeText, bool Exists);

/// <summary>Read-only disk inventory for the settings cache viewer. Every
/// entry is best-effort: an unreadable folder is reported as 0 bytes instead
/// of breaking the whole page.</summary>
public static class CacheFolderService
{
    public static IReadOnlyList<CacheFolderInfo> Scan(string root)
    {
        var list = new List<CacheFolderInfo>();
        if (!CacheFolderPolicy.IsSafeRoot(root)) return list;

        foreach (var spec in CacheFolderPolicy.Folders)
        {
            var path = CacheFolderPolicy.FullPath(root, spec);
            long size = 0;
            var count = 0;
            var exists = spec.IsDirectory ? Directory.Exists(path) : File.Exists(path);
            try
            {
                if (spec.IsDirectory && exists)
                {
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { size += new FileInfo(f).Length; count++; }
                        catch { /* locked or vanished mid-scan */ }
                    }
                }
                else if (!spec.IsDirectory && exists)
                {
                    size = new FileInfo(path).Length;
                    count = 1;
                }
            }
            catch { /* best effort */ }

            list.Add(new CacheFolderInfo(spec.Key, spec.DisplayName, path, spec.Description,
                size, count, CacheFolderPolicy.FormatSize(size), exists));
        }
        return list;
    }

    public static string TotalText(IEnumerable<CacheFolderInfo> items)
        => CacheFolderPolicy.FormatSize(items.Sum(i => i.SizeBytes));
}

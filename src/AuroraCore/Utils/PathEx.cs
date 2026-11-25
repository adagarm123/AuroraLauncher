namespace AuroraCore.Utils;

public static class PathEx
{
    public static string Combine(params string[] parts) => Path.Combine(parts);

    /// <summary>Round-106 (M1): single zip-entry path guard shared by EVERY
    /// extraction path (natives, Java packages, mrpacks). Rejects absolute
    /// paths, drive letters, parent traversal and NTFS alternate data streams
    /// before the entry is combined with the destination directory.</summary>
    public static bool IsSafeEntryPath(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        var normalized = fullName.Replace('\\', '/');
        if (normalized[0] == '/') return false;
        if (normalized.Contains("..", StringComparison.Ordinal)) return false;
        if (normalized.Contains(':', StringComparison.Ordinal)) return false;
        return !Path.IsPathRooted(fullName);
    }

    public static void EnsureDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>Converts a Maven-style library name (group:artifact:version[:classifier]) to a relative path.</summary>
    public static string MavenNameToPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) throw new ArgumentException($"Invalid Maven name: {name}", nameof(name));
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;
        var fileName = classifier == null
            ? $"{artifact}-{version}.jar"
            : $"{artifact}-{version}-{classifier}.jar";
        return $"{group}/{artifact}/{version}/{fileName}";
    }

    /// <summary>Removes illegal filename characters.</summary>
    public static string Sanitize(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}

using AuroraCore.Utils;
using Microsoft.Win32;

namespace AuroraCore.Services;

public class JavaInstallation
{
    public string JavaExecutable { get; set; } = "";
    public string Home { get; set; } = "";
    public string Version { get; set; } = "";
    public int Major { get; set; }
    public string Vendor { get; set; } = "";

    public override string ToString() => $"Java {Major} ({Version}) - {Vendor} @ {Home}";
}

/// <summary>
/// Discovers installed Java runtimes by scanning well-known directories and the
/// PATH, then probing each candidate with `java -version`. Does NOT modify any
/// system environment variables.
/// </summary>
public sealed class JavaFinder
{
    private readonly Logger _log = Logger.Instance;

    private static readonly string[] ScanRoots =
    {
        @"C:\Program Files\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\AdoptOpenJDK",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\BellSoft",
        @"C:\Program Files\Amazon Corretto",
        @"C:\Program Files\Zulu",
        @"C:\Program Files (x86)\Java",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
    };

    public async Task<List<JavaInstallation>> FindAllAsync(CancellationToken ct = default)
    {
        var found = new Dictionary<string, JavaInstallation>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>(ScanRoots);
        // Registry-based discovery (Temurin / Adoptium / Oracle).
        TryAddFromRegistry(found);

        foreach (var root in dirs)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    ProbeDir(dir, found);
                    // one level deeper (e.g. jdk-21.0.2+13\bin\java.exe)
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                            ProbeDir(sub, found);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // PATH
        ProbeExecutable("java", found);

        var list = found.Values.OrderByDescending(j => j.Major).ToList();
        _log.Info("Java", $"Discovered {list.Count} Java installation(s).");
        foreach (var j in list) _log.Info("Java", $"  {j}");
        return list;
    }

    private void ProbeDir(string dir, Dictionary<string, JavaInstallation> found)
    {
        var cand = Path.Combine(dir, "bin", "java.exe");
        if (File.Exists(cand)) ProbeExecutable(cand, found);
    }

    private void ProbeExecutable(string exe, Dictionary<string, JavaInstallation> found)
    {
        try
        {
            var (version, major, vendor) = ProbeVersion(exe);
            if (major <= 0) return;
            var home = FindHome(exe);
            var inst = new JavaInstallation
            {
                JavaExecutable = exe,
                Home = home,
                Version = version,
                Major = major,
                Vendor = vendor
            };
            // Key by home to dedupe.
            if (!found.ContainsKey(home)) found[home] = inst;
        }
        catch { }
    }

    private (string version, int major, string vendor) ProbeVersion(string exe)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        p.Start();
        var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        // e.g. openjdk version "25.0.3" 2026-04-21 LTS
        var m = System.Text.RegularExpressions.Regex.Match(output, @"version\s+""([^""]+)""");
        if (!m.Success) return ("", 0, "");
        var ver = m.Groups[1].Value;
        var major = MajorFromVersionString(ver);
        var vendor = output.Contains("Microsoft") ? "Microsoft" :
                     output.Contains("Adoptium") || output.Contains("Temurin") ? "Eclipse Adoptium" :
                     output.Contains("OpenJDK") ? "OpenJDK" : "Unknown";
        return (ver, major, vendor);
    }

    private static int MajorFromVersionString(string v)
    {
        var parts = v.Split('.');
        if (parts[0] == "1" && parts.Length > 1) return int.TryParse(parts[1], out var m) ? m : 0; // 1.8 -> 8
        return int.TryParse(parts[0], out var maj) ? maj : 0;
    }

    private static string FindHome(string exe)
    {
        // exe = ...\bin\java.exe -> parent of bin
        var bin = Path.GetDirectoryName(exe);
        return Path.GetDirectoryName(bin) ?? Path.GetDirectoryName(exe)!;
    }

    private void TryAddFromRegistry(Dictionary<string, JavaInstallation> found)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\Java Runtime Environment");
            // (Oracle key is sparse on modern JDKs; directory scan is primary.)
        }
        catch { }
    }

    /// <summary>Picks the best Java for a required major version. Prefers an exact
    /// match, then the closest higher version, then the highest available.</summary>
    public static JavaInstallation? SelectBest(List<JavaInstallation> all, int requiredMajor)
    {
        if (all.Count == 0) return null;
        var exact = all.FirstOrDefault(j => j.Major == requiredMajor);
        if (exact != null) return exact;
        var higher = all.Where(j => j.Major >= requiredMajor).OrderBy(j => j.Major).FirstOrDefault();
        if (higher != null) return higher;
        return all.OrderByDescending(j => j.Major).First();
    }
}

using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuroraCore;
using AuroraCore.Utils;

namespace AuroraLauncher.Desktop.Services;

/// <summary>Downloads project icons once, caches them on disk and in memory,
/// and returns frozen ImageSources that can be bound from the UI thread.</summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new();

    /// <summary>Round-88: inside the data root (<c>cache/icons</c>), not %TEMP% —
    /// see <see cref="LauncherConfig.IconsCacheDir"/>. Resolved per call because
    /// the root is read from the config, not baked in at type load.</summary>
    private static string IconDir => LauncherConfig.Instance.IconsCacheDir;

    public static Task<ImageSource?> GetAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource?>(null);
        return Cache.GetOrAdd(url, LoadAsync);
    }

    /// <summary>Round-83: drop the in-memory decode cache (the disk cache stays,
    /// so re-opening a list does not re-download anything). Bitmaps that are
    /// still bound by a visible row keep their own reference and simply survive;
    /// ones belonging to dismissed lists become collectable.</summary>
    public static void ClearMemory() => Cache.Clear();

    /// <summary>How many icons are currently held in memory (diagnostics).</summary>
    public static int Count => Cache.Count;

    private static async Task<ImageSource?> LoadAsync(string url)
    {
        try
        {
            Directory.CreateDirectory(IconDir);
            var file = Path.Combine(IconDir, Sha(url) + ".img");
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
            {
                byte[]? bytes = null;
                Exception? last = null;
                foreach (var hc in HttpClientFactory.Instance.AllClients)
                {
                    try { bytes = await hc.GetByteArrayAsync(url).ConfigureAwait(false); break; }
                    catch (Exception ex) { last = ex; }
                }
                if (bytes == null) throw last ?? new HttpRequestException("All routes failed.");
                await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            }
            // Create on a worker thread is fine: freeze before returning.
            var bmp = new BitmapImage();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.DecodePixelWidth = 96; // bound memory
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // placeholder border shows instead
        }
    }

    private static string Sha(string s)
    {
        var bytes = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

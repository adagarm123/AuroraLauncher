namespace AuroraCore.Utils;

/// <summary>
/// File/content hashing helpers used for integrity verification of every
/// downloaded artifact (libraries, assets, client jar, installers).
/// </summary>
public static class CryptoHelper
{
    public static string Sha1(Stream stream)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(stream);
        return ToHex(hash);
    }

    public static async Task<string> Sha1Async(Stream stream, CancellationToken ct = default)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
#if NET10_0_OR_GREATER
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
#else
        var hash = sha.ComputeHash(stream);
#endif
        return ToHex(hash);
    }

    public static string Sha1(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return Sha1(fs);
    }

    public static string Sha256(Stream stream)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return ToHex(hash);
    }

    public static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Returns true when <paramref name="expected"/> is null/empty OR equals the computed hash.</summary>
    public static bool Matches(string? expected, string actual)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}

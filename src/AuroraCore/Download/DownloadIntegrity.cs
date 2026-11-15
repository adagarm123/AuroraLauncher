using System.IO;
using AuroraCore.Utils;

namespace AuroraCore.Download;

/// <summary>Round-87: what we can actually say about a file on disk.</summary>
public enum IntegrityVerdict
{
    /// <summary>Bytes matched the expected SHA-1 (or the expected size when no hash was published).</summary>
    Match,
    /// <summary>We had something to compare against and it did not match.</summary>
    Mismatch,
    /// <summary>Nothing to compare against (no hash, no size). NOT the same as "complete".</summary>
    Unverifiable,
    /// <summary>The file is not there at all.</summary>
    Missing
}

/// <summary>
/// Round-87: the old <c>VerifyHash(file, null)</c> returned <c>true</c> — "no hash
/// published" was silently treated as "file is fine". On the cached-file path
/// that turned a truncated jar (interrupted copy, disk-full, a CDN node that
/// closed the connection mid-body) into "already downloaded", and the user got a
/// launch failure with no explanation. OptiFine-style and third-party-CDN
/// entries publish sizes but often no hash, so the hole was reachable in
/// practice.
///
/// The rule now: hash wins when present; otherwise the published size must
/// match exactly; otherwise we simply CANNOT tell (<see cref="IntegrityVerdict.Unverifiable"/>)
/// and the CALLER decides whether to re-download or accept. The engine
/// re-downloads on the cached path (cheap, and the only safe answer) and accepts
/// a freshly written body (there is nothing more to compare against, and
/// retrying would loop forever).
/// </summary>
public static class DownloadIntegrity
{
    public static IntegrityVerdict Check(string? path, string? expectedSha1, long? expectedSize)
    {
        if (string.IsNullOrWhiteSpace(path)) return IntegrityVerdict.Missing;

        long actualLength;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return IntegrityVerdict.Missing;
            actualLength = info.Length;
        }
        catch
        {
            // A locked file is not something we may call "complete".
            return IntegrityVerdict.Unverifiable;
        }

        if (!string.IsNullOrWhiteSpace(expectedSha1))
        {
            try { return CryptoHelper.Matches(expectedSha1, CryptoHelper.Sha1(path)) ? IntegrityVerdict.Match : IntegrityVerdict.Mismatch; }
            catch { return IntegrityVerdict.Unverifiable; }
        }

        if (expectedSize is long size)
        {
            // A zero-length expectation only makes sense for a genuinely empty file.
            return actualLength == size ? IntegrityVerdict.Match : IntegrityVerdict.Mismatch;
        }

        return IntegrityVerdict.Unverifiable;
    }

    /// <summary>May this existing file be skipped as "already downloaded"?
    /// Only a real match counts — see the class remarks for why
    /// <see cref="IntegrityVerdict.Unverifiable"/> is not good enough here.</summary>
    public static bool IsTrustworthyForSkip(IntegrityVerdict verdict) => verdict == IntegrityVerdict.Match;

    /// <summary>Was the bytes we just wrote acceptable? A fresh body cannot be
    /// compared when nothing was published, so only an explicit mismatch is a
    /// failure there.</summary>
    public static bool IsAcceptableAfterDownload(IntegrityVerdict verdict) => verdict != IntegrityVerdict.Mismatch;
}

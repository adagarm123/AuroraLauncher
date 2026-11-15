using System.Buffers.Binary;
using System.Security.Cryptography;
using AuroraCore.Utils;

namespace AuroraCore.Download;

/// <summary>
/// Round-74: a small, dependency-free binary delta (xdelta/rsync style, NOT
/// bsdiff). The update package is a 63 MB single-file EXE; shipping the whole
/// thing for a one-line fix costs the author bandwidth and the user time.
///
/// Format (little-endian), so an invalid or truncated patch fails loudly:
/// <code>
///   "AURDLT01"        8 bytes magic
///   int64  oldSize    exact size of the file the patch applies to
///   int64  newSize    size of the produced file
///   int32  blockSize  index block size used at creation (informational)
///   bytes  sha256[32] expected hash of the PRODUCED file
///   ops until EOF:
///     0x00 int64 offset int32 length   copy from old
///     0x01 int32 length bytes...       literal
/// </code>
///
/// Why not bsdiff: it needs a suffix array and (usually) bzip2, neither of
/// which this project wants to own. A rolling-hash block matcher gets most of
/// the win on an EXE whose layout barely moves between rounds, is ~300 lines,
/// and — more importantly — every byte it produces is verified twice (header
/// hash + the signed manifest hash).
/// </summary>
public static class BinaryDelta
{
    private static readonly byte[] Magic = "AURDLT01"u8.ToArray();
    public const int HeaderSize = 8 + 8 + 8 + 4 + 32;
    private const byte OpCopy = 0x00;
    private const byte OpLiteral = 0x01;

    /// <summary>Hard ceiling for in-memory diffing. The launcher EXE is ~65 MB;
    /// anything above this is a mistake, not a build.</summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>A patch is only worth publishing when it is clearly smaller
    /// than the full package. Below this saving the extra failure mode (merge
    /// could fail on an unexpected old build) is not worth it.</summary>
    public const double MaxPatchRatio = 0.70;

    /// <summary>Publish-side rule: is the patch small enough to be useful?</summary>
    public static bool IsWorthPublishing(long patchSize, long fullSize)
        => patchSize > 0 && fullSize > 0 && patchSize <= fullSize * MaxPatchRatio;

    /// <summary>Client-side rule: prefer the patch only when it saves real
    /// bytes. A "patch" nearly as big as the package must lose to the package.</summary>
    public static bool ShouldUsePatch(long patchSize, long fullSize)
        => patchSize > 0 && fullSize > 0 && patchSize <= fullSize * MaxPatchRatio;

    [Flags]
    public enum CreateOptions
    {
        None = 0,
        /// <summary>Use a smaller block index (better matches, bigger index).</summary>
        Fine = 1
    }

    /// <summary>Builds a patch that turns <paramref name="oldPath"/> into
    /// <paramref name="newPath"/>. Deterministic for the same inputs.</summary>
    public static void Create(string oldPath, string newPath, string patchPath, CreateOptions options = CreateOptions.None)
    {
        var oldBytes = File.ReadAllBytes(oldPath);
        var newBytes = File.ReadAllBytes(newPath);
        if (oldBytes.LongLength > MaxFileBytes || newBytes.LongLength > MaxFileBytes)
            throw new InvalidOperationException("文件超过增量更新上限，请直接分发完整包。");

        var blockSize = ChooseBlockSize(oldBytes.Length, options);
        var index = BuildIndex(oldBytes, blockSize);

        PathEx.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(patchPath))!);
        using var outStream = new FileStream(patchPath, FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 16, FileOptions.SequentialScan);
        using var writer = new BinaryWriter(outStream);

        writer.Write(Magic);
        writer.Write((long)oldBytes.Length);
        writer.Write((long)newBytes.Length);
        writer.Write(blockSize);
        writer.Write(SHA256.HashData(newBytes));

        var literalStart = 0;
        var pos = 0;
        // Rolling Adler-32 over the new file: recomputing a hash per byte would
        // be O(n * blockSize) and take minutes on a 65 MB EXE.
        uint s1 = 0, s2 = 0;
        var windowFilled = 0;
        if (newBytes.Length >= blockSize)
        {
            for (var i = 0; i < blockSize; i++) { s1 += newBytes[i]; s2 += s1; }
            s1 %= 65521; s2 %= 65521;
            windowFilled = blockSize;
        }

        while (windowFilled == blockSize && pos + blockSize <= newBytes.Length)
        {
            var hash = (s2 << 16) | s1;
            long matchOffset = -1;
            var matchLength = 0;
            if (index.TryGetValue(hash, out var candidates))
            {
                var checkedCount = 0;
                foreach (var offset in candidates)
                {
                    if (checkedCount++ >= 24) break;
                    if (!RegionEquals(oldBytes, (int)offset, newBytes, pos, blockSize)) continue;
                    var length = blockSize;
                    var maxLength = Math.Min(oldBytes.Length - (int)offset, newBytes.Length - pos);
                    while (length < maxLength && oldBytes[offset + length] == newBytes[pos + length]) length++;
                    if (length > matchLength) { matchLength = length; matchOffset = offset; }
                }
            }

            if (matchOffset >= 0)
            {
                if (pos > literalStart) WriteLiteral(writer, newBytes, literalStart, pos - literalStart);
                writer.Write(OpCopy);
                writer.Write(matchOffset);
                writer.Write(matchLength);

                // Re-seed the rolling window after the jump.
                pos += matchLength;
                literalStart = pos;
                if (pos + blockSize > newBytes.Length) break;
                s1 = 0; s2 = 0;
                for (var i = 0; i < blockSize; i++) { s1 += newBytes[pos + i]; s2 += s1; }
                s1 %= 65521; s2 %= 65521;
                continue;
            }

            if (pos + blockSize < newBytes.Length)
            {
                var outByte = newBytes[pos];
                var inByte = newBytes[pos + blockSize];
                s1 = (s1 + 65521 - outByte + inByte) % 65521;
                s2 = (s2 + 65521 - (uint)blockSize * outByte % 65521 + s1) % 65521;
            }
            pos++;
        }

        if (newBytes.Length > literalStart)
            WriteLiteral(writer, newBytes, literalStart, newBytes.Length - literalStart);
        writer.Flush();
    }

    /// <summary>Applies <paramref name="patchPath"/> to <paramref name="oldPath"/>
    /// and writes <paramref name="outPath"/>. The produced file's SHA-256 is
    /// compared against the hash baked into the patch header, so a mismatched
    /// old build cannot silently produce a corrupt EXE.</summary>
    /// <returns>SHA-256 (uppercase hex) of the produced file.</returns>
    public static string Apply(string oldPath, string patchPath, string outPath)
    {
        using var patch = new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.SequentialScan);
        using var reader = new BinaryReader(patch);

        var magic = reader.ReadBytes(8);
        if (magic.Length != 8 || !magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("补丁文件格式不正确（魔数不匹配）。");

        var oldSize = reader.ReadInt64();
        var newSize = reader.ReadInt64();
        _ = reader.ReadInt32(); // blockSize (informational)
        var expectedHash = reader.ReadBytes(32);
        if (oldSize < 0 || newSize < 0 || newSize > MaxFileBytes)
            throw new InvalidDataException("补丁头部损坏。");

        using var oldStream = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.RandomAccess);
        if (oldStream.Length != oldSize)
            throw new InvalidDataException(
                $"补丁与当前文件版本不匹配（补丁要求 {oldSize} 字节，实际 {oldStream.Length} 字节）。");

        PathEx.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 16, FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[1 << 16];
        long written = 0;
        while (patch.Position < patch.Length)
        {
            var op = reader.ReadByte();
            if (op == OpCopy)
            {
                var offset = reader.ReadInt64();
                var length = reader.ReadInt32();
                if (length < 0 || offset < 0 || offset + length > oldSize)
                    throw new InvalidDataException("补丁内容越界（复制段超出原文件）。");
                oldStream.Seek(offset, SeekOrigin.Begin);
                var remaining = length;
                while (remaining > 0)
                {
                    var chunk = Math.Min(remaining, buffer.Length);
                    var read = oldStream.Read(buffer, 0, chunk);
                    if (read <= 0) throw new InvalidDataException("补丁内容越界（原文件提前结束）。");
                    outStream.Write(buffer, 0, read);
                    hasher.AppendData(buffer, 0, read);
                    remaining -= read;
                    written += read;
                }
            }
            else if (op == OpLiteral)
            {
                var length = reader.ReadInt32();
                if (length < 0 || length > newSize)
                    throw new InvalidDataException("补丁内容损坏（字面段长度异常）。");
                var remaining = length;
                while (remaining > 0)
                {
                    var chunk = Math.Min(remaining, buffer.Length);
                    var read = patch.Read(buffer, 0, chunk);
                    if (read <= 0) throw new InvalidDataException("补丁内容被截断。");
                    outStream.Write(buffer, 0, read);
                    hasher.AppendData(buffer, 0, read);
                    remaining -= read;
                    written += read;
                }
            }
            else
            {
                throw new InvalidDataException($"补丁内容损坏（未知操作码 0x{op:X2}）。");
            }
        }

        outStream.Flush();
        if (written != newSize)
            throw new InvalidDataException($"补丁应用结果大小不符（期望 {newSize}，实际 {written}）。");

        var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
        var wantedHash = Convert.ToHexString(expectedHash);
        if (!string.Equals(actualHash, wantedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("补丁应用结果校验失败（SHA-256 不匹配）。");
        return actualHash;
    }

    /// <summary>Reads only the header, so the updater can decide (and log)
    /// whether a patch belongs to this build before downloading anything else.</summary>
    public static bool TryReadHeader(string patchPath, out long oldSize, out long newSize, out string hash)
    {
        oldSize = 0; newSize = 0; hash = "";
        try
        {
            using var stream = File.OpenRead(patchPath);
            if (stream.Length < HeaderSize) return false;
            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);
            if (!header[..8].SequenceEqual(Magic)) return false;
            oldSize = BinaryPrimitives.ReadInt64LittleEndian(header[8..16]);
            newSize = BinaryPrimitives.ReadInt64LittleEndian(header[16..24]);
            // magic(8) + oldSize(8) + newSize(8) + blockSize(4) = 28, then 32 hash bytes.
            hash = Convert.ToHexString(header[28..60]);
            return oldSize > 0 && newSize > 0;
        }
        catch { return false; }
    }

    private static int ChooseBlockSize(int oldLength, CreateOptions options)
    {
        // ~1024-byte blocks keep the index small (65k entries for a 65 MB EXE)
        // while still catching shifted regions. Fine mode is for tests.
        var target = (options & CreateOptions.Fine) != 0 ? 64 : 1024;
        var bySize = Math.Max(64, Math.Min(4096, oldLength / 8192));
        return (options & CreateOptions.Fine) != 0 ? target : Math.Max(target, bySize);
    }

    private static Dictionary<uint, List<long>> BuildIndex(byte[] oldBytes, int blockSize)
    {
        var index = new Dictionary<uint, List<long>>(Math.Max(16, oldBytes.Length / blockSize));
        for (var offset = 0; offset + blockSize <= oldBytes.Length; offset += blockSize)
        {
            uint s1 = 0, s2 = 0;
            for (var i = 0; i < blockSize; i++) { s1 += oldBytes[offset + i]; s2 += s1; }
            var hash = ((s2 % 65521) << 16) | (s1 % 65521);
            if (!index.TryGetValue(hash, out var list))
            {
                list = new List<long>(1);
                index[hash] = list;
            }
            if (list.Count < 32) list.Add(offset);
        }
        return index;
    }

    private static bool RegionEquals(byte[] a, int aOffset, byte[] b, int bOffset, int length)
    {
        if (aOffset + length > a.Length || bOffset + length > b.Length) return false;
        return a.AsSpan(aOffset, length).SequenceEqual(b.AsSpan(bOffset, length));
    }

    private static void WriteLiteral(BinaryWriter writer, byte[] data, int offset, int length)
    {
        writer.Write(OpLiteral);
        writer.Write(length);
        writer.Write(data, offset, length);
    }
}

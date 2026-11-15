namespace AuroraCore.Services;

/// <summary>
/// Round-83: "how much memory should I give Minecraft?" answered by the
/// launcher instead of by the player.
///
/// Two hard rules drive every number here:
/// 1. Never give the game more than <see cref="PhysicalShare"/> of physical RAM.
///    Past ~55% the OS starts paging out the very process it is serving, the
///    GPU driver loses its share, and the machine "freezes" while Minecraft is
///    still running — which users report as a launcher bug.
/// 2. Never go below <see cref="FloorMb"/>. A 512 MB heap cannot even finish
///    loading a modern client, and the resulting OOM looks like a crash.
///
/// Pure: same input → same output. IO lives in <see cref="Probe"/> so the math
/// stays unit-testable, and the physical-memory read is a separate helper.
/// </summary>
public static class MemoryAllocationPolicy
{
    /// <summary>Smallest heap that can plausibly start a modern client.</summary>
    public const int FloorMb = 1024;

    /// <summary>No Minecraft setup benefits from more than this; extra heap
    /// only lengthens GC pauses.</summary>
    public const int HardCapMb = 16384;

    /// <summary>Share of physical RAM the game may take. The test suite asserts
    /// every automatic path stays under 60%, so raising this to 0.6 is the
    /// documented absolute limit.</summary>
    public const double PhysicalShare = 0.55;

    /// <summary>Heaps are rounded to this step so values read as "4096" not
    /// "4137". Every base value below is already a multiple.</summary>
    public const int QuantumMb = 512;

    public readonly record struct Advice(
        int RecommendedMb,
        int CeilingMb,
        bool CappedByPhysical,
        bool LowPhysical,
        int BaseMb);

    /// <summary>Largest heap this machine should ever hand to the JVM, in MB.
    /// 0 physical memory (probe failed) → the hard cap; better a sane default
    /// than doubling a guess.</summary>
    public static int SafeCeiling(int physicalMb)
    {
        if (physicalMb <= 0) return HardCapMb;
        var cap = (int)(physicalMb * PhysicalShare);
        cap = cap / QuantumMb * QuantumMb;
        return Math.Clamp(cap, QuantumMb, HardCapMb);
    }

    /// <summary>Recommended heap from RAM + how heavy the instance is.
    /// `modded` = the instance has a mod loader at all; `modCount` = jars in
    /// mods/ (0 for vanilla); `hasShader` = shaderpacks/ holds at least one zip.</summary>
    public static Advice Recommend(int physicalMb, int modCount, bool hasShader, bool modded)
    {
        if (modCount < 0) modCount = 0;

        // 1. Base by workload. Vanilla is fine on 2 GB; a 200-mod pack is not.
        var baseMb = !modded
            ? 2048
            : modCount switch
            {
                < 30 => 3072,
                < 80 => 4096,
                < 160 => 5120,
                _ => 6144
            };

        // 2. Shaders need a framebuffer-sized working set on top of the game;
        //    below 4 GB the client stalls during chunk rebuilds.
        if (hasShader) baseMb = Math.Max(baseMb, modCount >= 160 ? 7168 : 4096);

        // 3. The machine always wins.
        var ceiling = SafeCeiling(physicalMb);
        var capped = baseMb > ceiling;
        var recommended = Math.Min(baseMb, ceiling);

        // 4. Floor, but only if the machine can actually afford it.
        recommended = Math.Max(recommended, Math.Min(FloorMb, ceiling));

        var lowPhysical = physicalMb is > 0 and < 6144;
        return new Advice(recommended, ceiling, capped, lowPhysical, baseMb);
    }

    /// <summary>What the launch actually uses. Manual values are honoured
    /// verbatim (the player may know better than our table — and silently
    /// rewriting a working setup is worse than a bad number), only the
    /// automatic path is bounded.</summary>
    public static int Effective(int configuredMb, bool auto, int physicalMb,
        int modCount, bool hasShader, bool modded)
        => auto ? Recommend(physicalMb, modCount, hasShader, modded).RecommendedMb : configuredMb;

    /// <summary>UI-side sanity check for a hand-typed value. Not applied at
    /// launch — shown as a warning instead of silently changing the number.</summary>
    public static bool ExceedsSafeShare(int requestedMb, int physicalMb)
        => physicalMb > 0 && requestedMb > SafeCeiling(physicalMb);

    /// <summary>Human explanation for the settings page. Never claims a number
    /// we did not compute; states the ceiling when it is the binding constraint.</summary>
    public static string Describe(in Advice advice, int physicalMb, int modCount, bool hasShader, bool modded)
    {
        var ram = physicalMb > 0 ? $"{physicalMb / 1024.0:0.#} GB" : "";
        var parts = new List<string>();
        if (physicalMb > 0) parts.Add($"物理内存 {ram}");
        if (modded) parts.Add($"{modCount} 个模组");
        else parts.Add("原版");
        if (hasShader) parts.Add("有光影");

        var head = string.Join(" · ", parts);
        string tail;
        if (physicalMb <= 0)
            tail = $"没能读到物理内存，保守给了 {advice.RecommendedMb} MB。";
        else if (advice.CappedByPhysical)
            tail = $"按内容想给 {advice.BaseMb} MB，但为了不把系统挤进交换，上限是物理内存的 " +
                   $"{PhysicalShare:P0}（{advice.CeilingMb} MB），所以实际用 {advice.RecommendedMb} MB。";
        else
            tail = $"推荐 {advice.RecommendedMb} MB（不超过物理内存的 {PhysicalShare:P0}）。";

        if (advice.LowPhysical)
            tail += " 这台机器内存偏小，建议关掉浏览器再玩，并把视距调到 8 以内。";

        return $"{head} → {tail}";
    }

    // ---------------------------------------------------------------- IO side

    /// <summary>Count jars in mods/ and look for a shader pack. Missing folders
    /// are normal (vanilla instance) and yield (0, false) instead of throwing.</summary>
    public static (int Mods, bool HasShader) Probe(string? instanceDir)
    {
        if (string.IsNullOrWhiteSpace(instanceDir)) return (0, false);
        var mods = 0;
        try
        {
            var dir = Path.Combine(instanceDir, "mods");
            if (Directory.Exists(dir))
                mods = Directory.EnumerateFiles(dir, "*.jar", SearchOption.TopDirectoryOnly).Count();
        }
        catch { /* unreadable mods/ → treat as none */ }

        var shader = false;
        try
        {
            var dir = Path.Combine(instanceDir, "shaderpacks");
            shader = Directory.Exists(dir) &&
                     Directory.EnumerateFiles(dir, "*.zip", SearchOption.TopDirectoryOnly).Any();
        }
        catch { }

        return (mods, shader);
    }

    /// <summary>Physical RAM in MB. GC.GetGCMemoryInfo reports the real physical
    /// limit on Windows (and the cgroup limit in a container), so no extra
    /// P/Invoke is needed here. 0 = unknown, handled by every caller.</summary>
    public static int DetectPhysicalMb()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return bytes > 0 ? (int)Math.Min(bytes / 1024 / 1024, int.MaxValue) : 0;
        }
        catch
        {
            return 0;
        }
    }
}

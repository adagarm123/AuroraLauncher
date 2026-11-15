using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AuroraLauncher.Desktop.Services;

/// <summary>
/// Round-83: the ONLY memory reclamation in this app, and it is deliberately
/// boring.
///
/// What it does: drops the launcher's own image cache and asks Windows to trim
/// THIS process's working set.
///
/// What it must never do (guarded by the self-test):
/// • enumerate or open other processes — "one-click RAM boosters" that touch
///   every process make the machine slower, because everything they page out is
///   paged right back in;
/// • touch the game: trimming a running JVM's working set forces a
///   stop-the-world collection and the game freezes for a second or more;
/// • call GC.Collect() on the UI thread — a full blocking collection there is a
///   visible stall. Only the manual deep clean forces a GC, and only inside a
///   background task.
/// </summary>
public static class MemoryHygiene
{
    /// <summary>Outcome of one trim. <see cref="FreedMb"/> can honestly be 0:
    /// Windows may keep the pages resident until pressure arrives, so the number
    /// in Task Manager does not always move.</summary>
    public readonly record struct Result(long BeforeMb, long AfterMb, bool ForcedGc)
    {
        public long FreedMb => Math.Max(0, BeforeMb - AfterMb);
    }

    /// <summary>Background thread only. `aggressive` = the manual deep clean
    /// (also forces a compacting GC); the idle path passes false so returning to
    /// the window never waits for a collection.</summary>
    public static Result Trim(bool aggressive)
    {
        var before = WorkingSetMb();
        try { IconCache.ClearMemory(); } catch { }

        if (aggressive)
        {
            // Safe here: every caller reaches this on a worker thread.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        }

        try { SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1); } catch { }
        return new Result(before, WorkingSetMb(), aggressive);
    }

    /// <summary>Current working set in MB (0 when the OS refuses to answer).</summary>
    public static long WorkingSetMb()
    {
        try { return Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024; }
        catch { return 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>-1 / -1 tells the memory manager "trim as much as you think is
    /// safe"; it is a hint, not a command, and never fails fatally.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}

using System.Threading.Tasks;
using AuroraCore;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.Desktop.Services;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>
/// Round-83: memory, in both directions.
///
/// Allocation is automatic (and bounded); reclamation is manual and honest
/// about what it can and cannot free. The rule that shapes this file: the
/// launcher may only ever manage ITS OWN memory — never the game's.
/// </summary>
public partial class MainViewModel
{
    // ==================== 1. 自动分配内存 ====================

    private int _physicalMb;      // cached: the probe is cheap but not free
    private bool _autoMemory;

    /// <summary>Derive -Xmx per launch instead of using the manual number.
    /// Persisted by <c>ApplySettings</c> like every other field on that page.</summary>
    public bool AutoMemory
    {
        get => _autoMemory;
        set
        {
            if (!SetProperty(ref _autoMemory, value)) return;
            OnPropertyChanged(nameof(MemoryManualEnabled));
            RefreshMemoryAdvice();
            Status = value
                ? "内存分配已切换为「自动」——每次启动按物理内存、模组数量与光影计算"
                : "内存分配已切换为「手动」——按你填写的数字来（下次启动生效）";
        }
    }

    /// <summary>The manual box is read-only while auto owns the number, otherwise
    /// the user types into a field that the next launch ignores.</summary>
    public bool MemoryManualEnabled => !AutoMemory;

    private string _memoryAutoHint = "";
    /// <summary>One line under the memory field: why this number, on this machine,
    /// for this instance. Empty until the first refresh.</summary>
    public string MemoryAutoHint
    {
        get => _memoryAutoHint;
        private set => SetProperty(ref _memoryAutoHint, value);
    }

    private string _memoryWarningText = "";
    public string MemoryWarningText
    {
        get => _memoryWarningText;
        private set => SetProperty(ref _memoryWarningText, value);
    }

    private bool _hasMemoryWarning;
    public bool HasMemoryWarning
    {
        get => _hasMemoryWarning;
        private set => SetProperty(ref _hasMemoryWarning, value);
    }

    private void InitRound83()
    {
        _physicalMb = MemoryAllocationPolicy.DetectPhysicalMb();
        var cfg = LauncherConfig.Instance;
        _autoMemory = cfg.AutoMemory;
        _releaseIdleMemory = cfg.ReleaseIdleMemory;
        OnPropertyChanged(nameof(AutoMemory));
        OnPropertyChanged(nameof(MemoryManualEnabled));
        OnPropertyChanged(nameof(ReleaseIdleMemory));
        RefreshMemoryAdvice();
        // The dashboard judges the peak against the effective heap, so it has to
        // be refreshed after the probe above fills _physicalMb.
        RefreshPerformanceCard();
    }

    /// <summary>Recomputes the recommendation for the CURRENT instance. Called on
    /// load, on auto/manual toggle, and whenever the selected instance changes —
    /// a 3-mod vanilla instance and a 200-mod pack must not show the same number.</summary>
    private void RefreshMemoryAdvice()
    {
        if (_physicalMb <= 0) _physicalMb = MemoryAllocationPolicy.DetectPhysicalMb();

        var inst = SelectedInstance;
        var (mods, shader) = MemoryAllocationPolicy.Probe(inst?.Directory);
        var modded = inst != null && inst.LoaderType != ModLoaderType.Vanilla;
        var advice = MemoryAllocationPolicy.Recommend(_physicalMb, mods, shader, modded);

        MemoryAutoHint = MemoryAllocationPolicy.Describe(advice, _physicalMb, mods, shader, modded);

        if (AutoMemory)
        {
            // Show the number that will actually be used; the manual value is
            // remembered in the config for when the user turns auto back off.
            MemoryMb = advice.RecommendedMb;
            OnPropertyChanged(nameof(MemoryMb));
        }

        RefreshMemoryWarning();
    }

    /// <summary>Cheap half of <see cref="RefreshMemoryAdvice"/>: only the warning.
    /// Called on every keystroke in the memory box (see the MemoryMb setter).</summary>
    private void RefreshMemoryWarning()
    {
        if (_physicalMb <= 0) return;
        var warn = !AutoMemory && MemoryAllocationPolicy.ExceedsSafeShare(MemoryMb, _physicalMb);
        HasMemoryWarning = warn;
        MemoryWarningText = warn
            ? $"⚠ {MemoryMb} MB 超过物理内存的 {MemoryAllocationPolicy.PhysicalShare:P0}" +
              $"（安全上限 {MemoryAllocationPolicy.SafeCeiling(_physicalMb)} MB）：" +
              "可能直接启动失败，或者让整台电脑一起卡死。建议点上面的「自动（推荐）」或把数字调小。"
            : "";
    }

    // ==================== 2. 手动释放内存（只碰自己） ====================

    private string _memoryCleanStatus = "";
    public string MemoryCleanStatus
    {
        get => _memoryCleanStatus;
        private set => SetProperty(ref _memoryCleanStatus, value);
    }

    private bool _isCleaningMemory;
    public bool IsCleaningMemory
    {
        get => _isCleaningMemory;
        private set
        {
            if (SetProperty(ref _isCleaningMemory, value)) OnPropertyChanged(nameof(CanCleanMemory));
        }
    }

    public bool CanCleanMemory => !IsCleaningMemory;

    private bool _releaseIdleMemory = true;
    /// <summary>Bound to the settings checkbox; persisted immediately because it
    /// only affects the launcher's own footprint (no restart needed).</summary>
    public bool ReleaseIdleMemory
    {
        get => _releaseIdleMemory;
        set => SetProperty(ref _releaseIdleMemory, value);
    }

    /// <summary>User-triggered. Runs entirely on a worker thread: a full GC on
    /// the UI thread would freeze the window for hundreds of milliseconds, which
    /// is exactly the kind of "optimisation" users notice as a stutter.</summary>
    public async Task DeepCleanMemoryAsync()
    {
        if (IsCleaningMemory) return;
        IsCleaningMemory = true;
        MemoryCleanStatus = "正在释放启动器占用的内存…（不会动游戏进程）";
        Status = MemoryCleanStatus;
        try
        {
            var r = await Task.Run(() => MemoryHygiene.Trim(aggressive: true)).ConfigureAwait(true);
            MemoryCleanStatus = r.FreedMb > 0
                ? $"已释放约 {r.FreedMb} MB（{r.BeforeMb} MB → {r.AfterMb} MB），只处理了启动器自己"
                : $"缓存已清空（当前占用 {r.AfterMb} MB）。系统可能暂时保留这些页，任务管理器里的数字不一定马上变小";
            Status = MemoryCleanStatus;
            AddLog($"[Memory] 手动清理：{r.BeforeMb} MB → {r.AfterMb} MB，仅本进程，未触碰游戏进程");
            ShowToast(MemoryCleanStatus);
        }
        catch (Exception ex)
        {
            MemoryCleanStatus = "释放失败: " + ex.Message;
            Status = MemoryCleanStatus;
        }
        finally
        {
            IsCleaningMemory = false;
        }
    }

    /// <summary>Idle path (window hidden in the tray for a few minutes). No forced
    /// GC — only cache + working-set trim, so coming back never waits on a
    /// collection.</summary>
    public async Task TrimIdleMemoryAsync()
    {
        if (!LauncherConfig.Instance.ReleaseIdleMemory) return;
        try
        {
            var r = await Task.Run(() => MemoryHygiene.Trim(aggressive: false)).ConfigureAwait(true);
            if (r.BeforeMb - r.AfterMb >= 8)
                AddLog($"[Memory] 闲置释放：{r.BeforeMb} MB → {r.AfterMb} MB（仅本进程缓存）");
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("Memory", "闲置释放失败: " + ex.Message);
        }
    }

    /// <summary>Effective -Xmx for the current instance, honouring auto mode.
    /// The dashboard's verdict must judge the heap the game will really get —
    /// otherwise "峰值 3900 MB / 上限 4096 MB" is computed against a stale value.</summary>
    private int EffectiveMemoryMb()
    {
        var inst = SelectedInstance;
        var (mods, shader) = MemoryAllocationPolicy.Probe(inst?.Directory);
        return MemoryAllocationPolicy.Effective(LauncherConfig.Instance.MaxMemoryMb,
            LauncherConfig.Instance.AutoMemory, _physicalMb, mods, shader,
            inst != null && inst.LoaderType != ModLoaderType.Vanilla);
    }
}

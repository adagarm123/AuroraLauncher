namespace AuroraCore.Services;

/// <summary>Round-101: where a brand-new installation keeps its files.</summary>
public enum FirstRunStorageChoice { AppData, BesideExe }

/// <summary>
/// Round-101: the first-run wizard asks this BEFORE downloading anything.
/// The choice only writes <c>portable.txt</c> — it deliberately does not move
/// existing files, so a wrong click cannot lose data. Taking effect after the
/// next restart is stated in the UI rather than hidden in a code comment.
/// </summary>
public static class FirstRunStoragePolicy
{
    public static FirstRunStorageChoice FromPortable(bool portable)
        => portable ? FirstRunStorageChoice.BesideExe : FirstRunStorageChoice.AppData;

    public static string Root(FirstRunStorageChoice choice, string appDataRoot, string exePath)
        => choice == FirstRunStorageChoice.BesideExe
            ? (Path.GetDirectoryName(exePath) ?? appDataRoot)
            : appDataRoot;

    public static string Describe(FirstRunStorageChoice choice, string appDataRoot, string exePath)
    {
        var root = Root(choice, appDataRoot, exePath);
        return choice == FirstRunStorageChoice.BesideExe
            ? $"启动器旁边：{root}\n配置、实例、模组缓存、Java 都会放进这个文件夹，拷走整个目录就能换电脑。"
            : $"系统默认位置：{root}\n不改动任何现有设置，Windows 会按用户账户隔离数据。";
    }

    public static bool ShouldCreateMarker(FirstRunStorageChoice choice, bool alreadyPortable)
        => choice == FirstRunStorageChoice.BesideExe && !alreadyPortable;

    public static string Note(bool markerCreated, bool alreadyPortable, string exePath)
    {
        if (alreadyPortable) return "✓ 已经是便携模式，无需重复创建标记。";
        if (!markerCreated) return "已选择系统默认位置；以后仍可在 设置 → 存储 里改成便携模式。";
        return $"✓ 已在启动器旁边创建 portable.txt：{exePath}\n重启后生效；已有文件不会自动搬移。";
    }

    /// <summary>The wizard must never apply a storage choice by accident: it
    /// is applied only on the explicit "下一步" of this dedicated step.</summary>
    public static bool IsDownloadStep(int step) => step == 2;
}

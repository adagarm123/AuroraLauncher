namespace AuroraCore.Services;

/// <summary>
/// Round-82: why a launch cannot start, decided in one place so the button label
/// and the command can never disagree.
///
/// The first-run case matters most: a brand-new user has zero instances, and a
/// button that silently does nothing is the worst possible answer. "NoInstances"
/// is therefore a <em>navigation</em> state, not an error message.
/// </summary>
public static class LaunchGuardPolicy
{
    public enum Block
    {
        None,
        /// <summary>Nothing installed yet → send the user to the download page.</summary>
        NoInstances,
        /// <summary>Instances exist but none is selected → send them to instance management.</summary>
        NoSelection,
        /// <summary>Ready to play except nobody is signed in.</summary>
        NoAccount
    }

    public static Block Decide(int instanceCount, bool hasSelection, bool hasAccount)
    {
        if (instanceCount <= 0) return Block.NoInstances;
        if (!hasSelection) return Block.NoSelection;
        if (!hasAccount) return Block.NoAccount;
        return Block.None;
    }

    /// <summary>Status-line text. Navigational states also return a page key so
    /// the caller never has to guess where to send the user.</summary>
    public static (string Message, string? Page) Describe(Block block) => block switch
    {
        Block.NoInstances => ("还没有任何实例，已为你打开「游戏下载」——选一个版本即可一键创建", "Download"),
        Block.NoSelection => ("请先在「实例管理」里选择一个实例", "Instances"),
        Block.NoAccount => ("请先选择/添加账户", null),
        _ => ("", null)
    };

    /// <summary>What the launch button should say. Kept next to the policy so a
    /// new blocking state cannot forget to relabel the button.</summary>
    public static string ButtonText(bool hasInstances) => hasInstances ? "启动游戏" : "去下载游戏";

    /// <summary>Hint under the instance card (same state, different sentence).</summary>
    public static string InstanceHint(bool hasInstances) => hasInstances
        ? "提示：未安装所选实例时会自动触发下载安装。"
        : "还没有实例。点上面的按钮去「游戏下载」选一个版本，装好就能直接玩。";
}

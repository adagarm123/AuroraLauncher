using System.Collections.Generic;

namespace AuroraLauncher.Desktop.ViewModels;

/// <summary>One searchable topic of the built-in help center.</summary>
public class HelpTopicVM
{
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Body { get; init; } = "";
    public string Glyph { get; init; } = "\uE7C3";
}

/// <summary>Built-in, offline help content (no network needed).</summary>
public static class HelpContent
{
    /// <summary>Round number of the newest entry in <see cref="Changelog"/> —
    /// bump together with the changelog entry; drives the one-time "已更新"
    /// startup toast (dismissed permanently once the user opens the page).</summary>
    public const int LatestChangelogRound = 57;
    /// <summary>更新日志（说人话版本：无技术名词、用户视角）——独立页面
    /// 「更新日志」使用；模板见 docs/release-notes-template.md。
    /// 每轮发布时在此置顶新增一条。</summary>
    public static IEnumerable<HelpTopicVM> Changelog() => new[]
    {
        new HelpTopicVM
        {
            Category = "更新日志", Glyph = "\uE777", Title = "【第 57 轮更新】新功能：自动更新",
            Summary = "再也不用手动下载替换了",
            Body =
                "新功能\n" +
                "· 自动更新：启动器每次打开会自动检查新版本，发现后自动在后台下载并校验；你只需要重启一次启动器，更新就会自动完成——不用再去网盘手动下载、手动替换。\n\n" +
                "修复\n" +
                "· 连续点击左侧页面切换可能卡死未响应——现在页面切换即点即达。\n" +
                "· 下载速度极慢时进度长期停住——现在会自动换更快的线路，并明确告诉你已切换。\n\n" +
                "优化\n" +
                "· 新增独立的「更新日志」页面（左侧「日志」下方），每次更新做了什么一目了然。\n" +
                "· 更新后有新内容时，会轻轻提示一次，不打扰使用。\n\n" +
                "如果你遇到问题，可在「设置 → 导出诊断包」，把文件附在反馈里，排查会快很多。"
        },
        new HelpTopicVM
        {
            Category = "更新日志", Glyph = "\uE777", Title = "【第 53 轮更新】稳定性与体验修复",
            Summary = "无新功能，主要是稳定性与体验修复",
            Body =
                "本次无新功能，主要是稳定性与体验修复：\n\n" +
                "修复\n" +
                "· 选择游戏版本后，加载器可能自己变成\"已选择\"，影响你选择其他加载器——现在只有你亲手选择才会选中。\n" +
                "· 设置页在 Java 列表附近滚动时容易被卡住——现在整页滚动都顺畅。\n" +
                "· 有时用到的还是旧版本的启动器——现在每次更新都会附带一个版本信息文件，一看便知。\n" +
                "· 下载模组或前置时，进度可能长期停在 0%、速度极慢——现在会自动改用更快的下载线路。\n\n" +
                "优化\n" +
                "· 切换游戏版本时，加载器列表 30 分钟内秒开，不联网也能看。\n" +
                "· 页面滚动更流畅（配置较低的电脑更明显）。\n" +
                "· 安装 Fabric 实例时会自动装好 Fabric API 前置，装完会弹提示告诉你（可在设置中关闭）。\n\n" +
                "如果你遇到问题，可在「设置 → 导出诊断包」，把文件附在反馈里，排查会快很多。"
        },
        new HelpTopicVM
        {
            Category = "更新日志", Glyph = "\uE777", Title = "更早的更新（第 40-52 轮）",
            Summary = "首次引导、诊断包、微软登录修复等",
            Body =
                "· 新增首次启动引导：第一次打开会自动检测 Java 并推荐安装。\n" +
                "· 新增一键诊断包：设置 → 导出诊断包，反馈问题时附上，排查更快。\n" +
                "· 修复微软登录失败的问题：现在会先告诉你缺什么配置、怎么配。\n" +
                "· 修复下载完成后按钮不变化的问题：完成后直接变成\"启动游戏\"。\n" +
                "· 游戏下载页的加载器列表改为即时显示，不再长时间等待。\n" +
                "· 下载速度很慢时会自动切换线路，进度条不再假死。\n" +
                "· 页面滚动与显示细节多处优化。"
        },
    };

    public static IEnumerable<HelpTopicVM> Topics() => new[]
    {
        // ---- 指南 ----
        new HelpTopicVM
        {
            Category = "指南", Glyph = "\uE80F", Title = "新手指南",
            Summary = "从零开始玩 Minecraft：创建实例 → 下载 → 启动",
            Body = "第一次使用本启动器只需要三步：\n\n" +
                   "1. 添加账户：在「首页」添加离线账户，或使用微软账号登录（需要正版）。\n" +
                   "2. 下载游戏：在「游戏下载」页选择一个版本（推荐 release 稳定版），选择是否安装 Fabric/Forge/NeoForge/Quilt 加载器，点击「下载并安装」。\n" +
                   "3. 启动游戏：回到「首页」，选择刚创建的实例，点击「启动游戏」。启动器会自动扫描文件，缺失的部分会自动补全。\n\n" +
                   "提示：所有游戏文件保存在「设置 → 存储」显示的目录中，可在那里修改或一键打开文件夹。"
        },
        new HelpTopicVM
        {
            Category = "指南", Glyph = "\uE71D", Title = "整合包指南",
            Summary = "在「内容中心」一键安装整合包",
            Body = "安装整合包最简单的方式：\n\n" +
                   "1. 打开「内容中心」，在左侧选择「整合包」。\n" +
                   "2. 搜索或浏览整合包（来源 Modrinth），点击「＋ 安装」。\n" +
                   "3. 启动器会自动：下载整合包 → 解析所需 MC 版本与加载器 → 创建匹配的全新实例 → 安装游戏与加载器 → 部署整合包文件。\n\n" +
                   "也可以导入本地 .mrpack 文件：「内容中心 → 本地管理 → 导入 .mrpack 整合包」。\n" +
                   "整合包需要的模组文件会自动从镜像下载并做 SHA-1 校验。"
        },
        new HelpTopicVM
        {
            Category = "指南", Glyph = "\uE8F1", Title = "资源安装指南",
            Summary = "Mod、存档、纹理包（材质包）、光影的安装方式",
            Body = "各类资源在本启动器中的安装位置：\n\n" +
                   "· Mod：内容中心搜索安装，或把 .jar 文件放入 实例目录/mods/。Fabric 实例通常还需要 Fabric API（安装 Mod 后会自动提示补装）。\n" +
                   "· 光影：需要实例已安装 Iris/OptiFine 等光影加载 Mod；光影包放入 实例目录/shaderpacks/。\n" +
                   "· 纹理包（资源包）：放入 实例目录/resourcepacks/，在游戏内 设置→资源包 启用。\n" +
                   "· 存档：把世界文件夹放入 实例目录/saves/。\n\n" +
                   "快捷方式：实例管理 → 右侧「快捷方式」可一键打开版本/存档/Mod 文件夹。"
        },

        // ---- 常见问题 ----
        new HelpTopicVM
        {
            Category = "常见问题", Glyph = "\uE896", Title = "下载失败 / 速度慢",
            Summary = "镜像回退机制与代理设置",
            Body = "本启动器内置多线路自动回退：官方源 → 直连 → 国内镜像（BMCLAPI / MCIM），哪条线路先用通哪条。\n\n" +
                   "如果仍然失败：\n" +
                   "1. 在「设置 → 代理地址」填写本地代理（如 http://127.0.0.1:7890）。\n" +
                   "2. 或在「设置 → 下载源」手动切换为 BMCLAPI。\n" +
                   "3. 取消下载会自动清理未完成的临时文件，重新开始即可。"
        },
        new HelpTopicVM
        {
            Category = "常见问题", Glyph = "\uE80F", Title = "启动失败排查",
            Summary = "看日志、查常见原因",
            Body = "启动失败时先看「日志」页的错误摘要，常见原因：\n\n" +
                   "· 内存不足 / JVM 无法创建：降低「设置 → 内存分配」。\n" +
                   "· Java 版本不对：MC 1.17+ 需要 Java 16+，1.18+ 建议 Java 17/21。可在「设置 → Java 环境」查看本机检测到的 Java 并指定。\n" +
                   "· 找不到主类 / 类缺失：实例文件不完整，使用实例管理 →「补全文件」。\n" +
                   "· 缺少前置：Fabric 模组通常需要 Fabric API。\n\n" +
                   "崩溃原因分析会在启动失败的弹窗与状态栏中直接给出。"
        },
        new HelpTopicVM
        {
            Category = "常见问题", Glyph = "\uE7C3", Title = "多人联机常见问题",
            Summary = "加入服务器的方法与常见限制",
            Body = "加入服务器：启动游戏 → 多人游戏 → 添加服务器 → 输入服务器地址。\n\n" +
                   "· 离线账户只能进入开启了「离线模式」的服务器，无法进入正版验证服务器。\n" +
                   "· 服务器版本需要与实例的游戏版本一致（或服务器支持跨版本）。\n" +
                   "· 客户端 Mod（如性能优化类）一般允许，但有反作弊的服务器可能限制。"
        },
        new HelpTopicVM
        {
            Category = "常见问题", Glyph = "\uE7C3", Title = "安装 Mod / Fabric API",
            Summary = "下载、导入 Minecraft Mod（模组）",
            Body = "推荐方式：内容中心 → 模组 → 搜索安装（会自动匹配实例的 MC 版本与加载器，并自动提示安装 Fabric API 前置）。\n\n" +
                   "手动方式：下载与实例 MC 版本、加载器都匹配的 Mod .jar 文件，放入 实例目录/mods/（可在「本地管理」查看并启用/禁用）。\n\n" +
                   "注意：Forge 与 Fabric 的 Mod 互不通用； loaders 不匹配的 Mod 不会被加载。"
        },
        new HelpTopicVM
        {
            Category = "常见问题", Glyph = "\uE7C3", Title = "安装存档 / 存档损坏",
            Summary = "存档的安装位置与损坏处理",
            Body = "安装存档：把整个世界文件夹（包含 level.dat 的那个文件夹）放入 实例目录/saves/ 下，重启游戏即可在单人游戏中看到。\n\n" +
                   "存档损坏：\n" +
                   "1. 先备份整个 saves 文件夹再尝试任何修复。\n" +
                   "2. 用游戏内备份或第三方工具尝试恢复。\n" +
                   "3. 本启动器导入与删除实例均为受保护操作，不会触碰 saves 目录。"
        },
        new HelpTopicVM
        {
            Category = "常见问题", Glyph = "\uE7C3", Title = "安装光影 / 纹理包",
            Summary = "光影包与资源包的安装位置",
            Body = "光影：需要先安装 Iris（Fabric）或在 OptiFine 环境；光影包（.zip，不要解压）放入 实例目录/shaderpacks/，游戏内 选项→视频设置→光影包 中启用。\n\n" +
                   "纹理包（资源包）：放入 实例目录/resourcepacks/，游戏内 设置→资源包 启用。\n\n" +
                   "两者都可以在「内容中心」对应分类中直接下载。"
        },

        // ---- 启动器 ----
        new HelpTopicVM
        {
            Category = "启动器", Glyph = "\uE713", Title = "账户说明",
            Summary = "微软正版账户与离线账户",
            Body = "微软账户：完整登录（设备码流程），可进入正版服务器。首次使用需要在「设置」中配置自己的 Azure 应用 Client ID（环境变量 AURORA_MICROSOFT_CLIENT_ID）。\n\n" +
                   "离线账户：无需网络，用户名即身份（UUID 由用户名生成）。仅适用于离线模式服务器，不能进入正版验证服务器。\n\n" +
                   "凭据使用 Windows DPAPI 加密保存，不会明文落盘。"
        },
        new HelpTopicVM
        {
            Category = "启动器", Glyph = "\uE713", Title = "存储目录说明",
            Summary = "游戏文件保存在哪里、如何修改",
            Body = "默认存储目录：%APPDATA%\\AuroraLauncher\n\n" +
                   "· versions/：游戏版本（客户端 jar 与版本 JSON，按版本共享）\n" +
                   "· libraries/：支持库（所有实例共享）\n" +
                   "· assets/：资源文件（音效、语言等）\n" +
                   "· instances/：每个实例独立的游戏目录（saves、mods、config 等）\n\n" +
                   "可在「设置 → 存储」修改目录并一键打开文件夹。修改需重启生效，已有文件不会自动迁移。删除实例时，不再被引用的版本文件夹会自动清理。"
        },
    };
}

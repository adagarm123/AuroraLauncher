# ADR-0019：进度诚实与加载器感知的模组体检

- 日期：2026-11-15（第 97 轮）
- 状态：已采纳（含 2 条静态防线 + 7 项单测）
- 关联：ADR-0018（下载任务单一状态来源）、ADR-0014（启动闸门）

## 背景

用户给两张截图。第一张：下载管理显示 `100 %`、`378.8 KB/s`、任务行 `安装 Forge 加载器 87/90`，看起来完成却还在跑。第二张：一个 Forge 1.20.1 实例的启动前检查报「缺少 19 个前置（共 29 项）」，列表里赫然有 `fabric-api` 两条、`kotlinforforge` 两条，并建议做「一键补齐」。

代码核实：

- **假 100% 不是四舍五入 96.67%**。`87/90` 是启动器自己的文件批次（`DownloadJob.Report` 把 `Stages[i].Label + " 87/90"` 拼进阶段文案），速度也来自我们的下载引擎，不是 Forge JVM 内部日志。真因有两层：`JobProgressPolicy.PercentFor(stageIndex == stageCount, …)` 合法地返回 100；`DownloadEngine` 把**失败**文件也计入 `CompletedFiles`（失败即 processed），`CompletedFiles/TotalFiles` 同样到 1.0。展示层却没有任何「任务未结束就不能 100」的约束。
- **fabric-api 误报来自双元数据 jar**。在用户真实目录 `instances/bdc7ca7d` 查到 96 个 jar、其中 6 个同时含 `fabric.mod.json` 与 `META-INF/mods.toml`；`EnhancedVisuals_FORGE_v1.8.31_mc1.20.1.jar` 和 `ItemPhysicLite_FORGE_v1.6.11_mc1.20.1.jar` 的 Fabric 元数据声明了 `fabric-api`，正是截图那两条。旧 `ModPreflight.TryParse` **无条件优先读 `fabric.mod.json` 并立即 return**，把 Forge 版模组当 Fabric 模组解析。
- 重复列出则因为去重键是 `depId|requiredByMod`，N 个依赖方产生 N 行。

用户明确要求「先修掉跨平台解析 Bug，再上一键补齐」，并警告在坏解析上接自动下载会把 `fabric-api.jar` 装进 Forge 目录导致崩溃。

## 决定

1. **进度诚实**（`ProgressDisplayPolicy`）：未完成的任务 `Floor` 后封顶 **99%**，真正完成才 100。接入 `DownloadJob.Report` 与 `JobProgressPolicy.Aggregate`（只要有活跃/排队任务就封顶）。保留 `PercentFor` 原有语义与单测，避免改动既有数学。
2. **黑盒不确定态**：`CurrentStageKey == "installer-run"` 时主进度条 `IsIndeterminate`，顶部 `HeaderText` 改为「正在执行 Forge / NeoForge 安装器内部下载（受网络影响，可能较慢）」，任务行阶段文案不再硬凑 `87/90`。
3. **文件完成停滞看门狗**：`DownloadJob` 跟踪 `LastCompletedFiles` / `TrackedTotalFiles` / `LastFileCompletedUtc`；`JobWatchdogPolicy` 增加 120 秒阈值。**即使速度非 0**，只要没有新文件完成就判卡住，中止 linked token、留红色失败任务可重试。原有 30s / 90s / 15min 分级不变（真实安装静默 94~165 秒，不能一刀切）。
4. **加载器感知解析**：`TryParse(jar, loader, log)` 按实例加载器决定元数据优先顺序（Forge → `mods.toml` 优先；Fabric → `fabric.mod.json` 优先），另一份仅作回退。`ReadFabricMeta` / `ReadForgeMeta` 拆成独立入口。
5. **策略层过滤与去重**（`ModDependencyPolicy`）：`IsRelevant(depId, loader)` 过滤跨加载器 id（Forge 忽略 `fabric-api` / `fabric-*` / `quilt*`，Fabric 忽略 `kotlinforforge` / `*-forge` 等）与平台 id；`Merge` 按 modId 合并，保留全部版本范围与依赖来源列表。`ModDependency` 增加 `Ranges` / `RequiredByList`（位置构造保持不变）。`FabricApiMissing` 改为基于合并结果、且只在 Fabric 实例成立。
6. **为自动补齐铺路但不启用**（`VersionRangePolicy` / `PreflightAutoFixPolicy`）：范围匹配 `[4.11.0,4.99.0)` 正确排除 4.10.0 与 4.99.0、支持无上界/`||`/通配符并取范围内最新版；唯一 `LoadersFor(loader)`、slug 映射（`kotlinforforge → kotlin-for-forge` 等）、`VersionAllowed` 双重校验加载器与 MC 版本。本轮**不实现** UI 勾选、Modrinth 下载与自动启动。

## 不做的事

- 不在本轮接自动补齐：用户明确要求先修解析。策略层已就位，下一轮只需接 UI + DownloadJob + 重扫。
- 不把 2 分钟停滞阈值套到所有阶段：只有 `TrackedTotalFiles > 0` 的文件批次流程启用，纯安装器 JVM 静默仍走 15 分钟上限。
- 不改 `PercentFor`：它服务于阶段图数学，展示约束属于展示层。
- 不自动删除那 4 个「加载器不符」的 jar：那是数据事实，应让用户决定。

## 后果与风险

- 真实数据复扫（`--preflight-scan <dir> Forge`）：**缺少 19 个前置（29 项）→ 6 条**，`fabric-api` 消失、`fabricApiMissing=False`，`kotlinforforge` 一行保留两个来源，`tacz` 一行保留 4 个来源与 3 个范围。
- 反向风险：某模组确实需要跨加载器 id 时会被过滤。对 Forge 而言 `fabric-api` 不可能是合法前置，风险可忽略；`WrongLoader` 列表仍会报告真正放错的 Fabric jar。
- 下一轮自动补齐必须复用 `ModDependencyPolicy.Merge` 的结果与 `PreflightAutoFixPolicy.LoadersFor`，否则 Guard 会红。

## 机制防线

- `Guard: Forge 实例不得产生跨加载器前置（第 97 轮）`：锁定 `TryParse(file, loader, log)`、两个元数据入口、`IsRelevant` 过滤、`Merge` 去重、`FabricApiMissing` 来源，以及 Forge 的查询 facet 不含 `fabric`。
- `Guard: 进度封顶、不确定态与停滞检测必须接线（第 97 轮）`：锁定 `ProgressDisplayPolicy.Display` 调用、`LastFileCompletedUtc` 跟踪、`FileStallSeconds = 120`、VM 调用 `IsFileProgressStalled`、XAML 的 `IsIndeterminate` 绑定。

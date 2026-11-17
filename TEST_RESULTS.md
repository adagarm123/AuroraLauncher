# 自测结论（AuroraSelfTest）

> 更新于 2026-11-17（第 101 轮）。
> **上次全绿：2026-11-17 —— Unit 166/166（离线）、All 173/173（含网络集成）。**
> 运行命令：`dotnet run --project tests/AuroraSelfTest -c Debug [-- --unit|--integration|--all]`
> 一键脚本：`powershell -File scripts/run-tests.ps1 [-Full]`（CI 见 `.github/workflows/selftest.yml`）
> 测试分层：**unit（离线，CI 必跑）/ integration（联网，CI 定时跑）/ all**。

## 最新结果（2026-11-01）

```
--unit 模式（离线，CI 必跑）：
[PASS] SHA-1 of 'abc' matches known digest
[PASS] MavenNameToPath (with classifier)
[PASS] OfflineUuid format
[PASS] LaunchArgumentBuilder builds classpath + JVM + game args + tokens
[PASS] Resolution feature rule injects --width/--height
[PASS] OS-rule filtering excludes non-windows natives from classpath
[PASS] ModManager.BuildFacets (content types)
[PASS] ModManager.ReadMrpackInfo + ImportMrpack overrides
[PASS] InstanceManager.Delete garbage-collects unreferenced versions
[PASS] DownloadEngine resume rule: Content-Range acceptance (pure logic)
[PASS] GameInstaller.Evaluate: offline state semantics
[PASS] CrashAnalyzer: rules translate technical logs into plain reasons
[PASS] Guard: VersionJson raw deserialize only inside VersionResolver
[PASS] Guard: merged fabric profile classpath == ResolveAsync libs + client jar
[PASS] Guard: shared HttpClient never cached or disposed outside factory
[PASS] Guard: instance-metadata IO only inside InstanceManager
[PASS] NetworkProbe.Decide: data-driven source selection (pure logic)
[PASS] Resolver: cache-hit path and local-first path agree (offline)
[PASS] Guard: every XAML command binding resolves to a VM property
[WARN] VM 命令从未被任何 XAML 绑定（死代码候选，仅警告不失败）: ChooseInstanceIconCommand, InstallContentCommand, ResetContentCommand
[PASS] Guard: every interactive element has a binding or event handler
[PASS] Guard: CHANGELOG round numbering is continuous
[PASS] DiagnosticsExporter: truncation keeps the TAIL (crash lines at end)
[PASS] DiagnosticsExporter: redaction replaces (not deletes) and reports hits
[PASS] DiagnosticsExporter: no over-redaction (mod names / non-user paths stay intact)
[PASS] AuthService: Microsoft client id resolution + configuration guard
[PASS] LauncherConfig: AutoInstallFabricApi defaults ON and survives JSON round-trip
[PASS] GenerationGate: stale loader-card responses are discarded (race regression)
[PASS] Guard: doc baselines agree (round + unit/all counts across the three main docs)
[PASS] SlowTransferWatcher: sustained crawl aborts, brief dips don't (pure logic)
[PASS] LoaderCardVM: null coercion + append-only fill never auto-select (round-50 regression)
[PASS] UpdateService: manifest signature + downgrade rejection (pure logic)
[PASS] UpdatePolicy: 3-way mode switch (startup check / notify / auto-download)
[PASS] JavaRuntimeService: Adoptium manifest parse + zip-slip guard (pure logic)
[PASS] CrashAnalyzer.BuildReport: all causes, case-insensitive, no-match fallback
[PASS] CurseForgeService: search parse + official-page URL builders (pure logic)
[PASS] OptiFineService: downloads-page parse + ad-token URL parse (pure logic)
[PASS] UpdateApplyArgs: visible updater handoff round-trips and fails closed
[PASS] JavaRequirement: MC version -> recommended Java major (pure logic)
[PASS] UpdateBannerPolicy: outdated launcher CTA + staged wording (pure logic)
[PASS] BulkObservableCollection + NavigationPolicy: one-shot UI updates / no redundant navigation
[PASS] Guard: cached pages never re-run init on every Loaded (round-63)
[PASS] CoalescingSingleFlight: A→B→C collapses to ONE rerun with the latest arg
[PASS] UiPolicies: selection restore / debounce guard / navigation sub-key
[PASS] LoadedGuardPolicy: accepts any single-shot field name, rejects unguarded Loaded
[PASS] PageHostPolicy: lazy page creation + exactly one visible page
[PASS] Guard: navigation keeps pages in the visual tree (round-65)
[PASS] Guard: no animation clocks / software effects in the UI (round-67)
[PASS] UiLatencyTrace: flags slow switches and summarises them
[PASS] PageWarmupPolicy: sidebar order, skip built pages, never warm while busy
[PASS] Guard: lists nested in a ScrollViewer stay bounded (round-68)
[PASS] Guard: page init runs on first SHOWN, not on first attach (round-68)
[PASS] Logger: file writes are batched off-thread and flushed on demand
[PASS] NavigationLatencyPolicy: 'page is slow' vs 'UI thread was busy'
[PASS] Guard: log I/O off the calling thread + Logs feed frozen while hidden (round-69)
[PASS] RouteFailurePolicy: 404 is permanent, no retry storm
[PASS] UiStallTrace: attributes a UI freeze to GC or to app work
[PASS] DiagnosticsExporter: 导出日志 writes a sanitized, headered text file
[PASS] NetRouting.BuildHosts: both hosts survive a mirrored input URL
[PASS] LauncherConfig: legacy mirror domain is healed in old configs
[PASS] ManifestCache: offline copy round-trips and rejects junk
[PASS] JobProgressPolicy: stage-aware percent, aggregate, throttle
[PASS] DownloadJobManager: per-task cancel, queue cap, clear finished
[PASS] DownloadJob: report throttling + failure message stays human
[PASS] PathWriteGate: one writer per destination, others run in parallel
[PASS] DownloadJobManager: a queued job can be withdrawn before it starts
[PASS] CloseGuardPolicy: only ask when work is really in flight
[PASS] BinaryDelta: patch round-trips and rejects the wrong base
[PASS] PortableModePolicy: portable.txt / config.json decide the data root
[PASS] DownloadJournal: resume only what really has a .part
[PASS] CloseBehaviorPolicy: tray decides between ask / exit / hide
[PASS] SingleInstancePolicy: one launcher per data root, not per exe path
[PASS] UpdateService: patch manifest must be signed and round-matched
[PASS] SingleInstanceGuard: only a shown window acknowledges an activation
[PASS] GameInstance: playtime reads like a human wrote it
[PASS] PlaytimeTracker: a killed launcher still gets credit for the session
[PASS] ModPreflight: missing prerequisite / conflict / duplicate / wrong loader
[PASS] MrpackExporter: Modrinth-hosted mods become links, the rest are bundled
[PASS] InstanceManager: launch count and playtime survive a reload
[PASS] CrashResolutionEngine: 常见崩溃 → 具体修复动作（第 78 轮规则库）
[PASS] CrashFixSafety: 自动修复只能改启动器自己的设置/下载文件
[PASS] SupportReport: 求助邮件由用户亲手发送（mailto 可回读）
[PASS] Guard: 崩溃修复不删文件、不阻塞界面（第 78 轮）
[PASS] SaveBackup: 触发矩阵 / 保留策略 / 只读存档（第 79 轮）
[PASS] PerformanceReport: FPS 解析 / 结论阈值 / CPU 数学（第 79 轮）
[PASS] CommandPalette: 排序 / 多词 AND / 索引来源（第 79 轮）
[PASS] GlassEffectPolicy: 默认关闭 / 降级 / 三种玻璃方案的边界（第 80–81 轮）
[PASS] GlassGuardPolicy: 模糊只允许在 MainWindow 的 Glass* 元素上（第 80 轮）
[PASS] LaunchGuardPolicy: 没有实例时按钮变成引导而非死按钮（第 82 轮）
[PASS] MemoryAllocationPolicy: 推荐阶梯 / 物理内存上限 / 不越界（第 83 轮）
[PASS] Guard: 内存清理只碰自己，GC.Collect 只在后台（第 83 轮）
[PASS] DataRootPolicy: 临时目录不得成为数据根（第 84 轮）
[PASS] InstanceManager: 删除先删文件夹，失败保留记录；扫描不碰陌生文件夹（第 84 轮）
[PASS] Guard: 新建实例只跳转，绝不偷偷建空实例（第 84 轮）
[PASS] Guard: 冒烟测试必须隔离配置文件（第 84 轮）
[PASS] DownloadRoutePolicy: 测速择优 / 强制单线路 / 回退集合（第 85 轮）
[PASS] Guard: 测速只用 HEAD / 有缓存 / 下载路径不等测速（第 85 轮）
[PASS] Forge 安装器 URL：必须拼成「MC版本-Forge版本」（第 86 轮）
[PASS] LauncherConfig.ToOfficialUrl: maven 路径可按 group 反解（第 86 轮）
[PASS] Guard: 安装器重试前必须清残留、且不违背强制线路（第 86 轮）
[PASS] DownloadIntegrity: 没有校验值不再等于「完整」（第 87 轮）
[PASS] Guard: 缓存命中必须走 DownloadIntegrity，VerifyHash(null) 已删除（第 87 轮）
[PASS] DownloadResidue: 正在下载的保留，其余清理（第 87 轮）
[PASS] DownloadResidue.ShouldWarn: 阈值任一超标才提示（第 87 轮）
[PASS] UpdateStaging: 只清 24 小时前的、且不是当前待装的包（第 87 轮）
[PASS] Guard: 清理残留的三处入口都接上了（第 87 轮）
[PASS] StorageCleanupPolicy: 白名单只放行缓存与临时文件（第 88 轮）
[PASS] StorageCleanupPolicy: 空壳版本目录的判定（第 88 轮）
[PASS] StorageCleanupPolicy: CostsOffline 一律默认不勾（第 88 轮）
[PASS] Guard: 清理服务只走白名单，绝不整根删除（第 88 轮）
[PASS] StorageCleanupService: 扫描分类与选择性删除（第 88 轮）
[PASS] DownloadJournal.Forget: 清理残片后台账不再指向已删除的文件（第 88 轮复核）
[PASS] Guard: 清理的忙碌态与两个入口的分工都写清了（第 88 轮复核）
[PASS] SettingsDirtyPolicy: 快照对比能发现任何一项修改（第 90 轮）
[PASS] Guard: 离开设置页必须拦截、有任务时关窗必须弹窗（第 90 轮）
[PASS] Guard: 首页内容必须可滚动，且列表有高度上限（第 91 轮）
[PASS] ModpackIntegrity: 模组数与清单双口径（第 92 轮）
[PASS] LaunchReadiness: 加载器与整合包状态必须拦住启动（第 92 轮）
[PASS] PerformanceReport.RunVerdict: 崩溃/闪退/未采集不再谎报「健康」（第 92 轮）
[PASS] Guard: 启动闸门与整合包预期不能被绕过（第 92 轮）
[PASS] CommandGate: 一次只放一个进去（第 93 轮）
[PASS] RelayCommand: 执行中再点不会重入，异常也会解锁（第 93 轮）
[PASS] Guard: 异步命令必须自带防重入，且不许「火后不管」（第 93 轮）
[PASS] LauncherProfiles: 安装器要的配置文件，缺了补、坏了修、好的不碰（第 94 轮）
[PASS] Guard: 跑 Forge/NeoForge 安装器前必须先补 launcher_profiles.json（第 94 轮）
[PASS] VersionSourcePolicy: 本地优先 / 第三方加载器 id 不得查官方清单（第 95 轮）
[PASS] InstallerDependencies: Maven 坐标 / 依赖清单 / 多线路候选（第 95 轮）
[PASS] InstallerDependencies: 只把真正失败的坐标列给用户（第 95 轮）
[PASS] GameInstaller.IsVersionJsonInstalled: 空壳 loader profile 不算已安装（第 95 轮）
[PASS] Guard: 本地版本 JSON 优先于官方清单，第三方 id 不得发起清单查询（第 95 轮）
[PASS] Guard: 安装器运行前必须预填充依赖，且成功必须校验真实 profile 文件（第 95 轮）
[PASS] LoaderProfileLocator: 认领已装好的 profile，拒绝空壳与假 id（第 96 轮）
[PASS] JobWatchdogPolicy: 准备 30 秒 / 安装器下载 90 秒 / 运行 15 分钟（第 96 轮）
[PASS] JobProgressPolicy / JobState: Preparing 也算活跃任务（第 96 轮）
[PASS] NavigationHighlight: 单一 CurrentPageKey 最多一个高亮（第 96 轮）
[PASS] Guard: 补全必须走 DownloadJob，标题栏不得脱离任务队列（第 96 轮）
[PASS] Guard: 全部取消置灰 / 错误可重试 / 导航单一来源（第 96 轮）
[PASS] VersionRangePolicy: [4.11.0,4.99.0) 边界与最新版选择（第 97 轮）
[PASS] ModDependencyPolicy: 跨加载器过滤与按 modId 去重（第 97 轮）
[PASS] PreflightAutoFixPolicy: 加载器标签、slug 映射与版本准入（第 97 轮）
[PASS] ProgressDisplayPolicy: 未完成任务永不显示 100%（第 97 轮）
[PASS] JobWatchdogPolicy: 有速度但文件完成数停滞也判卡住（第 97 轮）
[PASS] Guard: Forge 实例不得产生跨加载器前置（第 97 轮）
[PASS] Guard: 进度封顶、不确定态与停滞检测必须接线（第 97 轮）
[PASS] InstallFailureText: The operation was canceled. 不再裸奔给用户（第 98 轮）
[PASS] JavaDistributionPolicy: Zulu 只认普通 JDK、挑最新、拒绝坏包（第 98 轮）
[PASS] JobWatchdogPolicy: 已进入命名阶段的 Preparing 不再被 30 秒误杀（第 98 轮）
[PASS] JobProgressPolicy: 失败任务不能把总进度抬到 100%（第 98 轮）
[PASS] Guard: 安装器下载必须有心跳、镜像优先且失败清残片（第 98 轮）
[PASS] Guard: 失败消息不被二次覆盖、失败汇总必须可见（第 98 轮）
[PASS] Guard: Java 发行版白名单、校验与设置持久化（第 98 轮）
[PASS] CacheFolderPolicy: 缓存清单、体积格式与安全目录判定（第 99 轮）
[PASS] UninstallPolicy: 白名单、存档/Java 默认保留、危险目录全部保留（第 99 轮）
[PASS] Guard: 内容中心状态与列表同源、空状态三态、结果区有最小高度（第 99 轮）
[PASS] Guard: 子导航高亮只认 ContentKind，且有 active 竖条（第 99 轮）
[PASS] Guard: 引导便携选项、缓存展示、卸载必须两次确认（第 99 轮）

=== Result: 123 passed, 0 failed, 7 skipped ===（跳过的 7 项为网络集成测试）

运行时冒烟诊断（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --nav-smoke`
STA 线程加载真实样式字典、构造元素、强制布局并跑 12 轮显隐 / IsIndeterminate 循环；第 67 轮实测 `SMOKE OK`。

崩溃卡片渲染冒烟（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --crash-smoke`
第 78 轮新增。真实样式字典 + 真实 `MainViewModel` + 真实 `LogsView`，灌入「OOM + 重复模组」诊断，实测输出：

```
[crash-smoke] 渲染文本 26 条 / 按钮 8 个 / 绑定错误 0 条
[crash-smoke] SMOKE OK - 诊断卡渲染 + 一键修复生效
```

覆盖点：卡片标题 / 诊断正文 / 「可一键修复」标签 / 第二条诊断 / 计数文案确实渲染出来（WPF 绑定错误为静默失败，单测看不到）；「发送给作者」「打开设置」与行内修复按钮存在且可用；直接执行 `ApplyCrashFixCommand` 后**配置真的被改写**、结果回显、按钮置灰。数据根指向临时目录，绝不碰真实 config.json。

第 79 轮功能渲染冒烟（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --round79-smoke`

```
[r79-smoke] 首页文本 27 条 / 实例页文本 48 条 / 按钮 17 个 / 绑定错误 0 条
[r79-smoke] SMOKE OK - 性能卡 / 存档备份 / Ctrl+K 语义全部生效
```

覆盖点：首页性能卡（标题 / 「上次运行」/ `峰值内存 3900 MB` / 系统内存读数 / CPU 读数）与实例页存档备份卡（标题 / 「立即备份」/「打开备份文件夹」）确实渲染；**真的执行一次备份**并断言 zip 生成、原存档未被移动；Ctrl+K 打开 → 搜「备份」有结果且默认选中 → 搜「存档自动备份」执行后落在设置页；方向键不改变结果集。第 84 轮起还会把实例页渲染成 `%TEMP%\aurora-instances.png`（人工核对按钮行不折行、文案不截断）。数据根与**配置文件**都指向临时目录（`UseTestRoot`）。

第 91 轮起这段冒烟还会把窗口压到 **760 / 600 / 480** 三档，逐档滚到底后断言「性能概览」卡最底部那行字落在视口内，并断言 **hero 的启动按钮不随滚动移动**（用坐标比较）。实测输出：

```
[r79-smoke] 首页 760px: 内容 580.8 / 视口 580.8 / 可滚 0 / 锚点可见=True
[r79-smoke] 首页 600px: 内容 474.7 / 视口 420.8 / 可滚 54 / 锚点可见=True
[r79-smoke] 首页 480px: 内容 474.7 / 视口 300.8 / 可滚 174 / 锚点可见=True
[r79-smoke] 首页 600px 截图: %TEMP%\aurora-home-600.png
```

（760px 时 `可滚 0` 是正确行为：内容正好等于视口，不该出现滚动条。）另外断言实例列表带 `MaxHeight`——不限高的 `ListBox` 放进滚动容器会全量生成容器（第 68 轮的坑）。

第 80 轮玻璃效果冒烟（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --glass-smoke`

```
[glass-smoke] 警告文本 6 条 / 设置页文本 58 条 / 下拉框 4 个 / 绑定错误 0 条
[glass-smoke] SMOKE OK - 表面重着色可逆 / 噪点与渐变已冻结 / 红色警告可渲染 / 设置项已接线
```

覆盖点：`ApplyTranslucentSurfaces` 后 `PanelBrush` 真的变半透明、`Restore()` 后颜色与原来完全一致且**覆盖字典已摘除**（否则切主题会失效）；噪点刷为已冻结的 `ImageBrush` 且 `TileMode=Tile`；渐变已冻结且至少两个 stop；红色警告窗口真实渲染出标题/风险文案/「取消」按钮；设置页「界面材质」下拉框绑定到 4 个选项、默认「关闭」。

第 83 轮内存冒烟（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --memory-smoke`

```
[mem-smoke] direct trim 64 ms 236 -> 6 MB
[mem-smoke] 设置页文本 62 条 / 按钮 12 个 / 复选框 5 个 / 绑定错误 0 条 / 清理结果「已释放约 23 MB（24 MB → 1 MB），只处理了启动器自己」
[mem-smoke] SMOKE OK - 自动分配 / 超限警告 / 手动清理全部生效
```

覆盖点：真实样式 + 真实 `MainViewModel` 渲染设置页，「自动（推荐）」「推荐说明」「释放启动器占用内存」都确实出现（绑定路径写错是静默失败，单测看不到）；切到自动后输入框数字变成推荐值并**变灰**（`IsEnabled` 绑定生效）；手填 99999 立刻出现红字警告、改回 4096 消失；点按钮走**已绑定的命令**，等 `IsCleaningMemory` 翻回后断言拿到可读结果、且没有"失败"。顺带把设置页渲染成 PNG（`%TEMP%\aurora-mem-settings.png`）做人工目检。数据根指向临时目录。

第 87 轮残留清理冒烟（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --residue-smoke`
真实样式 + 真实 `MainViewModel`，先在临时数据根造两个无主 `.part`（`update/` 里 2 MB、`libraries/` 里 4 KB）与一个已完成的同名 `.jar`，把阈值压到 1 MB，实测输出：

```
[residue-smoke] banner=True text=「检测到 2 个未完成的下载残留（约 2.0 MB），可在设置中清理」
[residue-smoke] 清理结果=「已清理 2 个残留文件，释放 2.0 MB」
[residue-smoke] SMOKE OK - 扫描 / 提示 / 跳转 / 清理全部生效
```

覆盖点：启动扫描（后台线程）真的把提示顶到下载页、文案与按钮都渲染出来；「去设置清理」真的把 `CurrentPageKey` 切到 Settings；点设置页按钮走**已绑定的命令**后，两个 `.part` 在磁盘上真的消失、**相邻的已完成文件没被动**、提示自动消失、结果文案包含「释放」；两个页面绑定错误 0 条。同时把下载页渲染成 `%TEMP%\aurora-residue-downloads.png` 人工目检（横幅不挤压列表、两个按钮不折行）。

第 88 轮存储清理冒烟（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --cleanup-smoke`
真实样式 + 真实 `MainViewModel`，临时数据根里铺图标缓存 ×2、旧日志、加载器缓存（`CostsOffline`）、存档、待安装更新包、空壳版本目录，实测输出：

```
[cleanup-smoke] 扫描：共 4 类 · 4 个文件 + 1 个空目录 · 15.1 KB。确认无误后点「清理选中项」。
[cleanup-smoke] 分类行 4 个 / 文本 加载器元数据缓存 · 1 个文件 · 2.4 KB（会牺牲离线能力） | 空壳版本目录 · 1 个空目录 | 历史日志 · 1 个文件 · 2.9 KB
[cleanup-smoke] 结果：已释放 12.7 KB（3 个文件 + 1 个空目录）
[cleanup-smoke] SMOKE OK - 扫描 / 分类勾选 / 清理 / 保护项全部生效
```

覆盖点：点「扫描垃圾文件」后报告真的出现并渲染成 4 个复选框；**离线档未勾、安全档已勾**（默认值来自策略而不是硬编码）；选中汇总文案生成、清理按钮在有勾选时可用；点「清理选中项」走**已绑定的命令**后，图标缓存 / 旧日志 / 空壳目录在磁盘上真的消失，而未勾选的加载器缓存、`instances/` 存档、待安装更新包**一个没动**；绑定错误 0 条。设置页渲染成 `%TEMP%\aurora-cleanup-settings.png` 人工目检（两个按钮不折行、分类行与说明不重叠）。确认框用 `SuppressCleanupConfirmForTests` 绕过（模态框在无人值守时无法点击），生产路径默认仍会询问。

只读诊断（显式参数，不进 CI，**不删任何文件**）：`dotnet run --project tests/AuroraSelfTest -- --cleanup-scan`
对当前配置的数据根跑一次真实扫描并打印分类明细。本机实测 `117 ms · 4 类 · 175 个文件 · 97.7 MB`（版本清单 272 KB / 历史日志 2 个 5.5 MB / 无主残片 119 个 68.3 MB / 加载器缓存 53 个 23.7 MB，默认不勾）。

第 90 轮设置未保存冒烟（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --settings-smoke`
真实 VM + 真实设置页，用 `UnsavedSettingsPromptForTests` 替代模态框（无人值守点不了「是」），实测输出：

```
[settings-smoke] 截图: %TEMP%\aurora-settings-close.png
[settings-smoke] 截图: %TEMP%\aurora-settings-save.png
[settings-smoke] SMOKE OK - 脏检查 / 取消拦截 / 放弃回滚 / 保存离开 / 关窗询问 全部生效
```

第 92 轮启动闸门冒烟（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --launch-gate-smoke`
在隔离数据根里造一个「原版齐全、加载器没装、整合包应含 87 个模组但 mods 为空」的实例，用真实 `MainViewModel` 点「启动游戏」，断言：闸门报 `ModpackEmpty`、状态栏写明「未启动」、日志里**没有** `Launching:` 命令行（即真的没有启动任何进程）。全程离线（假版本没有 libraries，不需要网络）。

```
[launch-gate-smoke] SMOKE OK：整合包 0 个模组 → 拦住启动，未生成任何 Java 命令行
[launch-gate-smoke] 状态栏：整合包的模组一个都没有（未启动）
```

只读诊断（显式参数，**不启动任何东西**）：`dotnet run --project tests/AuroraSelfTest -- --readiness-scan`
对真实数据根逐个实例打印闸门结论。本机实测（4 个实例）：「BattleArmory TACZ」与「1.21.10 (Fabric)」的 `resolvedVersionId` 为空 → 均判 `LoaderNotInstalled` 拦住；两个 Fabric 实例指向 `fabric-loader-0.20.1-1.20.1` → 可以启动。

第 94 轮 Forge 安装冒烟（显式参数，**需要网络与 JDK**，不进 CI）：`dotnet run --project tests/AuroraSelfTest -- --forge-install-smoke`
在隔离目录里只放 `versions/1.20.1/1.20.1.json`，然后用**启动器自己的安装流程**（`ModLoaderInstaller.InstallAsync`）装 Forge 1.20.1-47.4.22，断言：`launcher_profiles.json` 被自动补上且合法、安装成功、版本目录检测正确、版本 JSON 真的存在。本机实测（Java 25）：

```
[forge-install] SMOKE OK：1.20.1-forge-47.4.22 安装成功（180 秒）
[forge-install] launcher_profiles.json 已自动补上：…\aurora-forge-install-f26b81\launcher_profiles.json
```

同一个探针在补文件之前是 `exit=1`，输出正是用户截图那句 `There is no Minecraft launcher profile in …, you need to run the launcher first!` —— 修复前后的对照。

覆盖点：刚进设置页不算脏；改一个字段后立刻判定为脏且橙色提示渲染出来（提示由 500 ms tick 刷新，所以测试等一个 tick）；选「取消」→ `CurrentPageKey` 仍是 Settings（**真的没切走**）；选「放弃」→ 离开且字段恢复成保存值、配置文件未被改写；选「保存并离开」→ 离开且值写进配置；`ConfirmCloseWithUnsavedSettings` 选「取消」返回 false（窗口留住）、选「保存」返回 true；两个页面绑定错误 0 条。截图人工核对：下拉框里「直接退出（有下载时仍会确认）」没被截断、保存按钮旁的未保存提示与按钮对齐。

切页布局基准（显式参数，不进 CI）：`dotnet run --project tests/AuroraSelfTest -c Release -- --nav-bench`
真实样式 + 真实 VM（103 个版本 / 600 行日志 / 12 个实例），1160×720 窗口。第 68 轮 Release 实测：

| 页面 | 首次构建 | 首次显示 | 预热后首次显示 | 稳态切页 |
|------|---------:|---------:|---------------:|---------:|
| Home | 10.4 ms | 12.1 ms | 0.1 ms | 0.1 ms |
| Download | 4.8 ms | 11.0 ms | 0.1 ms | 0.1 ms |
| Instances | 10.8 ms | 29.5 ms | 0.1 ms | 0.1 ms |
| Mods | 19.0 ms | 9.0 ms | 0.1 ms | 0.1 ms |
| Settings | 5.6 ms | 11.3 ms | 0.1 ms | 0.1 ms |
| Logs | 2.8 ms | 57.0 ms | 0.1 ms | 0.1 ms |
| Help | 3.6 ms | 12.5 ms | 0.1 ms | 0.1 ms |

结论：切页本身（稳态）0.1–0.3 ms；成本集中在「每一页的第一次」，预热后首次显示降到 0.1 ms 量级。

--all 模式（含网络集成）：
另含 [PASS] Fetch + parse official version manifest
      [PASS] Resolve 1.21.1 (inheritance merge) + build launch spec
      [PASS] DownloadEngine: fetch small file with SHA-1 + Range resume
      [PASS] Resolver cache: vanilla cache vs network consistent
      [PASS] Resolver cache: loader profile (inheritsFrom) cache merges vanilla
      [PASS] Resolver cache: multi-level inheritance chain
      [PASS] Resolver cache: corrupted & incomplete entries fall back to network

=== Result: 130 passed, 0 failed, 0 skipped ===
```

## 测试项增长记录（第 48 轮补记）

外部复核指出第 42 轮（Unit 22）到第 46 轮（Unit 25）之间新增的 3 项测试在更新日志中没有解释。逐项溯源如下（All 模式恒为 Unit + 7 项网络集成，故同步 +3）：

| 项 | 测试 | 来源轮次 | 说明 |
|----|------|----------|------|
| #23 | `NetworkProbe.Decide: data-driven source selection` | 第 42 轮（代码），第 43 轮计入口径 | 随「网络环境自动感知」功能加入；当时 TEST_RESULTS 清单漏列，第 44 轮修正口径（23=22+1） |
| #24 | `AuthService: Microsoft client id resolution + configuration guard` | 第 45 轮 | 随微软登录 400 根治加入（env > config 解析优先级 + 配置门卫） |
| #25 | `Guard: README round numbering is continuous` | 第 45 轮文档会话 | 随「第 44→46 轮编号跳轮」修复加入；**当时未在更新日志中说明**（本表补记），README 更新补充章节同轮补齐（第 59 轮起历史记录迁入 CHANGELOG.md，该 Guard 更名并改扫 CHANGELOG；第 60 轮扩展为同时校验 README 最新一轮标题 = CurrentRound、两份文档的历史范围声明 = CHANGELOG 末轮，漏改数字即红） |
| #26 | `LauncherConfig: AutoInstallFabricApi defaults ON and survives JSON round-trip` | 第 47 轮 | 随 Fabric API 前置自动安装加入 |
| #27 | `GenerationGate: stale loader-card responses are discarded (race regression)` | 第 48 轮 | 随加载器卡片延迟修复加入（乱序响应丢弃的纯逻辑回归测试） |
| #28 | `Guard: doc baselines agree (round + unit/all counts across the three main docs)` | 第 48 轮 | 文档基线一致性防线：README / ROADMAP / TEST_RESULTS 三份文档的轮次标记与 Unit/All 计数必须一致，否则测试红——"文档和代码对不上"从人工约定变成机制拦截 |
| #29 | `SlowTransferWatcher: sustained crawl aborts, brief dips don't (pure logic)` | 第 52 轮 | 随下载慢速看门狗加入（持续爬行中止 / 短暂波动不误杀，注入时钟纯逻辑） |
| #30 | `LoaderCardVM: null coercion + append-only fill never auto-select (round-50 regression)` | 第 53 轮 | 第 50 轮"卡片自动选中"根因的状态机回归锁（null 归一 / 追加不选中 / IsLoaderSelected 单一来源）；测试工程自此引用 Desktop 项目 |
| #31 | `UpdateService: manifest signature + downgrade rejection (pure logic)` | 第 57 轮 | 自动更新安全核心：验签通过 / 篡改 url|sha|size|round 任一字段必失败 / 防降级（仅严格更高轮次算更新）；文档基线 Guard 同轮新增 BuildInfo.CurrentRound 一致性核对 |
| #32 | `UpdatePolicy: 3-way mode switch (startup check / notify / auto-download)` | 第 58 轮 | 三态更新开关的纯逻辑状态机：模式 3 启动不检查 / 模式 1 自动下载 / 模式 2 弹窗 / 手动检查任何模式都弹窗 / 文案↔枚举往返 |
| #33 | `JavaRuntimeService: Adoptium manifest parse + zip-slip guard (pure logic)` | 第 59 轮 | 一键安装 Java 21 的安全核心：清单必须给出 HTTPS zip + 64 位 SHA-256，否则拒绝；zip 条目拒绝绝对路径 / 盘符 / `..` / ADS |
| #34 | `CrashAnalyzer.BuildReport: all causes, case-insensitive, no-match fallback` | 第 60 轮 | 图形化崩溃卡片数据核心：一次崩溃列出全部匹配规则、大小写不敏感、无匹配时走兜底提示；旧 `Analyze()` 保持兼容 |
| #35 | `CurseForgeService: search parse + official-page URL builders (pure logic)` | 第 60 轮 | CurseForge 搜索响应解析 / 无效条目跳过 / 畸形 JSON 失败关闭 / 下载动作只构造官方文件页 URL / 查询编码 |
| #36 | `OptiFineService: downloads-page parse + ad-token URL parse (pure logic)` | 第 60 轮 | OptiFine 官网版本列表去重与命名解析 / 隐藏表单与内联链接两种令牌形式 / 缺令牌失败关闭（不把广告页当安装器） |
| #37 | `UpdateApplyArgs: visible updater handoff round-trips and fails closed` | 第 61 轮 | 可见更新器链路：含空格路径作为单个 argv token 往返、畸形 token / 相对路径 / 非法 PID / 缺字段 / 多字段一律失败关闭；另含无 GUI 的完整换装演练（临时目录内等待已退出 PID → 二次 SHA-256 校验 → 覆盖目标 → 写 build-info → 注入式重启），以及「staged 被篡改时失败关闭且不破坏已安装 EXE」；换装重试预算有界 |
| #38 | `JavaRequirement: MC version -> recommended Java major (pure logic)` | 第 62 轮 | 实例级 Java 的推荐边界：1.7.10 / 1.12.2 / 1.16.5→8，1.17 / 1.18.2 / 1.20 / 1.20.4→17，1.20.5 / 1.21 / 1.21.1 / 1.22→21，快照 / 空 / 加载器版本串→21；推荐提示文案含大版本号 |
| #39 | `UpdateBannerPolicy: outdated launcher CTA + staged wording (pure logic)` | 第 62 轮 | 版本列表一键更新的文案与判定：仅轮次严格更高算「旧版启动器」（防降级）、四种状态文案（未检查 / 已是最新 / 发现新版 / 更新包已就绪）均给明确指引 |
| #40 | `BulkObservableCollection + NavigationPolicy: one-shot UI updates / no redundant navigation` | 第 63 轮 | 切换卡顿的两条核心契约：批量替换整个集合只发一次 Reset（含空集合）；导航 key 归一（null / 空白→Home）、首次导航放行、同页重复点击早退、换页放行 |
| #41 | `Guard: cached pages never re-run init on every Loaded (round-63)` | 第 63 轮 | 静态防线（round-64 起改为模式匹配）：所有 `Views/*.xaml.cs` 的 Loaded 处理器必须有任意命名的 `private bool` 一次性守卫或 `HookVm` 可重入订阅；禁用三个大集合 `Clear()` + `Add` 重建。负向验证：删掉 early-return 即红 |
| #42 | `CoalescingSingleFlight: A→B→C collapses to ONE rerun with the latest arg` | 第 64 轮 | 评审要求的注入式测试：进行中连续触发 A→B→C→D→E（gated runner），断言 `RunCount == 2`、第二次参数为 E、`Latest == E`；排除排队型（5 次重跑）与丢弃型（丢最新）两种退化；空闲期再触发正常执行一次 |
| #43 | `UiPolicies: selection restore / debounce guard / navigation sub-key` | 第 64 轮 | `SelectionPolicy.RestoreByKey` 按 id 恢复 / 缺失回退首项 / 空集合；`LoaderDebouncePolicy` 四种判定（正常放行、切页取消、版本变更、无目标版本）；`NavigationPolicy` 子 key 不同不得被同页早退吞掉 |
| #44 | `LoadedGuardPolicy: accepts any single-shot field name, rejects unguarded Loaded` | 第 64 轮 | Guard #41 假阳性修复：`_isLoaded` / `_firstLoadDone`（含 `if (!_x) return;`）/ `HookVm` 均通过；无守卫、只有 bool 字段无 early-return 均拒绝 |
| #45 | `PageHostPolicy: lazy page creation + exactly one visible page` | 第 65 轮 | 页面常驻契约：首次创建、二次不重建、同时只有一个可见、key 按序数比较；模拟 40 次快速切换只创建 4 个页面（对应反复点击卡顿场景） |
| #46 | `Guard: navigation keeps pages in the visual tree (round-65)` | 第 65 轮 | 静态防线：禁止 `Content="{Binding CurrentPage}"` 换装、要求 `PageHost` 与 `PageHost.Children.Add` + Visibility 切换、VM 不得赋值页面对象；隐藏页 LogsView / ModsView 必须有 `!IsVisible` 判定。负向验证：注入 Content 换装即红 |
| #47 | `Guard: no animation clocks / software effects in the UI (round-67)` | 第 67 轮 | 改为**禁止性**规则（第 66 轮该 Guard 的方案本身引入了回归）：样式中禁止 `<BeginStoryboard` / `<Storyboard` / `RepeatBehavior` / `<DropShadowEffect` / `<BitmapCache` / Effect 与 CacheMode Setter；所有 XAML 禁止 `Effect="{StaticResource ...}`；保留列表 Recycling + CanContentScroll、禁止同步 `Dispatcher.Invoke`、转换器复用冻结画刷、冒烟诊断文件存在；40 次显隐切换活动动画时钟恒为 0。负向验证：注入阴影标签即红 |
| #48 | `UiLatencyTrace: flags slow switches and summarises them` | 第 68 轮 | 切页计时器：50 ms 阈值判定（快/慢样本）、仅统计超阈值次数、最后样本覆盖、未知 stage 返回 −1、最慢 stage 归因、空/NaN 输入忽略、`Reset` 清零且空摘要不除零 |
| #49 | `PageWarmupPolicy: sidebar order, skip built pages, never warm while busy` | 第 68 轮 | 空闲预热策略：按侧栏顺序取第一个未构建页、已建页跳过、九页建完后返回 null、`PageOrder` 顺序稳定（Home 优先）；忙碌/下载中/关闭中一律不预热 |
| #50 | `Guard: lists nested in a ScrollViewer stay bounded (round-68)` | 第 68 轮 | 静态防线：扫描全部 `Views/*.xaml`，`ScrollViewer` 内嵌套的 `ListBox` 必须自带 `MaxHeight`/`Height`（否则测量高度无穷、虚拟化失效）；`<ListBox.ItemTemplate>` 属性元素不算列表、自闭合 ScrollViewer 不开作用域、`MinHeight` 不算有界。负向样例覆盖三类情形 |
| #51 | `Guard: page init runs on first SHOWN, not on first attach (round-68)` | 第 68 轮 | 静态防线：页面（UserControl）若在 `Loaded` 里做异步初始化，必须改用 `PageLifecycle.OnFirstShown`，否则空闲预热会在用户没打开页面时联网/扫 Java；三处迁移（DownloadView / ModsView / SettingsView）逐一核对；钩子必须是 `IsVisibleChanged` + 单次闸门 |
| #52 | `Logger: file writes are batched off-thread and flushed on demand` | 第 69 轮 | 日志异步化的回归锁：40 行写入后 `Flush` 必须全部落盘且保持顺序，第二次突发事件仍能追加；`Flush` 有界（不无限等待）。这是「切页时 UI 线程被写盘拦住」的直接修复验证 |
| #53 | `NavigationLatencyPolicy: 'page is slow' vs 'UI thread was busy'` | 第 69 轮 | 三段计时分类：`PageMs = flip + paint`；2 ms 不算慢；页面 1.3 ms 但排队 62 ms → `UiThreadBusy`（不得归咎页面）；布局 120 ms → `PageRender`；负值/时钟回拨一律夹到 0，且首次进入单独措辞 |
| #54 | `Guard: log I/O off the calling thread + Logs feed frozen while hidden (round-69)` | 第 69 轮 | 静态防线：`Log()` 方法体内禁止 `File.AppendAllText`（必须在写入线程里）；`WriterLoop` / `_pendingWrites` / `Flush` 契约必须存在；`MainViewModel` 必须有 `IsLogsPageActive` 门控、用 `Logs.Replace(snapshot)` 单次 Reset、且筛选不得被冗余赋值清空 |
| #55 | `RouteFailurePolicy: 404 is permanent, no retry storm` | 第 70 轮 | 竞速失败分类：404/403 判永久、超时/5xx/取消不判永久；全为永久失败时不再消耗重试轮（对应日志里成片的「404 + retrying once」）；取消（用户切走）绝不重试；永久失败降级为 Info 而非 Warn |
| #56 | `UiStallTrace: attributes a UI freeze to GC or to app work` | 第 70 轮 | 界面停顿归因：`GC.GetTotalPauseDuration()` 差值把「垃圾回收」与「界面线程上的工作」分开；停顿 = 超出看门狗间隔的部分（不是原始间隔本身，上一版 1–8ms 假警报由此而来）；GC 主导 / 工作主导 / 混合三种措辞 |
| #57 | `DiagnosticsExporter: 导出日志 writes a sanitized, headered text file` | 第 70 轮 | 「导出日志」端到端：头信息含版本与轮次、正文含日志行、令牌与启动器根路径必须被脱敏、取最新日志文件、无日志时返回可读错误 |
| #58 | `NetRouting.BuildHosts: both hosts survive a mirrored input URL` | 第 71 轮 | 「下载页空白」的核心回归锁：官方 URL 输入 → 官方+镜像两主机；**镜像 URL 输入仍必须出现官方主机**（第 70 轮塌缩成两条死镜像线路）；下载源只决定顺序；第三方 API 不增线路；`VersionManifestUrl` 恒官方且不含 bmclapi |
| #59 | `LauncherConfig: legacy mirror domain is healed in old configs` | 第 71 轮 | 旧配置自愈：反射扫描可写字符串字段，`bmclapi.cn` → `bmclapi2.bangbang93.com`（ProxyUrl / NetworkNote 均验证）；干净配置不得被重写（避免每次启动都改写文件） |
| #60 | `ManifestCache: offline copy round-trips and rejects junk` | 第 71 轮 | 断网兜底：清单缓存往返；坏 JSON 与**空清单**都判为「无缓存」，避免把空列表当有效数据显示给用户 |
| #61 | `JobProgressPolicy: stage-aware percent, aggregate, throttle` | 第 72 轮 | 多任务汇总算术：单步任务按自身比例、4 阶段第 1 阶段半程 = 13%（不是 50%，且 12.5 用 AwayFromZero 取整）、越界与 NaN 夹取；聚合跳过取消任务、速度只加运行中、计数区分进行中/排队；100 ms 节流判定 |
| #62 | `DownloadJobManager: per-task cancel, queue cap, clear finished` | 第 72 轮 | 「同时下载」的核心契约：并发上限 2 时第 3 个任务保持排队、有槽位自动开始；**取消一个任务不影响其他任务**；清理只删已结束、运行中不被删；全部取消覆盖运行中任务 |
| #63 | `DownloadJob: report throttling + failure message stays human` | 第 72 轮 | 200 次引擎进度回调产生的 UI 推送必须 < 40（节流有效，这是「多任务不卡」的关键指标）；百分比恒在 0–100；速度文本格式化 |
| #64 | `PathWriteGate: one writer per destination, others run in parallel` | 第 73 轮 | 并发写冲突锁：同目标路径串行、不同路径并行、Windows 语义大小写归一、取消等待不泄漏槽位、释放后仍可用；并静态检查 `DownloadEngine` 确实对目标路径取锁 |
| #65 | `DownloadJobManager: a queued job can be withdrawn before it starts` | 第 73 轮 | 「排队时点取消」契约：取消立刻变已取消、等待中的 `StartAsync` 抛取消、**空出槽位后不得偷偷启动** |
| #66 | `CloseGuardPolicy: only ask when work is really in flight` | 第 73 轮 | 退出确认策略：无任务不打扰、有任务（运行或排队）才问、确认后不重复问；文案必须包含进行中/排队数量 |
| #67 | `BinaryDelta: patch round-trips and rejects the wrong base` | 第 74 轮 | 增量更新核心：补丁必须远小于原文件、头部可预读（含目标 SHA-256）、合并结果字节一致；错误基文件 / 尺寸不符 / 截断补丁全部拒绝；90% 的"补丁"判为不值得 |
| #68 | `PortableModePolicy: portable.txt / config.json decide the data root` | 第 74 轮 | 便携模式：标记文件与既有 config.json 两条识别路径、删除标记即恢复标准模式、不存在的目录不误判、`Describe` 文案包含实际根目录 |
| #69 | `DownloadJournal: resume only what really has a .part` | 第 74 轮 | 断点恢复：记录→重放→完成移除；**没有 `.part` 的条目不算**（日志不是证据）；<4 MB 与 `update/` 暂存不记录；`Discard` 同时删临时文件与日志 |
| #70 | `CloseBehaviorPolicy: tray decides between ask / exit / hide` | 第 74 轮 | 关闭语义矩阵：无托盘退回第 73 轮、托盘+询问必问且给两个选项、托盘+最小化直接收起、显式退出遇到下载仍提醒；文案含数量与两个选项名 |
| #71 | `SingleInstancePolicy: one launcher per data root, not per exe path` | 第 74 轮 | 单实例：路径大小写与尾部反斜杠归一后同根同名、不同根不同名、Mutex 与事件名互不相同 |
| #72 | `UpdateService: patch manifest must be signed and round-matched` | 第 74 轮 | 补丁清单：正确签名通过、换掉 `patchUrl` 失败、未签名拒绝、轮次不符拒绝、体积不划算拒绝；**`SignedPayload` 必须与旧版本字节一致**（否则老客户端拒绝更新） |
| #73 | `SingleInstanceGuard: only a shown window acknowledges an activation` | 第 75–76 轮 | 「双击打不开」的回归锁：回调报告**没有可显示窗口**时不得回应答（第 76 轮修正了"答了却没显示"的语义错误），调用方据此判 stale 并自己开窗；有窗口则应答；`Release()` 后同名立刻可再次获取。另静态检查：唤醒不读 `Application.MainWindow`、`RestoreFromTray` 返回 bool、托盘动作走 `DispatcherPriority.Background`、`Closed` 有退出看门狗、`OnExit` 显式释放 |
| #74 | `GameInstance: playtime reads like a human wrote it` | 第 77 轮 | 时长文案：`不足 1 分钟` 而不是 `0 分钟`、整小时不带「0 分钟」尾巴、42 小时 15 分钟、负数夹 0；新实例显示「还没有游玩记录」 |
| #75 | `PlaytimeTracker: a killed launcher still gets credit for the session` | 第 77 轮 | 崩溃恢复：按最后心跳结算（89 分钟）、消费会话文件、游戏进程仍活着则不结算、3 天会话夹到 24h、结束早于开始为 0、`End()` 清理标记 |
| #76 | `ModPreflight: missing prerequisite / conflict / duplicate / wrong loader` | 第 77 轮 | 预检四类问题各一条；`provides` 让 Fabric API 模块依赖不误报；`mandatory=false` 忽略；平台 id 忽略；删掉 API 后置位「一键安装」 |
| #77 | `MrpackExporter: Modrinth-hosted mods become links, the rest are bundled` | 第 77 轮 | 导出：已收录模组进 `files[]`（CDN + sha1）、未收录与 config 进 `overrides/`、dependencies 含 minecraft + fabric-loader、**不含 servers.dat / disabled 模组**、响应解析取 primary |
| #78 | `InstanceManager: launch count and playtime survive a reload` | 第 77 轮 | 启动次数在启动时计数、时长经 `instances.json` 往返、48h 夹到 24h、0 时长无副作用、未知 id 返回 null |
| #79 | `CrashResolutionEngine: 常见崩溃 → 具体修复动作（第 78 轮规则库）` | 第 78 轮 | 规则→动作映射：OOM 涨价且带目标 -Xmx、物理内存没余量时降级为引导（不谎称能修）、`class file 65`→Java 21、已装 17 就复用而不是重下、缺 fabric-api 一键下载、重复模组只开文件夹、堆预留反向降档、未知崩溃指纹稳定且只取决于异常行；class-file→Java 映射（52/61/65/44）、内存天花板 55% 与 16 GB 上限、1 GB 地板 |
| #80 | `CrashFixSafety: 自动修复只能改启动器自己的设置/下载文件` | 第 78 轮 | 遍历**全部** `FixAction`：配置键 ⊆ `{MaxMemoryMb, MinMemoryMb, JavaPath}`、动作均非破坏性、枚举名不含 Delete/Remove/Kill/Env/Format/Overwrite/Admin；自动/引导边界（内存与模组下载=自动，开文件夹/发邮件=引导）；日志文案写明只改 `config.json` |
| #81 | `SupportReport: 求助邮件由用户亲手发送（mailto 可回读）` | 第 78 轮 | mailto 指向作者邮箱、无裸换行（否则客户端截断）、长度 < 2000、可回读出收件人/主题/正文；主题含轮次、正文含诊断、附件路径、已尝试的修复；正文不含 SMTP；非 mailto URL 拒绝 |
| #82 | `Guard: 崩溃修复不删文件、不阻塞界面（第 78 轮）` | 第 78 轮 | 源码 Guard：`ApplyCrashFixAsync` 方法体内不得出现 `File.Delete / Directory.Delete / File.Move / Directory.Move / SetEnvironmentVariable / .Kill(`，必须写 `MaxMemoryMb` + `cfg.Save()` 且含 `await`；`ShowCrashCardAsync` 必须以 `Task.Run(() => CrashResolutionEngine.Diagnose` 做后台诊断 |
| #83 | `SaveBackup: 触发矩阵 / 保留策略 / 只读存档（第 79 轮）` | 第 79 轮 | 触发矩阵（Both 两种都触发、AfterExit 不拖慢启动、Off 不触发、Manual 恒可用）；keep 夹取（0→默认、999→上限）；文件名安全（分隔符/空格替换、时间可排序）；保留策略（6 份留 2 删 4、**用户自己丢的 zip 不删**）；空目录/超大目录的跳过理由；服务往返：3 次备份留 2 份、zip 内含 `World1/level.dat`、**原存档文件仍在** |
| #84 | `PerformanceReport: FPS 解析 / 结论阈值 / CPU 数学（第 79 轮）` | 第 79 轮 | `fps: 60` 与 `144 fps` 都识别、`fps: 0` 与 `fps: 5000` 当噪声丢弃、无数据/传 null 不抛；三档结论（92% -Xmx → 吃紧、平均 <30 → 偏低、其余健康、无记录 → 无数据）；CPU：Δidle 100 / Δkernel 200 / Δuser 200 = 75%（kernel 含 idle 的经典坑）、计数器不前进为 0、回绕不出现负数 |
| #85 | `CommandPalette: 排序 / 多词 AND / 索引来源（第 79 轮）` | 第 79 轮 | 空查询以操作为首；「备份」同时命中操作与设置；实例名/模组名（大小写不敏感）可搜；输入 ≥2 字追加 Modrinth 行；多词必须全部命中（AND）；完全相等的标题排第一；limit 生效；**每个页面 payload 必须在 `CreatePage` 有分支、每个 `Action*` 常量必须在 `RunPalette` 被处理** |
| #86 | `GlassEffectPolicy: 默认关闭 / 降级 / 三种玻璃方案的边界（第 80–81 轮）` | 第 80–81 轮 | 默认必须是 Off；三项显示名往返解析；两种非默认模式都要过警告（Off 静默）；**旧配置 `System` 折叠为玻璃皮肤且显示名一致**（第 81 轮：壁纸铺满后 Mica 无可见表面）；只有 Local 允许瞬时模糊；警告文案必须含"卡顿/崩溃"并说明怎么改回；状态行描述实际效果 |
| #87 | `GlassGuardPolicy: 模糊只允许在 MainWindow 的 Glass* 元素上（第 80 轮）` | 第 80 轮 | 正反例：Glass* + MainWindow 通过；同内容换到页面文件违规；换普通元素名违规；放进 `DataTemplate`（列表行）违规；纯文本/空输入不抛；C# 侧 `glassBlur` 标记只在 MainWindow.xaml.cs 生效、无标记违规、别的文件带标记也违规 |
| #88 | `LaunchGuardPolicy: 没有实例时按钮变成引导而非死按钮（第 82 轮）` | 第 82 轮 | `Decide` 五态（0 实例优先于任何其它原因、有实例未选 → NoSelection、缺账户 → NoAccount、全满足 → None）；`Describe` 的跳转目标（无实例 → Download 且文案含"游戏下载"、未选 → Instances、缺账户不跳转）；按钮文案 `启动游戏`/`去下载游戏` 与卡片提示同源；静态断言 `MainViewModel` 必须调用 `LaunchGuardPolicy.Decide` / `.ButtonText`（防标签与行为再次分叉） |
| #89 | `MemoryAllocationPolicy: 推荐阶梯 / 物理内存上限 / 不越界（第 83 轮）` | 第 83 轮 | 原版 2048；模组数上升时推荐值单调上升；光影 ≥4096 且绝不降低推荐；**1024–131072 MB × 5 档模组 × 光影 on/off × 原版/模组全扫描：结果必须 ≤ 物理内存 60%、≤16 GB、≥ 地板、是 512 的整数倍**，`SafeCeiling` 自身同样满足；4 GB 机器被物理上限压住（≤2252）并带"内存偏小"提示，`Describe` 解释上限；读不到物理内存时退回表中值（只受硬顶约束）；手动模式原样返回、自动模式忽略存储值；`ExceedsSafeShare` 阈值；`Probe` 数 jar 与 shader zip（`notes.txt` 不计） |
| #90 | `Guard: 内存清理只碰自己，GC.Collect 只在后台（第 83 轮）` | 第 83 轮 | 源码扫描：`MemoryHygiene.cs` 禁 `Process.GetProcesses / GetProcessesByName / OpenProcess( / "javaw" / "java"`，必须是 `SetProcessWorkingSetSize(GetCurrentProcess())`；整个 Desktop 项目里 `GC.Collect` 只允许出现在该文件；VM 必须用 `Task.Run(() => MemoryHygiene.Trim(aggressive: true/false))`（手动与闲置两条路径），禁止 UI 线程内联；`LauncherService` 必须走 `MemoryAllocationPolicy.Effective` 且 `-Xms` 被 `Math.Min(cfg.MinMemoryMb, maxMemoryMb)` 压住；`LauncherConfig.AutoMemory` 必须是持久化开关 |
| #91 | `DataRootPolicy: 临时目录不得成为数据根（第 84 轮）` | 第 84 轮 | `IsEphemeral` 认得 `%TEMP%\aurora-*`（冒烟根），不误判 `%APPDATA%`、`D:/AuroraData`、空串、null，也不被 `TempX` 前缀骗过；`Sanitize` 把临时根折回默认根且 `Healed=true`、合法自定义根原样保留；源码守卫：`Save()` 必须含 `IsEphemeral(RootDirectory)`、`Load()` 必须走 `Sanitize`、必须存在 `UseTestRoot` |
| #92 | `InstanceManager: 删除先删文件夹，失败保留记录；扫描不碰陌生文件夹（第 84 轮）` | 第 84 轮 | 正常删除：文件夹与记录同时消失；**锁定文件时** `DeleteChecked` 返回 `FilesLocked`、文件夹仍在、**记录仍在**（否则就是幽灵实例），释放后重试成功；`Scan` 从 `.aurora-instance.json` 恢复幽灵实例并**保留原 Id**（不是新 GUID）、读回元数据名称；无身份证的陌生文件夹只进 `UnrecognizedFolders` 且文件未被动过；坏 JSON 进 `CorruptedFolders`；记录在、文件夹没了 → `MissingIds`；源码守卫：`Instances.Remove` 必须出现在 `if (result.Ok)` 之后、删除必须走 `DeleteChecked`、必须有确认框 |
| #93 | `Guard: 新建实例只跳转，绝不偷偷建空实例（第 84 轮）` | 第 84 轮 | `CreateInstance()` 方法体必须含 `Navigate("Download")`，且**不得**出现 `Instances.Create(` |
| #94 | `Guard: 冒烟测试必须隔离配置文件（第 84 轮）` | 第 84 轮 | 扫描 `tests/AuroraSelfTest/*Smoke.cs` 与 `Program.cs`：凡是碰 `RootDirectory` 的，必须调用 `LauncherConfig.UseTestRoot(...)`，且不得直接赋值 `LauncherConfig.Instance.RootDirectory`（正是这一行把用户配置写成了 `%TEMP%`） |
| #95 | `DownloadRoutePolicy: 测速择优 / 强制单线路 / 回退集合（第 85 轮）` | 第 85 轮 | `Decide`：官方 100/镜像 500 → 官方、镜像更快即换边、不可达（null 或 >3000ms）选另一条、两条都不通保持原选择、40ms 内视为平手不跳；`ProbeResult.Decide` 必须复用同一规则；`Order`：强制官方/镜像各只有一条、自动按优先级给两条；`BuildHosts`：核心文件强制单线时**不允许**出现另一条、非核心允许静默回退且仍首选强制线路、第三方 URL 不臆造镜像且不返回空集合；`IsCoreAsset` 分类（jar/库/版本 JSON/索引 = 核心，assets 对象与图片 = 非核心）；`MigrateLegacySource(0/1) → Auto`；显示名往返与说明文案 |
| #96 | `Guard: 测速只用 HEAD / 有缓存 / 下载路径不等测速（第 85 轮）` | 第 85 轮 | 源码扫描：`NetworkProbe.cs` 必须含 `HttpMethod.Head`、HEAD 被拒时只取 1 字节（`RangeHeaderValue(0, 0)`）、不得出现 `GetByteArrayAsync`/`ReadAsByteArrayAsync`、必须 `ResponseHeadersRead`；必须有 5 分钟缓存与 `_inFlight` 单飞；`NetRouting` 只能读 `NetworkProbe.Preference`、不得 `await NetworkProbe.`；`DownloadEngine` 不得直接引用测速，必须有 `RouteSwitchAllowed`/`AlternateRouteUrl`/`switchedRoute`（只换一次）；`AlternateRouteUrl` 官方↔镜像互转、第三方返回 null；强制官方时核心文件不可换线、非核心可换、自动模式都可换 |
| #97 | `Forge 安装器 URL：必须拼成「MC版本-Forge版本」（第 86 轮）` | 第 86 轮 | `ForgeMavenVersion`：裸版本 `47.4.22` → `1.20.1-47.4.22`、已组合的保持不变、`forge-1.20.1-47.4.22` 剥前缀、空白被裁掉、空串仍是空串；`BuildMavenInstallerUrl` 的路径与文件名同源；**断言报告里那个 404 地址（`forge/47.4.22/…`）再也不会被生成**；NeoForge 不做 MC 前缀（`21.1.72` 原样） |
| #98 | `LauncherConfig.ToOfficialUrl: maven 路径可按 group 反解（第 86 轮）` | 第 86 轮 | Forge / NeoForge / Fabric / Quilt 四条官方 maven 地址做 `ToOfficialUrl(ToBmclapiUrl(x)) == x` 往返（含 `releases/`、`repository/release/` 前缀保留）；`DownloadEngine.AlternateRouteUrl(镜像地址)` 必须能映射回官方，否则 404 无法换线路 |
| #99 | `Guard: 安装器重试前必须清残留、且不违背强制线路（第 86 轮）` | 第 86 轮 | 源码断言：安装器下载前删除 `jar` 与 `jar.part`（无校验值时"已完整"判定会把半截文件当成品）、镜像回退必须检查 `RoutePolicy != DownloadPolicy.Official`、解析必须经过 `ForgeMavenVersion` 与 `BuildMavenInstallerUrl` |
| #100 | `DownloadIntegrity: 没有校验值不再等于「完整」（第 87 轮）` | 第 87 轮 | 哈希命中/不命中；只有声明大小时的**截断文件必须判 Mismatch**（旧的 `VerifyHash(null)==true` 会放行）；两者都没有 → `Unverifiable` 且 `IsTrustworthyForSkip` 为假；缓存路径只信 `Match`；新下载的正文只有明确 `Mismatch` 才重试；缺失文件 → `Missing` |
| #101 | `Guard: 缓存命中必须走 DownloadIntegrity，VerifyHash(null) 已删除（第 87 轮）` | 第 87 轮 | 源码断言：`DownloadEngine.cs` 不得再出现 `VerifyHash(`；必须调用 `IsTrustworthyForSkip` / `IsAcceptableAfterDownload`；每个文件操作必须 `ActiveDownloads.Register(`；必须保留 `sizeComparable`（gzip/brotli 响应不做大小比对） |
| #102 | `DownloadResidue: 正在下载的保留，其余清理（第 87 轮）` | 第 87 轮 | 真实临时目录：`ActiveDownloads` 登记的 `.part` 归类 ActiveTask 且清理后仍在；台账里的归类 Journaled 可删；<4 MB 未入台账与 `update/` 残片归类 Unowned 可删；已完成的同名 `.jar` 不在扫描结果里、也不被删；`FreedBytes` 如实统计 |
| #103 | `DownloadResidue.ShouldWarn: 阈值任一超标才提示（第 87 轮）` | 第 87 轮 | 300 MB 单体触发、60 个小文件触发、1 KB 不触发、只剩正在下载的文件不触发、空扫描不触发；0 = 关闭该维度、负数不得变成「永远提示」；`Describe` 带计数 |
| #104 | `UpdateStaging: 只清 24 小时前的、且不是当前待装的包（第 87 轮）` | 第 87 轮 | 30 小时前的 `.patch.part`、3 天前的 `.merged.exe` 被选中；5 分钟前的 `.part` 不动；**4 天前的待安装包（`AuroraLauncher.new.exe` + `build-info.txt`）必须保留**；不存在的目录返回空集 |
| #105 | `Guard: 清理残留的三处入口都接上了（第 87 轮）` | 第 87 轮 | 设置页必须有「清理残留下载文件」并绑 `CleanResidueCommand`；下载页必须有 `HasResidueNotice` / `ResidueNoticeText`；扫描与删除都必须走 `Task.Run`（数据目录上万文件）；清理可续传文件前必须确认且默认「否」；启动扫描与命令初始化必须被调用；`UpdateService.CheckAsync` 必须调 `UpdateStaging.Clean` 且把待安装包放进豁免集 |
| #106 | `StorageCleanupPolicy: 白名单只放行缓存与临时文件（第 88 轮）` | 第 88 轮 | 允许 10 类（`cache/icons|optifine|installers`、清单缓存、`update/` 残留、`logs/aurora-*.log`、`versions/<id>`、`libraries`/`assets` 的 `.part`）；拒绝 19 类：根目录自身、`config.json`/`instances.json`/`accounts.json`、台账、`update/AuroraLauncher.new.exe`、`update/build-info.txt`、`versions/<id>/<id>.json`、`libraries` 里的实际 jar、`instances/*/saves`、`instances/*/mods/*.part`、`backups/*`、`java/*`、根之外的路径、null |
| #107 | `StorageCleanupPolicy: 空壳版本目录的判定（第 88 轮）` | 第 88 轮 | 只有 JSON、只有 jar、两者都有、真空目录、子目录里有文件、目录不存在 六种情况：只有真正的空目录算空壳（半装状态必须保留） |
| #108 | `StorageCleanupPolicy: CostsOffline 一律默认不勾（第 88 轮）` | 第 88 轮 | 每个 `CostsOffline` 必须 `DefaultSelected=false`、每个 `Safe` 必须为 true、不存在 `Never` 分类、每个分类都要有说明文案、分类不重复；提示语必须写明不删存档；**UI 的默认勾选必须读 `group.Info.DefaultSelected`**（不许硬编码） |
| #109 | `Guard: 清理服务只走白名单，绝不整根删除（第 88 轮）` | 第 88 轮 | 源码断言：扫描与删除前各调一次 `IsAllowedPath(_root, …)`；必须复查 `ActiveDownloads.IsActive` 与 `IsEmptyVersionShell`；不得出现 `Directory.Delete(_root` / `Delete(_root, true)`；不得拼接 `instances`/`backups`/`java` 路径；唯一的 `Directory.Delete(…, recursive: true)` 只允许作用在已验证的空壳目录上 |
| #110 | `StorageCleanupService: 扫描分类与选择性删除（第 88 轮）` | 第 88 轮 | 临时根铺 15 个样本（图标 ×2、OptiFine、清单、更新残留、旧日志、今天的日志、无主残片、加载器缓存、待安装更新包 ×2、存档、实例内残片、备份、库本体、空壳/半装版本目录、可续传残片）；扫描分类计数正确且清单里不含 `instances/`、`backups/`；只选 Safe 执行后逐项断言「该删的删了、该留的一个不少」，含 `FreedBytes` 如实统计 |
| #111 | `DownloadJournal.Forget: 清理残片后台账不再指向已删除的文件（第 88 轮复核）` | 第 89 轮 | 临时根造「台账条目 + `.part`」→ `DownloadResidue.Clean` → `Flush` 后 `Destinations(root)` 不再含该条目、`Load` 为空；源码断言两个入口（`DownloadResidue` / `StorageCleanupService`）都必须调用 `DownloadJournal.Forget`；VM 清理后必须重新加载「继续下载」横幅 |
| #112 | `Guard: 清理的忙碌态与两个入口的分工都写清了（第 88 轮复核）` | 第 89 轮 | 源码断言：两个按钮文案属性存在且各自 `OnPropertyChanged`（漏了会永远停在初始文字）；`CanEditStorageSelection` 存在并绑到列表 `IsEnabled`；设置页有 `IsIndeterminate="True"` 进度条；页面必须写明「清理残留下载文件」只处理下载残渣、这里扫「整个存储目录」；报告整体替换（`Clear()`） |
| #113 | `SettingsDirtyPolicy: 快照对比能发现任何一项修改（第 90 轮）` | 第 90 轮 | 快照与字段顺序无关（否则每次进设置页都像改过）；同值不算脏；改一个字段只报该字段；无基线时按脏处理；`BuildPrompt` 要点名改的项并写清「保存 / 放弃 / 留在」三个选择；值里含换行不得把快照切坏 |
| #114 | `Guard: 离开设置页必须拦截、有任务时关窗必须弹窗（第 90 轮）` | 第 90 轮 | 源码断言：`Navigate` 里 `ConfirmLeaveSettings` 必须出现在 `CurrentPageKey = target` **之前**（顺序错就拦不住）；脏检查走策略类、保存与放弃两条分支都在、回滚只调 `LoadSettingsFromConfig`（`LoadAll` 会重复添加账号/实例）；`OnClosing` 调 `ConfirmCloseWithUnsavedSettings`；`ExitFromTray` 里 `ConfirmExitWithActiveJobs` 必须早于 `_shuttingDown = true`；策略层断言 `Exit`/`Ask` 有任务必 Prompt、排队也算未完成；`DisplayText(Exit)` 含「有下载」且新旧文案都能解析 |
| #115 | `Guard: 首页内容必须可滚动，且列表有高度上限（第 91 轮）` | 第 91 轮 | 源码断言：`HomeView.xaml` 必须有具名的 `HomeScroller`、`VerticalScrollBarVisibility="Auto"`、`HorizontalScrollBarVisibility="Disabled"`；实例 `ListBox` 必须带 `MaxHeight`（不限高放进滚动容器会全量生成容器，第 68 轮的坑）；`<ScrollViewer` 必须出现在「欢迎使用 Aurora Launcher」之后（hero 不能被一起包进去，否则启动按钮会跟着滚）；设置页/帮助页的滚动容器也要在。运行时由 `--round79-smoke` 在 760/600/480 三档验证「性能概览」底部可达 + hero 按钮不随滚动移动 |

| #116 | `ModpackIntegrity: 模组数与清单双口径（第 92 轮）` | 第 92 轮 | 12/12 完整；少 1 个不打扰（用户自己删的）；12/40 判 Partial；预期 0（纯配置包）不判空；非整合包不参与；删光判 Empty 且文案直说「一个都没有」；清单写入/读回、`mods/*.jar` 才算模组、全空 13 项缺失、半截文件（尺寸不符）也算缺失 |
| #117 | `LaunchReadiness: 加载器与整合包状态必须拦住启动（第 92 轮）` | 第 92 轮 | id 判定（原版 / `1.20.1-forge-…` / `fabric-loader-…`）；`ResolvedVersionId` 为空或等于原版 id 一律判「没装」；profile 不在也判没装；加载器缺失**不可覆盖**且必须给出补全出路、文案含「原版」；整合包空/半缺可覆盖、无源时不得声称能补全；**真实 `LauncherService`**：Forge 实例（原版齐、resolved 为空）必须 `NeedsRepair=true` 且报 `LoaderNotInstalled` |
| #118 | `PerformanceReport.RunVerdict: 崩溃/闪退/未采集不再谎报「健康」（第 92 轮）` | 第 92 轮 | 退出码 1 → 异常退出且文案含退出码；2 秒 → 闪退；**1 分钟 + 2 MB → 未采集到**（用户截图那条）；3900/4096 → 吃紧；2500/4096 → 健康；无记录 → 无数据；老 `Verdict` 语义不变；`PerfSummaryText` 对 2 MB 写「未采集到」 |
| #119 | `Guard: 启动闸门与整合包预期不能被绕过（第 92 轮）` | 第 92 轮 | 源码断言：`BuildLaunchSpecAsync` 必须在 `_builder.Build` 之前检查加载器 id；`NeedsRepair` 必须覆盖加载器与整合包；`CheckReadiness` 必须早于模组预检且结论交给 `ConfirmLaunchGate`；启动异常必须走 `ReportLaunchFailure`（含完整堆栈）；`RecordPerformance` 必须传退出码；采样不得再用 `Task.Run`（实测只采到 2 MB）；预期必须在 `InstallMrpackAsync` 之前写入；两个导入入口都要写；安装后必须保存 `ResolvedVersionId` |

> 此后新增/修改测试项必须在对应轮次的更新补充章节中说明——该约定由 #28 号防线自动核对三份文档头部计数，漂移即红。
| #120 | `CommandGate: 一次只放一个进去（第 93 轮）` | 第 93 轮 | 第一次进得去、第二次被挡且计数 +1；`Exit` 后能再进；**32 线程并发只允许一个赢**；`Reset` 清空状态 |
| #121 | `RelayCommand: 执行中再点不会重入，异常也会解锁（第 93 轮）` | 第 93 轮 | 第一次 Execute 必进入；执行中 `IsRunning=true` 且 `CanExecute=false`；再 Execute 不重入、`SuppressedClicks=1`；完成后解锁并能再次执行；命令抛异常也必须解锁（否则按钮永久变灰）；同步命令不分配门、语义不变 |
| #123 | `LauncherProfiles: 缺了补、坏了修、好的不碰（第 94 轮）` | 第 94 轮 | 目录不存在也能建；写出的 JSON 必须有非空 `profiles` + `selectedProfile` + `launcherVersion`；已合法时**一字不改**（用户/官方写的文件不得被覆盖）；空文件与 `{}` 判为需要修复且留 `.bak`；`HasProfiles` 对空串/坏 JSON/数组/空 profiles 为假；`Explain` 能认出缺 profile / Java 版本 / 安装器断网三种，认不出时返回 null（不瞎翻译） |
| #124 | `Guard: 跑安装器前必须先补 launcher_profiles.json（第 94 轮）` | 第 94 轮 | 源码断言：`LauncherProfiles.Ensure(gameRoot)` 必须在 `RunJavaAsync` **之前**（顺序反了等于没补）；必须使用 `LauncherProfiles.Explain(`；补文件调用**全文件只允许一处**（多出来的往往是复制了安装流程却漏了这步） |
| #122 | `Guard: 异步命令必须自带防重入，且不许「火后不管」（第 93 轮）` | 第 93 轮 | 源码断言：`RelayCommand` 必须调用 `_gate.TryEnter()`、异步构造默认 `guardReentry = true`、执行中 `CanExecute` 返回 false、`IsRunning` 发通知、异常必须写日志（不能再只 `Debug.WriteLine`）；**扫描全部 ViewModel 禁止 `new RelayCommand(_ => _ = XxxAsync())`**（火后不管会让防重入失效，本轮据此抓出三个真 bug）；四个耗时按钮必须绑 `IsRunning`；`BoolToText` 必须支持「忙碌\|空闲」参数 |

## 测试分组与网络依赖

| 分组 | 测试 | 依赖网络 |
|------|------|----------|
| 基础工具 | SHA-1、Maven 坐标转路径、离线 UUID | **否** |
| 启动参数 | classpath/JVM/游戏参数与令牌替换、特性规则、OS 规则过滤 | **否** |
| Mod 管理 | facet 构建、.mrpack 解析与覆盖、实例删除版本 GC | **否** |
| 下载规则 | Content-Range 接受规则（含 `bytes 0-99/*` 容错） | **否** |
| 双路径一致性 | 缓存命中分支 vs 本地优先分支逐字段一致（合成 fixture，离线） | **否** |
| 状态评估 | Evaluate 语义（loader 无 client jar 要求 / 缺库清单 / vanilla 缺 jar） | **否** |
| 崩溃规则 | UnsupportedClassVersionError / OOM / 重复 Mod / 缺前置，专项优先 | **否** |
| 网络感知 | NetworkProbe.Decide 数据驱动线路选择（纯函数） | **否** |
| 账户认证 | 微软 Client ID 解析优先级（env > config）+ 配置门卫 | **否** |
| 机制防线 | VersionJson 单一入口、classpath 一致性、HttpClient 生命周期、实例元数据单一入口、XAML 命令绑定核对、交互元素全覆盖审计（第 46 轮起：七类交互属性 + 全部桌面 XAML 范围 + 警告级死命令探测，见 [INTERACTIVE_UI_AUDIT](INTERACTIVE_UI_AUDIT.md)）、列表嵌套滚动边界、首次显示才初始化（第 68 轮） | **否** |
| 切页性能 | `UiLatencyTrace` 阈值判定 / 最慢归因 / 空输入防护、`PageWarmupPolicy` 预热顺序与忙碌门控（第 68 轮，纯逻辑） | **否** |
| 多任务下载 | 任务百分比与聚合、并发上限与排队、单任务取消与清理、回调节流（第 72 轮）；目标路径写锁、排队撤销、退出确认（第 73 轮） | **否** |
| 体验与整合包 | 时长文案与崩溃补记（#74/#75/#78）、模组预检（#76）、.mrpack 导出（#77，第 77 轮） | **否** |
| 崩溃诊断与修复 | 规则库→动作映射与内存/Java 纯逻辑（#79）、自动修复安全边界（#80）、求助邮件 mailto 构造（#81）、修复不删文件/不卡界面源码 Guard（#82，第 78 轮） | **否** |
| 存档备份与性能 | 触发矩阵/保留策略/只读存档与 zip 往返（#83）、FPS 解析与结论阈值、CPU idle 口径（#84，第 79 轮） | **否** |
| 全局搜索 | 排序与多词 AND、索引来源、页面/动作可达性守卫（#85，第 79 轮） | **否** |
| 界面材质 | 玻璃模式矩阵与降级（#86）、模糊白名单正反例（#87，第 80 轮） | **否** |
| 启动引导 | 无实例/未选中/缺账户三态判定与跳转目标、按钮文案同源（#88，第 82 轮） | **否** |
| 内存策略 | 推荐阶梯与光影加成、60% 天花板全参数扫描、手动值不被改写、`Probe` 计数（#89）；清理只碰本进程 + `GC.Collect` 仅一处且后台（#90，第 83 轮） | **否** |
| 实例与数据根 | 数据根不得为临时目录、坏配置自愈（#91）；删除先删文件/失败保留记录、锁占用重试、扫描三类结果与原 Id 保留（#92）；「新建实例只跳转」「冒烟必须隔离配置」源码守卫（#93/#94，第 84 轮） | **否** |
| 下载线路策略 | 测速择优（40ms 裕度 / 3s 视为不可达）、强制单线的路由集合、核心 vs 非核心回退、老配置迁移（#95）；HEAD 测速 + 5 分钟缓存 + 单飞 + 下载路径不碰测速 + 跨线路只换一次（#96，第 85 轮） | **否** |
| 完整性 / 残留 | 无校验值的文件不再被当「完整」、截断文件靠声明大小抓出、无可比对时由调用方决定（#100 + 守卫 #101）；残留按归属分类清理、阈值提示语义、`update/` 24 小时保留策略（#102–#104 + 守卫 #105，第 87 轮） | 否（`--residue-smoke` 另做真实渲染，见下） |
| 存储清理 | 白名单矩阵（10 允许 / 19 拒绝）、空壳版本目录六态判定、会牺牲离线能力的分类默认不勾（#106–#108）；守卫：只走白名单、不整根删除、删除前复核归属与空壳状态（#109）；端到端 15 个样本的选择性删除（#110，第 88 轮） | 否（`--cleanup-smoke` 另做真实渲染，见下） |
| 设置未保存 / 关窗 | 快照对比（顺序无关、单字段、无基线、换行）（#113）；守卫：导航闸门顺序、回滚走 `LoadSettingsFromConfig`、`OnClosing` 与托盘退出都必须经过确认（#114，第 90 轮） | 否（`--settings-smoke` 另做真实 VM 验证，见下） |
| 启动前完整性 | 加载器是否真的装好（id 形态 + profile 存在）、整合包模组数与清单核对、空包/半缺/少一两个的分档（#116–#117）；守卫：构建参数前必须检查、启动前必须先过闸门、预期必须在下载之前落盘（#119，第 92 轮） | 否（`--launch-gate-smoke` 另做真实 VM 验证，见下） |
| UI 命令防重入 | 门的单槽语义（含并发只进一个）、真实 `RelayCommand` 的重入/解锁/异常后解锁、火后不管禁令与文案联动（#120–#122，第 93 轮） | 否（`--cleanup-smoke` 运行时断言「扫描中按钮必须置灰」） |
| 首页布局 | 滚动容器 + `Auto` 纵向 + 禁横向 + 列表 `MaxHeight` + hero 不被包进去（#115，第 91 轮）；运行时在 760/600/480 三档验证底部可达与 hero 固定 | 否（`--round79-smoke` 的三档实测，见下） |
| 加载器安装 | Forge 版本必须拼成「MC版本-Forge版本」、NEVER 生成 404 形状（#97）；四条 maven 地址的镜像↔官方往返（#98）；安装器重试前清残留 + 尊重强制线路源码守卫（#99，第 86 轮）；`launcher_profiles.json` 三态（缺→建 / 好→不动 / 坏→备份重建）与失败原因翻译（#123），守卫「必须先补再跑安装器、且只允许一处」（#124，第 94 轮） | **否**（`--forge-install-smoke` 另做真实安装，见下） |
| 分发与生命周期 | 增量补丁往返与拒绝非法输入（#67）、便携识别（#68）、断点恢复日志（#69）、关闭语义矩阵（#70）、单实例命名（#71）、补丁清单签名（#72，第 74 轮） | **否** |
| 日志导出与竞速 | 「导出日志」脱敏 txt + 头信息（#57）、竞速失败分类与重试预算（#55）、界面停顿 GC 归因（#56，第 70 轮） | **否** |
| 日志与计时 | 异步批量写 + `Flush` 不丢行保序（#52）、切页三段计时分类（#53）、`Log()` 禁止在调用线程碰磁盘（#54，第 69 轮） | **否** |
| 网络集成 | manifest 拉取、1.21.1 解析、下载引擎端到端 | **是** |
| 缓存一致性 | 4 项缓存 vs 网络对比（含真实 Fabric meta 拉取） | **是** |

> unit 模式下网络集成测试显示 `[SKIP] (integration test filtered by --unit)`，不计失败——**CI 上网络抖动不可能染红 unit job**。
>
> 注："缓存 vs 真实网络一致"的 4 项仅在 integration 覆盖；但**双路径一致性**（缓存命中分支 vs 本地优先分支，第 33 轮 bug 的两分支）已由离线单测覆盖——push 时即可捕获两分支分歧。

## 关键验证点

- **继承合并（inheritsFrom）**：解析 `1.21.1` 得 **97 个库**；**缓存命中路径与网络路径结果一致**（第 33 轮缓存短路 bug 的回归锁）。
- **classpath 一致性防线**：构建产物的 `-cp` 必须逐项等于 `ResolveAsync` 库集合 + client jar（unit 模式使用合成 fixture，离线可跑）。
- **HttpClient 生命周期防线**：`src/` 内禁止工厂外释放/字段缓存/using 包装共享 client（第 37 轮微软登录 disposed-object bug 的回归锁）。
- **实例元数据防线**：原始 GameInstance 序列化/反序列化仅允许 InstanceManager（第 31 轮脏数据 bug 的回归锁）。
- **下载引擎一致性**：引擎产物与直下载字节一致，含 Range 断点续传与损坏自愈；Content-Range 容错规则覆盖 `bytes 0-99/*` 等非标准格式。
- **崩溃规则语义**：专项规则（loader/mod）优先于泛化 JVM 规则；翻译为中文人话（"Java 版本过低"/"重复 Mod"/"缺前置"…）。
- **令牌脱敏**：命令行日志中 `--accessToken` 替换为 `<REDACTED>`；诊断包内 config 与路径同样脱敏。

## 真实机器端到端验证记录（2026-11-01，Windows 11 + Java 25）

| 验证项 | 结果 |
|--------|------|
| Fabric 1.21（fabric-loader 0.20.1）下载/补全/启动 | ✅ 游戏进程正常启动 |
| 内容中心：Modrinth 搜索/详情/版本分组/适配定位/安装 | ✅ |
| 实例创建/删除/导入 | ✅ |
| 断网/弱网场景 | ✅ 四线路竞速 + BMCLAPI 回退 + 本地缓存兜底 |
| 微软设备码登录 | ✅（需自备 Azure 应用 ID，经 `AURORA_MICROSOFT_CLIENT_ID` 注入；已修复登录初始化的 HttpClient 生命周期缺陷） |

## 未覆盖与计划

| 缺口 | 计划 |
|------|------|
| NeoForge/Forge 安装器全版本矩阵执行 | 部分版本已验；免安装器直装见 ROADMAP 工程债（完成后矩阵风险消失） |
| CurseForge 下载 | 尚未实现，见 ROADMAP 功能待办 |
| 微软登录令牌长期刷新与多账户并发 | 见 ROADMAP 功能待办（嵌入式登录窗口） |
| 启动器内嵌崩溃报告查看器 | 见 ROADMAP 功能待办（当前为文件日志 + 崩溃摘要 + 诊断包） |
| 诊断包导出的端到端测试 | 手工验证通过；脱敏/截断核心逻辑已有单测覆盖（第 40 轮），zip 端到端暂无自动化计划 |

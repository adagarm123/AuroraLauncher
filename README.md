# Aurora Launcher

一个从零自研、高性能的 **Minecraft Java 版启动器**（Windows）。

- 技术栈：**.NET 10 / C# 13 + WPF**，UI 与核心逻辑解耦（MVVM）。
- **零外部 NuGet 依赖**：仅使用 .NET 基类库（`System.Text.Json`、`HttpClient`、`System.IO.Compression` 等）。无网络可达性依赖，可离线构建。
- 许可：**MIT**。完全独立实现，未复用 PCL2 / HMCL 等受限制许可证项目的源代码（详见 `LICENSE`）。

> ### 当前状态速览（第 98 轮，2026-11-15）
>
> - **自测**：Unit 149/149（离线）· All 156/156（含网络集成），**机制防线 39 条**（编号连续性、命令绑定、交互元素、版本数据入口、classpath 一致性、HttpClient 生命周期、实例元数据入口、加载器卡片竞态、文档基线一致性、列表嵌套滚动边界、首次显示才初始化、日志 I/O 不占 UI 线程、崩溃修复不越界、搜索索引无死行、模糊白名单、启动拦截单一判断、内存分配不超 60% 且清理只碰自己、删除先删文件/扫描只加不删 + 测试必须隔离配置、测速只用 HEAD 且不进下载路径、Forge 安装器版本号必须拼 MC 前缀、全 src 不得出现 VerifyHash(、残留清理不得删正在下载的文件、存储清理只走白名单且不整根删除、会牺牲离线能力的分类默认不勾、清理忙碌态与两入口分工必须写清、离开设置页必须拦截 + 有任务时关窗必须弹窗、首页内容必须可滚动且列表有高度上限、加载器/整合包不完整必须拦住启动、异步命令必须自带防重入且不许「火后不管」、跑安装器前必须先补 launcher_profiles.json、第三方加载器 id 不得查官方清单且本地 JSON 优先、安装器依赖必须在跑 JVM 前预填充且成功必须有真实 profile、补全必须走 DownloadJob 且标题栏不得脱离任务队列、导航高亮与取消按钮必须由同一状态源判定、未完成任务不得显示 100% 且安装器黑盒必须转不确定态、Forge 前置体检必须过滤跨加载器 id 并按 modId 去重、安装器下载必须回报心跳且线路失败自动换镜像、失败任务不得贡献总进度且失败结论不得被二次覆盖、JDK 发行版必须走白名单与 SHA-256 校验）
> - **页面**：9 个功能页面（首页/游戏下载/实例管理/内容中心/下载管理/设置/日志/更新日志/帮助中心）+ 无边框自绘标题栏、Ctrl+K 全局搜索与**首次启动引导**；119 个交互元素全覆盖审计，零死按钮（含"无实例时启动按钮自动改成去下载"）
> - **下载/分发**：多任务并行、同目标文件单写者、断点续传；**线路三态**（自动测速择优 / 强制官方 / 强制镜像，核心文件失败不静默换线、非核心可静默回退）；**下载残留可收可查**（一键清理 `.part` + `update/` 残片，正在下载的不动，删了会同步台账）；增量更新只下补丁；便携模式；单实例（带唤醒握手）；托盘常驻；**Forge/NeoForge 安装器要的 launcher_profiles.json 自动补齐**（缺了它会 exit 1）
> - **存储**：**扫描垃圾文件**（图标/安装器/清单缓存、旧日志、无主残片、空壳版本目录），按分类勾选清理，会牺牲离线能力的项默认不勾；白名单 + 删除前复核，存档/模组/配置/备份/Java/库文件一律不碰；清理时有忙碌态（按钮变文案 + 进度条 + 冻结勾选）
> - **安全边**：**任何关闭路径都不会静默中断下载**（含「直接退出」与托盘菜单退出）；**设置改完忘保存会拦**（离开页面/关窗口时问「保存 / 放弃 / 取消」，保存按钮旁常驻未保存提示）；**耗时按钮执行中自动置灰并换文案**，被吞掉的重复点击计数写日志（异步命令默认带门）
> - **实例管理**：新建=跳转下载（不再造空实例）、删除=先删文件夹再删记录（失败保留记录并说明原因）、**扫描未注册实例**（找回幽灵实例 / 清理死链 / 陌生文件夹只报告）
> - **体验**：**游玩时长/启动次数**、**启动前模组体检**、**存档自动备份（保留 N 份）**、**性能仪表盘**、**Ctrl+K 全局搜索**、**崩溃诊断 + 一键修复 + 求助邮件**、**内存自动分配（上限物理内存 55%，手动值不静默改写）**、**光影壁纸 + 玻璃卡片外观（默认关闭，红色警告把关）**
> - **EXE**：`publish/AuroraLauncher.exe`（65.4 MB，单文件自包含）+ `publish/AuroraLauncher.patch`（97→98 补丁，供上一版用户升级）
> - **发布**：GitHub Actions 全自动（编译 → 单测 → 签名 → Release + Pages），无需手动上传
> - **文档**：[CHANGELOG](CHANGELOG.md)（第 1–97 轮历史；本 README 只保留最新一轮，每次发版把上一轮归档进去，故此处始终 = 当前轮次 − 1）· [ROADMAP](ROADMAP.md)（基线与待办）· [TEST\_RESULTS](TEST_RESULTS.md)（156 项自测）· [INTERACTIVE\_UI\_AUDIT](INTERACTIVE_UI_AUDIT.md)（交互元素检验）· [docs/PROJECT\_OVERVIEW](docs/PROJECT_OVERVIEW.md)（功能/性能/安全/限制）· [docs/adr](docs/adr/INDEX.md)（20 篇架构决策记录）

---

## 核心架构

```
AuroraLauncher/
├─ src/
│  ├─ AuroraCore/                  # 平台无关核心（类库，无 UI 依赖，零 NuGet 依赖）
│  │  ├─ BuildInfo.cs              # 版本轮次常量（防降级判定 + 文档基线 Guard）
│  │  ├─ LauncherConfig.cs         # 全局配置（JSON 持久化，绝不存密钥）
│  │  ├─ Models/                   # 版本清单 / 版本 JSON / 资源索引 / 实例 / Mod 模型
│  │  ├─ Download/DownloadEngine.cs# 高并发下载引擎（断点续传 / 断点失效泛化 / 哈希上限 / 四线路竞速）
│  │  │  ├─ BinaryDelta.cs         # 增量更新补丁（块哈希差分 / 头带 SHA-256 / 双向校验）
│  │  │  ├─ DownloadJournal.cs     # 未完成下载日志（后台批量写，启动时重放为「继续下载」）
│  │  │  └─ PathWriteGate.cs       # 目标路径写锁（同文件全程序单写者）
│  │  │  └─ SlowTransferWatcher.cs # 聚合慢速看门狗（纯逻辑、时钟注入）
│  │  ├─ Utils/                    # Logger / JsonHelper / HttpClientFactory / NetRouting /
│  │  │                            #   NetworkProbe / PathEx / CryptoHelper / GenerationGate
│  │  └─ Services/                 # 业务编排层
│  │     ├─ PlaytimeTracker.cs        # 游玩时长（落盘会话 + 心跳，崩溃后按心跳补记）
│  │     ├─ ModPreflight.cs           # 启动前模组体检（fabric.mod.json / mods.toml 的依赖与冲突）
│  │     ├─ MrpackExporter.cs         # 导出 .mrpack（Modrinth 直链 + overrides）
│  │     ├─ PortableModePolicy.cs     # 便携模式（portable.txt / config.json 决定数据根）
│  │     ├─ SingleInstancePolicy.cs   # 单实例命名（按数据根派生）
│  │     ├─ CloseBehaviorPolicy.cs    # 关闭窗口：退出 / 收起托盘 / 询问（含记住）
│  │     ├─ VersionManifestService.cs   # 版本清单与版本 JSON 拉取（经 NetRouting）
│  │     ├─ VersionResolver.cs         # 版本数据唯一合法入口（继承合并 + 缓存一致性）
│  │     ├─ GameInstaller.cs           # 安装与 Evaluate（安装状态单一来源）
│  │     ├─ JavaFinder.cs              # Java 运行时探测与版本匹配
│  │     ├─ JavaRuntimeService.cs      # 一键安装 Java 21（Adoptium API + SHA-256 + 静默解压）
│     ├─ CurseForgeService.cs       # CurseForge 搜索/浏览（自备 Key，下载跳官方页）
│     ├─ OptiFineService.cs         # OptiFine 官网解析 / 安装器校验 / 唤起
│  │     ├─ UpdateService.cs           # 自动更新（RSA 验签 / 防降级 / 后台下载 / 换装重启）
│  │     ├─ UpdatePolicy.cs            # 三态更新开关的纯逻辑决策
│  │     ├─ LaunchArgumentBuilder.cs   # 参数规则、token 替换、classpath 组装（有 Guard 锁定）
│  │     ├─ GameLauncher.cs            # 进程启动、日志流、CrashAnalyzer（规则表投影自诊断引擎）
│  │     ├─ CrashResolutionEngine.cs   # 崩溃诊断规则库（27 条）：诊断 + 修复动作 + 安全白名单
│  │     ├─ SupportReport.cs           # 求助邮件 mailto 构造（不发送、不存凭据）
│  │     ├─ AuthService.cs             # 微软设备码登录（Client ID 可配置）+ 离线账户（DPAPI）
│  │     ├─ DiagnosticsExporter.cs     # 一键诊断包（脱敏报告 / 尾部截断）
│  │     ├─ ModLoaders/                # Fabric/Quilt 免安装器直装；Forge/NeoForge 官方安装器
│  │     ├─ ModManager.cs              # Modrinth 搜索/详情/下载、.mrpack、前置检测
│  │     ├─ InstanceManager.cs         # 实例隔离、导入（仅复制）、元数据唯一读写入口
│  │     └─ LauncherService.cs         # 顶层编排（启动前补全、崩溃摘要、启动 UX）
│  └─ AuroraLauncher.Desktop/      # WPF 桌面端（WinExe）
│     ├─ App.xaml / MainWindow.xaml # 主题资源 + 侧边栏导航 + 首次启动引导浮层
│     ├─ Themes/ Styles/           # 深浅主题画刷与控制样式
│     ├─ Views/                    # 9 个页面 + ChangelogView / UpdateDialog
│     ├─ ViewModels/CrashReportVM.cs # 图形化崩溃卡片数据
│     └─ ViewModels/MainViewModel.cs# MVVM 绑定层
├─ tests/AuroraSelfTest/           # 85 项自测（--unit 78 离线 / --all 85 含网络），12 条机制防线
├─ scripts/                        # publish.ps1（单测前置+发布）/ sign-release.ps1 / run-tests.ps1
├─ .github/workflows/              # selftest.yml（分层 CI）/ release.yml（全自动发布）
├─ docs/                           # adr/ · auto-update.md · release-notes-template.md · PROJECT_OVERVIEW.md
├─ publish/AuroraLauncher.exe      # 已发布单文件 EXE
├─ CHANGELOG.md  README.md  ROADMAP.md  TEST_RESULTS.md  INTERACTIVE_UI_AUDIT.md  LICENSE
```

> 功能总览 / P0–P4 对照 / 性能基准 / 安全与合规 / 已知限制 / 测试结果：见 [docs/PROJECT_OVERVIEW.md](docs/PROJECT_OVERVIEW.md)。

---

## 最新一轮的更新内容
### 第 98 轮：不再「被取消」、失败不再是 100%，以及设置里可选的 Zulu Java（2026-11-15）

**先纠正诊断：这次既不是网络断连，也不是镜像竞速误伤，是我第 96/97 轮加的看门狗把正在下载的安装器掐了**

时间线在代码里是确定的：`RunRepairJobAsync` 以 `keepPreparing` 入队，`EnterStage("installer-download")` 会把任务切到 Running（该阶段静默上限 90 秒）；但 `ModLoaderInstallers` 下载 installer jar 时给 `DownloadEngine` 传的是 `null` 进度回调，任务收不到任何心跳。与此同时，第 97 轮的「2 分钟没有新文件完成」判定还留着上一批原版文件的 `TrackedTotalFiles`，在单文件安装器阶段同样会误判。于是看门狗 `AbortAsFailure` 取消 linked token → 引擎抛 `OperationCanceledException` → `catch (DownloadException)` 捕获不到（只接 DownloadException）→ 外层 `result.Error = ex.Message` 原样塞进 UI，于是出现了玩家什么都没点却看到 `The operation was canceled.`。

更糟的是 `RunRepairJobAsync` 的 catch 又调了一次 `FinishJob(job, ex, false)`，把看门狗刚写好的中文超时提示**二次覆盖**成英文原文 —— 截图里那句「启动失败: Forge 加载器补全失败: The operation was canceled.」就是这么来的。左侧 100% / 右侧「失败 0%」则是另一条独立 bug：`JobProgressPolicy.Aggregate` 把 `Failed` 也算进完成度平均（引擎把失败文件计为 CompletedFiles，最后一次 Report 常是 100），且 `Display(..., !anyPending)` 在没有活跃任务时允许 100。

**改法（下载韧性 + 状态诚实 + 人话报错）**

- **镜像优先、全异常可降级**：installer jar 改成 `BMCLAPI → 官方 Maven` 顺序，`InstallFailureText.IsRetryableRouteFailure` 覆盖 `DownloadException / HttpRequestException / IOException / TimeoutException / TaskCanceledException`（外部主动取消除外，绝不跟看门狗对着干）。每次重试前 `CleanInstallerResidue` 删掉半成品 jar / `.part` 并 `DownloadJournal.Forget`，避免下次从垃圾续传。
- **给任务心跳**：`InstallAsync` 新增 `Action<DownloadProgress>? onProgress`，`LauncherService` 安装与补全两条路径都透传；字节进度直接喂给 `job.Report`，看门狗不再把「正在慢慢下」当成死，速度也不再长期显示 0 B/s。
- **看门狗按阶段**：`TimeoutSeconds(Preparing, 命名阶段)` 不再一律 30 秒（installer-download 90 秒 / 其余 15 分钟）；`ShouldUseFileStall` 让单文件 jar 与安装器黑盒阶段豁免「2 分钟无新文件完成」—— 那里本来就没有文件计数器可推进。
- **报错说人话**：新增 `InstallFailureText` 统一翻译，`FinishJob` 增加 `alreadyFinished` 判定，任何后续异常都不得覆盖已交付的失败结论；`LauncherProfiles.Explain` 补上 `ConnectException / Connection timed out / Failed to download version manifest`，Forge JVM 自己联网失败也会给出原因和下一步。
- **失败就别装作完成**：`Aggregate` 让 `Failed` 行不贡献完成度、有失败任务时汇总永远到不了 100%；`MarkFinished` 对非 Done 状态把百分比钳到 99；下载页用 `HasFailedJobs / FailedJobsHint` 显示红色失败标记，汇总文案改成「N 个任务失败 · 可点重试」。

**设置里新增 Azul Zulu JDK 下载**

设置 → Java 环境新增「JDK 发行版」下拉（Eclipse Temurin / Azul Zulu），选中后「一键安装 Java 21」按该发行版下载。`JavaDistributionPolicy` 是纯策略：Zulu 走 `api.azul.com/zulu/download/community/v1.0/bundles/`，**必须带 `include_fields=sha256_hash,size`**（否则响应里没有校验和，安装链路会拒绝），从返回的一堆包里排除 `-fx-` / `-crac-` / `-jre` 变体、校验 `cdn.azul.com` 域名白名单与 64 位 SHA-256、选最高 patch 版本，再复用既有 `DownloadEngine → SHA-256 校验 → 静默解压 → 设为默认` 链路。选择写进 `config.json`，并纳入第 90 轮的「未保存设置」快照。

**验证**

- `--unit` **149/149**、`--all` **156/156**（新增 7 项：取消文案翻译、Zulu 包解析与白名单、看门狗阶段阈值、失败汇总、3 条静态防线）
- **机制防线 39 条**；交互元素 118 → **119**（新增 JDK 发行版下拉）
- `--java-dist-smoke` 实跑两家官方源：Temurin `jdk-21.0.13+9` 195 MB、Zulu `zulu21.54.109-ca-jdk21.0.13.0.3` 201 MB，均带 SHA-256
- **`--forge-install-smoke` 本轮两次未绿**（如实记录）：installer jar 走 BMCLAPI 下载成功、依赖预填充 29/29，但官方安装器 JVM 自己拉 Mojang 版本清单 / 跑 processor 时 `Connection timed out` 与 `Failed to download version manifest` 而退出码 1。它使用自己的网络栈，不经启动器镜像线路，属外部环境阻断；这两次失败恰好验证了本轮新增的中文失败翻译生效。第 95/96 轮同链路曾实跑安装成功，本轮未改动安装器运行阶段逻辑。
- 发布：EXE + 97→98 补丁 + 签名清单（见文末）；云端 Release 由 Actions 同一次 workflow 编译/签名/上传/部署

**配套**：`BuildInfo.CurrentRound` → 98、应用内更新日志置顶第 98 轮、第 97 轮归档进 CHANGELOG、新增 ADR-0020。

---

## 快速开始

### 方式一：直接运行已发布的可执行文件（推荐）

已生成**单文件、自包含（self-contained）**&#x7684; Windows EXE：

```
publish/AuroraLauncher.exe   (约 63 MB，内含 .NET 运行时不需安装)
```

双击即可在 Windows 10/11 上运行，无需安装 .NET 运行时。首次启动会出现**新手引导**（检测 Java → 推荐版本一键安装 → 完成），老用户自动跳过。

### 方式二：从源码构建

需要安装 [.NET 10 SDK](https://dot.net) 与 Windows（WPF 仅支持 Windows）。

```bash
# 构建桌面程序（Debug）
dotnet build src/AuroraLauncher.Desktop/AuroraLauncher.Desktop.csproj -c Debug

# 发布为单文件自包含 EXE（Release, win-x64）
dotnet publish src/AuroraLauncher.Desktop/AuroraLauncher.Desktop.csproj `
  -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

### 运行自测（分层：unit 离线 / all 含网络集成）

```bash
dotnet run --project tests/AuroraSelfTest -c Debug -- --unit   # 78 项，离线、毫秒级、CI 必跑
dotnet run --project tests/AuroraSelfTest -c Debug -- --all    # 85 项，含 7 项网络集成
```

本地一键 CI 脚本：`scripts/run-tests.ps1`（`-Full` 跑全量）；GitHub Actions 见 `.github/workflows/selftest.yml`。

需要桌面会话的两项显式诊断（不进 CI）：

```bash
dotnet run --project tests/AuroraSelfTest -c Release -- --nav-smoke  # 真实样式字典 + 12 轮显隐冒烟
dotnet run --project tests/AuroraSelfTest -c Release -- --nav-bench  # 每页构建/首次显示/稳态切页耗时
```

---


# Aurora Launcher

一个从零自研、高性能的 **Minecraft Java 版启动器**（Windows）。

- 技术栈：**.NET 10 / C# 13 + WPF**，UI 与核心逻辑解耦（MVVM）。
- **零外部 NuGet 依赖**：仅使用 .NET 基类库（`System.Text.Json`、`HttpClient`、`System.IO.Compression` 等）。无网络可达性依赖，可离线构建。
- 许可：**MIT**。完全独立实现，未复用 PCL2 / HMCL 等受限制许可证项目的源代码（详见 `LICENSE`）。

> ### 当前状态速览（第 61 轮，2026-11-05）
>
> - **自测**：Unit 37/37（离线）· All 44/44（含网络集成），**机制防线 9 条**（编号连续性、命令绑定、交互元素、版本数据入口、classpath 一致性、HttpClient 生命周期、实例元数据入口、加载器卡片竞态、文档基线一致性）
> - **页面**：9 个功能页面（首页/游戏下载/实例管理/内容中心/下载管理/设置/日志/更新日志/帮助中心）+ 主窗口侧栏导航与**首次启动引导**；86 个交互元素全覆盖审计，零死按钮
> - **新功能**：更新安装全程可见的进度窗口（不再黑屏）、设置页下载进度显示速度与体积、「立即重启并应用更新」按钮
> - **EXE**：`publish/AuroraLauncher.exe`（约 63 MB，单文件自包含）
> - **发布**：GitHub Actions 全自动（编译 → 单测 → 签名 → Release + Pages），无需手动上传
> - **文档**：[CHANGELOG](CHANGELOG.md)（第 1–60 轮历史；本 README 只保留最新一轮，每次发版把上一轮归档进去，故此处始终 = 当前轮次 − 1）· [ROADMAP](ROADMAP.md)（基线与待办）· [TEST\_RESULTS](TEST_RESULTS.md)（44 项自测）· [INTERACTIVE\_UI\_AUDIT](INTERACTIVE_UI_AUDIT.md)（交互元素检验）· [docs/PROJECT\_OVERVIEW](docs/PROJECT_OVERVIEW.md)（功能/性能/安全/限制）· [docs/adr](docs/adr/INDEX.md)（5 篇架构决策记录）

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
│  │  │  └─ SlowTransferWatcher.cs # 聚合慢速看门狗（纯逻辑、时钟注入）
│  │  ├─ Utils/                    # Logger / JsonHelper / HttpClientFactory / NetRouting /
│  │  │                            #   NetworkProbe / PathEx / CryptoHelper / GenerationGate
│  │  └─ Services/                 # 业务编排层
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
│  │     ├─ GameLauncher.cs            # 进程启动、日志流、CrashAnalyzer 崩溃规则翻译
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
├─ tests/AuroraSelfTest/           # 44 项自测（--unit 37 离线 / --all 44 含网络），9 条机制防线
├─ scripts/                        # publish.ps1（单测前置+发布）/ sign-release.ps1 / run-tests.ps1
├─ .github/workflows/              # selftest.yml（分层 CI）/ release.yml（全自动发布）
├─ docs/                           # adr/ · auto-update.md · release-notes-template.md · PROJECT_OVERVIEW.md
├─ publish/AuroraLauncher.exe      # 已发布单文件 EXE
├─ CHANGELOG.md  README.md  ROADMAP.md  TEST_RESULTS.md  INTERACTIVE_UI_AUDIT.md  LICENSE
```

> 功能总览 / P0–P4 对照 / 性能基准 / 安全与合规 / 已知限制 / 测试结果：见 [docs/PROJECT_OVERVIEW.md](docs/PROJECT_OVERVIEW.md)。

---

## 最新一轮的更新内容

### 第 61 轮：更新安装过程全程可见 + 下载进度增强（2026-11-05）

**问题**：此前更新换装由一个完全隐藏的 cmd 脚本完成（等旧进程退出 2 秒 → 复制 63 MB → 最多 10 次重试 × 2 秒 → 重启）。用户点「立即更新」或重启后，主窗口立刻消失、屏幕上十几秒空白，看起来像崩溃；下载阶段的设置页进度条也只有百分比，看不到速度和体积。

**1. 可见的换装窗口（根治黑屏）**：启动器现在以 `--apply-update` 模式**从临时副本启动自身**（`update/updater.exe`）——运行中的 EXE 在 Windows 上被锁定，所以执行换装的进程必须是另一份文件，否则无法覆盖目标。该模式不再创建主窗口，而是显示一个置顶、无边框的小窗口：「正在应用更新并重启…」+ 阶段文字 + 进度条，覆盖「等待旧版本退出 → 校验更新包 → 替换文件（被占用时显示重试次数）→ 写入版本信息 → 重新启动」。任何失败都变成可读错误 + 「重试 / 关闭」按钮，不再静默黑屏。旧的隐藏 cmd 脚本保留为**仅当可见更新器启动失败时**的兜底。

**2. 安全与正确性**：`UpdateApplier` 在换装前按 `build-info.txt` 里的 `exe-sha256` **二次校验**已下载包（防 staged 文件被改动）；复制带 10 次有界重试且每步上报；交接参数 `UpdateApplyArgs` 为单 argv token 的纯逻辑，畸形数据 / 相对路径 / 非法 PID 一律失败关闭（单测 #37：含一次不弹窗的完整换装演练，验证复制、二次校验、重启路径与「篡改后失败且不破坏已装 EXE」）；新实例启动后延迟清理临时副本。

**3. 下载进度更可见**：设置 → 更新 的进度条（第 58 轮已有）现在显示**百分比 + 已下载/总大小 + 实时速度**，并同步到主窗口状态栏；自动下载时轻提示明确告知「设置 → 更新 可查看进度」。下载完成后设置页出现「立即重启并应用更新」按钮，点它即进入可见换装窗口，不用猜什么时候会重启。

**配套**：`BuildInfo.CurrentRound` → 61、应用内更新日志置顶第 61 轮、第 60 轮归档进 CHANGELOG；交互元素 83→86（63 直接绑定 + 17 样式命令 + 6 事件处理器元素）；`docs/auto-update.md` 同步用户/作者流程说明。

自测 **Unit 37/37、All 44/44** 全绿；EXE 经 `scripts/publish.ps1` 重新发布。

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
dotnet run --project tests/AuroraSelfTest -c Debug -- --unit   # 37 项，离线、毫秒级、CI 必跑
dotnet run --project tests/AuroraSelfTest -c Debug -- --all    # 44 项，含 7 项网络集成
```

本地一键 CI 脚本：`scripts/run-tests.ps1`（`-Full` 跑全量）；GitHub Actions 见 `.github/workflows/selftest.yml`。

---


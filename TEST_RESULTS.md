# 自测结论（AuroraSelfTest）

> 更新于 2026-11-01（第 57 轮）。
> **上次全绿：2026-11-01 —— Unit 31/31（离线）、All 38/38（含网络集成）。**
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
[PASS] Guard: README round numbering is continuous
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

=== Result: 31 passed, 0 failed, 7 skipped ===（跳过的 7 项为网络集成测试）

--all 模式（含网络集成）：
另含 [PASS] Fetch + parse official version manifest
      [PASS] Resolve 1.21.1 (inheritance merge) + build launch spec
      [PASS] DownloadEngine: fetch small file with SHA-1 + Range resume
      [PASS] Resolver cache: vanilla cache vs network consistent
      [PASS] Resolver cache: loader profile (inheritsFrom) cache merges vanilla
      [PASS] Resolver cache: multi-level inheritance chain
      [PASS] Resolver cache: corrupted & incomplete entries fall back to network

=== Result: 38 passed, 0 failed, 0 skipped ===
```

## 测试项增长记录（第 48 轮补记）

外部复核指出第 42 轮（Unit 22）到第 46 轮（Unit 25）之间新增的 3 项测试在更新日志中没有解释。逐项溯源如下（All 模式恒为 Unit + 7 项网络集成，故同步 +3）：

| 项 | 测试 | 来源轮次 | 说明 |
|----|------|----------|------|
| #23 | `NetworkProbe.Decide: data-driven source selection` | 第 42 轮（代码），第 43 轮计入口径 | 随「网络环境自动感知」功能加入；当时 TEST_RESULTS 清单漏列，第 44 轮修正口径（23=22+1） |
| #24 | `AuthService: Microsoft client id resolution + configuration guard` | 第 45 轮 | 随微软登录 400 根治加入（env > config 解析优先级 + 配置门卫） |
| #25 | `Guard: README round numbering is continuous` | 第 45 轮文档会话 | 随「第 44→46 轮编号跳轮」修复加入；**当时未在更新日志中说明**（本表补记），README 更新补充章节同轮补齐 |
| #26 | `LauncherConfig: AutoInstallFabricApi defaults ON and survives JSON round-trip` | 第 47 轮 | 随 Fabric API 前置自动安装加入 |
| #27 | `GenerationGate: stale loader-card responses are discarded (race regression)` | 第 48 轮 | 随加载器卡片延迟修复加入（乱序响应丢弃的纯逻辑回归测试） |
| #28 | `Guard: doc baselines agree (round + unit/all counts across the three main docs)` | 第 48 轮 | 文档基线一致性防线：README / ROADMAP / TEST_RESULTS 三份文档的轮次标记与 Unit/All 计数必须一致，否则测试红——"文档和代码对不上"从人工约定变成机制拦截 |
| #29 | `SlowTransferWatcher: sustained crawl aborts, brief dips don't (pure logic)` | 第 52 轮 | 随下载慢速看门狗加入（持续爬行中止 / 短暂波动不误杀，注入时钟纯逻辑） |
| #30 | `LoaderCardVM: null coercion + append-only fill never auto-select (round-50 regression)` | 第 53 轮 | 第 50 轮"卡片自动选中"根因的状态机回归锁（null 归一 / 追加不选中 / IsLoaderSelected 单一来源）；测试工程自此引用 Desktop 项目 |
| #31 | `UpdateService: manifest signature + downgrade rejection (pure logic)` | 第 57 轮 | 自动更新安全核心：验签通过 / 篡改 url|sha|size|round 任一字段必失败 / 防降级（仅严格更高轮次算更新）；文档基线 Guard 同轮新增 BuildInfo.CurrentRound 一致性核对 |

> 此后新增/修改测试项必须在对应轮次的更新补充章节中说明——该约定由 #28 号防线自动核对三份文档头部计数，漂移即红。

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
| 机制防线 | VersionJson 单一入口、classpath 一致性、HttpClient 生命周期、实例元数据单一入口、XAML 命令绑定核对、交互元素全覆盖审计（第 46 轮起：七类交互属性 + 全部桌面 XAML 范围 + 警告级死命令探测，见 [INTERACTIVE_UI_AUDIT](INTERACTIVE_UI_AUDIT.md)） | **否** |
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

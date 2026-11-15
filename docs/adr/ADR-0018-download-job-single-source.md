# ADR-0018：下载任务的单一状态来源

- 日期：2026-11-15（第 96 轮）
- 状态：已采纳（含 2 条静态防线 + 6 项单测）
- 关联：ADR-0015（命令防重入）、ADR-0017（版本来源与安装器依赖）

## 背景

用户给了两张截图与一份 667 行日志。第一张同时出现三个互相矛盾的事实：标题栏「正在安装… 获取加载器安装器」、汇总面板 0% / 0 B/s /「当前没有下载任务」、右侧列表空白且「全部取消」仍高亮。第二张是补全失败后的红色横幅，左侧「实例管理」与「下载管理」同时呈淡紫。

代码核实结果：

- `LaunchAsync` 的补全分支走 `InitStages(stages, title)`（**全局**阶段表）+ 手动写 `Status` 字符串，失败时只抛一个全局异常；而正常安装流程走 `InitStages(job, …)`。补全从未进入 `DownloadJobManager`，所以列表为空、汇总为 0、标题栏与任务队列完全分叉。用户对 UI 的诊断成立。
- 导航高亮实际是**唯一** `CurrentPageKey` 经 `PageActiveConverter` 驱动的，两个淡紫中一个是鼠标悬停的默认 Button 背景，不是两个 `IsSelected`。是视觉混淆，不是状态分叉。
- 更关键的功能问题：第二张截图的「安装器运行结束，但没有生成可用的加载器版本文件」被解读为「第 95 轮防线抓到了假成功」，**实际是误伤**。日志显示 `13:06:16` 预填充「29 个依赖已在本地」、`13:06:46`（30 秒）即报错，而正常安装需 94~165 秒；磁盘上 `versions/1.20.1-forge-47.4.22/1.20.1-forge-47.4.22.json` 真实存在（16.6 KB、13:06 被重写、`mainClass=cpw.mods.bootstraplauncher.BootstrapLauncher`、29 个库）。原因是该目录 11:43 已存在，ADR-0017 引入的「成功必须检测到**新建**目录」把 `existing` 中的老目录排除，`detected=null` → 误报失败。

## 决定

1. **把补全纳入 DownloadJob 生命周期**。新增 `JobState.Preparing`；`BeginJobAsync` 一创建就 `MarkPreparing`，修复任务可 `keepPreparing` 到第一个真实阶段（`EnsureRunning`）。`JobProgressPolicy.Aggregate` 把 Preparing 计为 active，汇总面板不再宣称「当前没有下载任务」。
2. **单一来源的标题栏**。XAML 改绑 `HeaderText`：有活跃任务时完全由 `_jobManager.Jobs` 的第一个未完成任务（标题 + `StageText`）驱动；无任务且是安装流程则回「空闲」；其它状态文本原样透传。`Status` 仅保留给更新检查、导出、游戏退出与错误文本，并继续供 `StatusToBrush` 着色。
3. **失败闭环**。`RunRepairJobAsync` 把进度、阶段、安装器日志里程碑都写进 Job；失败 `FinishJob(job, ex, false)` 留下红色任务行，错误横幅新增「重试」按钮重跑同一实例。底层用 `CreateLinkedTokenSource(_cts.Token, job.Token)`，取消能真正中止安装。
4. **看门狗按阶段分级**（`JobWatchdogPolicy`）：准备中 30 秒、安装器下载 90 秒（日志实测该 jar 下过 33 秒，30 秒会误杀）、运行阶段 15 分钟（官方安装器实测静默 94~165 秒，且本身已有 15 分钟上限）。超时用 `AbortAsFailure` 取消 linked token 并把任务标成 Failed（不是 Canceled），保留重试入口。`CheckStalledJobs` 挂在 250 ms 轮询最前面，版本不变也执行。
5. **按钮防呆**：`CancelAllDownloadsCommand` 增加 `HasCancellableJobs` 的 `CanExecute`，XAML 同时 `IsEnabled="{Binding HasCancellableJobs}"`。
6. **导航视觉收敛**：新增纯策略 `NavigationHighlight.IsActive` 供背景与新增的 active 左侧竖条共用；9 个导航项的竖条与背景都只绑 `CurrentPageKey`。悬停仍可着色，但永远不产生竖条。Guard 锁定数量与绑定来源。
7. **认领已装 profile**（P0）：`LoaderProfileLocator.Pick` 在安装器跑完且无新建目录时，扫描已有 `versions/`，要求目录同时含 MC 版本、加载器名、版本 token，且 `<dir>/<dir>.json` 存在并含非空 `mainClass`。命中即作为 `result.VersionId`，`RepairAsync` 把真实 id 写回旧实例，旧的 hint id 被自然迁移。

## 不做的事

- 不把 30 秒套到所有阶段：会杀掉正常的 Forge 安装。超时必须与阶段语义一致。
- 不在超时时静默重试：网络可能是持续断的，自动重试只会把「失败」变成「卡更久」。留红色任务 + 一键重试，由用户决定。
- 不引入每页一个 `IsSelected`：现有 `CurrentPageKey` 已是单一来源，再加重置逻辑反而制造分叉。只做视觉区分。
- 不改动 `DownloadJobManager` 的 3 并发槽语义，Preparing 任务的槽位释放由 `Finish` 统一处理。

## 后果与风险

- 安装/补全期间标题栏、汇总面板、列表三者不会再分叉；失败有可见、可重试的记录。
- `HeaderText` 在无活跃任务但仍是安装流程时显示「空闲」，会覆盖 `FinishJob` 写入的结果文本吗？不会：`FinishJob` 会把 `_jobDrivenStatus` 收敛为 false，`HeaderText` 随即回落到 `Status`（错误文本仍显示且仍由 `StatusToBrush` 着红）。
- 认领逻辑要求 `mainClass`，与 `GameInstaller.IsVersionJsonInstalled` 的判定一致；空壳目录仍不会被当成已安装。
- 仍需真机验证：旧实例点一次「补全文件」后是否被迁移到 `1.20.1-forge-47.4.22` 并能启动；真实网络卡住时看门狗与重试的手感。

## 机制防线

- `Guard: 补全必须走 DownloadJob，标题栏不得脱离任务队列（第 96 轮）`：锁定 `RunRepairJobAsync`、禁止 `InitStages(BuildStages(instance.LoaderType)` 回归、`HeaderText` 由 `_jobManager.Jobs` 驱动、看门狗挂载、失败走 `FinishJob`、XAML 绑 `HeaderText`。
- `Guard: 全部取消置灰 / 错误可重试 / 导航单一来源（第 96 轮）`：锁定 `HasCancellableJobs`、`RetryLastFailedJobCommand`、9 个 active 竖条与 9 个 `CurrentPageKey` 背景绑定。
- 第 95 轮的「安装器成功必须验真」Guard 同步扩展：除 `HasUsableProfile` 外还必须存在 `LoaderProfileLocator.Pick(`。

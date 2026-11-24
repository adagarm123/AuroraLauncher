# ADR-0026 布局、进度诚实与全代码审查修复

- 状态：已采纳（含静态防线与离线单测）
- 轮次：第 104 轮（2026-11-24）
- 触发：用户截图「最大化后界面超出显示范围」「没有任务却显示 100% 总进度」，并要求修复单任务 0%、把 Java / 启动器更新收进下载管理、彻查代码。

## 背景

无边框 `WindowChrome` 窗口最大化后仍保留 6 px 缩放边框，WPF 把内容推到工作区之外。`JobProgressPolicy.Aggregate` 在空任务列表时用 `Display(0, finished:true)` 得到 100%。`DownloadJob.Report` 用「已完成文件数 / 总文件数」算进度，单文件下载在最后一字节前始终是 0%。

全代码审查另发现若干隐藏缺陷，按严重度排序：看门狗先 `MarkFailed` 后调用方 `FinishJob` 跳过 `_jobManager.Finish`，导致 semaphore 槽永久泄漏；`Cancel` 只释放 Running 不释放 Preparing；卸载清理助手对 manifest 的 `ExePath` 无校验；自动/手动更新并发覆盖暂存 EXE；下载日志压缩与 writer 不同锁；配置非原子写；单例惰性初始化竞态；主题 System 被当 Dark；async void 定时器无异常屏障；后台线程直接索引 UI 的 `ObservableCollection<JobStage>`。

## 决策

1. **布局**：新增纯策略 `WindowLayoutPolicy`，最大化时 `ResizeBorder=0` 并把窗口 Left/Top/Width/Height 钉到 `SystemParameters.WorkArea`；还原时恢复 6 px。
2. **进度**：`Aggregate` 空列返回 0；Done 行只作历史统计，不拉低新任务均值；失败不贡献完成度。新增 `FractionFor(totalFiles, completedFiles, totalBytes, downloadedBytes)`，优先用引擎实时累加字节数。`DownloadJob.Report` 改用该比例，并把阶段标签快照成普通字符串，避免跨线程读 UI 集合。
3. **下载管理集成**：DownloadsView 增加 Java / 检查更新 / 应用更新三个入口；Java 安装与更新下载都创建 `DownloadJob`，进度回调同时喂给原状态与任务，取消令牌链接到任务令牌。
4. **槽位**：`DownloadJob.SlotHeld` + `DownloadJobManager.ReleaseSlot(job)` 幂等回收；`StartAsync` 取槽后标记，`Finish` / `Cancel` / VM 的 `FinishJob(alreadyFinished)` 都走同一入口。
5. **更新暂存安全**：`StagingTempPath` 先落盘并校验 SHA-256，`CommitStaged()` 成功后才替换旧包；新增 `CancelStagingOnly()` 保护旧暂存；更新入口加 `IsCheckingUpdate` 并发闸，CTS finally 释放。
6. **卸载清单校验**：`IsPlausibleExePath` 只接受名为 `AuroraLauncher.exe` 且不在临时目录的路径；`IsPlausibleHelperPath` 只接受 `%TEMP%` 下 `aurora-uninstall-*.exe`。
7. **持久化与并发**：配置写临时文件 + 原子 Move + `.bak-last`；`Instance` getter 加锁；日志压缩投递到 writer 线程原子替换，写失败重新入队；主题 `ThemePolicy.Resolve` + 系统主题变化订阅；async void / fire-and-forget 全部 try/catch。

## 权衡

- 最大化时把窗口钉到主屏工作区，在多显示器间拖动最大化窗口不会跟随副屏 DPI；换来的是不再溢出屏幕。
- Done 任务仍单独保留一个「全部完成」语义（只有 Done 时显示 100%），避免清理记录后进度条失去意义。
- Java / 更新进入任务队列后，旧的设置页进度仍保留，二者显示同一份数据源，不是两套计数。

## 验证与已知限制

- `--unit` 179/179、`--all` 186/186；新增 7 项覆盖进度策略、槽位回收、清单校验、主题解析与 2 条静态 Guard。
- **未在真机验证最大化窗口边缘与真实下载观感**（截图场景需用户实测）；任务槽与进度策略以纯逻辑单测验证。
- 日志压缩改为异步后，进程在压缩排队但未执行时被强杀，日志可能仍保留冗余行（不会损坏数据，下次启动再压缩）。

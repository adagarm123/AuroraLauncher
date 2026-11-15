# ADR-0020 安装器下载韧性与 JDK 发行版可插拔

- 状态：已采纳（含静态防线与离线单测）
- 轮次：第 98 轮（2026-11-15）
- 触发：用户截图「启动失败: Forge 加载器补全失败: The operation was canceled.」+ 左侧总进度 100% / 右侧任务失败 0%；同时要求设置中增加下载 Zulu JDK 的能力。

## 背景

第 96 轮把补全流程纳入 `DownloadJob` 并加了看门狗，第 97 轮又加了「2 分钟无新文件完成」停滞判定。两者叠加后，`ModLoaderInstallers` 下载 installer jar 时传 `null` 进度回调，任务没有任何心跳；看门狗先中止 linked token，`RunRepairJobAsync` 的二次 `FinishJob` 又把中文超时提示覆盖成 CLR 原文。与此同时 `JobProgressPolicy.Aggregate` 把 `Failed` 行的百分比计入平均，导致汇总显示 100%。

## 决策

1. **下载韧性与心跳优先于更严的看门狗。** installer jar 改为 `BMCLAPI → 官方 Maven` 顺序（国内线路优先），`InstallFailureText.IsRetryableRouteFailure` 覆盖超时/断连/IO 类异常，但外部主动取消一律不重试；`Action<DownloadProgress>? onProgress` 从 `LauncherService` 透传到 `DownloadEngine`，让任务持续收到字节心跳。每次线路切换前 `CleanInstallerResidue` 删除半成品与 `.part` 并 `DownloadJournal.Forget`。
2. **看门狗按阶段，而不是按状态一刀切。** 已进入命名阶段的 `Preparing` 任务不再套用 30 秒准备上限；`ShouldUseFileStall` 让单文件 jar 与 `installer-run` 黑盒阶段豁免「无新文件完成」判定，因为那里根本没有可推进的文件计数器。
3. **失败结论是单次的、不可覆盖的。** `InstallFailureText` 统一拥有所有「为什么停了」的文案，`FinishJob` 先检查 `job.IsFinished`，已交付的失败结论不被底层异常二次覆盖。
4. **失败不等于完成。** `Aggregate` 中 `Failed` 行不贡献完成度，只要本会话存在失败任务，汇总就不能到 100%；`MarkFinished` 对非 Done 状态把百分比钳到 99；UI 用 `HasFailedJobs / FailedJobsHint` 显示红色标记。
5. **JDK 发行版是可插拔的纯策略，不是第二套安装流程。** `JavaDistributionPolicy` 只负责 URL 构造、JSON 解析与准入（排除 `-fx-`/`-crac-`/`-jre`、限定 `cdn.azul.com`、要求 64 位 SHA-256 与 `size`、选最高 patch）；解析出的 `JavaPackage` 与 Temurin 共用同一条 `DownloadEngine → SHA-256 → 静默解压 → 设为默认` 链路。选择持久化在 `config.json`，并纳入未保存设置快照。

## 权衡

- 镜像优先意味着「官方线路被显式选择」时仍按顺序只走官方；不偷偷改用户策略。
- Zulu API 若不带 `include_fields=sha256_hash,size` 会缺少校验和，策略直接拒绝 —— 宁可装不了，也不装未校验的 JDK。
- Forge 官方安装器 JVM 自己发起的联网（Mojang 版本清单、processor）不受本 ADR 控制；只能把失败原因翻译成可操作提示并保留重试入口。

## 验证与已知限制

- `--unit` 149/149、`--all` 156/156；`--java-dist-smoke` 实跑 Temurin 与 Zulu 官方源均解析出带校验和的包。
- `--forge-install-smoke` 本轮两次未通过：installer jar 镜像下载与 29/29 依赖预填充都成功，但官方安装器 JVM 拉 Mojang 版本清单 / 跑 processor 时连接超时。这是外部环境阻断，不是下载链路回归；第 95/96 轮同链路曾实跑成功，本轮未改安装器运行阶段逻辑。此项留待网络恢复后复跑。

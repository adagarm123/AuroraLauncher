# ADR 索引（Architecture Decision Records）

> 每篇一页纸：背景 / 决策 / 后果 / 反例。反例是"如果不这么做会怎样"的真实事故记录，
> 不是理论推演——全部对应第 31-39 轮的实际缺陷。
> 原则汇总见 [ROADMAP「可靠性哲学」](../../ROADMAP.md)。

| ADR | 主题 | 状态 |
|-----|------|------|
| [ADR-0001](ADR-0001-version-data-single-entry.md) | 版本数据单一合法入口 | 已采纳（含防线测试） |
| [ADR-0002](ADR-0002-install-state-single-source.md) | 安装状态单一来源（Evaluate） | 已采纳（含防线测试） |
| [ADR-0003](ADR-0003-httpclient-lifecycle.md) | HttpClient 生命周期契约 | 已采纳（含防线测试） |
| [ADR-0004](ADR-0004-download-resilience.md) | 下载韧性策略（四线路竞速 + 断点失效泛化 + 缓存兜底） | 已采纳 |
| [ADR-0005](ADR-0005-diagnostics-and-redaction.md) | 诊断包与脱敏（可发送、可信任） | 已采纳（含测试） |
| [ADR-0006](ADR-0006-crash-repair-safety.md) | 崩溃自动修复的安全边界（只碰启动器自己的东西） | 已采纳（含防线测试） |
| [ADR-0007](ADR-0007-glass-and-performance.md) | 玻璃效果与性能边界（默认关闭，模糊只留在临时浮层） | 已采纳（含防线测试） |
| [ADR-0008](ADR-0008-memory-policy.md) | 内存策略（自动分配硬上限 55%、清理只碰自己） | 已采纳（含防线测试） |
| [ADR-0009](ADR-0009-instance-delete-and-reconcile.md) | 实例删除与对账（先删文件再删记录；扫描只加不删） | 已采纳（含防线测试） |
| [ADR-0010](ADR-0010-download-route-policy.md) | 下载线路策略（自动有裕度，强制单线只对核心文件严格） | 已采纳（含防线测试） |
| [ADR-0011](ADR-0011-download-residue-and-integrity.md) | 下载残留与完整性（无校验值 ≠ 完整；归属由引擎登记，可续传的先问） | 已采纳（含防线测试） |
| [ADR-0012](ADR-0012-storage-cleanup-allowlist.md) | 存储清理（只写允许清单；空壳判定必须窄；牺牲离线能力的项默认不勾） | 已采纳（含防线测试） |
| [ADR-0013](ADR-0013-settings-dirty-and-close-gate.md) | 设置脏检查与关闭闸门（快照对比；任何会丢东西的关闭路径都要确认） | 已采纳（含防线测试） |
| [ADR-0014](ADR-0014-launch-readiness-and-modpack-expectation.md) | 启动前判定加载器与整合包（预期先落盘；加载器缺失不可覆盖；运行结果必须带退出码） | 已采纳（含防线测试） |
| [ADR-0015](ADR-0015-command-reentry-guard.md) | 命令层防重入（门在命令里，异步默认开；禁止「火后不管」；文案随状态变） | 已采纳（含防线测试） |
| [ADR-0016](ADR-0016-forge-installer-launcher-profiles.md) | 给官方加载器安装器补 `launcher_profiles.json`（缺→建 / 好→不动 / 坏→备份重建） | 已采纳（含实跑验证） |
| [ADR-0017](ADR-0017-version-source-and-installer-dependencies.md) | 版本来源与安装器依赖（本地优先；第三方 id 不查官方清单；依赖预填充且成功要验真） | 已采纳（含防线测试） |
| [ADR-0018](ADR-0018-download-job-single-source.md) | 下载任务单一状态来源（补全入队、标题栏跟任务走、看门狗、导航单高亮；含已装 profile 认领） | 已采纳（含防线测试） |
| [ADR-0019](ADR-0019-progress-honesty-and-loader-aware-preflight.md) | 进度诚实与加载器感知体检（未完不成 100%、黑盒不确定态、停滞看门狗；跨加载器元数据与依赖过滤） | 已采纳（含防线测试） |
| [ADR-0020](ADR-0020-installer-resilience-and-jdk-vendors.md) | 安装器下载韧性与 JDK 发行版可插拔（镜像优先、心跳、失败状态诚实、Zulu 白名单） | 已采纳（含防线测试） |
| [ADR-0021](ADR-0021-content-state-and-uninstall-safety.md) | 内容中心状态同源与安全卸载（列表驱动文案、空状态三态、白名单 + 两次确认 + 回收站） | 已采纳（含防线测试） |
| [ADR-0022](ADR-0022-missing-dependency-auto-fix.md) | 缺失前置一键补齐（精确匹配、四道版本门、无校验值不下载、重扫才算装好、逐项降级） | 已采纳（含防线测试） |
| [ADR-0023](ADR-0023-update-progress-and-first-run-storage.md) | 更新进度可见可取消 + 首次引导下载前选择文件位置（同源进度、真取消令牌、旧暂存保护、四步引导） | 已采纳（含防线测试） |

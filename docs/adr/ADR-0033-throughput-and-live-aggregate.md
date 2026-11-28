# ADR-0033：下载速率采样与总进度实时跟随

- 状态：已采纳（含防线测试）
- 日期：2026-11-28
- 相关：ADR-0032（三源网络诊断）

## 背景

第 110 轮的诊断只显示延迟。实测中发现 GitHub 延迟最低（169 ms）却无法下载，CNB / Mojang 延迟略高但速率约 2.1 MB/s——单看延迟会误判线路质量。

下载管理页另外存在一个真实缺陷：总进度条卡在旧值不动。根因是 `RefreshDownloadAggregate` 用 `_seenJobVersion == _jobManager.Version` 提前返回，而 `DownloadJob.Report()` 每次推进度/速度时**不 bump** manager 的 Version（它只跟踪任务增删），所以运行中的任务永远无法刷新汇总。

## 决策

1. **双行诊断**：`HostDiagnostic` 增加 `BytesPerSec` / `DisplaySpeed` / `SpeedBarWidth`。延迟条（越短越好）与速率条（越长越好）分两行渲染；`NormalizeSpeedBars` 纯函数按最快源归一化，测速失败为零且文案明确"测速失败"（不是"测速中…"）。
2. **采样方式**：`MeasureThroughputAsync` 对真实资产发 GET + Range(0..512KB-1)，最多读 512 KB 或 2 秒，用实收字节/耗时算速率。目标 URL 由 `ResolveFeedUrlsAsync` 从签名清单 `latest.json` 解析（约 2.8 KB），不会因发新版而指向过期 tag；解析失败回退内置地址。诊断永不抛异常。
3. **单任务镜像 / 多任务综合**：`JobProgressPolicy.Aggregate` 明确加 `counted == 1` 分支——只有一个 in-flight 任务时直接返回该任务的进度（经 Display 钳制），避免平均值四舍五入带来的偏差；多任务仍按平均进度 + 速度和综合，Done/Failed/Canceled 不参与拉高。
4. **总进度实时刷新**：去掉 Version 早退，定时器每次 tick 都读取 `_jobManager.Snapshot()`；仅窗口隐藏时保留 1 秒节流。

## 后果

- 用户能同时看到延迟与吞吐，避免"延迟低但下不动"的误判。
- 诊断耗时上限约 4 秒（清单解析 + 并行探测 + 2 秒采样），流量上限每源 512 KB。
- 总进度与任务行保持同步；隐藏窗口时仍有节流，避免无谓的 UI 开销。

## 验证

- `--unit` 187/187、`--all` 194/194；机制防线 70→71。
- 三源实测：CNB 2.1 MB/s、Mojang 2.1 MB/s、GitHub 测速失败（国内网络常见）。
- 新增防线：单任务总进度精确镜像、多任务综合、速率条归一化、测速失败文案、刷新不被版本号冻结、测速从签名清单解析、XAML 双行绑定。

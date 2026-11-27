# ADR-0031：主源三次重试与下载界面如实显示

- 状态：已采纳（含防线测试）
- 日期：2026-11-27
- 相关：ADR-0030（CNB 主源 + GitHub 兜底）

## 背景

第 108 轮实现了 CNB 主源 + GitHub 签名兜底，但用户提出两点：
1. CNB 超时应该**重试至少三次**再切换兜底源，而不是一失败就跳。
2. 下载更新界面应该**如实说明**当前状态，而不是只显示百分比。

排查发现：下载引擎 `DownloadFileAsync` 内部已有 `maxAttempts=5` 的重试与 `onStatus` 消息（重试中 n/5、切线路、直连重试等），但：
- 更新包调用时用的是引擎默认预算，且 `UpdateService.TryDownloadFullAsync` 只调一次——主源整体失败即切兜底，语义上没有"主源重试三次"。
- `DownloadProgress.Message` 虽然被引擎填充，却被 `MainViewModel` 的进度回调忽略（只用 pct/size/speed 覆盖 `UpdateStatusText`），用户完全看不到重试。

## 决策

1. **逐条目重试预算**：`DownloadEntry` 新增 `MaxAttempts`（0 = 引擎默认 5）。引擎改用 `entry.MaxAttempts > 0 ? entry.MaxAttempts : 5`，其他下载行为不变。
2. **双源各三次**：更新清单的完整包下载，主源 CNB 与备用源 GitHub 各传 `MaxAttempts=3`；增量补丁同样 3 次。补丁失败仍回退完整包（完整包再走双源链）。
3. **主机标签**：`TryDownloadFullAsync` 新增 `label` 参数，把引擎状态消息加上「主源 CNB：」「备用源 GitHub：」前缀；补丁消息加「增量补丁：」前缀。
4. **界面如实显示**：`MainViewModel` 的进度回调改为——`p.Message` 非空时显示「{消息}（{pct}%）」，否则沿用原来的百分比+大小+速度。`LastDownloadMode` 也注明主源/备用源。
5. **版本号说明**（同轮用户询问）：`BuildInfo.CurrentRound` 是单调轮次（防降级/文档基线）；Git tag `v1.12.x` 每轮 +1（第 108 轮 = v1.12.22）；`Version = 0.109.0` 只是轮次的三段式展示串。CNB 主源**支持增量更新**——`patchUrl` 也指向 CNB 直链，实测免登录 200。

## 后果

- 主源偶发超时可自愈，减少不必要的跨源切换；最坏情况总尝试次数由 5 变为 6（主 3 + 备 3），但每次尝试有 25 s idle 超时与退避，仍在可接受范围。
- 状态栏在重试期间会短暂显示文字状态而非纯百分比——这是有意的透明化。
- 手动触发的 `DownloadProgress` 只带 Message 会造成进度条归零闪烁，因此没有额外插入"正在切换"通知；切源后的第一条真实回调即带备用源标签。

## 验证

- `--unit` 185/185、`--all` 192/192；机制防线 68→69。
- 新增防线：`MaxAttempts` 接线、双源三次预算、补丁标签与预算、UI 显示 `p.Message`。
- 端到端待用户实测：让 CNB 不可达时观察下载窗口是否显示重试计数与切源提示。

# ADR-0025 启动拦截可见性与恢复下载线路归一

- 状态：已采纳（含静态防线与离线单测）
- 轮次：第 103 轮（2026-11-22）
- 触发：用户截图「点启动游戏无反应」+「下载/补齐反复失败，26.1.2.jar 一直报 bmclapi.bangbang93.com 连接失败」。

## 背景

截图 1：账户为「未登录」，`LaunchGuardPolicy` 正确返回 `NoAccount`，但 UI 只写入 `Status` 一行文字，而页面顶部正压着红色下载失败横幅——用户看不到任何变化，描述为「点了没反应」。同时拦截发生在 `NeedsRepair` 之前，首页「未安装所选实例时会自动触发下载安装」的承诺没有兑现。

截图 2：下载管理里恢复任务显示 25.9/36.3 MB（71%），重试后仍在 99% 失败。journal 中的 URL 指向退役域名 `bmclapi.bangbang93.com`；`LauncherConfig.ToOfficialUrl` 只识别 `bmclapi2.bangbang93.com` / `bmclapi.cn`，且把 `/v1/packages/...` 当成「模糊路径不猜测」，于是 `DownloadEngine` 的跨线路切换拿不到 alternate URL，重试永远在同一死域名上失败。

## 决策

1. **所有启动拦截都可见。** `LaunchAsync` 对 NoInstances / NoSelection / NoAccount 分别弹 toast；`Status` 仍保留但不再作为唯一反馈。
2. **账户条件不阻塞文件补齐。** `NoAccount` 分支里仍调用 `_svc.NeedsRepair` 并启动 `RunRepairJobAsync`（仅当 `!IsBusy`）；补齐完成后用户添加账户即可再启动。登录仍是启动游戏的必要条件，但下载文件不需要登录。
3. **退役镜像域名可反解。** `ToOfficialUrl` 的域名列表加入 `bmclapi.bangbang93.com`，并把 `/v1/packages/<hash>/<file>` 明确映射回 `piston-data.mojang.com`——client jar 不是模糊路径；`/maven/...` 仍只在组路径可判定时映射。`HealLegacyMirrorHost` 同时修正配置中的该域名。
4. **恢复下载先归一再选线。** 新增 `ResumeDownloadPolicy.ChooseUrl(journalUrl, policy, preferMirror)`：先 `ToOfficialUrl` 归一，再按当前 `DownloadPolicy` / `NetworkProbe.Preference` 选官方或**当前**镜像。`ResumePendingDownloadsAsync` 用它构造 `DownloadEntry`，不再把旧 URL 原样塞回队列。

## 权衡

- 未登录时也下载文件会消耗流量，但这正是首页已承诺的行为；相比「看起来没反应」，先补齐更有用。
- 把 `/v1/packages` 视为可反解路径依赖 BMCLAPI 的路径约定；如果未来镜像改变布局，`ToOfficialUrl` 会返回原 URL，最坏情况与修复前相同。
- 强制镜像用户仍被限制在镜像线路；这是明确的设置选择，不会静默越过。

## 验证与已知限制

- `--unit` 173/173、`--all` 180/180；新增 3 项覆盖旧域名反解、恢复策略与启动反馈 Guard。
- **未做真机下载验证**：截图里的那次失败任务需要用户点「重试」确认已切到可用线路；修复基于 URL 映射与线路选择的代码级验证。

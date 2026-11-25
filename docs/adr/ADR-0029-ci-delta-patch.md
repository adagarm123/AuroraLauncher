# ADR-0029：修复 CI 从未产出增量补丁的问题

- 状态：已采纳（含防线测试）
- 日期：2026-11-25
- 相关：ADR-0014（增量补丁）、ADR-0027/0028

## 背景

用户发现：即使只相差一个版本（104→105、105→106），自动更新仍要重新下载 65 MB 完整 EXE，而不是几百 KB 的补丁包。

排查两端后确认客户端逻辑完全正确：`UpdateService.ShouldUsePatch` 在清单带 `patchUrl`、`PatchFromRound == BuildInfo.CurrentRound`、签名有效且补丁小于完整包 70% 时，会优先走 `TryDownloadAndApplyPatchAsync`；`BinaryDelta` 格式（AURDLT01）也早已实现并测过。

**根因在 CI**：`.github/workflows/release.yml` 每次是干净 checkout，`publish/` 与 `artifacts/prev/` 都不存在。`scripts/publish.ps1` 依赖 `publish/.last-round` + `artifacts/prev/AuroraLauncher.exe` 判断是否产出补丁——两者都缺失，于是永远走「无上一版 → 仅完整包」分支，`sign-release.ps1` 也就不会往 `latest.json` 写 patch 字段。本地手动发布有这两个文件，所以本地清单带补丁，云端清单从不带。

## 决策

在 workflow 的 `Publish EXE` 之前新增 `Fetch previous release EXE for delta patch` 步骤（pwsh）：

1. `gh api repos/$repo/releases?per_page=10`，选 tag ≠ 当前 `github.ref_name`、非 draft/prerelease、且含 `AuroraLauncher.exe` 资产的最新 release。
2. `gh release download <prevTag> --pattern AuroraLauncher.exe --dir artifacts/prev` 和 `--pattern build-info.txt`，拿到上一版的**原始字节**（补丁格式要求 oldSize 精确匹配）。
3. 从上一版 `build-info.txt` 解析 `round : N`，用 `[System.IO.File]::WriteAllText` 写入 `publish/.last-round`（无 BOM）。
4. 找不到上一版或读不出 round 时，删除已下载的 EXE、警告并退出 0 → 完整包发布，不让发布流程失败。

之后完全复用既有链路：`publish.ps1`（`--make-patch` 内含 70% 阈值闸门，超阈值自动删补丁并返回 3）→ `sign-release.ps1`（补丁独立签名 `Round|patchUrl|patchSha256|patchSize`）→ Release 资产上传 + Pages 部署。

**未改**：客户端判定逻辑、补丁格式、签名载荷（保持旧客户端兼容）。

## 后果

- 相邻版本升级从 65 MB 降为几百 KB～几 MB（本轮 105→106 实测补丁约 610 KB，不到 1%）。
- 补丁只匹配一个 `patchFromRound`：落后多版的客户端仍下载完整包。这是刻意取舍——多版补丁链需要客户端逐级升级，复杂度和失败面都更大，当前不做。
- CI 首次发布（无任何历史 Release）自动退化为完整包，行为与之前一致。
- 上一版 Release 被删除/资产缺失时同样退化，不阻塞发布。

## 验证

- `--unit` 183/183、`--all` 190/190；机制防线 66→67。
- 新增防线：workflow 必须含拉取步骤、`artifacts\prev`、写 `.last-round`；用真实 RSA 签名清单验证「PatchFromRound=当前轮 → 用补丁；落后一轮 → 完整包」。
- 端到端核验以本轮发布为准：Pages `latest.json` 应带 `patchUrl` / `patchSha256` / `patchFromRound=106`，Release 应含 `AuroraLauncher.patch`，补丁签名用代码内公钥验过。

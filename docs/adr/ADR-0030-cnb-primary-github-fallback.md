# ADR-0030：CNB 作为主更新源、GitHub 签名兜底

- 状态：已采纳（含防线测试）
- 日期：2026-11-27
- 相关：ADR-0029（CI 增量补丁）

## 背景

GitHub Release 在国内访问慢。用户提出用腾讯 CNB（cnb.cool）做主更新源——HMCL 已把 CNB 用作主更新源。实测确认：

- `https://cnb.cool/{owner}/{repo}/-/releases/download/{tag}/{file}` 免登录、无 `share` 参数，跟随 302 到 CDN 后 HTTP 200 + 正确 Content-Length，长期有效。
- CNB **仓库文件 raw 直链需 token**，且没有 `releases/latest/download/{file}` 固定直链——所以 CNB 只能做"下载源"，不能做"清单发现源"。
- CNB 提供官方 OpenAPI（`api.cnb.cool`，Bearer token）用于创建 Release 和上传附件；调研文本里提到的 `git-cnb` / `cnbcool/attachments` 均不存在或文档 404，未采用。

## 决策

1. **清单发现地址不变**：`latest.json` 继续部署在 `https://adagarm123.github.io/AuroraLauncher/latest.json`（固定地址）。CNB 只承载下载资产。
2. **CNB 仓库**：`AuroraLauncher-2026/AuroraLauncher`（公开）。用 WebIDE 的 `cnb init-from https://github.com/adagarm123/AuroraLauncher.git` 一次性迁入代码与 TAG。CNB 设置里**没有**持续同步选项；持续同步由 CI 实现。
3. **清单双 URL**：
   - `url` = CNB 直链（主，CI 里按 tag 拼出）
   - `fallbackUrl` = GitHub 直链（兜底）
   - `fallbackSignature` = RSA 签名 `Round|FallbackUrl|Sha256|Size`
   - **`SignedPayload` 保持不变**（`Round|Version|Url|Sha256|Size`），因此旧客户端验签不受影响，只是 `Url` 值变成 CNB。
4. **CI**：`sign-release.ps1` 新增 `-FallbackUrl`；GitHub Release 上传后新增 `Mirror assets to CNB Release` 步骤，调用 `scripts/publish-to-cnb.ps1`，用官方 API 把 EXE/patch/build-info/latest.json 四件资产镜像到 CNB（创建或复用 Release、预签名 PUT、verify 确认、同名覆盖、幂等）。缺 Secrets 时跳过，退化为 GitHub-only。
5. **客户端**：`UpdateManifest` 加 `FallbackUrl`/`FallbackSignature`；`TryDownloadFullAsync` 单源下载+流式验 SHA-256，主源失败时只有 `VerifyFallbackSignature` 通过才切备用源重试一次；两个源都失败才报错。主源 hash 不匹配不会静默放行到备用源（避免"坏主源→未校验备用源"的绕过）。
6. **Secrets**：GitHub 仓库新增 `CNB_TOKEN`、`CNB_REPO`，不进版本库。

## 后果

- 国内更新下载走 CNB，显著提速；CNB 不可用时自动回退 GitHub，可靠性不降。
- 备用 URL 独立签名，防止篡改清单把兜底指向恶意主机。
- 旧的 `cnb-init-from` 只是一次性迁移：以后 GitHub 上新 push 的 commit **不会**自动出现在 CNB 的 git 历史里；但**发布资产由 CI 每次镜像**，这才是更新链路依赖的部分，不受影响。
- `fallbackUrl` 为空时：验签视为通过（向后兼容），客户端不会切换，行为与旧版一致。
- 本地手动发布不传 `-FallbackUrl` 时，清单与旧版完全相同。

## 验证

- `--unit` 184/184、`--all` 191/191；机制防线 67→68。
- `scripts/publish-to-cnb.ps1` 本地实跑 `v1.12.21`：4 件资产上传成功，CNB 直链免登录 200 可读。
- 新增防线：备用源签名正反例、篡改 fallbackUrl 必须验签失败、SignedPayload 不含 fallback、workflow 双源接线、镜像脚本走官方流程。

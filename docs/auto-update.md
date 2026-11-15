# 自动更新：作者发布流程与原理（round-57 建立，round-58 三态化，round-61 换装可见化）

## 用户侧发生了什么（round-58 / round-61）

设置 → 更新 提供三态开关（`UpdateMode`），默认是第 2 种：

| 模式 | 启动检查 | 发现新版本时 |
|------|----------|--------------|
| 1 自动更新 | ✅ 每次启动 | **不弹窗**，后台自动下载 + SHA-256 校验 → 下次启动显示安装进度窗口并换装重启 |
| 2 有更新时提醒我，但不自动更新（**默认**） | ✅ 每次启动 | **弹窗提醒**，只有「立即更新」「关闭」两个选项；点立即更新才下载，下载完自动重启换装；点关闭则本次不更新 |
| 3 不获取更新 | ❌ 不联网检查 | 无提示；**手动「检查更新」仍可用**（用户主动发起，任何模式都会弹窗给出结果） |

通用流程：启动检查有 8 秒超时，失败静默跳过绝不打扰；弹窗只在主窗口就绪后（启动延迟 1.2 秒）出现。下载阶段写入 `存储目录/update/AuroraLauncher.new.exe` 并做 SHA-256 校验。

**换装阶段（round-61，用户看到什么）**：启动器把已校验的新包复制成 `update/updater.exe`，以 `--apply-update|<pid>|<目标>|<新包>|<版本信息>` 启动这份临时副本，随后主进程退出。副本不创建主窗口，而是显示一个置顶、无边框的小窗口「正在应用更新并重启…」，阶段文字与进度条依次报告：等待旧版本退出 → 按 `exe-sha256` 二次校验更新包 → 替换文件（被占用时显示重试次数，最多 10 次）→ 写入 build-info.txt → 重新启动启动器。任一步失败都会显示原因和「重试 / 关闭」按钮，不再黑屏无提示。运行中的 EXE 在 Windows 上被锁定，所以换装进程**必须是另一份文件**；旧的隐藏 cmd 脚本只在可见更新器启动失败时兜底，临时副本在下次正常启动时清理。

**版本列表「一键更新」（round-62）**：游戏下载页的版本列表顶部有一张常驻卡片，显示当前启动器版本与「一键更新」按钮。启动检查发现新版后（即使弹窗被关闭，或模式 1 后台静默下载），卡片会明确写出「检测到新版启动器（第 N 轮）」；点击直接进两按钮更新弹窗 → 下载 → 可见换装窗口，无需先去设置页。从未检查过或已是最新版时点击会先检查并明确回报结果；待安装的更新包也会在卡片上提示。判定由 `UpdateBannerPolicy` 集中（轮次不高于当前一律不算更新，防降级）。

手动「检查更新」：检查阶段进度条显示不确定进度，下载阶段按字节数显示百分比，状态文字同时给出**已下载 / 总大小与实时速度**（如「正在下载更新 42%（26.5 / 63.0 MB · 2.4 MB/s）」），并同步到主窗口状态栏；自动下载时轻提示会告知「设置 → 更新 可查看进度」。下载完成后设置页出现「立即重启并应用更新」按钮，点击即进入上面的可见换装窗口。与内容中心搜索进度条同款做法。

## 决策逻辑单一来源

`UpdatePolicy`（AuroraCore 纯逻辑）集中判定「是否启动检查」与「发现更新后做什么」，VM 不再散落布尔/字符串比较；单测 #32 锁定三态语义。旧 config.json 中显式关闭过 `AutoCheckUpdates` 的用户，首次加载会一次性迁移到「不获取更新」。

## 安全模型（对应 ROADMAP「自动更新 + 签名验证」待办）

| 威胁 | 对策 |
|------|------|
| 清单/文件被篡改（含中间人） | latest.json 携带 **RSA-4096 签名**（覆盖 round|version|url|sha256|size），公钥**内嵌**在 UpdateService 中；验签失败静默忽略 |
| 被诱导降级到旧版本 | 只有 **round 严格大于** 当前构建（BuildInfo.CurrentRound）的清单才算更新 |
| 下载文件损坏 | 引擎断点续传 + 清单 SHA-256 复核，不符即删除重来 |
| 通道隔离 | 当前单一 stable 通道；下载 URL 只来自验签通过的清单 |

## 作者发布流程（每次发版，GitHub Actions 全自动）

推送 tag 后 `.github/workflows/release.yml` 在**同一次 workflow** 内原子化完成：

1. Run Unit Tests（`--unit`）；
2. `scripts/publish.ps1` 编译出 `publish/AuroraLauncher.exe` + `build-info.txt`；
3. `scripts/sign-release.ps1 -DownloadUrl <本次 tag 的 Release 下载地址>`（私钥来自 `secrets.RSA_PRIVATE_KEY`，签名的就是第 2 步刚生成的那个 EXE）→ 生成 `publish/latest.json`；
4. `softprops/action-gh-release@v2` 一次性上传 EXE + build-info.txt + latest.json；
5. `actions/deploy-pages@v4` 把 `publish/` 部署到 Pages（更新源 `https://adagarm123.github.io/AuroraLauncher/latest.json`，已内嵌于 `UpdateService.DefaultFeedUrl`）。

云端编译 / 签名 / 上传 / 部署同步完成，Actions 绿勾后用户才访问得到新清单，不存在「只传了清单没传 EXE」的错配。**作者无需手动上传任何文件。**

发版前本地仍必做（项目约定）：

- `BuildInfo.CurrentRound` **+1**（用户端识别新版本的核心）；
- `HelpContent.Changelog()` 置顶本轮说明 + `LatestChangelogRound` 同步 bump；
- README → CHANGELOG 轮动，README / ROADMAP / TEST_RESULTS 三份基线同步；
- `scripts/publish.ps1` 出一份可直接运行的本地 EXE。

签名密钥对（一次性生成，已就位）：`scripts/signing/private_key.xml`（**绝对保密**，泄露 = 任何人可伪造更新）+ `public_key.xml`（已内嵌进 UpdateService）。仅当离线自签 / 调试更新源时才需要手动运行 `sign-release.ps1`。

## 关键配置

| 位置 | 项 | 说明 |
|------|-----|------|
| config.json / 设置页 | `UpdateMode` | 三态：1 自动更新 / 2 提醒但不自动更新（默认）/ 3 不获取更新 |
| config.json / 设置页 | `UpdateFeedUrl` | 覆盖内置更新源（留空则用 `UpdateService.DefaultFeedUrl`） |
| config.json | `AutoCheckUpdates` | 旧布尔开关，round-58 起仅用于一次性迁移，不再写入 |
| config.json | `SlowTransferKBsPerSec` | 下载慢速看门狗阈值（与更新包下载共用引擎） |

## 文件清单

- `scripts/signing/private_key.xml` — 签名私钥（保密）
- `scripts/signing/public_key.xml` — 公钥备份（已内嵌 UpdateService.PublicKeyXml）
- `scripts/sign-release.ps1` — 生成签名的 latest.json
- `publish/latest.json` — 更新清单（与 EXE 一起上传）

## 增量更新（第 74 轮）

一次完整包 65 MB；多数轮次只改几个 DLL，因此发布时同时生成二进制补丁。

**发布侧（本地）**
1. `scripts/publish.ps1` 会先把当前 `publish/AuroraLauncher.exe` 归档到 `artifacts/prev/`，并把当前轮次写进 `publish/.last-round`；
2. 编译新 EXE 后用 `--make-patch` 生成 `publish/AuroraLauncher.patch`，并写 `patch-info.txt`（来源轮次 / SHA-256 / 体积）；
3. **补丁超过完整包 70% 就自动丢弃**（不划算，且多一条失败路径）；
4. `scripts/sign-release.ps1` 读取 `patch-info.txt`，在 `latest.json` 里写入 `patchUrl / patchSha256 / patchSize / patchFromRound`，并对 `round|patchUrl|patchSha256|patchSize` **单独签名**。

**为什么单独签名**：`SignedPayload`（`round|version|url|sha256|size`）从第 72 轮起未变。若把补丁字段拼进去，所有旧版本客户端算出的载荷就会与签名不符，从而**拒绝整份清单**——等于用一个优化把所有存量用户锁在旧版。

**客户端**
- 只有 `patchFromRound == 当前轮次`、补丁签名有效、且补丁确实更小时才走增量；
- 补丁下载后先校验自身 SHA-256，再用 `BinaryDelta.Apply` 与**正在运行的 EXE** 合并；
- 合并结果必须等于清单里的 `sha256`，否则日志记一条并**自动回退完整下载**（补丁永远只是优化，不是唯一入口）。

**CI 注意**：GitHub Actions 的新 runner 上没有上一版 EXE，因此 CI 发布的是"仅完整包"。需要补丁时，在本机保留 `artifacts/prev/` 后发布，或后续把 `artifacts/prev/` 做成缓存。

# 自动更新：作者发布流程与原理（round-57 建立，round-58 三态化）

## 用户侧发生了什么（round-58）

设置 → 更新 提供三态开关（`UpdateMode`），默认是第 2 种：

| 模式 | 启动检查 | 发现新版本时 |
|------|----------|--------------|
| 1 自动更新 | ✅ 每次启动 | **不弹窗**，后台自动下载 + SHA-256 校验 → 下次启动自动换装重启 |
| 2 有更新时提醒我，但不自动更新（**默认**） | ✅ 每次启动 | **弹窗提醒**，只有「立即更新」「关闭」两个选项；点立即更新才下载，下载完自动重启换装；点关闭则本次不更新 |
| 3 不获取更新 | ❌ 不联网检查 | 无提示；**手动「检查更新」仍可用**（用户主动发起，任何模式都会弹窗给出结果） |

通用流程：启动检查有 8 秒超时，失败静默跳过绝不打扰；弹窗只在主窗口就绪后（启动延迟 1.2 秒）出现。下载阶段写入 `存储目录/update/AuroraLauncher.new.exe`，校验通过后由分离的 cmd 脚本在旧进程退出后覆盖 EXE 并重启（带 10 次重试）。

手动「检查更新」：检查阶段进度条显示不确定进度，下载阶段按字节数显示百分比，旁边有状态文字（已是最新版本 / 暂时无法连接更新源 / 签名校验失败 / 正在下载更新 X% 等）。与内容中心搜索进度条同款做法。

## 决策逻辑单一来源

`UpdatePolicy`（AuroraCore 纯逻辑）集中判定「是否启动检查」与「发现更新后做什么」，VM 不再散落布尔/字符串比较；单测 #32 锁定三态语义。旧 config.json 中显式关闭过 `AutoCheckUpdates` 的用户，首次加载会一次性迁移到「不获取更新」。

## 安全模型（对应 ROADMAP「自动更新 + 签名验证」待办）

| 威胁 | 对策 |
|------|------|
| 清单/文件被篡改（含中间人） | latest.json 携带 **RSA-4096 签名**（覆盖 round|version|url|sha256|size），公钥**内嵌**在 UpdateService 中；验签失败静默忽略 |
| 被诱导降级到旧版本 | 只有 **round 严格大于** 当前构建（BuildInfo.CurrentRound）的清单才算更新 |
| 下载文件损坏 | 引擎断点续传 + 清单 SHA-256 复核，不符即删除重来 |
| 通道隔离 | 当前单一 stable 通道；下载 URL 只来自验签通过的清单 |

## 作者发布流程（每次发版）

1. 改完代码：更新 `HelpContent.Changelog()`（说人话，模板见
   `docs/release-notes-template.md`）并 bump `AuroraCore/BuildInfo.cs` 的 `CurrentRound`。
2. `powershell -File scripts/publish.ps1` —— 单测前置 + 发布 EXE + build-info.txt。
3. 签名清单：
   `powershell -File scripts/sign-release.ps1 -DownloadUrl "https://你的托管地址/AuroraLauncher.exe" [-NotesFile 更新说明.txt]`
   → 生成 `publish/latest.json`（Round 自动从 BuildInfo.cs 读取）。
4. 上传**两个文件**到托管地址：`AuroraLauncher.exe` + `latest.json`。
   托管要求：静态 HTTPS 可直连即可（Gitee Releases / GitHub Releases +
   ghproxy 类加速 / 对象存储 OSS/COS / 自有服务器）。把 latest.json 的地址
   填进 `UpdateService.DefaultFeedUrl`（一行常量），重新 publish 一次即可。
5. （一次性）生成签名密钥对：`scripts/signing/private_key.xml`（**绝对保密**，
   泄露 = 任何人可伪造更新）+ `public_key.xml`（已内嵌进 UpdateService）。

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

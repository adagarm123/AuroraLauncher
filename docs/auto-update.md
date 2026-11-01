# 自动更新：作者发布流程与原理（round-57）

## 用户侧发生了什么

1. 启动器每次开启**静默检查更新**（8 秒超时，失败即静默跳过，绝不打扰）。
2. 发现新版本 → 非阻塞 toast「发现新版本（第 X 轮），正在后台下载」→ 后台下载到
   `存储目录/update/AuroraLauncher.new.exe`，完成后 **SHA-256 校验**。
3. 校验通过 → toast「新版本已就绪：重启启动器即可自动完成更新」。
4. 用户下次启动时：启动器检测到 staged 更新 → 弹出一个等待 2 秒的隐藏脚本
   （旧进程退出后把新 EXE 覆盖到位）→ 自动重启，即已是新版本。全程用户只做一件事：**重启**。

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
| config.json | `UpdateFeedUrl` | 覆盖内置更新源（测试新清单用；普通用户留空） |
| config.json | `AutoCheckUpdates` | 启动时自动检查更新（默认 true） |
| config.json | `SlowTransferKBsPerSec` | 下载慢速看门狗阈值（与更新包下载共用引擎） |

## 文件清单

- `scripts/signing/private_key.xml` — 签名私钥（保密）
- `scripts/signing/public_key.xml` — 公钥备份（已内嵌 UpdateService.PublicKeyXml）
- `scripts/sign-release.ps1` — 生成签名的 latest.json
- `publish/latest.json` — 更新清单（与 EXE 一起上传）

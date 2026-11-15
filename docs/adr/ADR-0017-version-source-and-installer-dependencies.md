# ADR-0017：版本来源与官方安装器依赖

- 日期：2026-11-15（第 95 轮）
- 状态：已采纳（含 2 条静态防线 + 6 项单测）
- 关联：ADR-0014（启动闸门）、ADR-0016（launcher_profiles.json）

## 背景

用户提供了一份 630 行全链路日志，同一台机器上先后出现两个故障：

1. `11:44:53` Forge 官方安装器自己下载依赖失败：
   `These libraries failed to download. Try again. com.google.code.findbugs:jsr305:3.0.2`
   → `Installer exited with code 1`。安装器用 JVM 自带的 HTTP 客户端访问
   `maven.creeperhost.net`，不经过启动器的镜像/重试线路；同期日志里有镜像 502、
   官方与镜像来回切换，判定为网络波动。
2. `11:48:34` 点启动时抛
   `InvalidOperationException: Version 'forge-1.20.1-47.4.22' not found in manifest.`
   堆栈是 `LaunchAsync → RepairAsync → GameInstaller.InstallAsync →
   VersionResolver.ResolveRecursive → GetVersionJsonRawAsync → GetEntryAsync`。

诊断里三处需要修正：

- `launcher_profiles.json` 已由 ADR-0016 自动补齐（日志 `10:59` 的旧错误在
  `11:42` 换到新 EXE 后消失），不需要用户手动创建。
- Java 25 不是根因：第 94 轮在同一台机器上用 Java 25 实跑同一版本安装成功，
  这次 `trove` 也下载并校验通过，只有 `jsr305` 失败。
- `VersionResolver.ResolveRecursive` **本来就是本地优先**。真正的问题是实例里
  存了 `forge-1.20.1-47.4.22` —— 这是 `ResolveMavenLoaderAsync` 从下载地址
  推导出的 **versionIdHint**，被 `result.VersionId = detected ?? versionIdHint`
  当成「已安装版本」写回了实例。磁盘上没有对应 profile，于是本地优先落空，
  回落到 Mojang 官方清单，而第三方加载器 id 永远不可能在清单里。

## 决定

1. **版本来源单一策略**：新增纯策略 `VersionSourcePolicy`。
   本地 `versions/<id>/<id>.json` 存在 → `Local`（零网络）；不存在且是
   第三方加载器 id → `LoaderProfileMissing`（给出可操作的补全提示，
   **不发任何官方清单请求**）；其余 → `Official`。
   识别覆盖前缀 `forge-` / `neoforge-` / `fabric-loader-` / `quilt-loader-`
   与中缀 `-forge-` / `-neoforge-` / `-fabric-` / `-quilt-`。
2. **在正确的位置落地本地优先**：`VersionManifestService.GetVersionJsonRawAsync`
   先读本地文件；`VersionResolver` 里损坏的本地 loader profile 不再回落清单；
   `GameInstaller.InstallAsync` 入口挡住「没有本地 profile 的 loader id」；
   `RepairAsync` 在把它交给 GameInstaller 前再拦一道。
   不把本地逻辑塞进 `GetEntryAsync` —— 它返回的是 `VersionManifestEntry`，
   与本地版本 JSON 不是同一个模型。
3. **已安装判定必须验内容**：`IsVersionJsonInstalled` 从「文件 >128 字节」
   改为「合法 JSON 且含非空 `mainClass`」（只读原始文档，不反序列化
   `VersionJson`，遵守既有的模型入口 Guard）。上次安装中断留下的空壳不再
   骗过启动闸门。
4. **安装器成功必须验真**：`ModLoaderInstaller` 不再把 hint 当结果，
   必须存在 `versions/<id>/<id>.json` 才算成功；否则明确报错，绝不把假 id
   写回实例。
5. **依赖预填充（可选、容错）**：跑安装器 JVM 之前读 installer jar 内的
   `installer_profile.json` / `version.json`，把缺失库用启动器的
   `DownloadEngine` 补到 `gameRoot/libraries/`。候选顺序：BMCLAPI maven 镜像
   → Maven Central → 安装器原始地址，三轮，全部 `Required=false`；
   解析失败或全部下载失败都只记日志，不阻断安装器自行重试。
   解析是纯函数（含路径穿越防护），可离线单测。
6. **失败原因翻译**：`These libraries failed to download` 点名具体 maven
   坐标并说明下一步，不再把六行 maven 输出丢给用户。

## 不做的事

- 不自动重跑安装器：已有 15 分钟总超时与用户可取消机制，静默重试会把
  「网络坏了」变成「卡住几分钟」，不如给明确提示让用户决定。
- 不改 `VersionResolver` 的本地优先原则，只补上它缺失的失败分支。
- 不让预填充决定安装成败：安装器自己的重试仍是最后一道，避免我们的镜像
  覆盖不全时反而把可成功的安装挡住。

## 后果与风险

- 第三方加载器 id 缺失时用户看到的是「加载器没装好，请补全」而不是
  「not found in manifest」；日志里不会再出现对这类 id 的官方清单请求。
- 预填充会额外产生一批小 jar 的 HTTP 请求；只在文件不存在时发起，
  且三轮候选在成功即停，正常情况下增量很小。
- BMCLAPI 的 `/maven/` 不保证覆盖所有通用库，所以必须有 Maven Central 兜底；
  两者都不通时退回安装器原地址并如实记录。
- 仍需真机验证：网络波动下预填充能否稳定补齐 `jsr305`，以及 NeoForge
  installer jar 的清单结构（与 Forge 共用同一条通道，本轮只做纯逻辑验证）。

## 机制防线

- `Guard: 本地版本 JSON 优先于官方清单，第三方 id 不得发起清单查询（第 95 轮）`
  静态锁定「本地 → 第三方守卫 → 官方清单」的源码顺序，并检查
  `VersionResolver` / `GameInstaller` / `RepairAsync` 三处拦截点。
- `Guard: 安装器运行前必须预填充依赖，且成功必须校验真实 profile 文件（第 95 轮）`
  锁定 `PrefillInstallerLibrariesAsync` 早于 `RunJavaAsync`、`Required = false`、
  成功判定必须 `File.Exists(versions/<id>/<id>.json)`，且禁止
  `result.VersionId = detected ?? versionIdHint` 回归。

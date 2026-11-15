# ADR-0016：给官方加载器安装器补 `launcher_profiles.json`

- 状态：已采纳（第 94 轮，含防线测试与实跑验证）
- 日期：2026-11-15
- 相关：[ADR-0014](ADR-0014-launch-readiness-and-modpack-expectation.md)（启动闸门发现了「加载器从未装成」）

## 背景

用户导入 Forge 整合包，卡在「安装 Forge 加载器」，日志：

```
Installer exited with code 1. 输出末尾: … Target Directory: C:\Users\…\AuroraLauncher
| There is no Minecraft launcher profile in "C:\Users\…\AuroraLauncher",
  you need to run the launcher first! | There was an error during installation
```

Forge / NeoForge 的官方安装器启动后先确认目标目录「像一个 Minecraft 安装目录」，判断依据是目录里存在 `launcher_profiles.json`。我们用自定义实例结构（`instances/instances.json` + 每实例 `.aurora-instance.json`），数据根从来没有这个文件 —— 于是安装器在第一步就以 1 退出，加载器从未装成。

这也是上一轮「整合包只装到原版」的另一半原因：启动闸门修好了「假装完整」，但真正让加载器装上的链路仍然断在这里。

## 决定

### 1. 安装前补齐，而不是让用户手动建

新增 `LauncherProfiles.Ensure(gameRoot)`，在**启动安装器之前**调用：

| 状态 | 处理 |
|---|---|
| 文件不存在 | 写一个最小但结构完整的文件 |
| 存在且有非空 `profiles` | **一个字都不改** |
| 存在但不可用（空文件 / `{}` / 截断） | 先备份 `.bak`，再重建 |

写入用「临时文件 + 移动」，保证安装器不会读到半截 JSON。任何 IO 异常都被吞掉（目录只读时让安装器自己报错，比从安装流程里抛出去更好定位）。

### 2. 只补安装器真正需要的字段

`profiles`（非空）+ `selectedProfile` 是安装器检查的部分；同时带上 `clientToken`、`authenticationDatabase`、`launcherVersion`，避免别的工具（PCL/HMCL 互操作、打包工具）拒收。

### 3. 失败原因翻译

`LauncherProfiles.Explain(tail)` 认出三类：缺 profile / Java 版本不符 / 安装器自己断网。认不出就返回 null（不瞎翻译），原始末尾仍然保留 —— 用户 paste 给开发者时还需要它。

### 4. 目标目录一律用调用方传入的 `gameRoot`

原来参数里拼的是 `LauncherConfig.Instance.RootDirectory`，而安装与检测用的是 `gameRoot`。两者在正式运行时相同，但便携模式、测试、或任何「装到别的目录」的调用会写错地方。现在四处（Forge/NeoForge/Fabric/Quilt）统一用参数。

## 被否决的方案

- **让用户手动放文件**：用户已经手动试过，但这不该是产品行为。
- **把实例目录做成「标准 .minecraft」**：数据根里其实是共享的 `versions/`、`libraries/`、`assets/`，形态本来就接近官方；改实例结构成本极高且会破坏既有实例。
- **只吞掉安装器错误、改用 Fabric**：把用户的整合包换成别的加载器跑不起来。

## 证据

隔离目录里只放 `versions/1.20.1/1.20.1.json`（安装器要它打补丁）+ 安装器本身：

```
补文件之前：exit=1  "There is no Minecraft launcher profile in …"
用启动器自己的代码补（--profiles-ensure）：created=True hasProfiles=True
补文件之后：exit=0  "Successfully installed client into launcher."
                    versions/1.20.1-forge-47.4.22 出现
```

再走**启动器自己的安装流程**（`--forge-install-smoke`）：Java 25、180 秒、`VersionId=1.20.1-forge-47.4.22`、版本 JSON 存在。

顺带证伪了一个猜测：这次失败**不是** Java 版本问题 —— 同一条链在 Java 25 上装成功了（1.20.1 通常推荐 17，所以仍保留一条预警日志）。

## 后果

- 数据根会多一个 `launcher_profiles.json`。它是官方格式的空壳（只有一个 `(Default)` profile），官方启动器读到也不会出事。
- NeoForge 走同一条通道，但我只实测了 Forge 1.20.1；Fabric/Quilt 走直装路径，不需要这个文件。
- 用户之前失败的实例需要重新导入或点「补全文件」；旧的整合包预期没有清单（第 92 轮之前），模组仍需重新导入才能核对。

## 防线

- `#123` 三态（缺→建 / 好→不动 / 坏→备份重建）+ 判定函数 + 翻译函数
- `#124` 源码守卫：`Ensure(gameRoot)` 必须在 `RunJavaAsync` 之前、全文件只允许一处调用
- `--forge-install-smoke`（需网络，不进 CI）：真实安装一遍并断言 profile 文件、`VersionId` 与版本 JSON

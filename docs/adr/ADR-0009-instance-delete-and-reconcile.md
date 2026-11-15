# ADR-0009：实例删除与对账——先删文件、再删记录，扫描只加不删

- 状态：已采纳（含防线测试）
- 日期：2026-11-12
- 关联：第 31 轮（实例元数据单一写入方）、第 32 轮（Evaluate 单一状态来源）、第 84 轮（本 ADR）

## 背景

用户报告两个看似无关的问题：

1. 点「新建实例」冒出一个 `1.21.1 (Vanilla)`，点了启动就失败。
2. 删了实例，列表里没了，硬盘上几十 GB 还在。

第三个问题是追查时发现的、更严重的：启动器读的数据根变成了 `%TEMP%\aurora-crash-smoke-xxxx`，实例列表只剩一个空壳。

## 决定

### 1. 实例只在「文件真的存在」时诞生

`CreateInstance()` 曾直接 `Instances.Create("1.21.1 (Vanilla)", …)`。版本未安装 → 一个没有 jar/libraries 的记录，永远无法启动。
现在它只做 `Navigate("Download")`。实例产生的唯一入口是**安装完成**或**导入**。

### 2. 删除 = 先删文件夹（并验证），再删元数据

```
DeleteChecked(id):
  folder = instances/<id>
  若存在 → 3 次尝试 / 每次 400ms / 尝试前清只读属性
         → Directory.Delete(recursive)
         → 若目录仍存在 → 返回 FilesLocked（记录不动）
  只有目录确实不存在 → 从 instances.json 移除 + GC 未引用版本
```

旧实现是「删目录 → 删记录」一条直线，异常在任何地方抛出都会让流程停在半路；只要有人 `catch {}` 了，就留下一个**列表里没有、硬盘上还在**的文件夹——用户既看不见也删不掉。

配套：删除前弹确认框，写明「整个文件夹 + 约 X GB + 不可恢复」；失败时弹错误框说明原因，**列表记录保留**。

### 3. 对账（Scan）：只加记录，不删文件夹

每个实例目录放一份 `.aurora-instance.json`（`instances.json` 仍是唯一真源，这份只是“身份证”）。`Scan()` 返回四类结果：

| 情况 | 处理 |
|---|---|
| 有身份证、列表里没有 | 询问后**加回列表，Id 保留为文件夹名** |
| 列表里有、文件夹没了 | 询问后移除记录（只删记录，不删文件） |
| 没有身份证的文件夹 | **只报告，绝不改动** |
| 身份证损坏 | 报告，跳过 |

为什么 Id 必须保留：`Directory` 由 `Id` 派生。重新生成 GUID 会让这个实例的保存、模组、配置全部与目录脱钩——比不恢复更糟。

为什么陌生文件夹不能自动加：它可能是用户手动复制的备份、别的启动器的实例、或者根本不是实例。启动器没有资格替用户判断。

### 4. 数据根永远不能是临时目录

`Save()` 在 `IsTestIsolated == false` 时拒绝持久化 `%TEMP%\aurora-*` 这样的根；`Load()` 把已经写坏的配置折回默认根并记 Warn。测试用 `UseTestRoot()` 同时隔离**配置文件**与**数据根**——只隔离数据根而让 `Save()` 写真实配置，正是这次事故的成因。

## 后果

- 好：删不掉时用户能知道原因并重试，不会产生幽灵；`instances.json` 丢失/损坏也能靠身份证找回；配置再也不会被测试写坏。
- 代价：删除多了一次 `Directory.Exists` 复查和最多 3 次重试（最坏 +800 ms，只在真的被占用时）。
- 已知未做：扫描不会自动修复「实例存在但游戏文件缺失」——那是「补全文件」的职责，两者不合并。

## 防线（测试，不是注释）

1. 锁定文件夹时 `DeleteChecked` 返回 `FilesLocked`，**文件夹与记录都仍在**；释放后重试成功。
2. `Scan` 恢复时 Id 必须等于文件夹名（≠ 新 GUID），只有有效身份证的文件夹会被恢复；陌生文件夹内的文件必须原封不动；坏 JSON 单独归类。
3. 源码守卫：`MainViewModel.CreateInstance()` 必须含 `Navigate("Download")` 且不得含 `Instances.Create(`；`DeleteSelectedInstanceAsync` 的 `Instances.Remove` 必须出现在 `if (result.Ok)` 之后，且必须有确认框。
4. 源码守卫：所有冒烟/测试入口碰 `RootDirectory` 时必须调用 `LauncherConfig.UseTestRoot`，禁止直接赋值 `LauncherConfig.Instance.RootDirectory`。
5. `DataRootPolicy.IsEphemeral` / `Sanitize` 的边界（`%APPDATA%`、`D:\AuroraData`、空串、`TempX` 前缀）逐个断言。

## 反例（如果不这么做）

- 若继续「先删记录再删文件」：被占用的文件夹永远消失于 UI，用户只能去 `instances/` 里考古。
- 若扫描自动添加所有文件夹：用户手写的 TODO 文件夹、`backup-2024/` 会变成“实例”，一启动就报错。
- 若恢复时重新生成 Id：存档在 `instances/<旧id>/saves`，新记录指向 `<新id>`，玩家看到“存档没了”。

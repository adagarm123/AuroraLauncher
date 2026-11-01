# ADR-0001：版本数据单一合法入口

- 状态：已采纳（第 36 轮，含防线测试）
- 关联轮次：第 31、33、34、36 轮

## 背景

版本 JSON（原版清单、加载器 profile、合并结果）被启动器的多个环节消费：
启动参数构建、Java 版本选择、库文件下载、安装状态评估。

第 31-34 轮连续出现同类事故，模式完全一致——**某条代码路径直接
`JsonHelper.Deserialize` 了未合并的原始版本 JSON**：

- 第 31 轮：实例 `LoaderVersion` 被写成完整版本 ID，profile URL 变成
  `/loader/1.21/fabric-loader-0.20.1-1.21/profile/json` → 官方 400、BMCLAPI 404；
- 第 33 轮：`VersionResolver` 缓存命中分支短路返回了未合并的 Fabric profile
  （它有 mainClass 但没有 `arguments` 与原版 97 个库）；
- 第 34 轮：`BuildLaunchSpecAsync` 干脆绕过 resolver 直接反序列化原始文件，
  导致第 33 轮的修复根本作用不到启动路径。

三次修复各自"改对了当下这条路径"，但**没有任何机制阻止下一个入口出现**。

## 决策

1. **收敛读取入口**：`VersionResolver.TryReadLocalRaw(versionId)` 是类外
   读取原始（未合并）版本 JSON 的**唯一**合法方法；其余代码一律通过
   `ResolveAsync(id)` 获取版本数据（内部保证合并语义）。
2. **防线测试锁死**：自测 `Guard: VersionJson raw deserialize only inside
   VersionResolver` 扫描 `src/` 全部源码，resolver 之外出现
   `JsonHelper.Deserialize<VersionJson>` 即失败。测试即审查清单。
3. **双路径一致性**：缓存命中分支（合并后返回）与本地优先分支
   （ResolveRecursive，同样本地合并）的结果必须逐字段一致——由离线单测
   `Resolver: cache-hit path and local-first path agree` 锁死。

## 后果

- 正面：第 33/34 轮"路径分叉"类 bug 结构性绝迹；新增消费方天然获得正确
  合并语义；classpath 一致性防线（ADR-0004 关联）有了确定前提。
- 代价：新增消费方必须理解"要合并数据走 ResolveAsync，要原始结构走
  TryReadLocalRaw"两个 API 的区别。
- 顺带收益：防线上线当天即抓到一处人肉 review 漏掉的裸读
  （`GetJavaForAsync`），该处还带着隐藏的 Java 版本误选 bug
  （裸读 loader profile 无 `javaVersion`）。

## 反例（不采纳此 ADR 的已发生后果）

第 34 轮启动崩溃：`ClassNotFoundException: KnotClient`，启动命令完全没有
`-cp` 参数。根因即"原始 JSON 未经合并直接使用"。若本 ADR 早一周存在，
防线测试会在提交时拒绝该改动，事故不会发生。

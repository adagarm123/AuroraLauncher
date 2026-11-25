# ADR-0028：核验第 104 轮代码彻查报告并修复确认存在的问题

- 状态：已采纳（含防线测试）
- 日期：2026-11-25
- 来源：用户提供的 `代码彻查报告_第104轮.md`（桌面文件）

## 背景

用户提供了一份第 104 轮代码彻查报告，要求逐条核验并修复。核验发现报告内容与实际代码状态并不完全一致：

- **确实已修**：#1 空任务 100%（`Aggregate` 已用 `counted > 0`）、#2 字节进度（`FractionFor` 已上线）、#4 Java/更新入队（第 104 轮已改）、M2 配置原子写（第 104 轮已改 tmp + Move + bak）。
- **声称已修但未落地**：#5 `ModPreflight.fabricApiMissing` 死变量仍在（CS0219）；#6 `LaunchAsync` 无局部空守卫（CS8604×2 + CS8602）；#7 完整包哈希仍用 `ReadAllBytesAsync`；配套改动（watchdog 单文件阶段、`KindGlyph` 更新字形、`MaximizeWorkArea.cs`）均不存在。
- **遗留清单属实**：M1 natives zip-slip、M4 字节下载不走直连、L1 哈希为空跳过校验、L2 旧 HttpClient 不释放、L3 PlaytimeTracker 未 Dispose、L4 Dispatch 无异常屏障、L6 缓冲区每次新建、L7 正则未预编译、L9 卸载助手异常无日志。
- **暂不动**：M3（多源清单，架构级需先出设计）、L5（仅显示层抖动）、L8（JVM 单进程，报告自评低优先级）、L2（GC 延迟释放是刻意设计，避免打断在飞请求，强改有回归风险）。

## 决策

本轮修掉所有「确认存在且低风险」的问题：

1. **zip-slip（M1）**：`PathEx.IsSafeEntryPath` 成为唯一闸门（拒绝对对路径、绝对路径、盘符、ADS、`.` segments）；`JavaRuntimeService.IsSafeEntryPath` 转发同一实现；natives 解压加校验 + 日志跳过。
2. **更新校验 fail-closed（L1 + #7）**：完整包改流式 `Sha256OfFile` + `Task.Run`；清单/补丁/合并结果的 SHA-256 缺失一律视为失败；补丁失败仍回退完整包。注意：这要求正式清单必须带哈希（当前 CI 签名清单一直带）。
3. **告警清零（#5/#6）**：删死变量；`LaunchAsync` 在主流程入口重新判空并快照 `account`，所有下游用局部非空变量。
4. **单文件看门狗**：`JobWatchdogPolicy` 新增 `java-download`/`update-download` 阶段（90 s 静默、无文件停滞判定）；`SetStageText(text, stageKey)` 支持设置阶段键而不改阶段图。
5. **直连兜底（M4）**：文件下载首次失败改用 `HttpClientFactory.DirectClient` 重试一次，与既有域名切换正交。
6. **稳定性**：`ArrayPool<byte>.Shared` 复用下载缓冲区；`CrashRule.Compiled` 预编译正则；`Dispatch` 用 try/catch 包裹回调；`MainViewModel.DisposeResources` 在窗口 `Closed` 时释放 playtime；卸载助手 `Task.Run` 加 catch + finally 保证退出并落日志。

## 后果

- Release 构建 **0 错误 0 警告**（此前 4 条告警）。
- fail-closed 让「清单缺哈希」从静默放行变为明确失败——若未来测试渠道想发无哈希包，须显式在清单标注渠道并单独签名。
- 直连兜底会多一次失败请求的开销（首次失败后立刻重试），与域名切换共享同一 5 次尝试预算，不会无限重试。
- 未修项（M3/L2/L5/L8）保留为已知限制，见报告对应条目。

## 验证

- `--unit` 182/182、`--all` 189/189；机制防线 64→66，交互元素保持 145。
- 新增防线：PathEx 安全闸门、natives 解压接线、fail-closed 文案、流式哈希、单文件看门狗、告警清零、直连兜底、正则预编译、缓冲区池化、字形与 stage key。
- CRLF 检出（模拟 CI）Release 单测通过。

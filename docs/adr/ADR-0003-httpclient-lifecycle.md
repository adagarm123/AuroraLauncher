# ADR-0003：HttpClient 生命周期契约

- 状态：已采纳（第 37 轮，含防线测试）
- 关联轮次：第 37 轮

## 背景

`HttpClientFactory` 持有共享 HttpClient（可配置代理，代理变更时重建实例）。
两个看似无害的实现组合出了"Cannot access a disposed object"：

- `AuthService` 在构造时把共享 client **缓存到字段**
  （`_http = HttpClientFactory.Instance.Client`）；
- `LauncherConfig` 每次加载/保存配置都调用 `ConfigureProxy`——
  **即使代理没变也 dispose 旧实例并重建**。

结果：启动器一启动，配置加载触发重建，登录服务手里的引用已失效，
微软正版登录一碰即炸。用户无法使用正版账户。

## 决策

1. **服务侧契约**：永不缓存共享 client——每次调用通过
   `HttpClientFactory.Instance.Client` 属性解析当前实例
   （AuthService 全部 7 处请求点已改造）。
2. **工厂侧契约**：`ConfigureProxy` 仅在代理**实际变化**时重建；
   旧实例**不主动 Dispose**（交给 GC，在途请求安全结束）——
   单例启动器场景下无资源风险，却消灭了整个释放时序问题。
3. **防线测试**：`Guard: shared HttpClient never cached or disposed
   outside factory` 扫描 `src/`，禁止工厂外的
   `_client.Dispose()`、字段缓存共享 client、`using` 包装共享 client。

## 后果

- 正面：微软登录恢复；未来任何服务新增 HTTP 调用都无需（也不能）
  关心 client 的生命周期——契约由测试执行而非口头约定。
- 代价：被替换的旧 client 由 GC 决定回收时机（极端高频换代理场景
  可能短暂堆积 socket——已在 ROADMAP「已知风险」记录，当前启动器
  仅在设置变更时重建一次，风险可控）。

## 反例（不采纳此 ADR 的已发生后果）

第 37 轮微软正版登录不可用（截图见用户报告）。两个"单独看都没问题"
的实现——字段缓存是常见优化写法、无条件重建是防御性写法——组合后
变成必现崩溃。此事故发生在已有人工评审的情况下，证明"口头约定谁可以
dispose"不可靠，必须由测试执行契约。

# ADR 索引（Architecture Decision Records）

> 每篇一页纸：背景 / 决策 / 后果 / 反例。反例是"如果不这么做会怎样"的真实事故记录，
> 不是理论推演——全部对应第 31-39 轮的实际缺陷。
> 原则汇总见 [ROADMAP「可靠性哲学」](../../ROADMAP.md)。

| ADR | 主题 | 状态 |
|-----|------|------|
| [ADR-0001](ADR-0001-version-data-single-entry.md) | 版本数据单一合法入口 | 已采纳（含防线测试） |
| [ADR-0002](ADR-0002-install-state-single-source.md) | 安装状态单一来源（Evaluate） | 已采纳（含防线测试） |
| [ADR-0003](ADR-0003-httpclient-lifecycle.md) | HttpClient 生命周期契约 | 已采纳（含防线测试） |
| [ADR-0004](ADR-0004-download-resilience.md) | 下载韧性策略（四线路竞速 + 断点失效泛化 + 缓存兜底） | 已采纳 |
| [ADR-0005](ADR-0005-diagnostics-and-redaction.md) | 诊断包与脱敏（可发送、可信任） | 已采纳（含测试） |

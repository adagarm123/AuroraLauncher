# ADR-0002：安装状态单一来源（Evaluate）

- 状态：已采纳（第 36 轮，含防线测试覆盖语义）
- 关联轮次：第 32、36、39 轮

## 背景

"这个实例的文件装齐了吗？"至少有三个地方各自回答：

- 启动前检查（NeedsRepair）：要求 loader 版本目录里存在 **client jar**；
- 补全流程（RepairAsync）：同样的判断，决定是否重跑加载器安装；
- UI 状态展示：又一处实现。

口径不一致的直接后果：**Fabric/Quilt/Forge 版本没有自己的 client jar**
（继承原版的），所以"每次启动都判定未安装 → 反复补全"——补全全部命中缓存、
秒级完成、但每次都要跳转下载页跑一遍流程图，用户体验即"卡在补全"。

## 决策

1. **单一函数**：`GameInstaller.Evaluate(versionId, requireNatives) →
   InstallState`。检查项收敛于此：版本 JSON 存在；client jar **仅原版版本
   要求**（loader 版本只查 JSON）；合并后库文件逐项在场；natives 仅在
   `requireNatives` 时检查（启动时会无条件重新解压，评估阶段检查它只会
   产生误报——真实数据里原版 natives 目录为空即证明）。
2. **返回缺失清单**：`InstallState.Missing` 让补全流程和 UI 知道具体缺什么
   （"缺 2 个库文件"而非笼统"需要补全"），且不必重新扫描。
3. **所有消费方统一调用**：NeedsRepair、启动流程日志（"缺: …"）均走
   `Evaluate`；防线测试锁死 Evaluate 的状态语义（loader 不要求 client jar、
   缺库逐项报告、vanilla 缺 jar 报告）。

## 后果

- 正面：补全一次即认可，后续启动零补全直接拉起游戏；缺失清单让排查
  精确到文件；新增检查项只改一处。
- 代价：Evaluate 成为热点路径，必须保持纯读（无 IO 副作用、无网络）。
- 顺带收益：natives 误报问题（真实机器上原版 natives 目录为空属正常）
  通过 `requireNatives` 参数显式化，而非靠巧合。

## 反例（不采纳此 ADR 的已发生后果）

第 32 轮：用户报告"每次启动都要跳转下载页反复补全"。三个判断点口径
互不一致，任何一处收紧都会让另两处的修复失效。收敛到 Evaluate 后，
该类"状态不一致"bug（用户估计占未来 30%）失去滋生土壤。

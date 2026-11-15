# 可交互 UI 实际功能全量检验报告（INTERACTIVE UI AUDIT）

> 更新于 2026-11-15（第 94 轮复核；扫描规则于第 46 轮升级，自第 40-44 轮「按钮全量审计」演变而来）。
> 范围：**软件内所有可交互 UI 元素的实际功能反馈路径**——不只按钮，
> 而是任何携带交互属性（`Command` 绑定 / `Click` / `Checked` / `Unchecked` /
> `SelectionChanged` / `MouseDoubleClick` / `KeyDown` 及 Preview 变体）的元素。
> 方法：静态审计全部桌面 XAML（`Views/*.xaml` + `MainWindow.xaml` +
> `Themes/*.xaml` + `Styles/*.xaml` + 任何 ResourceDictionary），逐个核对
> 绑定与处理器真实存在。结果固化为两条防线测试（自测 Unit 第 21、22 项），
> **违规即测试红**——本报告不是一次性人工核对，而是持续生效的机制。

## Guard 扫描规则（第 46 轮升级）

1. **扫描规则扩展到七类交互属性**：不再只认 4 类控件（Button / ToggleButton /
   MenuItem / Hyperlink），而是**任何**元素只要携带 `Command="{Binding ...}"`、
   `Click`、`Checked`、`Unchecked`、`SelectionChanged`、`MouseDoubleClick`、
   `KeyDown`（含 `PreviewKeyDown` / `PreviewMouseDoubleClick`）即纳入核对。
   不是每个元素都要人工审计，而是**扫描规则本身必须覆盖这些属性**——否则未来
   加一个 CheckBox 绑定了不存在的处理器，Guard 不会报。
2. **扫描范围覆盖全部桌面 XAML**：`Views/*.xaml`、`MainWindow.xaml`、
   `Themes/*.xaml`、`Styles/*.xaml` 及任何 ResourceDictionary（bin/obj 除外）。
   样式 Setter 提供的命令（`<Setter Property="Command" Value="{Binding ...}"/>`）
   **无论当前是否有元素引用该样式**都核对——否则「样式提供的命令」这条防线是空的。
3. **检查重点（刻意声明，避免误读）**：**Command 绑定是静默失败，事件处理器是
   编译期失败，Guard 主要防前者**。事件处理器（Click / Checked 等）由 XAML 编译器
   在编译期接线，缺失即编译错误；Command 绑定写错只在运行时静默吞掉。防线测试
   对事件处理器做存在性核对是「保险带」，不是主要防线。
4. **警告级反向检查（死命令探测）**：VM 中的 public RelayCommand 如果从未在任何
   XAML（直接绑定或样式 Setter）中被引用，输出 `[WARN]`——**警告不失败**（有些
   命令可能供 code-behind 调用），但至少让你知道「哪些命令是死代码」。

### 当前警告输出（第 46 轮实测）

```
[WARN] VM 命令从未被任何 XAML 绑定（死代码候选，仅警告不失败）:
       ChooseInstanceIconCommand, InstallContentCommand, ResetContentCommand
```

三个候选命令为历史迭代遗留（功能被其他入口取代或按钮移除后命令未清理），
处理决策：保留观察，下次功能调整时一并清理或接线。

## 结论

**117 个交互元素全部有功能反馈路径，无死按钮。**（闭合口径：直接绑定 **87** + 样式命令 **17** + 事件处理器 **13**；第 82 轮无新增元素、第 83 轮 +1、第 84 轮 +1、第 85 轮无新增、第 86 轮无新增、第 87 轮 +3、第 88 轮 +2 直接绑定、第 89 轮无新增元素（只改文案与绑定）、第 90 轮无新增元素（保存按钮包进一行 + 两个文本提示）、第 91 轮无新增元素（只加滚动容器）、第 92 轮无新增元素（只有拦截对话框与状态栏文案）、第 93 轮无新增元素（按钮 Content 改为绑定 IsRunning）、第 94 轮无新增元素（只改安装流程与错误文案），明细见下节）

- 直接 Command 绑定：**87 个**（全部核对存在于 MainViewModel；含 `DataContext.` 相对源绑定与主题切换按钮 ×2）
- 事件处理器元素：**6 个**（LogsView 只看错误 Checked/Unchecked → `ErrorsOnly_Changed`；ModsView 定位适配版本 Click → `OnJumpToCompatible`；UpdateDialog 立即更新 / 关闭 → `UpdateNow_Click` / `Close_Click`；UpdateApplyingWindow 重试 / 关闭 → `Retry_Click` / `Close_Click`，均已核对存在于对应 code-behind）
- 样式 Setter 提供 Command：**17 个**（`NavItem` → NavigateCommand ×9；`SubNavItem` → SelectContentKindCommand ×8；样式命令已独立核对，不依赖元素引用）
- 静态命令（非绑定，如滚动条模板内 `ScrollBar.PageUpCommand`）：XAML 解析期即报错，不属静默失败类，核对后放行
- 本轮新增发现：**3 个死代码候选命令**（见上节 WARN）

### 数字修正记录（第 48 轮复核）

外部复核指出报告数字与清单对不上，逐项重数后确认四处口径错误并修正：

| 项 | 原报告 | 实数 | 错因 |
|----|--------|------|------|
| 样式 Setter 命令 | 16（NavItem ×9 + SubNavItem ×6 + 主题按钮 ×1） | **14**（NavItem ×8 + SubNavItem ×6） | 侧栏导航实为 8 项误记 9；主题按钮是**直接绑定**（MainWindow/SettingsView 各 1 处 `Command="{Binding SwitchThemeCommand}"`）误归入样式类 |
| 事件处理器 | 3 | **2** | 按条目计数而非按元素计数（见下方数字链分解） |
| 元素总数 | 66 | **65** | 「全局」小节把切换主题按钮**重复计入**一次；下载管理页的「启动游戏」按钮（第 43 轮新增）当时漏列 |

修正后精确闭合：**49（直接绑定）+ 14（样式命令）+ 2（事件处理器）= 65**，与逐 XAML 文件的元素计数一致。

> **第 55 轮更新**：新增独立「更新日志」页面与侧栏导航按钮——元素 65→**66**（MainWindow 16→**17**），NavItem 样式命令 8→**9**（样式命令合计 14→**15**）；直接绑定与事件处理器不变。
> **第 57 轮更新**：设置页「更新」卡片新增「检查更新」按钮——66→**67**，直接绑定 49→**50**。
> **第 58 轮更新**：新增更新提醒弹窗 UpdateDialog（立即更新 / 关闭两个按钮，均为事件处理器）——67→**69**，事件处理器元素 2→**4**。
> **第 59 轮更新**：设置页 Java 卡片新增「一键安装 Java 21」按钮——69→**70**，直接绑定 50→**51**。
> **第 88 轮**：设置页「存储」卡片新增「扫描垃圾文件」「清理选中项」两个按钮（`ScanStorageCleanupCommand` / `ApplyStorageCleanupCommand`，后者带 `CanExecute`：没勾选任何分类时禁用）——元素 115→**117**，直接绑定 85→**87**，闭合 **87 + 17 + 13 = 117**。分类列表用 `ItemsControl` + `CheckBox`（`IsChecked` 绑定 `Selected`，行文本绑定 `Label`），`IsChecked` 不属于扫描的七类交互属性，整体按 1 个模板计，未计入元素数。
> **第 87 轮**：设置页「存储」卡片新增「清理残留下载文件」（`CleanResidueCommand`，带 `IsCleaningResidue` 互斥），下载管理页新增一条非阻塞提示条及其两个按钮（`OpenResidueSettingsCommand` 跳设置、`DismissResidueNoticeCommand` 本次运行不再提示）——元素 112→**115**，直接绑定 82→**85**，闭合 **85 + 17 + 13 = 115**。提示条本身（`Border` + 两段文字）不属于扫描的七类交互属性，未计入。
> **第 84 轮**：实例页新增「扫描未注册实例」（`ScanInstancesCommand`）——元素 111→**112**，直接绑定 81→**82**，闭合 **82 + 17 + 13 = 112**。两个删除按钮（底部「删除实例」/ 高级区「删除实例（含文件）」）绑定未变，只改了文案与行为（加确认框、先删文件夹）。
> **第 83 轮**：设置页新增「释放启动器占用内存」（`DeepCleanMemoryCommand`，只清启动器自己的缓存，带 `IsEnabled` 绑定到 `CanCleanMemory`）——元素 110→**111**，直接绑定 80→**81**，闭合 **81 + 17 + 13 = 111**。「自动（推荐）」与「闲置 5 分钟后释放」是两个复选框，不属于扫描的七类交互属性，未计入。
> **第 82 轮**：无实例时的启动引导——首页两个「启动游戏」按钮**文案随状态切换**（`LaunchButtonText`：启动游戏 / 去下载游戏），点击改为跳转「游戏下载」。没有新增元素，闭合仍为 **80 + 17 + 13 = 110**。
> **第 81 轮**：无边框窗口的自绘标题栏新增「最小化 / 最大化 / 关闭」三个按钮（均为 Click 处理器）——元素 107→**110**，事件处理器 10→**13**，闭合 **80 + 17 + 13 = 110**。标题条拖动（`MouseLeftButtonDown`）与窗口尺寸变化（`SizeChanged`）不属于扫描的七类交互属性，未计入。
> **第 80 轮**：液态玻璃的红色警告窗口（`GlassWarningWindow`）新增「取消（保持原主题）」「我已知晓风险，仍然开启」两个按钮，均为 Click 处理器——元素 105→**107**，事件处理器 8→**10**，闭合 **80 + 17 + 10 = 107**。设置页「界面材质」下拉框不属于四类交互元素，未计入。
> **第 79 轮**：存档备份（「立即备份 / 打开备份文件夹」）、首页性能卡（「调整内存」）、Ctrl+K 全局搜索（状态栏「搜索」、面板「✕」，以及面板 ListBox 双击 / 窗口 `PreviewKeyDown` 两个手感入口）——元素 98→**105**，直接绑定 75→**80**，事件处理器 6→**8**，闭合 **80 + 17 + 8 = 105**。
> **第 78 轮**：崩溃诊断卡升级为「诊断 + 修复」——新增「发送给作者」「打开设置」两个按钮，以及每条诊断行内的修复按钮（`ApplyCrashFixCommand`，模板 1 个）——元素 95→**98**，直接绑定 72→**75**，闭合 **75 + 17 + 6 = 98**。
> **第 77 轮**：实例页新增「导出整合包 / 导入整合包」，设置页新增「启动前检查模组依赖与冲突」——元素 92→**95**，直接绑定 69→**72**，闭合 **72 + 17 + 6 = 95**；启动前体检对话框为模态窗口，不计入主界面元素。
> **第 76 轮**：修复唤醒跨线程访问与应答语义、托盘菜单动作移出嵌套消息循环、退出看门狗——**无新增交互元素**，闭合仍为 **69 + 17 + 6 = 92**。
> **第 75 轮**：修复单实例唤醒（`Closed` 强制结束进程 + 唤醒应答握手）——纯生命周期逻辑，**无新增交互元素**，闭合仍为 **69 + 17 + 6 = 92**。
> **第 74 轮**：下载页新增「继续下载 / 忽略并清理」（断点恢复横幅），设置页新增「创建 portable.txt」——元素 89→**92**，直接绑定 66→**69**，闭合 **69 + 17 + 6 = 92**；关闭窗口对话框为模态窗口，不计入主界面元素。
> **第 73 轮**：并发写锁与退出确认（`PathWriteGate` / `CloseGuardPolicy`）——一个关窗确认对话框，**无新增主界面交互元素**，闭合仍为 **66 + 17 + 6 = 89**。
> **第 72 轮**：下载管理页重做为多任务列表——旧「取消下载 / 启动游戏」2 个按钮 → 新「全部取消 / 清理已完成 / 行内取消」3 个（启动游戏回到首页入口）；元素 88→**89**，直接绑定 65→**66**，闭合 **66 + 17 + 6 = 89**。
> **第 71 轮**：修复下载页空白（清单 URL、双主机竞速、旧配置自愈、离线缓存）——纯服务层与配置层改动，**无新增交互元素**，闭合仍为 **65 + 17 + 6 = 88**。
> **第 70 轮**：日志页右上角新增「导出日志」按钮（直接绑定 `ExportLogsCommand`）——元素 87→**88**，直接绑定 64→**65**；闭合 **65 + 17 + 6 = 88**。
> **第 69 轮**：日志改为后台批量写、日志页隐藏期冻结、切页三段计时（全部在服务层 / code-behind），**无新增交互元素**，闭合仍为 **64 + 17 + 6 = 87**。
> **第 68 轮**：切页计时（`UiLatencyTrace`）+ 空闲预热（`PageWarmupPolicy`）+ 首次显示才初始化（`PageLifecycle.OnFirstShown`）全部落在 code-behind / 服务层，**无新增交互元素**，闭合仍为 **64 + 17 + 6 = 87**（本条由 `Guard: every interactive element has a binding or event handler` 自动复核）。
> **第 67 轮**：彻底移除软件阴影 / 位图缓存 / 动画时钟（样式层回退），**无新增交互元素**，闭合仍为 **64 + 17 + 6 = 87**。
> **第 66 轮**：样式层性能修复（ProgressBar 动画启停、Card 隐藏释放特效、全局 ListBox 容器回收），**无新增交互元素**，闭合仍为 **64 + 17 + 6 = 87**。
> **第 65 轮**：主窗口由 ContentControl 换装改为 PageHost 常驻 + Visibility 切换（页面在 code-behind 惰性创建，XAML 中无新增交互元素），**无新增交互元素**，闭合仍为 **64 + 17 + 6 = 87**。
> **第 64 轮**：纯逻辑补测与边界收口（无 UI 改动），**无新增交互元素**，闭合仍为 **64 + 17 + 6 = 87**。
> **第 63 轮**：纯稳定性修复（页面初始化单飞 / 导航早退 / 批量集合替换），**无新增交互元素**，闭合仍为 **64 + 17 + 6 = 87**。
> **第 62 轮更新**：游戏下载页版本列表顶部新增「一键更新」按钮（直接绑定 +1）——86→**87**，直接绑定 63→**64**。实例管理页新增的是 Java ComboBox（双向绑定，不属于四类交互元素）。当前闭合：**64 + 17 + 6 = 87**。
> **第 61 轮更新**：设置页新增「立即重启并应用更新」按钮（直接绑定 +1）、更新安装窗口新增「重试 / 关闭」两个按钮（事件处理器 +2）——83→**86**，直接绑定 62→**63**，事件处理器元素 4→**6**。当前闭合：**63 + 17 + 6 = 86**。
> **第 60 轮更新**：日志页崩溃卡片（关闭 / 一键导出诊断包 / 前往求助，3 个）+ 内容中心新增 CurseForge / OptiFine 两个子导航与两个面板（10 个：CurseForge 申请 Key / 搜索 / 网页搜索 / 前往下载页；OptiFine 刷新版本 / 一键安装 / 打开官网 / 选择本地安装器；子导航 ×2）——70→**83**，直接绑定 51→**62**，SubNavItem 6→**8**（样式命令 15→**17**）。当前闭合：**62 + 17 + 4 = 83**。

### 数字链闭合（原 66 → 现 65 的逐项分解）

原报告 47（直接）+ 16（样式）+ 3（事件处理器条目）= 66；实数 49 + 14 + 2 = 65。差 1 来自四个变化的代数和（直接 +2、样式 −2、处理器 −1）：

| 变化 | 直接 | 样式 | 处理器 |
|------|------|------|--------|
| NavItem 9 → 8（侧栏导航实为 8 项） | — | −1 | — |
| 主题按钮从样式类归位为直接绑定（原样式类只算了 2 个主题按钮中的 1 个） | +1 | −1 | — |
| 下载管理「启动游戏」补列（第 43 轮新增，原清单漏列） | +1 | — | — |
| 事件处理器按**元素**计数：「只看错误」的 Checked 与 Unchecked 指向**同一** ToggleButton，2 条目合 1 | — | — | −1 |
| **合计** | **47 → 49** | **16 → 14** | **3 → 2** |

关于事件处理器 3 → 2 的明确说明：原 3 个条目 = 「只看错误」Checked + Unchecked（**同一元素**的两个事件属性，按元素应记 1）+ 「定位适配版本」Click（1 个元素）。**没有任何处理器被改归类为直接绑定**——两个事件处理器元素（LogsView、ModsView 各 1）第 48 轮仍列在事件类；变的只是计数口径（条目 → 元素），与第 46 轮"扩展到七类交互属性"的扫描规则无关（该规则第 48 轮原样生效）。

## 本轮修复的 2 个死按钮（第 40 轮，历史记录）

| 位置 | 按钮 | 问题 | 修复 |
|------|------|------|------|
| DownloadView L187 | **安 装** | 绑定 `InstallFromCardsCommand`，VM 无此属性（安装方法从未包装成命令）；WPF 绑定失败**静默** | 补齐命令属性 + 构造初始化 |
| ModsView L448 | 在 Modrinth 打开 | 绑定 `OpenModrinthPageCommand`，同上（方法未包装） | 补齐命令属性 |

两个死按钮均由第 21 项防线测试（XAML Command 绑定全量核对）在上线当日抓出。

## 逐页清单（Views 70 项 + MainWindow 17 项）

### 首页 HomeView（5）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 启 动 游 戏 ×2 | `LaunchCommand` | ✅ |
| 添加（离线账户） | `AddOfflineAccountCommand` | ✅ |
| 开始微软登录 | `MicrosoftBeginCommand` | ✅ |
| 完成登录 | `MicrosoftCompleteCommand` | ✅ |

### 游戏下载 DownloadView（3）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 刷新 | `RefreshVersionsCommand` | ✅ |
| 一键更新（版本列表顶部卡片） | `OneClickUpdateCommand` | ✅（第 62 轮） |
| **安 装** | `InstallFromCardsCommand` | ✅（第 40 轮修复） |

### 下载管理 DownloadsView（2）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 取消下载 | `CancelDownloadCommand` | ✅ |
| 启动游戏（完成后主按钮） | `LaunchCommand` | ✅（第 43 轮新增，原清单漏列，第 48 轮补记） |

### 实例管理 InstancesView（13）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 新建实例 | `CreateInstanceCommand` | ✅ |
| 删除实例 | `DeleteSelectedInstanceCommand` | ✅ |
| 删除版本 | `DeleteSelectedInstanceCommand` | ✅ |
| 导入 .minecraft | `ImportMinecraftCommand` | ✅ |
| 导入其他启动器 | `ImportLauncherCommand` | ✅ |
| 修改版本名 / 修改版本描述 | `RenameInstanceCommand` / `EditNotesCommand` | ✅ |
| 收藏（图标随选中切换） | `ToggleFavoriteCommand` | ✅ |
| 版本 / 存档 / Mod 文件夹 | `OpenInstanceFolderCommand` 等 ×3 | ✅ |
| 导出启动脚本 | `ExportLaunchScriptCommand` | ✅ |
| 补全文件 | `RepairInstanceCommand` | ✅ |

### 内容中心 ModsView（27）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 子导航卡片：模组 / 整合包 / 数据包 / 资源包 / 光影包 / CurseForge / OptiFine / 本地管理 ×8 | `SubNavItem` 样式 → `SelectContentKindCommand` | ✅（第 60 轮 +2） |
| 搜索 | `SearchContentCommand` | ✅ |
| 结果卡片 / 整合包卡片 → 打开详情 | `OpenContentDetailCommand` ×2 | ✅ |
| 分页：第一页 / 上一页 / 下一页 / 最后一页 | `SearchFirstPageCommand` 等 ×4 | ✅ |
| 返回 | `BackFromContentDetailCommand` | ✅ |
| 在 Modrinth 打开 | `OpenModrinthPageCommand` | ✅（第 40 轮修复） |
| 定位适配版本 | Click `OnJumpToCompatible` | ✅ |
| 安装此版本（适配行） | `InstallSpecificVersionCommand` | ✅ |
| CurseForge：申请 Key / 搜索 / 网页搜索 / 前往下载页 | `OpenCurseForgeKeyPageCommand` / `SearchCurseForgeCommand` / `OpenCurseForgeWebCommand` / `OpenCurseForgeItemCommand` | ✅（第 60 轮） |
| OptiFine：刷新版本 / 一键安装 / 打开官网 / 选择本地安装器 | `LoadOptiFineBuildsCommand` / `InstallOptiFineCommand` / `OpenOptiFineWebCommand` / `PickLocalOptiFineCommand` | ✅（第 60 轮） |

### Mod 管理（ModsView 下半区，3）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 启用 / 禁用所选 Mod | `ToggleModCommand` | ✅ |
| 刷新 | `ScanModsCommand` | ✅ |
| 导入 .mrpack 整合包 | `ImportMrpackCommand` | ✅ |

### 设置 SettingsView（9）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 打开文件夹 | `OpenRootDirectoryCommand` | ✅ |
| 重新检测（Java） | `ReloadJavaListCommand` | ✅ |
| 使用（选择 Java） | `UseJavaCommand` | ✅ |
| 导出诊断包 | `ExportDiagnosticsCommand` | ✅ |
| 切换主题 | `SwitchThemeCommand`（直接绑定） | ✅ |
| 检查更新 | `CheckUpdateCommand` | ✅（第 57 轮） |
| 立即重启并应用更新 | `RestartToApplyCommand` | ✅（第 61 轮，仅待装更新就绪时可见） |
| 一键安装 Java 21 | `InstallJava21Command` | ✅（第 59 轮） |
| 保存设置 | `ApplySettingsCommand` | ✅ |

### 更新提醒弹窗 UpdateDialog（2）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 立即更新 | Click → `UpdateNow_Click` | ✅（第 58 轮） |
| 关闭 | Click → `Close_Click` | ✅（第 58 轮） |

### 更新安装窗口 UpdateApplyingWindow（2）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 重试 | Click → `Retry_Click` | ✅（第 61 轮，仅失败时可见） |
| 关闭 | Click → `Close_Click` | ✅（第 61 轮，仅失败时可见） |

### 日志 LogsView（4）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 只看错误（ToggleButton） | Checked/Unchecked → `ErrorsOnly_Changed` | ✅ |
| 崩溃卡片：关闭 | `DismissCrashReportCommand` | ✅（第 60 轮） |
| 崩溃卡片：一键导出诊断包 | `ExportDiagnosticsCommand` | ✅（第 60 轮） |
| 崩溃卡片：前往求助 | `OpenHelpLinkCommand` | ✅（第 60 轮） |

### MainWindow（17，含首次启动引导）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 侧栏导航：首页/游戏下载/实例管理/内容中心/下载管理/设置/日志/更新日志/帮助 ×9 | `NavItem` 样式 → `NavigateCommand` | ✅ |
| 切换主题（侧栏底部） | `SwitchThemeCommand` | ✅ |
| 引导：跳过引导 | `FirstRunSkipCommand` | ✅ |
| 引导：重新检测（Java） | `FirstRunRescanCommand` | ✅ |
| 引导：打开 Java 下载页 | `FirstRunOpenJavaSiteCommand` | ✅ |
| 引导：下一步 | `FirstRunNextCommand` | ✅ |
| 引导：一键安装 | `FirstRunInstallCommand` | ✅ |
| 引导：稍后再说 / 完成 | `FirstRunSkipCommand` / `FirstRunDoneCommand` | ✅ |

> 页面合计核对：Home 5 + Download 3 + Downloads 2 + Instances 13 + 内容中心 27 + Mod 管理 3 + Settings 9 + Logs 4 + UpdateDialog 2 + UpdateApplyingWindow 2 = **70**（Views）+ **17**（MainWindow）= **87**，与分类合计 64 + 17 + 6 一致。

## 附带反馈保障（第 40 轮同步加入，历史记录）

- `InstallFromCardsAsync` / `LaunchAsync` 增加 **IsBusy 门卫**：任务进行中
  重复点击会得到明确状态提示（"有任务正在进行中…"），而不是静默吞掉。

## 审计边界（明确声明）

防线由**两层检查**构成，「七类属性」和「四类控件」分属不同层，不矛盾：

1. **属性级核对（全局，任何元素）**：任何 XAML（Views + MainWindow + Styles +
   Themes + ResourceDictionary）中只要元素**携带七类交互属性之一**——
   `Command="{Binding ...}"` / `Click` / `Checked` / `Unchecked` /
   `SelectionChanged` / `MouseDoubleClick` / `KeyDown`（含 Preview 变体）——
   就核对该属性真实有效：Command 绑定必须解析到 VM 属性，事件处理器必须
   存在于配对 code-behind。**CheckBox 的 `Checked`、ComboBox 的
   `SelectionChanged` 属于这一层**——携带即核对，不管控件类型。
2. **底线检查（应用界面 + 四类控件）**：Views + MainWindow 中的
   Button / ToggleButton / MenuItem / Hyperlink 即使**不携带任何交互属性**
   也必须有反馈路径（防"什么都没绑"的死按钮）。
3. **模板内部件豁免底线**：Styles/Themes 内的模板部件（如 ComboBox 模板的
   ToggleButton，经 TemplatedParent 绑定）**只做第 1 层属性级核对，不做第 2 层
   底线要求**——它们绑定的是模板化控件而非 VM，"没有 Command"是正常的。

- **扫描范围**：`src/AuroraLauncher.Desktop/` 下**全部** XAML（Views +
  MainWindow + Styles + Themes + 任何 ResourceDictionary；bin/obj 除外）。
- **静态命令**（`Command="ScrollBar.PageUpCommand"` 语法）：XAML 解析期报错、
  非静默失败类，核对后放行。
- **数据绑定类属性**（如 `IsChecked="{Binding ShowX}"`）的静默失败属另一类
  问题（属性名拼写），不在本防线范围；如需可另立 Guard。
- **事件处理器核对**是保险带（编译器已管），仅核对同名方法存在于配对
  code-behind（`*.xaml.cs`）。

## 机制保障

- 防线测试 `Guard: every XAML command binding resolves to a VM property`（第 21 项）
- 防线测试 `Guard: every interactive element has a binding or event handler`（第 22 项，含警告级死命令探测）
- 两者均在 `--unit` 模式运行（CI 每次 push/PR 必跑）——**未来任何交互元素失去
  反馈路径，或样式命令失效，提交时即被拦截**。

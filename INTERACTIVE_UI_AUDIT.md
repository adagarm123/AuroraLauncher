# 可交互 UI 实际功能全量检验报告（INTERACTIVE UI AUDIT）

> 更新于 2026-11-01（第 55 轮复核；扫描规则于第 46 轮升级，自第 40-44 轮「按钮全量审计」演变而来）。
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

**66 个交互元素全部有功能反馈路径，无死按钮。**（Views 49 + MainWindow 17，含首次启动引导 7 个按钮；第 48 轮全量重数修正，第 55 轮新增「更新日志」页导航按钮 +1，见下节）

- 直接 Command 绑定：**49 个**（全部核对存在于 MainViewModel；含 `DataContext.` 相对源绑定与主题切换按钮 ×2）
- 事件处理器元素：**2 个**（LogsView 只看错误 Checked/Unchecked → `ErrorsOnly_Changed`；ModsView 定位适配版本 Click → `OnJumpToCompatible`，均已核对存在于对应 code-behind）
- 样式 Setter 提供 Command：**15 个**（`NavItem` → NavigateCommand ×9（第 55 轮新增「更新日志」导航）；`SubNavItem` → SelectContentKindCommand ×6；样式命令已独立核对，不依赖元素引用）
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

> **第 55 轮更新**：新增独立「更新日志」页面与侧栏导航按钮——元素 65→**66**（MainWindow 16→**17**），NavItem 样式命令 8→**9**（样式命令合计 14→**15**）；直接绑定与事件处理器不变。当前闭合：49 + 15 + 2 = 66。

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

## 逐页清单（Views 49 项 + MainWindow 16 项）

### 首页 HomeView（5）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 启 动 游 戏 ×2 | `LaunchCommand` | ✅ |
| 添加（离线账户） | `AddOfflineAccountCommand` | ✅ |
| 开始微软登录 | `MicrosoftBeginCommand` | ✅ |
| 完成登录 | `MicrosoftCompleteCommand` | ✅ |

### 游戏下载 DownloadView（2）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 刷新 | `RefreshVersionsCommand` | ✅ |
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

### 内容中心 ModsView（17）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 子导航卡片：模组 / 整合包 / 数据包 / 光影包 / 资源包 / 本地管理 ×6 | `SubNavItem` 样式 → `SelectContentKindCommand` | ✅ |
| 搜索 | `SearchContentCommand` | ✅ |
| 结果卡片 / 整合包卡片 → 打开详情 | `OpenContentDetailCommand` ×2 | ✅ |
| 分页：第一页 / 上一页 / 下一页 / 最后一页 | `SearchFirstPageCommand` 等 ×4 | ✅ |
| 返回 | `BackFromContentDetailCommand` | ✅ |
| 在 Modrinth 打开 | `OpenModrinthPageCommand` | ✅（第 40 轮修复） |
| 定位适配版本 | Click `OnJumpToCompatible` | ✅ |
| 安装此版本（适配行） | `InstallSpecificVersionCommand` | ✅ |

### Mod 管理（ModsView 下半区，3）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 启用 / 禁用所选 Mod | `ToggleModCommand` | ✅ |
| 刷新 | `ScanModsCommand` | ✅ |
| 导入 .mrpack 整合包 | `ImportMrpackCommand` | ✅ |

### 设置 SettingsView（6）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 打开文件夹 | `OpenRootDirectoryCommand` | ✅ |
| 重新检测（Java） | `ReloadJavaListCommand` | ✅ |
| 使用（选择 Java） | `UseJavaCommand` | ✅ |
| 导出诊断包 | `ExportDiagnosticsCommand` | ✅ |
| 切换主题 | `SwitchThemeCommand`（直接绑定） | ✅ |
| 保存设置 | `ApplySettingsCommand` | ✅ |

### 日志 LogsView（1）
| 元素 | 绑定 | 状态 |
|------|------|------|
| 只看错误（ToggleButton） | Checked/Unchecked → `ErrorsOnly_Changed` | ✅ |

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

> 页面合计核对：Home 5 + Download 2 + Downloads 2 + Instances 13 + 内容中心 17 + Mod 管理 3 + Settings 6 + Logs 1 = **49**（Views）+ **17**（MainWindow，第 55 轮新增「更新日志」导航按钮）= **66**，与分类合计 49 + 15 + 2 一致。

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

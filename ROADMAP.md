# 路线图 / 开发状态（Roadmap）

> 更新于 2026-11-25（第 105 轮）。当前基线：**自测 187/187 通过**（Unit 180 离线·连跑 20 次实测无抖动 / All 187 含网络集成），真实机器端到端验证完成（Fabric 1.21 成功启动；Forge 1.20.1-47.4.22 安装器实跑通过）。

## 已完成（全部真实验证）

### 核心链路

- [x] **完整启动链**：官方清单 → 版本选择 → 下载/校验/安装（client/libraries/assets/natives）→ **继承合并（含缓存路径）** → Java 探测匹配（按合并后 javaVersion）→ 微软设备码 + 离线认证 → 参数构建 → 启动 + 文件日志 + 进度。
- [x] **端到端真机验证**：Fabric 1.21（fabric-loader 0.20.1）真实启动游戏进程。
- [x] **下载引擎**：有界并发、Range 续传、指数退避、**25s 空闲超时**、SHA-1、断点失效规则泛化（416 / Content-Range 不匹配 / .part ≥ 预期 → 从头重下；哈希连续 3 次失败上限）、原子提交、取消、去重、聚合进度（节流 10 次/秒）。
- [x] **四线路竞速下载**（NetRouting）：官方默认/直连 + BMCLAPI 默认/直连并行竞速，整体重试一轮，失败聚合报因；元数据本地缓存兜底（弱网/断网可用缓存安装）。

### 加载器

- [x] **Fabric / Quilt 免安装器直装**：直接下载官方 profile JSON（不依赖 JVM 安装器，规避安装器内部下载失败），库文件走标准管线 + BMCLAPI 回退。
- [x] **Forge / NeoForge**：官方安装器 JAR + JVM 代理参数注入 + 15 分钟总时长上限 + 失败输出末尾采集。
- [x] 版本列表查询（本地缓存兜底）。

### 内容中心

- [x] Modrinth 搜索 / 安装 / 版本历史分组（扁平行虚拟化，千级版本丝滑滚动）/ Modrinth 整合包（.mrpack）/ 一键定位适配版本（精确坐标置顶）。
- [x] 兼容性徽章：适配版本才有安装按钮。

### 实例与 Mod 管理

- [x] 多实例隔离；导入 .minecraft / PCL2 / HMCL / Prism（仅复制，保护存档）；Mod 扫描/启停/Modrinth/整合包导入。

### UI（WPF，全中文）

- [x] 9 个页面：首页 / 游戏下载 / 实例管理 / 内容中心 / Mod 管理 / 下载管理 / 设置 / 日志 / 关于。
- [x] 安装流程图（阶段化 + 失败红 ✕ + 失败原因面板）。
- [x] 日志页：环形缓冲 600 行、批量派发、Trace 仅入文件、"只看错误"开关、自动滚动（上翻暂停）、崩溃摘要、崩溃自动联动错误过滤。
- [x] 滚动条：16px、轨道底槽、48px 最小滑块、点击/拖动轨道直接跳转（全局）、悬停高亮。
- [x] 状态栏语义着色（错误红 / 成功绿 / 中性默认）。
- [x] 启动 UX：文件完整直接启动（不跳转不补全）；缺文件才跳转下载页并显示缺失清单。

### 诊断与容错

- [x] **一键诊断包**（设置 → 导出诊断包）：最近 3 个日志 + config.json（脱敏）+ 系统信息 + Java 清单 + 实例概览（含 Mod 数量/最近 Mod）+ 最近 20 条安装/下载记录。
- [x] **崩溃原因规则匹配**：UnsupportedClassVersionError → Java 过低；重复 Mod → Mod 冲突；缺前置 → Fabric API 缺失；专项规则优先于泛化 JVM 规则；崩溃摘要自动附"可能原因"。
- [x] **结构化崩溃摘要**：退出码 / Java / 内存 / 版本 / 加载器 / Mod 概况 / 最后 50 行游戏输出，自动写入文件日志；崩溃时自动切换日志页并开启错误过滤。
- [x] **崩溃本地诊断与修复引擎**（第 78 轮）：`CrashResolutionEngine` 27 条规则，每条产出「诊断 + 动作」（自动 / 引导 / 仅提示）；自动修复覆盖调内存、换/装 Java（按 `class file version` 推大版本、优先复用已装）、补 Fabric API、补全游戏文件；引导修复打开 mods/shaderpacks/saves/驱动页/账户区；罕见崩溃给稳定指纹 + `mailto:` 求助（程序不发送、不存凭据）；诊断在后台线程，修复全异步；三条 Guard 锁死爆炸半径（配置键白名单 + 枚举名扫描 + 源码禁令）。单测 #79–#82。
- [x] **存档自动备份**（第 79 轮）：`SaveBackupPolicy`（触发矩阵 / 保留 N 份 / 只清自己创建的 `saves-*.zip` / 3 GB 自动上限）+ `SaveBackupService`（`.tmp` 后原子改名）；默认「退出后」，可切「启动前 / 两者」；接入启动前（await，游戏动存档之前）与退出后（进程已结束）；实例页可「立即备份 / 打开备份文件夹」。**只读 saves，绝不移动或删除存档**——单测直接断言原 `level.dat` 仍在。单测 #83。
- [x] **性能仪表盘**（第 79 轮）：`GameLauncher` 每 5 秒采样 JVM 工作集，峰值 + 日志 FPS（`fps: N` / `N fps`，过滤 0 与 >1000）写入实例元数据；结论三档（峰值 ≥ 92% -Xmx → 吃紧，平均 FPS < 30 → 偏低，其余健康，无记录 → 无数据）；整机 CPU/内存用 `GetSystemTimes` + `GlobalMemoryStatusEx`（零 NuGet，3 秒后台刷新）。单测 #84。
- [x] **Ctrl+K 全局搜索**（第 79 轮）：`CommandPalette` 纯逻辑索引（10 操作 / 9 页面 / 7 设置 + 实例 + 已装模组 + 动态 Modrinth 行），多词 AND、前缀 > 包含、完全相等加权；MainWindow 浮层 + Ctrl+K/↑↓/Enter/Esc + 点背景关闭 + 双击执行；守卫：每个 Action 常量必须在 `RunPalette` 被处理、每个页面 payload 必须在 `CreatePage` 有分支。单测 #85。
- [x] **液态玻璃（默认关闭，红色警告把关）**（第 80 轮）：`GlassEffectPolicy` 纯逻辑（模式矩阵 / 是否需要警告 / 是否允许瞬时模糊）+ `GlassVisuals`（半透明表面刷放在最后一个 merged dictionary，关闭即摘除，切主题不失效）+ 红色警告窗（取消即回滚、不写配置）；Guard 从"全禁模糊"改为白名单（`GlassGuardPolicy`：只允许 MainWindow 的 `Glass*` 元素、DataTemplate 内一律违规、C# 只允许 `GlassBlur*`）。单测 #86–#87。
- [x] **界面重构：光影壁纸 + 玻璃卡片**（第 81 轮）：无边框窗口（`WindowChrome` 保留缩放/贴边，自绘标题栏拖动、双击最大化、三个窗口按钮、12px 圆角裁剪且最大化转直角）；矢量光影壁纸（`DrawingGroup` 极光 + 暖光 + 噪点，全部 Freeze，无位图无 Effect）；卡片 16px 圆角 + 半透明 + 1px 渐变 `CardEdgeBrush`；侧栏透明 + 细线、导航选中淡白胶囊；首页去掉大色块、标题 32px、主按钮胶囊 / 次按钮描边；玻璃模式一次替换 14 个画刷（含文字转白）；砍掉不可见的 Mica 选项（旧配置 `System` 自动折叠为玻璃皮肤）。新增 `scripts/screenshot-preview.ps1` 做真机视觉回归。单测 #86 更新。
- [x] **无实例时的启动引导**（第 82 轮）：`LaunchGuardPolicy`（纯逻辑，`Decide` 五态 + `Describe` 文案/目标页）统一判定「没实例 / 没选中 / 没账户」；`LaunchAsync` 首行拦截，无实例直接 `Navigate("Download")` + toast；按钮文案与卡片提示同源（`LaunchButtonText` / `InstanceCardHint`），`Instances.CollectionChanged` 一次性通知，加删实例当场改字；首页占位符「未选择实例 / 未登录」；静态守卫断言 VM 必须走该策略。单测 #88。
- [x] **玻璃抛光**（第 82 轮）：卡片 40% 黑 → 30% 深蓝、侧栏 20% → 15%；`CardEdgeBrush` 改斜向三段高光；次要文字 70% → 85% 白；`Card` 内边距 18 → 22；壁纸底色抬亮并加底部环境光；主按钮加径向渐变光晕（不用 `DropShadowEffect`，守住第 67 轮禁令）。截图脚本修掉"截到残留进程"与"最小化窗口 353×56"两个坑。
- [x] **首页矮窗口被裁**（第 91 轮）：`HomeView` 的卡片区包进 `ScrollViewer`（纵向 `Auto`、禁横向），`CardArea.MinHeight` 绑定到 `HomeScroller.ActualHeight`——内容比视口矮时撑满（保持原来"两张卡填满窗口"的观感）、更高时正常滚动、正好塞下时不会多出一条滚动条；hero（欢迎语 + 启动按钮）留在滚动容器外。冒烟在 760/600/480 三档断言「性能概览」底部可达、hero 按钮不随滚动移动，并守住实例列表的 `MaxHeight`（第 68 轮的容器全量生成坑）。单测 #115。
- [x] **设置未保存拦截 + 关闭闸门**（第 90 轮）：`SettingsDirtyPolicy`（快照对比，纯逻辑）在进设置页时拍基线、保存后重拍；`Navigate` 的第一道门在改页面前问「保存 / 放弃 / 取消」，放弃走 `LoadSettingsFromConfig`（不重新 `LoadAll`，否则重复添加账号实例）；`OnClosing` 同样询问；保存按钮旁常驻「有 N 项修改尚未保存（改了哪几项）」（500 ms tick 刷新——这些字段是纯自动属性，没有变更通知）。关闭侧：`Exit` 的选项文案改为「直接退出（有下载时仍会确认）」（旧文案仍能解析），并把 `ExitFromTray` 接到同一个闸门 `ConfirmExitWithActiveJobs`——它以前直接 `_shuttingDown = true`，绕过了弹窗。单测 #113–#114，新增 `--settings-smoke`。
- [x] **第 88 轮复核的五处加固**（第 89 轮）：① `VerifyHash(` 守卫从"只扫 DownloadEngine.cs"扩到**整个 `src/`**（唯一豁免是 `DownloadIntegrity.cs` 的说明注释，且断言那里不得重新定义）；② `DownloadJournal.Forget`——两个清理入口删掉台账内的残片时同步写 `-` 记录并重载「继续下载」横幅；③ 设置页写明两个入口分工（只处理下载残渣 / 扫整个存储目录）；④ 忙碌态：按钮文案切换 + 禁用 + 不确定态进度条 + 冻结勾选，报告整体替换；⑤ 帮助主题补「版本目录有残留文件但启动不了 → 用删除实例（含文件）／补全文件」。单测 #111–#112。
- [x] **存储垃圾扫描与选择性清理**（第 88 轮）：`StorageCleanupPolicy`（纯白名单 + 分类元数据 + 空壳判定 + 日志保留期）+ `StorageCleanupService`（扫描 → 报告 → 按勾选执行）。9 类：图标缓存、OptiFine 缓存、版本清单、`update/` 残留、>14 天日志、无主 `.part`、空壳版本目录（默认勾），加载器缓存（`CostsOffline`）与可续传残片（默认不勾）。扫描与删除前各校验一次白名单，删除前复查 `ActiveDownloads` 与 `IsEmptyVersionShell`；`instances/`、`backups/`、`java/`、配置与台账一律拒绝；确认框默认「否」，结果如实回报释放量与占用数。图标缓存从 `%TEMP%` 迁至 `cache/icons`（否则这一项根本无处可清）。单测 #106–#110，新增 `--cleanup-smoke`。
- [x] **下载残留可收可查 + 完整性判定收紧**（第 87 轮）：`DownloadIntegrity` 把"没有校验值"从"等于完整"改成三级判定（哈希 → 声明大小 → `Unverifiable`），缓存命中只信 `Match`、新下载只对 `Mismatch` 重试，gzip/brotli 响应不做大小比对；`ActiveDownloads` 登记正在写的目标路径，`DownloadResidue` 按归属（正在下载 / 台账里可续传 / 无主）扫描并清理，值得续传的先问一句；设置页给「清理残留下载文件」入口并如实回报释放量，下载页只在超过阈值（默认 200 MB 或 50 个）时给一条可关闭的提示；`UpdateStaging` 在每次更新检查时清掉 `update/` 里超过 24 小时且不是待安装包的残片（待安装包与年龄无关地豁免）。单测 #100–#105，新增 `--residue-smoke` 真实渲染。
- [x] **Forge 整合包安装 404 修复**（第 86 轮）：`ForgeMavenVersion(mc, ver)` 把裸 Forge 版本补成 `1.20.1-47.4.22`（已组合/旧版完整 id 均兼容，NeoForge 不改写）；`BuildMavenInstallerUrl` 让路径与文件名同源；`ToOfficialUrl` 按 group id 反解 `/maven/…`（Forge/NeoForge/Fabric/Quilt 四条往返），使镜像 404 能换回官方；安装器下载前删除残留 `jar`/`.part`（无校验值时的"已完整"误判）；两条线路都失败时报错列出两个地址与"版本号要带 MC 前缀"的提示；强制官方时不静默改走镜像。单测 #97–#99，`--forge-url-check` 实测两条线路各下到 8.4 MB ZIP。
- [x] **下载线路三态**（第 85 轮）：`DownloadPolicy{Auto,Official,Mirror}` + `DownloadRoutePolicy`（纯逻辑：40 ms 裕度择优、>3 s 视为不可达、两条全死保持原选择）；`NetworkProbe` 改 HEAD（拒绝 HEAD 时只取 1 字节）、5 分钟缓存 + 单飞，下载路径只读静态 `Preference`；`NetRouting.BuildHosts` 按策略给出路由集合（强制单线只给一条，核心文件不静默换线、非核心可回退）；`DownloadEngine` 补跨线路重试（只换一次）；设置页三选项 + 实时解释（当前毫秒数 + 为什么优先这条）；老配置 `downloadSource 0/1` 统一迁成 Auto（旧值多为探测自动写入）。单测 #95–#96。
- [x] **实例管理三处根治**（第 84 轮）：① 新建实例改为纯导航（`Navigate("Download")`），实例只在安装完成/导入时诞生；② 删除改为 `DeleteChecked`——先删文件夹（3 次重试 + 清只读）并复查目录真的消失，才删 `instances.json` 记录，失败返回 `FilesLocked` 且保留记录；③ 新增「扫描未注册实例」：每个实例目录写 `.aurora-instance.json` 身份证，扫描可找回幽灵实例（**保留原 Id**）、清理死链，陌生文件夹只报告不改动。④ 数据根自愈：`DataRootPolicy.IsEphemeral/Sanitize` + `Save()` 拒绝持久化临时根 + `Load()` 修复被冒烟测试写坏的配置（`UseTestRoot` 隔离配置文件本身）。单测 #91–#94。
- [x] **内存自动分配 + 不骗人的释放**（第 83 轮）：`MemoryAllocationPolicy` 纯逻辑（原版 2 GB → 模组分档 3/4/5/6 GB，光影 ≥4 GB，上限 = 物理内存 55% 并对齐 512 MB，硬顶 16 GB，地板 1 GB；`Describe` 解释为什么是这个数）；在 `LauncherService` 里解析（按钮/首启引导/加载器修复同源），`-Xms` 压到 ≤ `-Xmx`；`AutoMemory` 是开关、`MaxMemoryMb` 保留手动值（老配置默认关、新装默认开）；手填值不静默改写，超 55% 输入框下方实时变红。`MemoryHygiene` 只清自己的图标缓存并 `SetProcessWorkingSetSize(当前进程)`，手动「释放启动器占用内存」在 `Task.Run` 内跑，托盘闲置 5 分钟自动清一次（不含强制 GC）。单测 #89–#90。

### 稳定性与工程纪律

- [x] **测试分层**：`--unit`（47 项，离线、毫秒级、CI 必跑，**连跑 20 次实测无抖动**）/ `--integration` / `--all`（54 项，含网络集成）——网络抖动无法染红 CI。
- [x] **复核落地**（第 53 轮）：加载器选择哨兵单一来源（`IsLoaderSelected`/`IsAutoPick` 派生布尔 + 常量，杜绝散落字符串比较）；旧卡片回调改用 GenerationGate 代数标记（集合成员检查在异步 null-push 场景不可靠）；新增 LoaderCardVM 状态机回归测试 #30（null 归一 + 追加不选中）；publish.ps1 新鲜度检查仅 git 仓库内启用、`--unit` 写死。
- [x] **自动更新 + 签名验证**（第 57 轮）：启动静默检查 → 后台下载 + SHA-256 校验 → 重启自动换装（分离 cmd 脚本带重试）；latest.json 携带 **RSA-4096 签名**（公钥内嵌、私钥作者保管）防篡改，round 严格递增防降级；设置页「更新」卡片（自动检查开关 + 手动检查）；"已更新"轻提示。作者流程：publish.ps1 → sign-release.ps1 → 上传 EXE + latest.json（见 docs/auto-update.md）。多通道（stable/beta）与增量更新为后续可选扩展。
- [x] **更新体验三态化**（第 58 轮）：启动检查默认弹窗提醒（只有「立即更新 / 关闭」两选项）；设置页三态下拉开关（自动更新 / 提醒但不自动更新 / 不获取更新，默认第二种）、可配更新源地址、手动「检查更新」+ 状态文字与进度条（检查不确定进度 / 下载按字节百分比）；`UpdatePolicy` 纯逻辑单一来源 + 旧布尔配置一次性迁移；单测 #32。
- [x] **一键安装 Java 21**（第 59 轮）：内置 Adoptium API（NetRouting 竞速）→ DownloadEngine 后台下载 → SHA-256 校验 → 静默解压到启动器自有 `java/` 目录（zip-slip 防护）→ 自动设为默认 Java；已安装则复用、API 不可达时离线兜底；JavaFinder 纳入自有目录扫描。设置页有按钮/进度条/状态文字；单测 #33。
- [x] **文档归档**（第 59 轮）：新建 `CHANGELOG.md` 收录第 1–58 轮全部历史（正文原样搬迁）；README 精简为「简介 / 当前状态速览 / 核心架构 / 最新一轮的更新内容 / 快速开始」五个板块；静态说明移入 `docs/PROJECT_OVERVIEW.md`；轮次编号 Guard 改扫 CHANGELOG 并要求末轮 = CurrentRound−1。
- [x] **图形化崩溃报告**（第 60 轮）：`CrashAnalyzer.BuildReport` 输出结构化全部匹配原因；游戏非零退出时日志页顶部显示红色卡片（原因+建议 / 一键导出诊断包 / 前往求助 / 关闭），自动切「只看错误」，正常退出清除卡片；单测 #34。
- [x] **CurseForge 搜索浏览**（第 60 轮）：内容中心「CurseForge」子页，用户自备 API Key、遵守平台下载限制（不抓文件，跳官方文件页）、网页搜索与申请 Key 入口；单测 #35。**直接下载仍不支持**（平台政策限制，见下方待办）。
- [x] **OptiFine 一键安装**（第 60 轮）：官网下载页解析 → ad 页短时令牌 → 流式下载 → JAR 结构校验 → 本机 Java 唤起官方安装器 GUI；失败兜底「打开官网 / 选择本地安装器」；单测 #36。**官方安装器无静默参数**（与 Forge 不同），最后一步需用户点 Install。
- [x] **回退第 66 轮「切一次即卡死」回归**（第 67 轮）：新增 `--nav-smoke` STA 运行时冒烟诊断，确认不是样式异常而是规模效应——隐藏页释放 Effect/CacheMode 使每次切换重生成整页软件阴影位图。彻底移除 Card 的 Effect/CacheMode/Triggers、所有显式阴影引用与 DropShadowEffect 资源、不确定进度条的全部动画时钟（改静态渐变）、DpiChanged 缓存遍历；Guard #47 改为禁止性规则；保留页面常驻、容器回收、冻结画刷、BeginInvoke。
- [x] **隐藏页幽灵动画与渲染资源累积**（第 66 轮，用户四个嫌疑犯逐条取证）：ProgressBar 模板的 `RepeatBehavior="Forever"` 动画无 `StopStoryboard`，第 65 轮页面常驻后永不释放且随 `IsIndeterminate` 状态切换叠加 → 累积型崩溃。改为 `MultiDataTrigger`（IsIndeterminate + IsVisible 绑定式门控，只读 DP 必须走绑定）+ `ExitActions/StopStoryboard`；隐藏 Card 用 DataTrigger 释放 Effect/CacheMode 并缩小阴影半径；全局 ListBox 补齐容器回收与 CanContentScroll；唯一同步 `Dispatcher.Invoke` 改 `BeginInvoke`；`PageActiveConverter` 复用冻结画刷。单测 #47 静态 Guard + 40 次显隐模拟，负向验证删 Stop 即红。
- [x] **反复切换页面卡死：架构级根治**（第 65 轮，第三次反馈）：查明前两轮只省掉 XAML 解析与重复异步初始化，真正的同步成本是 `ContentControl.Content` 换装导致 WPF 每次销毁/重建整棵可视化树（103 行版本列表、更新日志卡片、`BitmapCache` + `DropShadowEffect` 纹理）。改为 `PageHost` 常驻 Grid + 惰性创建 + 只翻 Visibility（`PageHostPolicy`），VM 删除 `CurrentPage`；`DpiChanged` 只刷可见页；隐藏页 `!IsVisible` 判定避免 Collapsed 页强制布局；下载页去抖补调度移到 `Navigate`。单测 #45（40 次切换只创建 4 页）+ #46（静态 Guard 禁止 Content 换装）。
- [x] **稳定性机制补测与边界收口**（第 64 轮，评审驱动）：`CoalescingSingleFlight<TArg>` 把「一个 pending + 最新参数 + 只重跑一次」变成可注入测试的纯契约（#42），两处 VM 复用；骨架卡从去抖中拆出、setter 同步即时渲染，去抖回调加「页面/版本仍匹配」判定并支持切页取消与回来补调度（#43）；`Versions` 有 SelectedItem，Replace 后用 `SelectionPolicy.RestoreByKey` 按 id 恢复（#43），无选中语义的搜索结果/Java 列表记录边界；Guard #41 改为模式匹配（任意 `private bool _x` + early-return 或 HookVm），不再硬编码变量名（#44）；`NavigationPolicy` 增加可选子 key，确认内容中心子页走 SelectContentKindCommand 不经过导航（#43）。README 附 6 项实测的「自动化覆盖 / 需真机」矩阵。
- [x] **页面切换卡死（未响应）根治**（第 63 轮）：缓存页每次重新附加都会再触发 `Loaded`，异步初始化重入（版本清单 / 4 路加载器 / Modrinth 搜索 / Java 探测并发 + `Clear()` + N 次 `Add` 重建集合）榨干 UI 线程。分层修复：`NavigationPolicy` 同页早退、页面 `_initialized` 一次性守卫 + `DispatcherPriority.Background`、VM 单飞 / 合并式单飞 + `SelectedVersion` 180 ms 去抖、`BulkObservableCollection.Replace()` 一次 Reset；顺带修 LogsView 缓存后自动滚动失效、更新器窗口防重复换装；单测 #40 / #41 + 静态 Guard 防回归。
- [x] **多实例级 Java 配置**（第 62 轮）：`GameInstance.JavaPath` 可选覆盖；`GetJavaForAsync` 优先级「实例 → 全局 → 合并版本 JSON 自动」，启动与加载器修复共用单一入口；实例管理页 Java 下拉 + 推荐提示（`JavaRequirement`：≤1.16.5→8 / 1.17–1.20.4→17 / ≥1.20.5→21）；旧元数据缺字段按 null 无需迁移、自定义路径探测不到也保留；单测 #38。
- [x] **版本列表一键更新**（第 62 轮）：游戏下载页常驻版本卡片 + 「一键更新」按钮；启动检查发现新版（含弹窗被关 / 模式 1 静默下载）时高亮提示，点击直达两按钮弹窗 → 下载 → 可见换装窗口；未检查 / 已最新也明确回报，不是死按钮；`UpdateBannerPolicy` 防降级 + 单测 #39。
- [x] **更新换装可见化**（第 61 轮）：以 `--apply-update` 模式从临时副本启动自身（避免运行中的 EXE 自锁），显示置顶无边框进度窗口覆盖「等待旧进程退出 → 二次 SHA-256 校验 → 带可见重试的替换 → 写版本信息 → 重启」；失败给可读错误 + 重试 / 关闭，旧隐藏 cmd 仅作启动失败兜底。设置页下载进度补百分比 / 已下载大小 / 速度并同步状态栏，新增「立即重启并应用更新」按钮；`UpdateApplyArgs` 交接参数纯逻辑 + 单测 #37。
- [x] **页面切换缓存 + 更新日志页**（第 55 轮）：侧栏页面改为惰性构建一次、之后复用（原每次点击重建整棵 XAML 树，连点即"未响应"）；侧栏「日志」下新增独立「更新日志」页面（说人话卡片式呈现，内容 HelpContent.Changelog()，从帮助页移出）；交互元素 65→66（49 直接绑定 + 15 样式命令 + 2 事件处理器）。
- [x] **下载慢速看门狗**（第 52 轮）：聚合速度连续 4 窗口（约 20 秒）低于 16 KB/s 即中止当前线路并自动切 MCIM 镜像（.part 断点保留）——"慢而不断"的 CDN 线路不再表现为进度条卡死；镜像同样爬行则快速失败可见报错。`SlowTransferWatcher` 纯逻辑 + 单测 #29。
- [x] **下载页体验**（第 47 轮）：加载器卡片选中版本即时显示（骨架 + 逐卡填充 + GenerationGate 代数门防竞态，#27 项单测覆盖乱序场景）；Fabric 实例安装后自动安装适配的 Fabric API 前置（设置项默认开启，关闭退回弹窗询问；第 48 轮补非阻塞 toast 告知 + 触发原因写入文件日志）。
- [x] **文档基线防线**（第 48 轮）：新增 `Guard: doc baselines agree`——README / ROADMAP / TEST_RESULTS 三份文档的轮次标记与 Unit/All 计数必须一致，否则测试红（"文档和代码对不上"从约定变成机制）；交互元素审计全量重数修正 66→65（49 直接绑定 + 14 样式命令 + 2 事件处理器，精确闭合）。
- [x] **发布与体验收尾**（第 49 轮）：publish/ EXE 更新到第 49 轮构建（63MB 单文件，旧 EXE 被运行中进程锁定→关闭后替换；**每轮功能改动必须重新发布**）；Forge/NeoForge 大元数据内存缓存 30 分钟 + 解析结果缓存（切换版本零网络）；设置页 Java 列表滚轮边缘转发页面滚动；Card 阴影 BitmapCache 缓存（滚动流畅性）。
- [x] **双路径一致性单测（离线）**：缓存命中分支 vs 本地优先分支逐字段一致——两分支分歧在每次 push 即被捕获，不等每日集成。
- [x] **网络请求收敛**：manifest / 版本 JSON / 资源索引 / Modrinth / 加载器元数据全部经 NetRouting（策略单点）；仅 OAuth POST 独立（无镜像语义）。
- [x] **全量交互元素审计**（第 40-42 轮建立，第 46 轮升级，第 48 轮重数修正为 65）：65 个交互元素（Views 49 + MainWindow 16，含首次启动引导）全部有功能反馈路径，精确闭合 49 直接绑定 + 14 样式命令 + 2 事件处理器；修复 2 个静默绑定失败死按钮（安装 / Modrinth 打开）；Guard 扫描规则扩展到七类交互属性（Command / Click / Checked / Unchecked / SelectionChanged / MouseDoubleClick / KeyDown）且覆盖全部桌面 XAML（含 Styles/Themes 的样式 Setter 命令独立核对）；新增警告级死命令探测（首次运行发现 3 个死代码候选）；报告更名为 [INTERACTIVE\_UI\_AUDIT](INTERACTIVE_UI_AUDIT.md)。
- [x] **首次启动引导**（第 42 轮）：Java 检测 → 推荐版本一键安装 → 完成，仅出现一次。
- [x] **网络环境自动感知**（第 42 轮）：启动探测官方 vs BMCLAPI 延迟，数据驱动切换默认线路（用户显式选择优先）。
- [x] **安装完成反馈**（第 43 轮）：成功横幅 + 速度归零 + "取消下载"完成后自动变"启动游戏"。
- [x] **机制防线（自测内置，违规即红）**：
  - 版本 JSON 原始反序列化仅允许 VersionResolver（第 36 轮上线当天即抓到 1 处裸读违规）；
  - 构建产物 classpath == ResolveAsync 库集合 + client jar（锁死"路径分叉"类 bug）；
  - 共享 HttpClient 禁止缓存/释放/using 包装（第 37 轮微软登录 bug 的回归锁）；
  - 实例元数据 IO 仅允许 InstanceManager（第 31 轮脏数据 bug 的回归锁）。
  - 页面 XAML 中嵌套在 ScrollViewer 内的 ListBox 必须自带 `MaxHeight`/`Height`（第 68 轮，锁死"测量高度无穷 → 虚拟化失效"）；
  - 页面异步初始化必须走 `PageLifecycle.OnFirstShown`（第 68 轮，锁死"空闲预热触发联网/扫 Java"）；
  - 日志文件 I/O 不在调用线程、日志页隐藏期冻结、切页三段计时（第 69 轮：UI 线程不再等磁盘）；
  - 竞速输家即时取消 + 永久失败（404 / DNS）不重试（第 70–71 轮）；
  - 版本清单恒为官方 URL、双主机必须同时在场、旧配置废弃域名自愈、断网用本地清单缓存（第 71 轮，锁死「下载页空白」）；
  - 多任务下载：一任务一 token/阶段、并发上限 3、单任务撤销不波及他人、UI 刷新 ≤10 次/秒（第 72 轮）；
  - 同目标路径全程序单写者（`PathWriteGate`）、排队撤销后不得启动、有任务时关窗必须确认（第 73 轮）；
  - 增量补丁必须校验目标 SHA-256 且清单补丁字段单独签名、便携根每次强制覆盖、未完成下载只认 `.part`、单实例按数据根命名（第 74 轮）；
  - 窗口关闭必须结束进程、唤起必须收到应答才退出（第 75 轮）；
  - 游玩时长按心跳结算且 ≤24h、预检不误报平台 id 与 Fabric 模块、导出不含存档/日志/servers.dat（第 77 轮）；
  - 崩溃自动修复只能改 `MaxMemoryMb/MinMemoryMb/JavaPath` 或向实例下载文件（枚举名禁 Delete/Remove/Kill/Env，全枚举扫描）、诊断必须在 `Task.Run` 内、`ApplyCrashFixAsync` 方法体禁 `Delete/Move/SetEnvironmentVariable/Kill`（第 78 轮）；
  - 备份只读 `saves` 且只清理自己创建的 `saves-*.zip`、搜索索引里的每个 Action 必须在 `RunPalette` 有分支、每个页面 payload 必须在 `CreatePage` 有分支（第 79 轮）；
  - 启动拦截只有一张表：`LaunchAsync` 必须走 `LaunchGuardPolicy.Decide`，按钮文案必须来自 `LaunchButtonText`（第 82 轮，锁死"按钮点了没反应"）；
  - 内存分配不越界：自动模式的每个结果 ≤ 物理内存的 60%（实际 55%）、≤16 GB、≥ 地板、512 的整数倍（全参数扫描）；清理只碰本进程（`MemoryHygiene.cs` 禁 `Process.GetProcesses` / `GetProcessesByName` / `OpenProcess(` / `"javaw"`），`GC.Collect` 全项目只允许出现在该文件、且调用点必须在 `Task.Run` 内（第 83 轮）；
  - 实例删除必须先删文件夹再删记录（`Instances.Remove` 只能在 `if (result.Ok)` 内）、失败保留记录；`Scan` 只加记录不动文件夹、恢复必须保留原 Id；「新建实例」只能 `Navigate("Download")` 不得 `Instances.Create`；冒烟测试必须 `UseTestRoot` 隔离配置文件、`Save()` 拒绝持久化临时根（第 84 轮）；
  - 测速只用 HEAD（HEAD 被拒时最多取 1 字节）且结果缓存 5 分钟、并发单飞；`NetRouting` 只读缓存好的 `Preference`，`DownloadEngine` 不得引用测速；跨线路只允许换一次，强制单线时核心文件不得静默换线（第 85 轮）；
  - Forge 安装器地址必须经 `ForgeMavenVersion` 补 MC 前缀（裸/组合/旧 id 三种写法都要对，NeoForge 不改写），路径与文件名必须由 `BuildMavenInstallerUrl` 同源生成；`/maven/` 镜像地址必须能按 group 反解回官方；安装器下载前必须删除残留 `jar`/`.part`；镜像回退必须尊重「仅官方」策略（第 86 轮）；
  - 缓存命中必须走 `DownloadIntegrity.IsTrustworthyForSkip`（只有 `Match` 才能跳过，`Unverifiable` 必须重下），`DownloadEngine` 不得再出现 `VerifyHash(`；每个文件操作必须 `ActiveDownloads.Register` 登记归属，残留清理不得删除正在下载的文件；`update/` 清理必须豁免当前待安装包（`AuroraLauncher.new.exe` + `build-info.txt`）（第 87 轮）；
  - 存储清理只能删 `StorageCleanupPolicy.IsAllowedPath` 放行的路径，扫描与删除前各判一次，删除前还要复查 `ActiveDownloads` 与空壳状态；服务里不得出现 `Directory.Delete(_root`，唯一允许的递归删除只作用于已验证的空壳目录；`CostsOffline` 分类必须默认不勾，且 UI 的默认勾选只能读 `CleanupCategoryInfo.DefaultSelected`（第 88 轮）；
  - `VerifyHash(` 的禁令必须覆盖整个 `src/`（只放行 `DownloadIntegrity.cs` 的说明注释，且那里不得重新定义）；两个清理入口删掉台账内残片时必须调用 `DownloadJournal.Forget` 并重载续传横幅；清理的忙碌态（文案切换 + 禁用 + 进度条 + 冻结勾选）与两入口分工文案必须存在（第 89 轮）；
  - 导航闸门必须在改页面之前（`ConfirmLeaveSettings` 早于 `CurrentPageKey = target`）；回滚只能走 `LoadSettingsFromConfig`；`OnClosing` 与 `ExitFromTray` 都必须经过确认，且托盘退出的确认要早于 `_shuttingDown = true`；「直接退出」的选项文案必须写明有下载时会确认（第 90 轮）；
  - 首页必须有具名滚动容器（纵向 `Auto`、禁横向），hero 区不得被包进去，实例列表必须带 `MaxHeight`；设置页/帮助页的滚动容器不得被重构掉（第 91 轮）；
  - 加载器/整合包不完整不得启动：`LaunchReadiness` 不可覆盖的分支（加载器缺失）与可覆盖的分支（模组缺失）必须分开；`BuildLaunchSpecAsync` 不得为加载器实例构建原版命令行；导入必须在下载之前写入 `ExpectedModCount` + 清单；性能采样不得退回 `Task.Run`，且「未采集到」不得写成数字（第 92 轮）；
  - 异步命令必须自带防重入（`RelayCommand` 的 `CommandGate`）；**禁止 `new RelayCommand(_ => _ = XxxAsync())` 这种「火后不管」写法**（命令秒回，门形同虚设）；耗时按钮的 `Content` 必须绑 `IsRunning`（第 93 轮）；
  - 内容中心状态文案必须读实际列表数、切分类清残留、子导航高亮只认 ContentKind；缓存目录必须只读展示；卸载必须白名单 + 两次确认且默认保留存档与 Java（第 99 轮）；
  - 安装器 jar 必须回报字节心跳、线路失败自动换镜像且重试前清 .part 与续传台账；失败任务不贡献总进度、失败结论不得被二次覆盖；JDK 发行版只允许白名单两家且必须带 SHA-256（第 98 轮）；
  - 未完成任务进度封顶 99%（Floor）、installer-run 转不确定态、2 分钟无新文件完成即判卡住；模组体检按实例加载器选元数据、跨加载器依赖必须过滤、按 modId 去重；自动补齐查询只能带唯一 loader facet（第 97 轮）；
  - 补全/安装流程必须包进 DownloadJob（入队即 准备中）、标题栏只绑 HeaderText 且由活跃任务驱动；无任务时全部取消置灰；看门狗按阶段超时；导航高亮只由 CurrentPageKey 生成（第 96 轮）；
  - 本地版本 JSON 优先；第三方加载器 id 缺本地 profile 时不得请求 Mojang 官方清单；安装器「成功」必须校验 `versions/<id>/<id>.json` 真实存在，且跑 JVM 前必须预填充 installer jar 的依赖（第 95 轮）；
  - 跑 Forge/NeoForge 安装器前必须 `LauncherProfiles.Ensure(gameRoot)`（缺文件安装器直接 exit 1），且补文件逻辑全文件只允许一处；安装器参数的目标目录必须用传入的 `gameRoot`，不得读全局配置（第 94 轮）；
  - 模糊白名单：`BlurEffect` 只允许 MainWindow 的 `Glass*` 元素、DataTemplate 内一律违规，C# 侧只允许 `GlassBlur*`；`DropShadowEffect` / `BitmapCache` / 样式级 Effect 仍全禁（第 80 轮，改写自第 67 轮的全禁规则）；
  - 唤醒只在界面线程调度、应答 == 窗口已显示、托盘动作移出菜单消息循环、4 秒退出看门狗 + 显式释放互斥（第 76 轮）；
  - 文档基线（轮次 + Unit/All 计数）、CHANGELOG 编号连续性与历史范围声明（第 59–60 轮）。
- [x] **安装状态单一来源**：`GameInstaller.Evaluate(versionId) → InstallState`（含缺失清单），启动前补全 / 状态显示统一走它。
- [x] **最大化贴靠工作区**（第 105 轮）：用 WM_GETMINMAXINFO 钩子按显示器工作区回答最大化矩形，任务栏不再被遮挡
- [x] **全屏布局 / 进度诚实 / 下载管理集成 / 全线审查**（第 104 轮）：最大化贴靠工作区；空任务 0%、单文件按字节计进度；Java 与启动器更新入队；修复任务槽泄漏、暂存包覆盖、卸载清单越权、日志写竞争、配置原子性、跟随系统主题、async void 崩溃等
- [x] **启动反馈与恢复下载线路修复**（第 103 轮）：未登录/无实例/未选实例都有可见 toast；未登录也照常补齐缺失文件。退役 BMCLAPI 域名可反解，client jar 的 `/v1/packages` 路径不再当成模糊路径；恢复下载先归一 URL 再按当前线路策略重试
- [x] **卸载残留根治**（第 102 轮）：临时清理进程在启动器退出后立即删除 EXE 与残留文件（路径二次白名单校验）；HKCU 下 5 类专属注册表项默认勾选清理、只动当前用户并逐项验证；失败才回退到重启后删除
- [x] **更新下载可见可取消 + 引导前置存放选择**（第 101 轮）：所有下载路径共用可见进度窗（轮次/百分比/体积/速度），取消接真实令牌并清理半成品但保护旧暂存；安装阶段明写不可取消。首次引导改为 4 步，步骤 2 在游戏下载之前独立询问「系统默认 / 全部放 EXE 目录」并显示真实路径
- [x] **一键补齐缺失前置**（第 100 轮）：`MissingDependencyFix` 纯策略（精确 slug / 标题匹配、`VersionRangePolicy` + 加载器 / MC 版本四道门、无 SHA-1 拒绝下载）+ `AutoFixSession` 重扫结论（下载成功但重扫没认到 → `NotRecognized`）；逐项失败不中断、七种结果都有中文说明与手动出口；补齐是可取消的 `DownloadJob`，本地管理页与启动前检查窗双入口
- [x] **整合包完整性 + 启动闸门 + 运行判定**（第 92 轮）：`LaunchReadiness` 纯策略（加载器缺失不可覆盖 / 模组缺失可覆盖）、导入前落盘预期与清单、按清单补全、运行判定带退出码、采样改专用线程
- [x] **命令层防重入**（第 93 轮）：`CommandGate` 单槽 + 异步命令默认带门 + 执行中文案联动；顺手修掉三个「点一次跑两次」的火后不管命令
- [x] **Forge/NeoForge 安装器缺 launcher_profiles.json**（第 94 轮）：安装前自动补齐（缺→建、好→不动、坏→备份重建）+ 失败原因翻译 + `--forge-install-smoke` 实跑通过
- [x] 零外部 NuGet 依赖；单文件自包含 EXE（63 MB）；MIT 许可与合规说明。
- [x] CI：`unit` job 每次 push/PR（离线）；`integration` job 每日定时（联网全量）；`scripts/run-tests.ps1 [-Full]`。

## 待办

### 功能待办

- [ ] **实例级内存覆盖**：第 83 轮的自动分配已按"当前实例的模组数与光影"计算，但手动值仍是全局一份。若有人希望"这个整合包固定 8 GB、原版实例保持 2 GB"，需要给 `GameInstance` 加一个可空的 `MaxMemoryMb` 覆盖字段（比照第 62 轮的 Java 覆盖），并在设置页显示"跟随全局 / 本实例单独设置"。
- [ ] **CurseForge 官方直连下载**：第 60 轮已实现搜索/浏览/官方网页下载；CurseForge 对多数项目返回 `downloadUrl = null`（平台政策），进一步直连需申请额外的分销权限，非纯技术问题。
- [ ] **OptiFine 无头安装**：官方安装器是 GUI 且无静默参数，当前为「自动下载校验 + 唤起安装器 + 复制目录」；若要全自动需等官方提供 CLI 或改走合规的版本 JSON 构造方案。
- [ ] **NeoForge/Forge 免安装器直装**（比照 Fabric/Quilt 方案，消除对安装器 JVM 的最后依赖）。
- [ ] **微软登录体验**：设备码流程已可用；后续支持嵌入式 Web 登录窗口与多账户令牌刷新。
- [ ] **多 JDK 选择 UI**：当前已能「一键安装 Java 21」并指定任意已探测 Java；后续可按实例 MC 版本自动推荐所需 Java 大版本（含微软构建 JDK 源）。
- [ ] **AuroraCore 独立发布**：核心层零 UI 依赖、架构干净，可拆分为独立仓库 / NuGet 包，供他人构建 CLI、其他 UI 甚至服务端工具，扩大生态并分散维护压力（并非所有事都需一人承担）。
- [ ] **资源索引增量下载**与「已下载」缓存校验的 UI 进度细化。
- [ ] **崩溃规则持续扩充**：第 78 轮已从「17 条翻译规则」升级为「27 条带修复动作的诊断规则」（`CrashResolutionEngine`），并接入自动修复与求助邮件；后续按用户反馈继续沉淀规则（新规则 = 表里加一条 + 一条单测）。配套的「邮件 → fixture」工具见下方已知边界第 3 条。

### 已知边界（第 78 轮记录，明确「先不做」）

> 这三条都是第 78 轮交付后才暴露的**边缘情况**，不影响当前主路径，刻意留到有真实反馈再动。
> 记录的目的不是"以后再说"，而是把**触发条件**和**当时的判断**写下来，避免下次重新推演一遍。

- [ ] **求助邮件的 URL 长度：网页版邮件客户端比本地客户端更严**
  - **触发条件**：用户点「发送给作者」后浏览器/客户端毫无反应、也不弹邮件窗口——大概率是 `mailto:` 正文超长被浏览器（而非 Outlook/QQ 桌面版）吃掉了。`SupportReport.MaxUrlLength=1600` 是保守值，但 Chrome/Edge 的 `mailto:` 处理、以及"网页版 Outlook / QQ 邮箱"作为默认处理程序时，长度阈值并不统一。
  - **建议方案**：① 立即止血——在「发送给作者」按钮上加 tooltip：「如果邮件客户端没有弹出，请手动把已导出的诊断包发到 adagarm0216@outlook.com」；② 中期——正文本地落一份 `.eml`/`.txt` 并在点击后同时提示路径，让用户能复制粘贴；③ 根治——把正文移出 URL（只保留 subject，正文写成剪贴板 + 提示"已复制，粘贴即可"）。
  - **为何先不做**：当前 1600 的上限（正文实际约 600–900 字）对桌面客户端足够；改交互要动 UI + 文案 + 测试，收益取决于是否真的有人用网页版。
  - **判据**：出现第一条"点了没反应"的反馈，就按 ①→② 落地。

- [x] **缺失前置的通用下载 fallback**（第 100 轮落地，原为「先不做」边界）：`AutoFixStatus` 七态（`NotFound` / `NoMatchingVersion` / `Unverifiable` / `DownloadFailed` / `NotRecognized` 等）全部有中文说明，失败项一律附「打开 mods 文件夹 + 搜索 slug + 版本范围」；逐项记录后继续下一项，整批收口为可取消 `DownloadJob`，不存在「只剩一个转圈」的状态。单测覆盖七态文案与重扫降级。

- [ ] **规则库自进化：把收到的崩溃邮件一键转成单测 fixture**
  - **触发条件**：第 78 轮起，罕见崩溃会带着**稳定指纹**（`BuildSignature`，第一条异常行的 FNV-1a）和诊断包发到作者邮箱。积累到若干封之后，"读邮件 → 手写规则 → 手写断言"会变成重复劳动。
  - **建议方案**：写一个**只在内网/本地用**的小工具（可以是 `tests/AuroraSelfTest` 的一个隐藏参数，如 `--new-rule-fixture <mail.txt>`）：从邮件正文/日志里抽出崩溃片段 → 生成 `fixtures/crash/<rule-id>.log`（脱敏后）→ 生成测试骨架（`CrashResolutionEngine.Diagnose` 的期望 RuleId / Action / Payload 断言）→ 打印可直接粘贴进 `Program.cs` 的代码块。同时准备一份 markdown 模板（新增规则清单：pattern 优先级、是否需要 `Refine`、动作属于 Auto 还是 Guided、写进哪几条测试）。
  - **为何先不做**：目前只有 27 条规则、手写成本低；工具本身要设计脱敏与"邮件里混着无关文本"的抽取规则，属于典型的"先有量再做工具"。
  - **判据**：当"加一条规则"需要改超过 3 处、或累计新增规则 ≥ 5 条时启动；同时它必须默认走脱敏（复用 `DiagnosticsExporter.Sanitize`），生成的文件不得包含用户路径/令牌。

### 工程债 / 防线待办

- [ ] **NetRequest 收敛收尾**：GET 类请求已全部走 NetRouting（第 39 轮完成）；剩余按各自语义保留独立实现并**登记豁免**——① 下载引擎的文件级重试/断点策略（引擎专属，非简单 GET）；② OAuth POST（无镜像语义，已有 60s 超时）；③ CurseForge API（需 `x-api-key` 请求头）；④ OptiFine ad 页 / 下载（需 `Referer` + 短时令牌、不支持 Range）。若后续新增外部请求，必须走 NetRouting 或在此登记豁免理由。
- [ ] **HttpClient 作用域化**（第 37 轮根治版）：一次操作（安装/登录）开始时取 client 快照，操作内复用——避免中途配置变更导致前半段旧 client、后半段新 client 的时序问题；当前以「仅代理变化时重建 + 不 Dispose 旧实例」缓解，长期目标为作用域模式。
- [ ] **缓存分类：原始 vs 派生**（**P1 · 建议第 45 轮前落地**）——派生缓存（合并后的版本 JSON 等）必须带**输入指纹**，输入（父版本/加载器版本）变化即失效；当前派生缓存无指纹，父版本更新可能读到过期合并结果。当前缓解：合并结果不持久化（每次现场计算），无指纹需求；若未来引入持久化派生缓存，必须先落此防线（对应可靠性哲学第 4 条）。
- [ ] **BitmapCache 主题切换实测**（低优先）：Card 的 CacheMode=BitmapCache 在切深浅主题时依赖 DynamicResource 换刷触发重渲染——机制推导成立，需真机实测一次"切换主题后卡片阴影/背景立即更新"；若出现切换后卡片发糊，根因在此（可改为切主题时遍历清 CacheMode）。
- [ ] **加载器缓存指纹升级为内容哈希**（**已知、可接受、非紧急**）：当前时间戳指纹正确性没有问题，唯一代价是"大 XML 内容没变但 TTL 过期重拉后，解析结果多一次本地重算"；改为对大 XML 求 SHA-256（几十毫秒）可避免该开销。**明确不做除非**用户反馈"元数据更新了但列表没变"——未来接手者请勿误以为这是遗漏。

## 设计取舍记录

- **UI 框架**：选 WPF 而非 Avalonia——目标为 Windows 且 SDK 内置、零额外依赖；VM 层与核心已解耦，跨平台可迁移 Avalonia。
- **加载器安装**：Fabric/Quilt 免安装器直装（profile JSON 与标准管线合并）；Forge/NeoForge 因格式复杂保留官方安装器（带代理注入与超时上限）。
- **下载策略**：四线路并行竞速 + BMCLAPI 合规镜像回退 + 元数据本地缓存兜底；断点失效统一规则。
- **依赖策略**：「仅 BCL、零 NuGet」策略保留——可复现性与安全性优先。

## 可靠性哲学（每条都有反例佐证，完整决策记录见 docs/adr/）

1. **机制防线优于代码评审约定**  
   反例：第 36 轮 Guard（禁止裸读 VersionJson）上线当天就抓到一处人肉 review 漏掉的裸读——那处代码还带着隐藏的 Java 版本误选 bug。
2. **单一状态来源优于多处各自判断**  
   反例：第 32 轮前 NeedsRepair / RepairAsync / UI 各自判断安装状态且口径不一（loader 被要求 client jar），导致"每次启动都补全"。
3. **生命周期契约优于"这次改对了"**  
   反例：第 37 轮微软登录崩溃——AuthService 缓存共享 client、ConfigureProxy 无条件 dispose 重建，两处单独看都"没问题"，组合即炸；防线测试随后锁死该模式。
4. **缓存必须分类：原始（可直接复用）vs 派生（必须带输入指纹，输入变则失效）**  
   反例：第 33 轮缓存命中的 Fabric profile 未与父版本合并即被使用（派生缓存无指纹、无失效机制）→ classpath 缺失、游戏秒退。派生缓存指纹化仍在工程债清单。

## 已知风险（未来可能出问题的地方，提前记录）

- **HttpClient 是可变全局单例**：ConfigureProxy 换 client 后旧实例靠 GC 回收；若未来出现高频换代理场景，可能有短暂的 socket 堆积。当前启动器只在设置变更时重建一次，风险可控。
- **派生缓存无输入指纹**：`versions/<loader-id>/<loader-id>.json` 是合并输入的产物，父版本 JSON 或加载器版本变化时不会自动失效（靠用户重装触发）。对应工程债待办。
- **BMCLAPI 为社区服务**：无 SLA，历史上有限流/宕机先例；已用四线路竞速 + 缓存兜底缓解，但极端情况下元数据仍可能全线路失败（此时依赖本地缓存）。
- **Forge/NeoForge 仍依赖安装器 JVM**：安装器内部下载不受启动器控制（已注入代理 + 15 分钟上限缓解），是其偶发失败的主要来源。
- **Java 25 运行 Minecraft 1.21**：超过官方推荐的 Java 21，绝大多数场景正常，但特定 Mod 组合可能触发类文件版本兼容问题（崩溃摘要规则会指认 UnsupportedClassVersionError）。

# ADR-0027：最大化窗口贴靠工作区——用 WM_GETMINMAXINFO 回答问题栏遮挡

- 状态：已采纳（含防线测试）
- 日期：2026-11-25
- 相关：ADR-0026（首轮全屏溢出修复）

## 背景

第 104 轮针对「全屏后界面超出显示范围」做了两件事：最大化时把 `WindowChrome.ResizeBorderThickness` 归零，以及给 `Left/Top/Width/Height` 赋值为 `SystemParameters.WorkArea`。前者修掉了几像素的边框溢出；后者**完全无效**——WPF 中处于 `Maximized` 状态的窗口，其位置与尺寸由 Windows 接管，赋值会被忽略。

用户第 105 轮实测反馈：最大化后界面最底下仍被任务栏遮挡（截图：底部「性能概览」卡片被任务栏截断）。根因即无边框 `WindowChrome` 窗口的最大化矩形覆盖整块屏幕，包括任务栏区域。

## 决策

在 `SourceInitialized`（HWND 已创建）时通过 `HwndSource.AddHook` 注册窗口过程钩子，处理 `WM_GETMINMAXINFO (0x0024)`：

1. 用 `MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)` 找到窗口所在显示器（多显示器场景正确）。
2. 用 `GetMonitorInfo` 取 `MONITORINFO.rcWork`——这是物理像素，与 `WM_GETMINMAXINFO` 期待的单位一致，无需 DPI 换算。
3. 填充 `MINMAXINFO.ptMaxPosition = rcWork.Left/Top`、`ptMaxSize = rcWork 宽/高`，`StructureToPtr` 写回并置 `handled = true`。
4. 钩子**不判断 `WindowState`**：Windows 在窗口状态翻转到 Maximized *之前*就发送该消息，若此时因 `WindowState != Maximized` 而跳过，就达不到目的。
5. 保留第 104 轮的边框归零（`WindowLayoutPolicy.ResizeBorder(maximized)`），移除无效的 `Left/Top/Width/Height` 赋值。

尝试过但在本项目的 WPF 版本中不可用的方案：XAML 里给 `WindowChrome` 设置 `IgnoreTaskbarOnMaximize="False"` —— XAML 编译器报 MC3072「命名空间中不存在属性」，且从实测看该默认值也未能阻止遮挡，故放弃，不引入伪修复。

## 后果

- 最大化窗口严格落在显示器工作区内，任务栏始终可见且不被遮挡。
- 不同 DPI 缩放比例、双显示器均按窗口所在显示器的工作区计算。
- 纯规则（消息号、是否贴靠工作区）留在 `WindowLayoutPolicy`，可在无 HWND 的单测中验证；P/Invoke 结构体封装在 `MainWindow` 内部。
- 风险：`GetMonitorInfo` 失败时直接返回、不接管消息，退化为系统默认行为（即旧表现），不会出现异常或崩溃。

## 验证

- `--unit` 180/180、`--all` 187/187；机制防线 63→64，交互元素保持 145。
- 防线断言：钩子已接线、`WM_GETMINMAXINFO` 消息号正确、使用 `GetMonitorInfo` 的 `rcWork`、XAML 保留 `WindowChrome`、且源码不再出现对最大化窗口的 `Left = work.Left` 无效赋值。
- CRLF 检出（模拟 CI）下 Release 单测通过。
- **真机待验**：最大化后底边应正好停在任务栏上方；不同缩放比例 / 双显示器各验一次。

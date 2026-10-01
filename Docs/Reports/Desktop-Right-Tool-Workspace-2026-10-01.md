# 右侧多 Tab 工具工作区 · 第一阶段实现记录

日期：2026-10-01。关联设计：[右侧多 Tab 工具工作区设计](../Features/Agent-Browser-Right-Panel-Design-2026-10-01.md)（`6a6dab8` 仅设计，本次为代码实现）。

## 交付范围（对应设计「第一阶段」）

统一分栏、Tab 容器、工具入口、收起/展开、拖动宽度与浏览器适配。左侧 Shell 导航、Agent 列表与中间聊天保持原结构与交互。

## Shell 布局（WinUI 3）

`Source/PuddingDesktop/MainWindow.xaml`：

```text
NavigationView 内容区
└── WorkbenchPane（三列）
    ├─ 列 0  WorkbenchHost（Web 聊天，原样）+ LoadingPanel 叠层 + 收起态工具入口
    ├─ 列 1  SplitterHandle（6 像素命中区，内部 1 像素线）
    └─ 列 2  ToolPanel
             ├─ 行 0  Tab 栏（实例 Tab 横向滚动 + 溢出列表 + 「+」+ 放大 + 收起）
             ├─ 行 1  当前工具操作栏（浏览器 Tab 显示导航/地址/目标/接管）
             ├─ 行 2  内容区（ToolTabContentHost ∥ BrowserSurfaceHostPanel 常驻宿主）
             └─ 行 3  状态栏（控制态 · Agent 目标 · 当前 Agent）
```

- 删除左侧导航「浏览器」整页入口（设计 §39 要求迁入工具菜单），同时删除 `BrowserPane` 与其内层标签列表。
- 工具区展开状态独立于主导航；切到运行中心/启动设置时工具区随之隐藏，但 Tab、页面与任务都保留。

## 纯度与可测性（架构第一原则）

| 位置 | 职责 |
|---|---|
| `Source/PuddingDesktop.Foundation/ToolWorkspaceLayout.cs` | BCL-only：宽度比例、最小宽度、覆盖/放大判定、拖动与双击复位 |
| `Source/PuddingDesktop.Foundation/ToolWorkspaceTabs.cs` | BCL-only：实例身份与去重、激活/关闭、未读与运行标记、自动展开抑制策略 |
| `Source/PuddingDesktop/ToolTabItem.cs` | WinUI 投影（字形、可见性），不含策略 |
| `Source/PuddingDesktop/SplitterHandle.cs` | 仅因 `UIElement.ProtectedCursor` 需要派生类；`Border` 在 WinUI 密封，故派生自 `Grid` |
| `Source/PuddingDesktop/MainWindow.xaml.cs` | 布局应用、指针/键盘交互、内容宿主、浏览器适配、偏好持久化 |

`PuddingDesktop.Foundation` 仍满足既有边界测试（无 UI/Host/包依赖），因此全部布局与实例规则可在无窗口环境下单测。

## 行为要点

- **宽度**：初始为「聊天 + 工具区」合计宽度的 45%（`DefaultWidthRatio`）；工具区最小 360；聊天最小 560；两者不能同时满足时自动切换为覆盖面板，不挤压聊天与输入框。
- **分隔线**：视觉 1 像素、拖动命中约 6 像素、`SizeWestEast` 光标；双击恢复默认比例；聚焦后 `←`/`→` 每次调整 24 像素。持久化的是**比例**，每次展开按当前窗口重新限幅，不会把宽屏上的宽度带到窄屏。
- **默认收起**：点击右上角入口或 `+` 菜单才展开；展开时显示工具首页 Tab。
- **收起保留状态**：收起只改可见性，不停止任务、不销毁运行时、不导航到空白页。
- **新产物不抢焦点**：面板收起时后台新页面/新输出只置未读标记；「任务开始时自动展开」默认关闭，且用户在本轮活动期间主动收起后不再自动展开（活动全空闲后解除抑制）。
- **放大**：工具区暂时占据内容区，返回后恢复分栏与原比例。
- **窄窗口**：覆盖聊天；点击面板外或按 Esc 收起（Esc 只在覆盖形态生效，不干扰分栏形态）。

## Tab 内容现状（诚实状态）

| 类型 | 现状 |
|---|---|
| 工具首页 Home | 可用：入口列表（单例、不可关闭、为空时兜底） |
| Agent 浏览器 | 可用：复用 `BrowserWorkspaceController` + `WinUiBrowserSurfaceHost`；每个 `PageId` 一个外层实例 Tab，同一页面重复打开只激活已有 Tab |
| 输出物 Output | 可用：选择文件 → 路径/类型/大小 + ≤256 KB 文本预览 + 「用默认程序打开」/「复制路径」；路径按大小写与分隔符归一化去重 |
| 终端 Terminal | **未接入**（`Deferred`）：只有实例与状态位，不执行命令、不显示运行中 |
| 制成品 Artifact | **未接入**（`Deferred`）：等待 Core 成果接口 |
| 交互面板 Panel | **未接入**（`Deferred`）：等待 Agent 交互通道 |

未接入的能力由 `ToolTabAvailability.Deferred` 显式标注，界面文案说明缺什么；不嵌入假提示符、假输出或假的运行指示（遵守设计「不可只嵌入输出文本便宣称完整终端已实现」）。

## 配置

`desktop.json` 新增 `toolWorkspace` 节（`DesktopBootstrapSettings.ToolWorkspace`）：

```json
{ "toolWorkspace": { "widthRatio": 0.45, "autoExpandOnActivity": false } }
```

- 只持久化布局偏好；Tab 集合、Agent 目标、进程与终端状态都不落盘，重启后不伪装恢复为运行中。
- 不持久化展开状态：每次启动默认收起。
- 写入前要求配置文件已存在且 `DataRoot` 非空，避免用空配置覆盖启动参数。

## 验证

| 门禁 | 结果 |
|---|---|
| `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj -c Release` | 0 错误；0 新增警告（6 条为链接的 WpfArchive 既有 CS8625） |
| `dotnet test Source\PuddingDesktop.FoundationTests` | 36/36 通过（新增布局/实例/活动策略用例，基线 4） |
| `dotnet test Tests\PuddingDesktop.Tests` | 259/259 通过（新增 `toolWorkspace` 配置合同用例） |
| DEBUG-only 启动冒烟路径编译 | 通过：`#if DEBUG` 段以临时符号在 Release 下编译验证后已还原 |

启动冒烟脚本 `TestScripts/test-pudding-desktop-launcher.ps1` 未改动；`RunSmokeAsync` 已扩展到工作区：默认收起 → 展开 → 每个浏览器页面一个实例 Tab → Agent 目标不随可见 Tab 改变 → 输出物 Tab 真实文件 → 延迟能力不伪装运行 → 拖动按比例、双击复位、最小宽度 → 窄窗口覆盖 + 收起不销毁页面 → 关闭页面移除实例 Tab。

**未验证（需外部控制器）**：真实桌面上的键盘焦点、Esc、中文输入法、第三方网页叠层、收起后网页与自动化的后台行为、Core 断连恢复。本机运行中的产品进程按用户要求未停止、未重启，因此新构建尚未被加载；这些验收项在外部控制器重启到明确新构建后执行，未验证一律不视为通过。

## 未纳入本阶段

- 第二阶段：Core 成果接口与制成品预览、受控交互面板、Web → 桌面桥（聊天成果卡片「在工具区打开」）。
- 第三阶段：终端会话独立组件（ConPTY）先独立交付并测试，再接入宿主；放大/窄窗口在真实 DPI 与叠层下的实测。

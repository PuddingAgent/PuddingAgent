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

## 视觉样式（按参考图对齐）

工具区顶部与操作栏按用户提供的参考图重做，`MainWindow.xaml` 的 `Root.Resources` 集中定义：

| 元素 | 样式 |
|---|---|
| 实例 Tab | `ListViewItem` 药丸 chip（`CornerRadius=9`、`BasedOn DefaultListViewItemStyle`）：图标 + 截断标题 + 运行 `ProgressRing` + 未读点 + 18×18 圆形关闭按钮 |
| 「+」 | 无边框幽灵图标按钮，紧贴最后一个 chip 之后（`HorizontalAlignment=Left` 让 Tab 列表按内容宽度排布） |
| 放大 / 收起 / 溢出 | 同款幽灵图标按钮（32×32、`CornerRadius=8`），悬停由 WinUI 默认状态笔刷提供 |
| 操作栏 | 圆形图标按钮（34×34、`CornerRadius=17`、`ControlFillColorDefaultBrush`）：后退 / 前进 / 刷新；「设为 Agent 目标」「人工接管」为同形状带标签按钮；地址栏为药丸 omnibox（`CornerRadius=17`、居中文本、placeholder「搜索或输入网址」）；右侧圆形「新建页面」与「更多」菜单 |
| 形式可用性 | 后退/前进按当前页面 `CanGoBack`/`CanGoForward` 置灰，不再永远显示为可点 |
| 分隔线 | 悬停时 1 像素线加粗到 3 像素并显示 accent 色，明确可拖动 |
| 主题 | 每个颜色都按 `ThemeDictionaries`（Default/Light/HighContrast）分别给值，未硬编码单一主题；选中态采用参考图的中性填充，未使用设计原稿提到的紫色选中态（以最新参考图为准，此处为有意的偏离） |

**WinUI 3 关键事实（本次踩到并验证）**：给系统控件设置自定义 `Style` **必须** `BasedOn="{StaticResource Default<控件名>Style}"`，否则默认样式失效（按钮丢圆角等）；框架默认样式在编译期不校验 key，写错只会在**运行时**抛资源异常。本次用到的 16 个框架 key（`DefaultButtonStyle`、`DefaultDropDownButtonStyle`、`DefaultToggleButtonStyle`、`DefaultTextBoxStyle`、`DefaultListViewItemStyle`、`ListViewItemBackgroundSelected*`、`ListViewItemSelectionIndicatorBrush`、`SymbolThemeFontFamily`、`ContentControlThemeFontFamily`、`ControlFillColorDefaultBrush`、`ControlStrokeColorDefaultBrush`、`AccentFillColorDefaultBrush`）已逐个在 Windows App SDK 的框架资源索引（`Microsoft.UI.Xaml.Controls.pri`）中确认存在；`BasedOn="{StaticResource DefaultButtonStyle}"` 的可用性依据 WinUI 3 官方示例级说明（[System controls need BasedOn Styling in WinUI](https://www.reflectionit.nl/blog/2023/system-controls-need-basedon-styling-in-winui)）与 [Styling WinUI Controls and Staying Fluent](https://inthehand.com/2023/07/26/styling-winui-controls-and-staying-fluent/)。

### 工具首页（按第二张参考图重做）

首页内容改为 **XAML 声明**（`ToolHomePanel`，直接作为 `ToolTabContentHost` 的子元素，Shell 不再重新挂载它；`CreateToolContent` 仅在 `Parent is null` 时才加入子级）：

- 顶部居中的装饰性罗盘：76 像素圆环 `Ellipse` + 旋转 35° 的菱形 `Path`，整体 45% 不透明度，不依赖字体图标（避免图标缺失显示方框）。
- 每张入口卡为圆角 12 的 `Button`（`ToolHomeCardStyle`，`BasedOn DefaultButtonStyle`）：左侧彩色图标、中间「标题 + 灰色说明」两行、右侧灰色快捷键提示；卡片填充 `CardBackgroundFillColorDefaultBrush`、描边 `CardStrokeColorDefaultBrush`。
- 五张卡与真实能力一致：输出文件（Ctrl + O）、新建终端（未接入，无快捷键）、浏览器（Ctrl + T）、制成品（等待 Core 成果接口）、交互面板（等待接入）。页面不放置额外的说明段落（首次实现里那行「拖动分隔线 / 双击复位 / 收起不停止任务」的脚注按反馈移除）；宽度与收起行为本身不变，只是不再用文案占位。
- 快捷键**真的可用**：`Root.KeyboardAccelerators` 增加 `Ctrl + O`（打开输出文件）与 `Ctrl + T`（Agent 浏览器）；卡上的提示与实际加速键一一对应，不写「装饰性」快捷键。终端未接入，因此**不显示**快捷键（参考图中的 `Ctrl + \`` 未照抄）。

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

**未验证（视觉）**：本次按参考图重做按钮样式，编译与资源 key 可用性已核，但**实际观感**（chip 选中填充、圆形按钮悬停、omnibox 居中文本、分隔线悬停高亮）必须在真实桌面上肉眼确认；如与参考图有偏差，改动集中在 `MainWindow.xaml` 的 `Root.Resources` 与工具区两个 Grid，不影响行为逻辑。

## 未纳入本阶段

- 第二阶段：Core 成果接口与制成品预览、受控交互面板、Web → 桌面桥（聊天成果卡片「在工具区打开」）。
- 第三阶段：终端会话独立组件（ConPTY）先独立交付并测试，再接入宿主；放大/窄窗口在真实 DPI 与叠层下的实测。

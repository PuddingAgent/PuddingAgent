# PuddingAgent CodeMAP

> 最后更新: 2026-08-04 | Phase 0 Closeout ✅ | Phase 1A WPF Desktop ✅ | Phase 1B-R Runtime Center ✅ | Phase 1B-S Storage ✅ | Phase 2A-1/2 accepted ✅ | Phase 2A-3 automated accepted ✅ | Git Tools ✅ | 29 项目（真实 DeepSeek smoke pending）

---

## 项目概览

Pudding 是 Windows 桌面智能助手与 IDE（V1: Windows First and Only, DeepSeek First）。
ASP.NET Core 是由 Desktop 子进程托管的独立 Core API/Service Plane，不是产品入口。
Console Host (`PuddingAgent.exe`) 仅作为开发、诊断入口。
产品界面统一称为 Workbench，`/admin/` 是 Workbench 的内部路由。

技术栈: .NET 10 / SQLite (EF Core) / React + TypeScript / Serilog / WPF / WebView2

### Windows Desktop 与通用浏览器（Phase 0 / Phase 1A / Phase 1B-R/S / Phase 2A-1/2/3 已落地）

| 文档 | 规划边界 |
|------|----------|
| `../Docs/07架构/67ADR-066抖音个人开发者评论接入与浏览器自动化ADR.md` | 通用 Browser 能力与 Douyin 分层决策来源；Desktop 进程边界以 68 实施规格的 Phase 1A 更新为准 |
| `../Docs/07架构/68抖音接入与通用WebView2自动化开发实施规格.md` | 分阶段总实施规格；WPF Launcher 监督 ASP.NET Core Core，Windows 11 Shell 内嵌隔离 WebView2；Phase 2A-3 已开放 Snapshot/Locator/Interact/Wait，Douyin Adapter 仍待实现 |
| `../Docs/07架构/69PuddingDesktop浏览器工作区运行中心与存储管理实施规格.md` | Phase 1B/2 开发输入；Phase 1B-R/S 已完成；Phase 2A Bridge 修正为现有动态 HTTP 端口上的认证 WebSocket，不采用同端口明文原生 gRPC |
| `../Docs/07架构/70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令.md` | 可直接交给开发 Agent 的 Phase 2A-1 工作包；包含 Protocol、Core WebSocket Broker、Desktop Client/Dispatcher、真实 WebView2 双标签 UI、函数签名、测试和 DoD |
| `../Docs/07架构/71Phase2A-1验收补丁真实BrowserWorkspace与Bridge可靠性工作指令.md` | Phase 2A-1 初始实现验收结论与收口工作包；要求替换占位 UI、接通真实 Runtime/Context/Page、修复 Hello/单发送循环/心跳/连接代际/重连，并补齐 Host/Desktop 测试和隔离 smoke |
| `../Docs/07架构/72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令.md` | 对 71 第二轮实现的最终验收修复；关闭 HelloAck 接收死锁、阻塞 Receive 超时、可测试 transport、Tab/Activity 绑定、Surface/Agent target、UI 线程、初始化重试和新版 smoke 阻断 |
| `../Docs/07架构/73Phase2A-1验收证据收口与Phase2A-2准入工作指令.md` | Phase 2A-1 最终验收记录；Host 43/43、Desktop 92/92、Release publish 和真实双标签/Bridge restart/Stop/Exit smoke 已通过；后续 Phase 2A-2 结果转入 74 |
| `../Docs/07架构/74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md` | Phase 2A-2 最小闭环验收；记录 Remote Runtime/Context/Page、三项 Agent Tools、DesktopChild 条件注册、54/54 Host、94/94 Desktop、7/7 AgentTools、Release publish 与退出 smoke |
| `../Docs/07架构/75Phase2A-3SnapshotLocatorInteractWait开发工作指令.md` | Phase 2A-3 开发契约；定义 Snapshot 预算、版本化 ref、Locator、八项交互、Wait、稳定错误和分层测试矩阵 |
| `../Docs/07架构/76Phase2A-3通用WebView2页面操作实施验收报告.md` | Phase 2A-3 自动验收；10/10 AgentTools、56/56 Host、102/102 Desktop、真实 WebView2 TestSite、Release/Desktop smoke；真实 DeepSeek smoke 仍需用户测试配置 |
| `../Docs/07架构/77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令.md` | 两段式验收：内部开发 Agent 交付 ready-for-external-deploy，外部控制器重启到新版本，Pudding 内新会话测试已加载的 DeepSeek Browser Tools 并交付 in-product-functional-complete，最终生命周期和退出回收仍由外部控制器验收 |
| `../Docs/07架构/78Phase2A-3B外部验收控制器与脱敏BrowserActivity证据开发工作指令.md` | 可交给 Pudding 内部开发 Agent 的下一包：实现 Browser Activity 脱敏导出和外部 Desktop/TestSite 生命周期控制脚本；内部只运行 PrepareOnly 并交付 ready-for-external-deploy，正式验收由外部 Codex 执行 |
| `../Docs/07架构/79Phase2A-3C真实Agent会话WebView2控制闭环开发工作指令.md` | 真实 Agent 控制闭环开发包：冻结 cap-browser 到七项工具映射，沿 ToolExecution/RemoteRuntime/Bridge 传递脱敏调用来源，自动投影 Desktop 控制状态和 Agent Activity，并补齐 Runtime Profile 到认证 Bridge 的组合测试 |
| `../Docs/QA/QA-2026-08-03-LLM输入预算与Provider上限修复.md` | Qwen 983,616 输入硬上限事故修复验收；覆盖独立 maxInputTokens、输出预留、Provider 用量校准、最终请求裁剪、单次受控恢复及验证命令 |

关键 Desktop 入口：

| 文件 | 用途 |
|------|------|
| `PuddingDesktop/App.xaml` | Windows 11 Light/Dark 动态颜色、Typography、Corner、Card、Button、Navigation 和 Caption 样式 Token |
| `PuddingDesktop/App.xaml.cs` | WPF 产品入口；在创建 Coordinator 前取得单实例所有权，第二实例通过 Named Pipe 激活主窗口，并将早期未处理异常写入 Desktop 独立日志 |
| `PuddingDesktop/MainWindow.xaml(.cs)` | 48px 自定义标题栏、240px Navigation、Workbench/Agent Browser/Runtime/Storage/Settings 页面、Core 状态控制条、Workbench 可见时按需 WebView2 初始化，以及有界 Browser 释放/明确退出生命周期 |
| `PuddingDesktop/Hosting/DesktopApplicationCoordinator.cs` | 始终可用的 Launcher 与可失败 Core 子进程之间的状态机；DataRoot Ready 后独立初始化 Browser，协调 Runtime Orchestrator、Bridge intent generation、Workbench Ready、后台模式和明确退出 |
| `PuddingDesktop/Core/CoreProcessSupervisor.cs` | 启动 `core/PuddingAgent.exe --desktop-child`、隔离为 Production 子进程环境并以 Core 目录作为工作目录、解析 Ready、健康检查、环形 stdout/stderr、关闭与进程树回收 |
| `PuddingDesktop/Runtime/DesktopRuntimeOrchestrator.cs`、`CoreRestartPolicy.cs` | 在单进程 Supervisor 上实现异常退出恢复、2s/4s/8s 退避、60 秒 3 次熔断，以及用户 Stop/Restart 与恢复取消语义 |
| `PuddingDesktop/Runtime/DesktopSingleInstanceService.cs`、`DesktopTrayIconService.cs` | 本地命名 Semaphore + 当前用户 Named Pipe 单实例激活；纯 WPF/Win32 托盘菜单（独立浅色可读调色板）和 Explorer 重启后的图标恢复 |
| `PuddingDesktop/Runtime/DesktopBackgroundModeService.cs`、`AutoStartRegistrationService.cs` | 默认关闭到托盘/明确退出策略，以及只在用户保存设置时写入 HKCU Run 的登录后启动 |
| `PuddingDesktop/ViewModels/RuntimeCenterViewModel.cs`、`Views/RuntimeCenterView.xaml(.cs)` | Windows 11 运行中心；显示 Core 状态、PID、健康、退出/恢复、环境、最近 500 行输出并提供启停重启与诊断操作；日志 TextBox 覆盖单行默认高度，以 340px 顶部对齐可滚动视口展示 |
| `PuddingDesktop/Runtime/DiagnosticBundleService.cs` | 用户触发的诊断 ZIP；只收集脱敏运行快照、配置键名和最近日志，过滤 Token/Cookie/Authorization/Secret |
| `PuddingDesktop/Configuration/SystemConfigurationService.cs` | `<DataRoot>/config/system.json` 的保留未知字段 Patch 与原子写入 |
| `PuddingDesktop/Views/WorkbenchView.xaml(.cs)` | 使用隔离 UDF 的 `WebView2CompositionControl`；加载/失败原生遮罩、导航和进程故障处理 |
| `PuddingDesktop/Views/BrowserWorkspaceView.xaml(.cs)`、`Browser/BrowserWorkspaceController.cs` | Phase 2A-1 Agent Browser；Controller 是 Tab/Activity/active/target/navigation 的唯一 UI 状态源，View 承载双标签、地址栏、Surface 和 Agent Activity Pane |
| `PuddingDesktop/Browser/DesktopBrowserBridgeClient.cs`、`BrowserBridgeCommandDispatcher.cs` | Desktop 认证 WebSocket Client；单 Receive Loop、HelloAck、heartbeat/watchdog、connection generation、重连/断线隔离，并把命令安全摘要投影到 UI Activity |
| `PuddingBrowser.Abstractions/BrowserInterfaces.cs`、`PuddingBrowser.WebView2/WebView2BrowserPage.cs`、`WebView2DomClient.cs` | 通用 Browser Runtime/Context/Page/Surface 契约与真实 WebView2 Driver；提供导航、Snapshot、Locator、八项交互、Wait、版本化 ref 与稳定错误，不依赖 Douyin |
| `PuddingHost/BrowserBridge/RemoteBrowserRuntime.cs`、`RemoteBrowserContext.cs`、`RemoteBrowserPage.cs` | Core 侧平台无关 Browser 代理；把 Context/Page/导航调用映射到认证 Bridge，使用稳定错误码，并保证 Core proxy Dispose 不关闭 Desktop 浏览器状态 |
| `PuddingHost/BrowserBridge/BrowserBridgeServiceCollectionExtensions.cs` | 仅在 DesktopChild + BrowserAutomationEnabled 注册 Broker、Remote Runtime 和 Browser Agent Tools；Console/disabled Host 不暴露工具 |
| `PuddingBrowser.AgentTools/BrowserContextTool.cs`、`BrowserTabsTool.cs`、`BrowserNavigateTool.cs` | Phase 2A-2 Context/Tab/Navigation 工具；统一结构化成功/错误结果 |
| `PuddingBrowser.AgentTools/BrowserSnapshotTool.cs`、`BrowserLocateTool.cs`、`BrowserInteractTool.cs`、`BrowserWaitForTool.cs` | Phase 2A-3 页面观察/操作工具；只依赖 `IBrowserRuntime`，不回显 fill/type 值，交互提交后不重查旧 Locator |
| `PuddingDesktop/Storage/StorageAnalysisService.cs`、`StorageCategoryCatalog.cs` | DataRoot first-match 分类与文件逻辑大小扫描；跳过 Junction/Reparse Point，并同时返回卷总量/可用量和 Warning |
| `PuddingDesktop/Storage/LogRetentionService.cs`、`DataRootSafetyValidator.cs` | 只在真实 `<DataRoot>/logs` 内执行 24 小时日志 Preview/重校验/逐文件删除；拒绝盘符根、越界、链接和变化文件 |
| `PuddingDesktop/ViewModels/StorageViewModel.cs`、`Views/StorageView.xaml(.cs)` | Windows 11 存储空间页；后台扫描、分类统计、内联清理确认/结果与完成后重扫 |
| `PuddingPlatformAdmin/src/pages/home/index.tsx` | Workbench 认证后默认首页；展示 Core 就绪、工作空间概览、最近工作入口和对话/模型/诊断快捷入口 |
| `PuddingDesktop/Theming/WindowsThemeService.cs` | 读取 Windows Apps Light/Dark 与 DWM Accent，更新动态 Resource |
| `PuddingDesktop/Theming/WindowsBackdropService.cs` | Windows 11 Mica、沉浸式深色和系统圆角；Windows 10/失败时无阻塞回退 |
| `PuddingDesktop/Diagnostics/DesktopDiagnosticLog.cs` | Core/Serilog 尚不可用时写 `%LOCALAPPDATA%/Pudding/logs/desktop.log` |
| `../TestScripts/start-phase1a-desktop-smoke.ps1` | 以系统 Temp 下隔离 DesktopHome/DataRoot 启动 Phase 1A/1B 发布包进行真实窗口/Core/WebView2 smoke |
| `../TestScripts/start-phase2a1-browser-smoke.ps1` | Phase 2A-1 发布 smoke 入口；校验 Desktop/Core/Workbench 布局，使用独立 Temp DesktopHome/DataRoot/UDF，报告 PID/路径并验证退出后子进程回收 |
| `../TestScripts/start-phase2a3-webview2-smoke.ps1`、`../Tests/PuddingBrowser.TestSite/`、`../Tests/PuddingBrowser.WebView2.Smoke/` | 真实 WPF/WebView2 页面操作 smoke；覆盖八项 Interact、Wait、Snapshot、版本化 ref 和 stale navigation，DataRoot 位于系统 Temp |
| `../Tests/PuddingDesktop.Tests/Browser/`、`../Tests/PuddingHost.Tests/BrowserBridge/`、`../Tests/PuddingBrowser.AgentTools.Tests/` | Browser Controller/Client、Host Endpoint、Remote proxy 与七项 Agent Tools 阻断性测试；覆盖认证、重连、Tool → authenticated Bridge、Snapshot/Locator/Interact/Wait 和结构化错误 |

### 开发启动与诊断

| 文件 | 用途 |
|------|------|
| `../Agents.md` | 仓库级开发约束；明确 Desktop 为最终产品入口、ASP.NET Core 子进程边界、dev-up 仅用于源码开发，以及 Desktop 串行构建与 DataRoot 隔离要求 |
| `../dev-up.py` | 本地 Backend/Codex MCP Service/Frontend/Proxy 监督器；以 `tmp/dev/supervisor.pid` 保证重启时先终止旧监督器，避免旧实例抢占重建；`--rebuild` 复用预构建产物且 `--auto-yolo` 通过仓库根 `yolo.signal` 激活 Runtime；`--clear` 仅在进程全停后清理仓库白名单日志/临时目录并拒绝触碰 `D:\data`；各受管角色有快速失败熔断 |
| `../How-Debuge.md` | 可重复使用的启动、会话、SSE、子代理与工具诊断路径 |

---

## 顶层目录结构

| 项目 | 类型 | 说明 |
|------|------|------|
| `PuddingAgent/` | 🔑 入口 | 入口项目 (Program.cs, 启动配置) |
| `PuddingHost/` | 🔑 核心 | 唯一 Host 组合根 — Console 与 Desktop 共用 (Phase 0 Closeout ✅) |
| `PuddingDesktop/` | 🔑 核心 | Windows WPF 产品 Launcher（Phase 1A + Phase 1B-R/S + Phase 2A-1/3 已落地） |
| `PuddingBrowser.AgentTools/` | 生产 | 七项通用 Browser Agent Tools（Phase 2A-2/3） |
| `PuddingBrowser.Abstractions/` | 生产 | 通用 Agent Browser 契约 |
| `PuddingBrowser.WebView2/` | 生产 | 通用 WebView2 Driver（Context/Page/Surface/Snapshot/Locator/Interact/Wait） |
| `PuddingCodexService/` | 🔑 核心 | 宿主外 Codex MCP Sidecar（持久任务/自修复重启握手） |
| `PuddingRuntime/` | 🔑 核心 | 运行时核心 (Agent Loop, LLM 调用, 工具系统, Git 工具) |
| `PuddingPlatform/` | 🔑 核心 | 平台层 (Session 管理, API, 数据持久化) |
| `PuddingMemoryEngine/` | 🔑 核心 | 记忆引擎 (Library/Book/Chapter, FTS5) |
| `PuddingCore/` | 🔑 核心 | 核心抽象与契约 (接口、模型、解析器) |
| `PuddingController/` | 生产 | 代理控制层 |
| `PuddingGateway/` | 生产 | LLM 网关适配 |
| `PuddingCodeIntelligence/` | 生产 | 代码索引/分析 |
| `PuddingFullTextIndex/` | 生产 | 全文索引引擎 |
| `PuddingBrowser.Protocol/` | 🆕 生产 | Browser Bridge 线协议（命令名/载荷/信封/错误码/序列化，8 个 .cs） |
| `PuddingCodeIndexer.Cli/` | 🆕 生产 | 代码索引 CLI（index/search/status/watch/definition/references/hover） |
| `PuddingGit.Tools/` | 🆕 生产 | Git 工具（实现在 PuddingRuntime/Tools/BuiltIns/Git/，20 tools + GitConstants） |
| `PuddingPlatformAdmin/` | 🆕 生产 | Workbench 管理前端（Ant Design Pro v6, React 19, UmiJS, TypeScript） |
| `PuddingCoreTests/` | 🧪 测试 | 核心抽象与契约测试 |
| `PuddingRuntimeTests/` | 🧪 测试 | 运行时核心测试 |
| `PuddingPlatformTests/` | 🧪 测试 | 平台层测试 |
| `PuddingMemoryEngineTests/` | 🧪 测试 | 记忆引擎测试 |
| `PuddingMemoryEngineBenchmarks/` | 🧪 测试 | 记忆引擎基准测试 |
| `PuddingCodeIntelligenceTests/` | 🧪 测试 | 代码索引/分析测试 |
| `PuddingCodexServiceTests/` | 🧪 测试 | 宿主外 Codex MCP Service 测试 |
| `PuddingFullTextIndexTests/` | 🧪 测试 | 全文索引引擎测试 |
| `PuddingWebApiTests/` | 🧪 测试 | Web API 测试 |
| `build/` | 构建 | 构建输出目录（Release publish / smoke 产物） |

## 🆕 PuddingBrowser.Protocol — Browser Bridge 线协议

Phase 2A-1/2/3 认证 WebSocket Bridge 的独立线协议契约库；只包含命令名/载荷/信封/错误码/序列化，不依赖具体传输，Host Remote Browser 代理与 Desktop Client 共用。

| 文件 | 用途 |
|------|------|
| `BrowserBridgeProtocol.cs` | 协议版本与能力常量 |
| `BrowserBridgeCommandNames.cs` | 全部 Bridge 命令名 |
| `BrowserBridgeCommandPayloads.cs` | 各命令载荷模型 |
| `BrowserBridgeEnvelope.cs` | 请求/响应统一信封 |
| `BrowserBridgeErrorCodes.cs` | 稳定错误码（与 Remote Browser proxy 共用） |
| `BrowserBridgeMessages.cs` | 消息类型与序列化标记 |
| `BrowserBridgeSerializer.cs` | 协议序列化实现 |
| `BrowserBridgeJsonSerializerContext.cs` | 源生成 JSON 序列化上下文 |

## 🆕 PuddingCodeIndexer.Cli — 代码索引 CLI

基于 PuddingCodeIntelligence 的独立命令行入口：

| 文件 | 用途 |
|------|------|
| `Program.cs` | CLI 入口；index / search / status / watch / definition / references / hover 子命令 |
| `Scripts/` | 索引与发布辅助脚本 |
| `TestFixtures/` | 索引查询测试夹具 |
| `pub/` | 发布产物输出 |

## 🆕 PuddingGit.Tools — Git 工具集

Git 工具工程壳；20 个工具 + GitConstants 实现在 `PuddingRuntime/Tools/BuiltIns/Git/`，由 Runtime 注册为内置工具：

| 工具 | 用途 |
|------|------|
| `GitInitTool` / `GitCloneTool` | 初始化 / 克隆仓库 |
| `GitStatusTool` / `GitLogTool` / `GitDiffTool` / `GitBlameTool` | 仓库状态与历史查询 |
| `GitAddTool` / `GitCommitTool` / `GitResetTool` / `GitStashTool` | 暂存 / 提交 / 回退 / 储藏 |
| `GitBranchListTool` / `GitBranchCreateTool` / `GitBranchSwitchTool` / `GitCheckoutTool` / `GitMergeTool` | 分支列表 / 创建 / 切换 / 检出 / 合并 |
| `GitFetchTool` / `GitPullTool` / `GitPushTool` / `GitRemoteTool` / `GitTagTool` | 远程同步与标签 |
| `GitConstants.cs` | 工具名与参数常量 |

## 🆕 PuddingPlatformAdmin — Workbench 管理前端

Workbench 的 React 管理前端（Ant Design Pro v6 + React 19 + UmiJS + TypeScript，pnpm workspace）：

| 目录 / 文件 | 用途 |
|------|------|
| `src/pages/chat/` | Chat 工作台（多模态、子代理运行坞、SSE/replay、视口虚拟化） |
| `src/pages/workspace/[id]/` | 工作空间 Agent 管理（六面板设置抽屉、Smart 角色模型） |
| `src/pages/memory-library/` | 记忆图书馆工作台 |
| `src/pages/llm-resource-pool/` | LLM 服务商与模型管理 |
| `src/pages/home/index.tsx` | Workbench 认证后默认首页 |
| `config/` + `package.json` + `pnpm-lock.yaml` | UmiJS 配置与依赖锁定 |
| `dist/` | 构建产物（发布到 Host wwwroot/admin） |

## 🧪 测试项目总览

| 项目 | 覆盖范围 |
|------|------|
| `PuddingCoreTests/` | 核心抽象与契约（工具契约、LLM 网关、消息围栏、MessageFabric） |
| `PuddingRuntimeTests/` | 运行时核心（Host 生命周期、Agent Loop、上下文管线、语音/图片 Provider） |
| `PuddingPlatformTests/` | 平台层（渠道配置、Artifact 存储、视觉观察、图片生成/投递） |
| `PuddingMemoryEngineTests/` | 记忆引擎（Library/Book/Chapter、FTS5、Skill 进化去重） |
| `PuddingMemoryEngineBenchmarks/` | 记忆引擎基准测试（BenchmarkDotNet） |
| `PuddingCodeIntelligenceTests/` | 代码索引/分析 |
| `PuddingCodexServiceTests/` | 宿主外 Codex MCP Service |
| `PuddingFullTextIndexTests/` | 全文索引引擎 |
| `PuddingWebApiTests/` | Web API（含 asr/图片工具授权回归） |

---

## 🔑 PuddingRuntime — 运行时核心

### 入口 & 配置
| 文件 | 用途 |
|------|------|
| `DependencyInjection.cs` | Runtime 服务注册入口 |
| `Services/PuddingConfigLoader.cs` | 加载 JSON 配置文件 |
| `Services/PuddingJsonConfig.cs` | 配置模型定义 |
| `Services/RuntimeExecutionConfigService.cs` | 运行时执行配置；统一归一化父 Turn 24h 硬上限、1h 无进展窗口、LLM 首块/流空闲窗口，以及子代理并发、timeout 与父 Turn 收尾预留 |

### Agent Loop (核心执行循环)
| 文件 | 用途 |
|------|------|
| `Services/AgentExecutionService.cs` | 🔑 Agent 执行编排入口；所有入口先经过 session 单写者，工具调用轮次在 Assistant + 全部 Tool results 完整后原子写入历史；把父 `ExecutionDeadlineUtc` 传入每次工具调用；以稳定 identity 报告 LLM/工具/子代理的 liveness 与带指纹 meaningful progress；子代理执行按 runId 发出 round/LLM/tool/terminal 审计事件，并以绝对 deadline 区分 timed_out/cancelled、以实际 round-start 计数提交终态统计；对 canonical `ExpectedOutputContract` 保留最近一次合格报告，防止最终 DONE 状态摘要覆盖完整交付；纯工具参数转换、KeyVault 空实现与流式诊断聚合已移入 `Services/AgentExecution/` |
| `Services/AgentExecution/AgentExecutionService.Buffered.cs` + `AgentExecutionService.Streaming.cs` | 同一执行器的过渡期 partial 主循环边界；分别承载结构化非流式循环和面向 UI 的 SSE 流式循环；大工具集按通用 OpenAI-compatible 顶层 `tools` 做首轮收敛，并在 `search_tools` 返回后于下一轮注入命中的标准函数定义；后续仍须沿 facade 继续拆解长方法 |
| `Services/AgentExecution/AgentToolArguments.cs` | LLM tool-call JSON 到 legacy skill 参数与 terminal payload 的纯转换边界；执行器保留薄委托以兼容既有反射测试 |
| `Services/AgentExecution/NoOpKeyVaultService.cs` + `StreamPipelineDiagnosticsAccumulator.cs` | 可选 KeyVault 的无副作用 fallback，以及流式 KeyVault/SSM 热路径指标的线程安全聚合；均不再作为执行器内嵌类型 |
| `Services/AgentLoop/CanonicalWorkReport.cs` | Smart 子代理五段报告合同的共享解析/校验与执行期候选保留器；只在显式 canonical `ExpectedOutputContract` 下恢复完整报告 |
| `Services/AgentLoop/AgentExecutionOutcomePolicy.cs` | 运行终态兼容判定；历史工具失败仍保留审计，但完整 canonical `DONE` 报告优先，报告正文中的 `Failed/timed out` 术语不得反向污染终态 |
| `Services/AgentLoop/AgentLoopResponse.cs` | 结构化 Agent Loop 响应解析；支持纯 JSON、起始代码围栏，以及模型先输出说明文字再返回 fenced JSON 的格式，确保 `DONE.message` 而非整段 provider 原文进入最终结果 |
| `PuddingCore/Runtime/RuntimeExecutionIdentity.cs` | 主 Agent、工具调用和子代理共用的稳定执行身份；贯穿 Conversation/Turn/Command/Run/Tool/Invocation |
| `PuddingCore/Runtime/ExecutionProgressRegistry.cs` | 主 Run 进程内进展注册表；按 Conversation 汇聚子执行信号，区分 liveness/meaningful，并拒绝相同 Run+阶段+指纹的重复续期 |
| `Services/SessionExecutionGate.cs` + `PuddingCore/Runtime/ISessionExecutionGate.cs` | Runtime 会话进程内单写者；统一串行化 Conversation Worker、MessageDelivery、Heartbeat 与直接 Runtime 调度对同一 session 的状态修改 |
| `Services/AgentLoop/CompletionPolicy.cs` | 判断 Agent 何时完成（stop reason 处理） |
| `Services/AgentLoop/ExecutionJournal.cs` | 执行日志记录 |
| `Services/AgentLoop/AgentExecutionGuardrails.cs` | 全局执行护栏（最大轮次、最大耗时、重复工具与无进展）；工具调用总预算由 `RuntimeDispatchRequest.MaxToolCallsTotal` 唯一决定，不再接受全局二次裁剪 |
| `Services/AgentLoop/ExecutionControlRegistry.cs` | 注册执行控制策略 |
| `Services/YoloSignalService.cs` | 开发机 `--auto-yolo` 文件信号消费者；从显式 `PUDDING_REPOSITORY_ROOT` 定位仓库根，消费后切换共享 Runtime mode 并删除信号文件 |
| `Services/StreamWatchdog.cs` + `DirectLlmClient.cs` | LLM 流操作级滑动看门狗；首块默认 300 秒，首块后相邻流块默认 120 秒，Provider 配置只能收紧空闲窗口，不再施加固定流总时长；使用 Stopwatch 单调时钟 |

### LLM 调用
| 文件 | 用途 |
|------|------|
| `Services/IRuntimeLlmClient.cs` | LLM 客户端接口 |
| `PuddingCore/Core/OpenAiLlmGateway.cs` + `PuddingCore/Models/ChatMessage.cs` + `PuddingCore/Models/StreamDelta.cs` | OpenAI-compatible Chat Completions 适配；只为带对应 capability 的模型解析受控 Vision/Audio Artifact，音频序列化为 `input_audio` Data URI；流式解析保留同一 chunk 的全部 `tool_calls`，按 index 维持延迟 ID，并为缺失/重复 ID 生成单轮稳定协议 ID，避免工具结果在下一轮被判 orphan |
| `Services/DirectLlmClient.cs` | 直连 LLM 客户端；统一区分 HTTP/网络瞬态错误，流式路径仅在首个 Delta 前按 Provider 策略重试，首块后禁止重试以避免重复输出/工具调用；将解析后的输出上限下传为 Provider `max_tokens`；仅当当前模型带 `vision` 能力标签时才把 workspace 授权视觉制品序列化为多模态内容，文本模型不再接收 `image_url` |
| `Services/ControllerRoutedLlmClient.cs` | 通过代理路由的 LLM 客户端 |
| `Services/LlmInvocationService.cs` | LLM 调用服务（统一入口）；Provider 调用前校验/修复 tool-call 消息序列并记录诊断；调用方取消必须重新抛出，禁止降级为普通 Provider 失败 |
| `Services/LlmProfileResolver.cs` | 解析遗留 Profile/Binding 配置；主 Agent 执行模型不从此处选择 |
| `Services/LlmOptions.cs` | LLM 请求选项与 `ContextUsageSnapshotStore`；最终请求快照同时记录工具定义 token 和规范化 schema 哈希，`RecordProviderUsage` 只更新 Provider 诊断字段并保留请求快照事实 |
| `Services/ProviderRateLimiter.cs` | Provider 级限流器 |

### 上下文管理
| 文件 | 用途 |
|------|------|
| `Services/ContextWindowManager.cs` | 🔑 上下文窗口管理（token 驱动裁剪 + 自动压缩触发）；比较持久化快照前先修复内存中的不完整工具轮次 |
| `PuddingCore/Models/LlmMessageSequenceNormalizer.cs` | OpenAI-compatible 消息协议守卫；保留完整工具轮次、移除 orphan Tool、降级或丢弃不完整 Assistant tool-call |
| `Services/ContextCompactionService.cs` | 上下文压缩执行与 ContextHealth 用量解析；Provider usage 是本地估算的硬下界；provider/snapshot/Memory 均无数据时，以最近 500 条 canonical ChatMessages 估算并标记 `canonical_chat_transcript`，避免重启后错误显示 0 used |
| `Services/ContextHealthEvaluator.cs` | 🔑 上下文健康度评估；有效窗口为 `min(maxInputTokens, maxContextTokens - maxOutputTokens - safetyBuffer)` |
| `Services/LlmRequestBudgetGuard.cs` + `PuddingCore/Platform/LlmOptions.cs` | 最终 LLM 请求预算守卫与 Provider 校准快照；完整计入 reasoning/tool-call payload，超限时按会话单元裁剪最旧历史；识别 Provider 输入范围 400，并在单次执行内校准后恢复一次 |
| `Services/ContextAssemblyService.cs` | 上下文组装（System Prompt + 历史 + 记忆） |
| `Services/SystemPromptBuilder.cs` + `Services/ContextPipeline.cs` | 系统提示与上下文管线编排；两条提示入口复用唯一的飞书语音输出协议与音频输入安全协议，出站依据 `channel_type=feishu` 指导 `send_voice`/`voice` 围栏，入站依据平台 attached-audio notice 指导原生听取或精确路径 `asr`；L6 召回内容严格限制为 5K tokens，并在裁剪完成后才计入 `UsedTokens`，保证注入内容、层快照和预算账目一致 |

### 工具系统
| 文件 | 用途 |
|------|------|
| `PuddingCore/Tools/PuddingToolContracts.cs` | 原生 Tool 契约、描述符与强类型参数基类；调用方取消必须透传，Provider 超时和普通异常仍映射为工具失败 |
| `Tools/Platform/PuddingToolRegistry.cs` | 🔑 工具注册表与执行硬边界；支持 `IWorkspacePuddingToolSource` 的 Workspace 动态工具快照，强制 MainAgentOnly/DelegatedSubAgent、AllowSubDelegation 和 DelegationDepth，模型无法用伪造工具名或跨 Workspace 绕过 |
| `Tools/Platform/ToolInvocationService.cs` | 工具调用分发（解析工具名 → 透传配置所有者/委派深度 → 执行） |
| `Tools/Platform/ToolPermissionPolicyService.cs` | 工具权限策略（安全区检查） |
| `Services/Tools/ToolExposurePlanner.cs` | Provider 无关的工具暴露规划器；工具数超过阈值时只暴露核心工具与已检索工具，解析 `search_tools` 结果时只接受当前已授权目录中的工具 id |
| `Tools/Approval/InMemoryToolApprovalService.cs` | 高危工具审批服务 |
| `PuddingCore/Platform/ToolProfileConfig.cs` | 工具 schema 场景配置；心跳仅由可信 `system:heartbeat` Origin 触发，子代理显式 capability/template 工具选择优先于静态兜底配置，主代理默认保持完整工具集 |

### 核心工具
| 目录 | 工具 | 用途 |
|------|------|------|
| `Tools/BuiltIns/Files/` | `FileTools.cs` + `FileSearchTool.cs` | 文件读写、搜索、grep；`file_search` 在工具边界统一把任意 provider/fallback 结果规范化为绝对路径 |
| `Tools/BuiltIns/Memory/` | `MemoryTools.cs` | 记忆读写（save/manage/search/grep） |
| `Tools/BuiltIns/Agents/` | `SubAgentTool.cs` | 🔑 子代理派生入口；将 model/capability 一次解析为不可变 `LlmProfile + LlmConfig` 路由快照，并完整保留 `origin_tool_id + reuse_parent_context + pool_* + max_rounds + WorkingDirectory + ConfigurationAgentInstanceId + DelegationDepth + ParentExecutionDeadlineUtc`；同步委派由 Manager 统一保留父级收尾时间 |
| `Tools/BuiltIns/Agents/` | `AgentSleepTool.cs` | 心跳睡眠控制（max 86400s） |
| `Tools/BuiltIns/Search/` | `SmartSearchTool.cs` | 🔑 语义代码搜索 — 薄包装子代理，三层搜索协议，MainAgentOnly，Explorer 模型 |
| `Tools/BuiltIns/Search/` | `SearchToolsTool.cs` | 通用工具目录检索；只查询当前 CapabilityPolicy/Workspace 可见目录，返回工具 id 供 Agent Loop 下一轮以标准顶层 `tools` 暴露 |
| `Tools/BuiltIns/Search/` | `AnySearchSearchTool.cs` | AnySearch 通用搜索（Web/文档） |
| `Tools/BuiltIns/Search/` | `DoubaoSearchTool.cs` | 豆包搜索 Global 版；从 `search.providers.json` 的 `doubao_search` 节读取凭据，映射文本/图片摘要并保留双层业务错误与 RequestId |
| `Tools/BuiltIns/Search/` | `GitHubSearchTool.cs` | GitHub REST API 搜索 |
| `Tools/BuiltIns/Sessions/` | `SmartQuerySessionLogsTool.cs` | 🔑 语义会话日志查询 — 薄包装子代理，MainAgentOnly，Explorer 模型 |
| `Tools/BuiltIns/Sessions/` | `QuerySessionLogsTool.cs` | 会话日志查询（支持 exclude_heartbeat） |
| `Tools/BuiltIns/Sessions/` | `QuerySessionsTool.cs` | 会话列表查询 |
| `Tools/BuiltIns/SmartWorkflow/` | `SmartWorkflowToolBase.cs` + `Smart*Tool.cs` | 🔑 7 个角色化 Smart 工作流工具；统一 `task`、角色模型和父 deadline/120 秒收尾预留；每次调用默认使用一次性子代理且 `reuse_parent_context=false`，角色工具集显式有界，跨模型 fallback 仅在 `allow_fallback=true` 时启用；单次调用默认上限 3600 秒，`smart_plan` 为 3600 秒/48 轮只读规划，`smart_explore` 为 1800 秒/32 轮只读探索；显式透传 canonical `expected_output_contract` 并与 Runtime 共享五段报告校验；验证结构化结果时以完整 `rawOutput` 为权威并解包嵌套 `DONE.message`，不把短 `summary` 误当完整报告 |
| `Tools/BuiltIns/Management/` | `LlmResourcePoolTool.cs` | LLM 资源池查询（Provider + Model + 能力标签），MainAgentOnly |
| `Tools/BuiltIns/Management/` | `AgentStateTool.cs` | Agent 私有状态自维护：检查、诊断、读取、原子更新白名单 Markdown；Low 风险且只使用当前 `AgentInstanceId` |
| `Tools/BuiltIns/Http/` | `HttpFetchSkill.cs` | HTTP 请求 |
| `Tools/BuiltIns/Shell/` | Shell 工具 | 终端命令执行（支持 tail_lines）；执行边界强制应用不可绕过的宿主进程保护 |
| `Tools/BuiltIns/Terminal/` + `Services/TerminalSecurity.cs` | 后台终端与命令策略 | Normal/YOLO 共享宿主安全不变量；任意进程终止命令必须改用当前会话持有 job id 的 `terminal_cancel` |
| `Tools/BuiltIns/CodeIntelligence/` | `CodeQueryTools.cs` | 代码索引查询 |
| `Tools/BuiltIns/Documents/` | `ReadOfficeDocumentTool.cs` | Office 文档读取（NPOI 2.8.0） |
| `PuddingAgent/Tools/` | `ImageReaderTool.cs` | 文本主 Agent 的显式视觉回退：读取受控本地 PNG/JPEG/WebP 路径，以 `vision` 能力解析模型并返回文字观察；不会替换主 Agent 自身 Provider/Model |

### 事件系统
| 文件 | 用途 |
|------|------|
| `Services/Events/InternalEventBus.cs` | 内部事件总线（进程内） |
| `Services/Events/EventDispatcher.cs` | 事件分发器 |
| `Services/Events/EventPreprocessor.cs` | 事件预处理（上下文注入） |
| `Services/Events/PriorityEventQueue.cs` | 优先级事件队列 |

### 其他服务
| 文件 | 用途 |
|------|------|
| `Services/HeartbeatService.cs` | Agent 心跳服务（定时唤醒） |
| `Services/AgentSessionManager.cs` | Agent 会话管理 |
| `Services/SseEventForwarder.cs` | SSE 事件转发到前端 |

### Chat 前端 Viewport
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts` | Chat 页面组合与跨域协调入口（1,314 行）；P0/P1 业务逻辑已委托专用 hook，并通过兼容导出维持现有调用方 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useWorkspaceAgentSelection.ts` | Workspace/Agent 选择域：路由解析、列表加载、默认 Agent 创建、选择项投影、`creatingSession` 与一次性主会话重建抑制 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionCatalog.ts` | Session 目录与身份 ref 所有者：列表刷新、主/选中会话、重命名、删除、归档 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionSelection.ts` | Session 切换事务：取消旧请求、加载历史、恢复 replay、同步 route 与 unread；replay/cursor 两条分支都用 bootstrap failed/cancelled Turn 快照收口，防止首块前失败在刷新后表现为卡住 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionHistoryProjection.ts` | 持久消息到 `ChatTurn` 的投影与安全历史对账；完成后同步事件 cursor，并消费 bootstrap 的 failed/cancelled Turn 快照，把未持久化 Agent 正文的终态恢复成明确且可刷新的错误卡片 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventBuffers.ts` | delta/thinking 批处理缓冲与 timer 所有者 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventConnection.ts` | Conversation SSE 连接、健康重连、在线恢复与 replay poll 生命周期；首次连接保留历史/bootstrap 已同步的 cursor 并通过 `Last-Event-ID` 续读，禁止刷新时从 sequence 0 重放完整事件库 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventReplay.ts` | 按 sequence/cursor 的缺口恢复、条件补偿与最新 Turn replay；分页最大 sequence 必须以有限哨兵归并并单调推进，不能以 `NaN` 为 reduce 初值；对仍 active 的子代理低频读取 canonical session 状态，校正有界 bootstrap 遗漏的历史终态 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventProjection.ts` | 持久/实时事件到 Turn、SubAgent、usage、cache 与 working-agent 状态的统一投影；`subagent.*` 只进入独立 reducer/运行坞并提前返回，禁止缺失父 Turn 身份的历史事件回退污染最新主消息 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useMessageSend.ts` | 发送事务：乐观 Turn、Outbox、202 acceptance 身份收敛、SSE/replay 衔接与失败回收；除专用 `/compact` 生命周期外，所有斜杠输入统一调用 Web system-command endpoint，不进入 Agent Turn |
| `PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx` + `visionArtifactImage.ts` | Composer 图片暂存边界：多选、Ctrl+V/拖放、发送前预览与移除；BMP/GIF/AVIF 等浏览器可解码格式先转为 provider-safe PNG，再上传全部图片并用单次消息携带 `visionArtifactIds` |
| `PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.tsx` + `types.ts` + `viewport/messageProjection.ts` | 用户多图气泡与历史/实时元数据投影；按 artifact id 渲染图片画廊并保留单图兼容字段 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useMessageInteractionQueue.ts` | Composer 输入、服务端命令队列、steering 队列、快捷键与定时刷新 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useCompaction.ts` | Compaction lifecycle、手工 compact、生命周期 Turn 与压缩后会话切换 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useMessageHistoryPagination.ts` | 历史分页状态、旧消息前插与 projector 绑定 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useWorkspaceNotifications.ts` | Workspace 通知 SSE、未读计数与通知流生命周期 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useChatModals.ts` / `useChatRuntimeEvents.ts` | Chat Modal 状态与有界 interaction runtime event 通道 |
| `PuddingPlatformAdmin/src/pages/chat/types/chatStateTypes.ts` | Chat 主 hook 的共享常量、跨模块状态类型与 `UseChatStateReturn` 接口 |
| `PuddingPlatformAdmin/src/pages/chat/utils/chatStateUtils.ts` | Chat 状态纯转换、格式化与 replay/cursor 判定的无 React 边界 |
| `PuddingPlatformAdmin/src/pages/chat/utils/chatDiagnostics.ts` | ChatDiag 有界序列化、错误终态识别与可检索 Markdown 格式化、控制台记录和 sessionStorage 持久化边界；诊断失败不得影响聊天流程 |
| `PuddingPlatformAdmin/src/pages/chat/utils/sessionEventReplay.ts` | 持久事件 wrapper 规范化与 replay page HTTP/404 边界 |
| `PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts` | Agent conversation 查询缓存与轮询收敛；终态 cursor 暂时领先消息读模型、快照仍以 user 结尾时禁用条件 GET，避免相同 cursor 的 304 固化不完整投影 |
| `PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts` | 消息视口唯一滚动权威；按帧合并 scroll，按 message id 缓存行高，并综合 row 数与 Markdown/process 内容重量选择正常流/virtualizer；历史前插恢复 DOM 锚点；首次打开在用户第一次真实滚动前以有界收敛窗口跟随虚拟行/Markdown 的分批测量，直到最新消息稳定可见 |
| `PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx` | 消息列表渲染与 viewport overlay；优先按 canonical `turnId` 合并用户/助手投影并替换本地运行壳，同时保留 canonical 用户消息 metadata 以在刷新后恢复图片/语音模态；React/virtualizer row key 使用真实 message id，避免同一 Turn 多消息复用 key；canonical conversation 落后时保留本地 SSE 终态与 reasoning，activeRun 采用非回退合并且不得以不完整 `localTurns` 过滤服务端事实；会话首次装载等待 canonical 网络刷新完成后只向 viewport 发出一次定位最新消息意图，避免先对 IndexedDB 旧快照定位 |
| `PuddingPlatformAdmin/src/pages/chat/components/AgentMessageBubble.tsx` + `WaitingBubble.tsx` + `ReasoningPreview.tsx` + `ParticleDots.tsx` + `MessageStream.tsx` | 主 Agent 消息呈现边界；等待、推理、工具活动与正文共享克制的活动面视觉语法，运行过程仅消费投影后的 timeline；memo 比较包含 timeline/process 摘要，确保正文未变化时工具进度仍实时重渲染；等待计时锚定 Turn 服务端 `createdAt`，刷新/虚拟行重挂载不归零；入场动画仅对活动或最近消息启用，等待粒子与答案完成粒子为纯呈现状态，完成粒子仅在活动答案落定时触发，历史消息挂载不得重放动效 |
| `PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx` | Chat 工作台布局壳层；`chatBody`/开发面板/历史搜索保持合法 JSX 嵌套，并展示 SSE 重连提示 |

---

## 🔑 PuddingCodexService — 宿主外 Codex 执行边界

| 文件 | 用途 |
|------|------|
| `PuddingCodexService/Program.cs` + `CodexServiceOptions.cs` | loopback Streamable HTTP MCP Host；开发机固定 `danger-full-access/never`，同时保留严格仓库 cwd 边界与 Service/数据/监督器路径配置 |
| `Services/CodexTaskCoordinator.cs` + `FileCodexTaskStore.cs` | `taskId` 持久异步队列；HTTP Client 断开后继续执行，Service 重启恢复 Queued/Running；自修复任务固定禁止 Codex 控制 Pudding 进程，并在完成后自动、幂等地安排 staging Backend restart |
| `Services/CodexMcpExecutor.cs` | Service 独占的 `codex mcp-server` stdio Client；固定 Yolo 权限、顺序执行、超时、断线重连与结构化结果保留 |
| `Tools/CodexTaskTools.cs` | 普通 `codex_task_start/get/reply/cancel`、专用 `pudding_self_heal_start` 与特权 `pudding_build_restart/restart_get`；调用方不能覆盖 Codex sandbox/approval |
| `Services/SupervisorRestartRequestWriter.cs` | 只接受 Completed Codex Task；原子写入延迟 Backend restart request，并按 taskId 复用 pending/result 身份，避免崩溃窗口重复重启 |

## 🔑 PuddingPlatform — 平台层

### 数据层
| 文件 | 用途 |
|------|------|
| `Data/PlatformDbContext.cs` | 🔑 EF Core 主 DbContext |
| `Data/Entities/*.cs` | 实体定义（40+ 实体） |

### 核心实体（最常用）
| 实体 | 用途 |
|------|------|
| `WorkspaceEntity.cs` | 工作区 |
| `WorkspaceAgentEntity.cs` | Agent 实例 |
| `WorkspaceAgentTemplateEntity.cs` | Agent 模板 |
| `ChatMessageEntity.cs` | 聊天消息 |
| `ChatExecutionCommandEntity.cs` | Conversation Turn 的可靠执行命令 |
| `AcceptanceBatchEntity.cs` | 用户提交批次与 `clientRequestId` 幂等事实 |
| `ConversationTurnEntity.cs` | Conversation Turn 独立实体（ADR-059 Execution Kernel） |
| `ExecutionRunEntity.cs` | 每次执行尝试独立记录（ADR-059 Execution Kernel） |
| `ControlMessageEntity.cs` | 统一控制消息收件箱（ADR-059 Cancel/Steering/Approval） |
| `ConversationEventEntity.cs` | Conversation Event Store 事件 Envelope |
| `ConversationHeadEntity.cs` | Conversation 内已提交事件 Head Sequence |
| `ConversationProjectionCheckpointEntity.cs` | 物化视图投影进度 |
| `LlmProviderEntity.cs` / `LlmModelEntity.cs` | LLM 提供者/模型 |
| `SessionEventLogEntity.cs` | 会话事件日志 |

### 核心服务
| 文件 | 用途 |
|------|------|
| `Services/SessionStateManager.cs` | 遗留 Session 状态与 SSE/WS 推送；`session_sub_agents` 作为按 SubSessionId 唯一的当前状态投影，池化复用时用原子 UPSERT 重置终态并拒绝跨父会话重绑定 |
| `Services/SessionStateStore.cs` | 🔑 会话状态持久化 — 重启后恢复（data/sessions/{id}.json） |
| `Services/SessionCompactionEventEmitter.cs` | 自动压缩生命周期适配器；只写 canonical Conversation Event Store |
| `Services/SessionRedirectStore.cs` | 会话重定向（压缩后新旧 Session 映射） |
| `Services/PlatformApiClient.cs` | 平台 API 客户端（内部调用） |
| `Services/ChatHistoryService.cs` | 聊天历史查询 |
| `Services/AgentLLMConfigResolver.cs` | Agent 语义角色的 LLM 路由边界：从持久 Agent 实例解析 `conscious` / `subconscious` / smart 子代理角色，再从 provider registry 精确补齐连接配置；缺失、跨 workspace、profile 不一致或路由不存在时 fail-closed，不在调用方硬编码模型 |
| `Services/AgentDailySummaryBatchService.cs` | 按日期发现 Agent 消息日志，使用该 Agent 的 `subconscious` 角色生成 `memory/daily/{day}.md` 并维护 `memory/index.json`；源哈希不变时幂等跳过，角色解析失败时不回退到平台默认模型 |
| `Services/AgentRuntimeProfileResolver.cs` | Agent 执行配置唯一解析边界；只以实例 manifest 的 `preferredProviderId + preferredModelId` 作为主 Agent 模型身份，再由 `llm.providers.json` 精确补齐连接配置；缺失或无效时返回 `agent_configuration_invalid`，不回退 |
| `Services/WorkspaceAgentFileService.cs` | Agent 实例定义写入权威；创建/管理端更新同步维护 manifest、Markdown 与 `config/llm.json`，实现 `IAgentSelfMaintenanceService` 的受控自维护写入，并以 `IAgentChannelBinder` 单写者维护 Agent `channelIds` 引用 |
| `Services/ChannelConfigurationFileService.cs` | 文件化渠道配置唯一写入边界；维护 `config/channel.providers.json` 与 `channels/{channelId}/manifest.json`，Secret 只返回是否已配置，校验唯一 Feishu App ID 和 Agent 绑定，并在启动时把旧 Agent `feishu` 对象原地迁移为渠道实例 |
| `Services/Mcp/McpServerConfig.cs` + `McpConnectionManager.cs` | Workspace MCP Client 生命周期；官方 SDK Streamable HTTP/SSE 与本地 stdio 子进程、严格配置、KeyVault Bearer 引用、受限子进程环境、DNS/SSRF 防线、工具热发现和 fail-closed 状态；Codex 自修复场景连接独立 HTTP Service，不由 Backend 托管进程 |
| `Services/Mcp/McpPuddingTool.cs` | MCP Tool → `IPuddingTool` 适配；稳定命名空间、原始 JSON Schema、高风险审批、Workspace 二次隔离、超时与结果上限 |
| `Services/VisionArtifactStorageService.cs` + `Controllers/Api/VisionArtifactApiController.cs` + `Services/VisualArtifactReference.cs` + `Services/VisualArtifactResolverBridge.cs` + `Services/RemoteImageArtifactImportService.cs` | 无状态 singleton 视觉制品存储/解析边界；只持久化 provider-safe JPEG/PNG/WebP，同时提供 LLM 可消费引用与经过 workspace 根目录校验的受控本地路径；远程导入只接受 public HTTPS、以 public-only DNS/连接策略防 SSRF，并在 50 MiB 内按 Workspace 稳定复用；不支持的 MIME 返回 HTTP 415，不得成为 500 |
| `Services/AudioArtifactStorageService.cs` + `Services/AudioArtifactReference.cs` + `Services/AudioArtifactResolverBridge.cs` | Workspace 受控音频制品边界；只持久化经过文件头校验的 16-bit mono/stereo PCM WAV，以稳定 `audio-*` 身份同时提供精确本地路径与 provider-safe Data URI，拒绝路径穿越和伪装格式 |
| `PuddingCore/Abstractions/IAudioTranscriptionService.cs` + `PuddingPlatform/Services/AudioTranscriptionService.cs` | Provider-neutral 文件 ASR 边界；从 `config/voice/providers.json` 解析 Provider/模型与 Provider 自有默认 ASR 模型，再通过 `IVoiceProviderFactory/IAsrHttpRecognizer` 调用具体服务 |
| `PuddingCore/Abstractions/IImageGenerationService.cs` + `PuddingPlatform/Services/ImageGenerationService.cs` + `PuddingRuntime/Services/VolcengineArkImageGenerationProvider.cs` | Provider-neutral 图片生成/编辑边界；按 default/precision/sequence capability 选择 Seedream Lite/Pro，支持最多 10 个参考 Vision Artifact、0~999 坐标提示、精确尺寸、PNG/JPEG、提示词优化、联网搜索和 1~4 张组图；Ark 临时 URL 立即限流下载并物化为 Workspace Vision Artifact，终态稳定操作键可复用已生成 Artifact |
| `Services/SubAgentManager.cs` | 子代理统一调度边界；按父 deadline 归一化子 deadline，同步委派额外保留默认 120 秒父级收尾窗口并在不足时拒绝创建 run，把并发门等待计入预算；每次执行创建新 run，再投影可复用 SubSessionId 当前状态，投影失败时终结 run |
| `Services/SubAgentPool.cs` | 池化子代理生命周期；create/自动创建只原子预留稳定 SubSessionId，execute 才调用 `ExecuteSyncAsync`，避免隐藏异步 run 与首轮双执行 |
| `Services/FileSubAgentRunStore.cs` | 子代理运行审计与终态仲裁；`run.json/input.json/run.created` 持久化精确 `ExecutionDeadlineUtc`，终态提交前从 events.jsonl 合并真实轮次/工具/耗时/失败统计，先写自带 `run_id` 的事件，再按持久游标投影到父执行身份对应的 canonical Conversation Event，供父 Chat 的 bootstrap/replay/live SSE 观察；有界后台补投使用跨轮次扫描游标，避免 run 数量超过单批上限后永久饥饿 |
| `Services/SubAgentConversationProjectionWorker.cs` | 启动时将上一进程遗留的非终态 run 仲裁为 `interrupted`，随后扫描 run archive 投影积压 |
| `Services/ConversationAcceptanceStore.cs` | 原子受理：Message + Batch + Turn + Command + Event 单事务 |
| `Services/ExecutionCommandReader.cs` | Command 稳定执行引用的只读适配器；不拥有任何状态转换 |
| `Services/ConversationEventStore.cs` | Conversation Sequence 分配、事件追加、历史读取和 `subagent.*` 类型前缀补读；事件分页读取 `limit + 1` 条并准确计算 `hasMore` |
| `Services/ConversationProjector.cs` | Event Store 到查询模型的 checkpoint 投影；除按稳定身份物化 ChatMessages 外，还将带不可变 Provider/Model 归因的 `usage.recorded` v2 必达写入 Token 明细账本 |
| `Services/ConversationProjectionWorker.cs` | 按持久 Conversation Head/Checkpoint 扫描投影积压；与具体事件写入者解耦并支持重启追平 |
| `Services/TokenUsageRecorder.cs` | Token 明细账本唯一增量写入器；计费用量事实使用 `RecordRequiredAsync` 并由拥有方等待完成，只有非权威遥测可使用 best-effort `RecordAsync`；`layer-v2` 将 `L1-TOOL-DEFINITIONS` 插入真实前缀顺序，按规范化 schema 哈希追踪跨轮变更 |
| `Services/BenchmarkCaseCatalogService.cs` + `BenchmarkRunService.cs` + `BenchmarkEvaluationService.cs` | 可重复 Agent 评估闭环；case 配置声明 deterministic artifact/budget contract，run 固化版本与配置哈希，evaluator 汇总 parent/subagent Token/成本/缓存/轮次/模型与 role/profile，并将 `passed/failed/unscored` 结果原子归档到 `runtime/benchmark-runs`；无 oracle 不得伪造完成分 |
| `Controllers/Api/BenchmarkCasesController.cs` + `Tools/Diagnostics/run_benchmarks.py` | Benchmark list/prepare/evaluate/result API 与无人值守 CLI runner；runner 使用 fresh session、等待 canonical Turn 终态、支持 repeat/baseline 输出，并用 `excludeFromLearning=true` 阻止经验→SKILL 测试集污染 |
| `Services/TokenUsageSchemaBootstrapper.cs` | Platform SQLite 的 Token 用量 Schema 升级边界；启动时幂等补齐 `TokenUsageEvents.ParentSessionId` 与索引，DDL 失败直接阻止启动，避免 EF 模型与旧数据库静默失配 |
| `Services/ConversationCommandSchemaBootstrapper.cs` | Platform SQLite 的可靠命令 Schema 升级边界；启动时通过 `PRAGMA table_info` 幂等补齐 `chat_execution_commands.metadata_json/reply_projected_at`，避免已有数据库在 Turn 受理或渠道回复投影时因 EF 模型漂移失败 |
| `Services/TokenUsageRebuildService.cs` | 从 Conversation Event Store 的 `usage.recorded` v2 重建 `agent_llm` 明细，再从完整账本重建月度汇总；禁止猜测历史路由，仅在同一事务中替换可成功重建的 sourceId，未归因事实不得触发删除 |
| `Services/AgentChat/ChatExecutionWorker.cs` | Worker v5 — 通过 IExecutionLeaseStore 原子 CAS 领取，透传 Lease 到 Coordinator |
| `Services/AgentChat/ExecutionRunCoordinator.cs` + `ExecutionWatchdogPolicy.cs` | Execution Kernel 入口 — 接收 Lease，冻结 24h 硬上限并运行 1h 滑动无进展看门狗，读取 Command 稳定引用，组装 Snapshot，执行 Runtime，向全部输出事件贯穿 assistant MessageId，仲裁 `execution_timeout/execution_stalled/cancelled` 并提交 Journal；从 gateway metadata 构造 typed `MessageOrigin`；附图由文本主模型先获平台视觉观察，附音频按精确 Provider+Model `audio` capability 在原生 `AudioArtifactIds` 与受控路径 `asr` notice 间分流；终态写入失败时执行 fenced 基础设施兜底 |
| `Services/VisualArtifactObservationService.cs` | 文本主模型的强制视觉预处理边界；按 `vision` capability 选择视觉模型，把多图事实/OCR 与不确定性作为不可信媒体观察注入本轮，失败即阻断主 Agent；原生视觉主模型跳过二次调用 |
| `Services/AgentChat/TurnOutputChunker.cs` | Runtime delta 聚合边界；持久事件必须持有独立 JsonElement，非 delta 事件必须原样保留 Runtime SchemaVersion |
| `Services/AgentChat/AgentConversationProjectionService.cs` | Chat 历史与活动 Run 查询投影；Agent 来源名取实例 manifest 显示名（禁止拿 Session title 冒充发送者），以 `conversation_events` 为过程事实源，按 `ChatMessages.turn_id` 或 command 的 user/assistant message 映射补齐 canonical `turnId`；初始 conversation 只返回过程计数摘要且不读取事件 payload，完整过程按稳定 `messageId/runId` 经单消息详情端点延迟加载 |
| `Services/AgentChat/AgentRunProjectionService.cs` | Agent 联系人当前状态投影；状态与 cursor 均来自 canonical Conversation Event sequence，失败/取消/LeaseLost 终态结束后回到 idle，失败详情留在 Turn 事件 |
| `Services/Execution/SqliteExecutionLeaseStore.cs` | 原子 CAS 领取与恢复：BEGIN IMMEDIATE + fencing；释放/过期时事务恢复 Run、Command、Turn |
| `Services/Execution/SqliteExecutionJournal.cs` | 统一 fenced 事件写入、原子终态和 Worker 基础设施失败兜底；终态从 Command 读取 assistant MessageId |
| `Services/Execution/SqliteControlInbox.cs` | 控制消息只读/确认端口；写入只允许经 ExecutionControlService |
| `Services/Execution/ExecutionControlService.cs` | Cancel/Control 的唯一事务写入权威 |
| `Services/PlatformReadinessProbe.cs` | Conversation 执行链 readiness：DB + Submit Handler + Coordinator |
| `Services/Snapshot/AgentExecutionSnapshotFactory.cs` | 只消费 AgentRuntimeProfile 的无密钥快照工厂；冻结 Provider/Profile/Model 与能力引用 |
| `Services/Conversation/SubmitTurnHandler.cs` | Submit Turn 应用处理器；公开请求的 `gateway_*` 保留 metadata 会被过滤，只有进程内可信 Gateway 命令可保留渠道回复路由事实 |
| `PuddingCore/Platform/ISystemStatusSnapshotProvider.cs` + `Services/Conversation/SystemStatusSnapshotProvider.cs` | `/status` 的渠道无关状态快照边界；统一读取 Agent Profile、Provider/Model、模型容量、ContextHealth、canonical Session/子代理状态和 Runtime mode/error window，数据源降级时返回 warning 而不调用 Agent 补猜 |
| `Services/Conversation/SystemCommandHandler.cs` | Web/飞书共享的系统命令执行边界；`/status` 格式化共享快照并显示 remaining/effective context、模型和 Agent/Session 基础状态；`/compact` 复用 `IRequestCompactionHandler` 并回复压缩统计/后继会话，跨会话使用稳定 request/response ID 防止飞书重投二次压缩；`/whoami` 只回显已验证 Feishu `externalUserId`；特权命令要求渠道已验证用户，默认禁止创建 Agent Turn/Command |
| `Services/Conversation/RequestTurnCancellationHandler.cs` | Cancel 处理器 — 写 turn.cancel.requested |
| `Services/Conversation/CreateSteeringHandler.cs` | Steering 应用 Handler；端点在 Runtime 消费器落地前保持关闭 |
| `Services/Conversation/RequestCompactionHandler.cs` | 手动压缩唯一应用入口；解析 Agent Profile、执行压缩、写生命周期事件并创建后继 Conversation |
| `Services/Conversation/CompactionSessionSuccessor.cs` | 压缩后继会话边界；以幂等单 `压缩 - ` 前缀创建 Session，持久化 Agent mainSessionId，并注册旧→新重定向 |

### 工作空间 Agent 管理前端
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/workspace/[id]/index.tsx` | 工作空间 Agent 列表、渠道服务商、渠道管理及其它 Workspace 配置入口；渠道 Secret 只允许写入、不回显；飞书可独立开启成功终态语音并配置渠道音色 |
| `PuddingPlatformAdmin/src/pages/workspace/[id]/WorkspaceAgentSettingsDrawer.tsx` | Agent 自包含设置抽屉；单 Form 的六个互斥面板、错误分组跳转、脏表单关闭确认、Markdown/高级环境折叠与 `maxReplyTokens` 编辑 |
| `PuddingPlatformAdmin/src/pages/workspace/[id]/SmartRoleModelFields.tsx` | 7 个 Smart 子代理角色模型下拉；读取服务商模型目录并写入 Agent manifest 字段，支持批量填充未配置项或全部角色 |

### 记忆图书馆管理前端
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/memory-library/index.tsx` | 记忆图书馆工作台入口；组织 Workspace/Agent/Library 作用域、搜索、Page/Book/Chapter 操作、内联更新与详情 Drawer |
| `PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageTree.tsx` | Page/Book/Chapter 多节点树；当前 Book 的 Chapter 按需挂为子节点，支持长标题省略与完整行选择 |
| `PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageEditor.tsx` | 类 Notion 文档画布；当前 Page/Chapter 的 Markdown 阅读、内联编辑及新建/归档入口 |
| `PuddingPlatformAdmin/src/pages/memory-library/components/MemoryInspector.tsx` | 节点信息、来源引用与链接检查器；ID 可复制且展示层去重 |
| `PuddingPlatformAdmin/src/pages/memory-library/styles.less` | 阅读优先的双栏工作台、详情 Drawer、Chapter 文档画布及 900/600px 响应式规则 |

### LLM 资源池前端
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/llm-resource-pool/providerTemplates.ts` | 服务商预设目录；包含 DeepSeek、Moonshot Kimi K3、小米 MiMo、DashScope、OpenAI、BigModel 等 Provider/Model 初始配置 |
| `PuddingPlatformAdmin/src/pages/llm-resource-pool/index.tsx` | LLM 服务商与模型管理；服务商配置支持并发数、TPM、RPM 并写入 `llm.providers.json` |

### PuddingHost 组合根
| 文件 | 用途 |
|------|------|
| `PuddingAgent/Program.cs` | Console 开发/诊断薄入口；调用 `PuddingHostOptionsFactory`、`PuddingApplicationHost` 后运行 Host |
| `PuddingHost/Hosting/PuddingApplicationHost.cs` | Console 与 DesktopChild Core 共用的 Host 创建、middleware 组装、初始化和启动后 Loopback 地址捕获入口 |
| `PuddingHost/Hosting/PuddingControllerAddressRewriteHandler.cs` | Desktop/DesktopChild 内部控制面请求重写器；将 `PlatformApiClient` 的路径发送到 Host 启动后捕获的真实动态 Loopback，Console 保留配置端点 |
| `PuddingHost/Extensions/PuddingServiceCollectionExtensions*.cs` | Platform、Runtime、Connector、Bootstrap 的服务注册组合根 |
| `PuddingHost/Extensions/PuddingWebApplicationExtensions.cs` | HTTP middleware、健康检查、Legacy API 诊断与 Workbench SPA fallback；路由顺序是运行时契约 |
| `PuddingHost/Hosting/PuddingApplicationInitializer.cs` | 🔑 Phase 0 Closeout: 单次初始化权威（Platform/Memory schema、Conversation Event Store、Workspace Catalog、jieba backfill）；仅此一条执行路径 |
| `PuddingHost/Hosting/PuddingServerAddressAccessor.cs` | Phase 0 Closeout: Loopback HTTP 地址捕获（仅在 StartAsync 后可用） |
| `PuddingHost/Hosting/ConnectorHostLifecycleService.cs` | Phase 0 Closeout: 三阶段启动（P2P 失败不阻断 Connector） |
| `PuddingHost/Build/PuddingHostContent.props` | Phase 0 Closeout: 统一内容发布（default-data/Config/Prompts/Admin SPA → wwwroot/admin） |
| `PuddingRuntimeTests/Hosting/HostLifecycleTests.cs` | Phase 0 Closeout: Host 生命周期测试 |

### 消息系统
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/chat/types.ts` + `components/MessageList.tsx` | ChatTurn→虚拟消息→MessageStream 投影；必须保留 `sourceId/sourceType`，系统命令不得退化为 Agent 身份；canonical/active-run `processItems` 防御过滤 legacy 子代理 kind，子代理状态不得进入主消息流；仅 timeline 内容变化也必须穿透 MessageStream memo |
| `PuddingPlatformAdmin/src/pages/chat/reducer/subAgentReducer.ts` | 子代理 UI 唯一纯投影：按 eventId 幂等折叠 bootstrap/replay/live 的 created/round/LLM/tool/terminal，并允许 canonical session status 只把 active run 推进到终态；拒绝旧事件或快照把终态降级为 running |
| `PuddingPlatformAdmin/src/pages/chat/components/SubAgentActivityDock.tsx` | 子代理右上角悬浮运行坞与详情检查器；显示活动阶段、模型消息、脱敏工具输入输出、轮次、预算和有界事件时间线；成功/异常终态分别停留 12/30 秒后自动隐藏，完整结果仍按 Run ID 从归档 `output.md` 懒加载 |
| `PuddingPlatformAdmin/src/pages/chat/viewport/messageProjection.ts` | 纯消息虚拟项投影；只生成用户、主 Agent、系统消息和历史加载项，不投影子代理 run，避免多子代理调用污染文档流 |
| `Services/MessageFabric/MessageSystem.cs` | 消息系统核心 |
| `Services/MessageFabric/MessageRouter.cs` | 消息路由（Topic → Channel → Room）；为 `(message,target)` 生成稳定 DeliveryId，并保留 Conversation/reply/correlation/causation/metadata |
| `Services/MessageFabric/MessageFabricStore.cs` | 消息持久化与 Inbox 原子 claim/ack/retry；持久化渠道路由事实，并从 `queued/retrying` 投递发现待处理 Agent/Connector 目标 |
| `Services/MessageFabric/MessageQueueProjectionService.cs` | Agent 交互队列读模型；默认排除 `visibility=system`，诊断模式可显式包含并把 Pudding envelope 投影为正文 |
| `PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs` | Runtime 消息投递唯一消费者；普通消息保留 legacy Runtime 路径，`gateway_ingress` delivery 坚持一条投递一个 ADR-059 Turn，并只在 canonical acceptance 成功后 ack |
| `PuddingCore/Models/AgentReplyVoiceDirective.cs` + `Services/MessageGateway/ConversationTerminalMessageFormatter.cs` + `ConversationReplyProjectionWorker.cs` + `FeishuTtsProjection.cs` | 从 succeeded/failed/cancelled Command 的 committed terminal event 生成统一用户文案并幂等创建 Connector delivery；成功答复仅在显式 `voice` 围栏时追加 typed audio，V1 的纯围栏与混合回复都先发送含围栏的完整原始 Markdown，配置关闭/超长时保留原文且只发文字；活跃 CardKit stream 拥有终态投影，stream `failed` 后走普通文本兜底；`reply_projected_at` 与实际 Connector delivered 状态分离 |
| `PuddingCore/Abstractions/IVoiceSynthesisService.cs` + `PuddingPlatform/Services/VoiceSynthesisService.cs` + `PuddingRuntime/Services/VoiceProviderFactory.cs` | Web/渠道共享的 Provider-neutral TTS 边界；从 `config/voice/providers.json` 解析 Provider/模型与 Provider 自有默认模型，通过 `ITtsProvider` 适配 Qwen/CosyVoice 等服务，并将 URL/Provider 输出收敛为有界音频字节 |
| `PuddingCore/Abstractions/IAudioTranscoder.cs` + `PuddingRuntime/Services/ManagedOggOpusTranscoder.cs` | 进程内短音频双向转码边界；NAudio.Core/Concentus 完成 WAV→16 kHz mono 24 kbps Ogg/Opus 出站与飞书 Ogg/Opus→16 kHz mono PCM WAV 入站，不依赖 ffmpeg/native codec；按实际样本计算时长并限制解码时长 |
| `PuddingAgent/Services/MessageGatewayIngress.cs` | 飞书 V1 Gateway ingress；验证 channel-owned Connector、渠道实例与 Agent `channelIds` 引用，解析 Agent main Conversation，以外部 message_id 生成稳定消息/请求身份；斜杠指令在 Agent delivery 前拦截，并按渠道 `privilegedUserOpenIds` 校验特权用户、可靠回复飞书 |
| `PuddingAgent/Services/FeishuConnectorFactory.cs` + `AgentManifestCatalog.cs` | 从启用的渠道服务商/渠道实例和 Agent 引用动态装配一 Agent 一机器人；Connector 身份为 `feishu:{channelId}`，拒绝重复 AppId，凭据不进入公共 DTO |
| `PuddingHost/Connectors/FeishuConnector.cs` + `FeishuInboundMessageMapper.cs` + `PuddingHost/Services/FeishuTtsDeliveryService.cs` + `FeishuImageUploadPreparationService.cs` + `src/HarnessAgent/Core/Connectors/Feishu/MessageMapper.cs` + `FeishuPostContentConverter.cs` | 飞书 OpenAPI/长连接协议适配；官方 pbbp2 长连；`post` 入站优先把 `content_v2/content` 转 Markdown、未知结构至少提取纯文本，禁止降级为 `[post]`；CardKit v1；图片入站按 50 MiB 边界落 Vision Artifact，图片出站从 Artifact 上传并以 `msg_type=image` 回复，超过飞书 10 MiB 上传边界时在 C# 层生成有界 JPEG 投递副本且保留原图；语音入站按 `file_key/type=file` 下载、纯托管解码并幂等落 Audio Artifact；语音出站经共享 TTS、Opus 转码、file upload 与 `msg_type=audio` reply |
| `PuddingAgent/Services/FeishuStreamingProjectionWorker.cs` + `PuddingCore/Platform/ConnectorStreamContracts.cs` | 将 committed `message.content.appended` 按 durable cursor 原样投影到同一飞书流式卡片，V1 有意保留 `voice`/`ImageGeneration` 围栏供客户端核对 Agent 输出；对可能成为纯 `image` 围栏的前缀延迟建卡，终态解析已有图片围栏后追加 Artifact；sequence/uuid 稳定重试；failed/cancelled 用统一错误文案关闭卡片；任一投影阶段连续失败 5 次进入 `failed` 并回退普通文本，不无限重试、不重跑 Agent |
| `PuddingAgent/Tools/SendVoiceTool.cs` | Agent 显式语音工具；仅从当前 Feishu main Turn 的受信任 Command metadata 解析目标，以 `commandId + toolCallId` 幂等排队 audio delivery，不允许模型指定任意收件人；成功后抑制工具调用后的终态确认文字，已开始文字流时拒绝并提示改用混合 `voice` 围栏 |
| `PuddingAgent/Tools/AsrTool.cs` | 文本模型的受控音频读取工具；只接受 attached-audio notice 中当前 Workspace `audio-*.wav` 的精确绝对路径，拒绝任意文件和跨 Workspace 访问，并把 Provider-neutral ASR 结果标记为不可信用户媒体 |
| `PuddingAgent/Tools/ImportImageTool.cs` + `GenerateImageTool.cs` + `SendImageTool.cs` + `PuddingCore/Models/AgentReplyImageDirective.cs` + `AgentReplyImageGenerationDirective.cs` + `PuddingPlatform/Services/MessageGateway/FeishuImageArtifactProjection.cs` + `FeishuImageGenerationProjection.cs` + `FeishuImageProjection.cs` | Agent 图片工具与 Markdown 多入口；联网参考图按 search→受控导入→precision 编辑，工具支持普通生成、参考图精细编辑/坐标定位及组图并逐 Artifact 发送；小写 `image` 围栏只解析当前 Workspace Artifact，纯围栏为 image-only、混合回复去围栏文本后追加，最多四张；`ImageGeneration` 围栏保留原文后按序生成/追加；可信 metadata 决定目标并抑制重复 |
| `PuddingPlatform/Controllers/Api/FeishuImageDebugController.cs` | Admin-only 增强图片链路调试端口；复用最近可信 Feishu ingress route，`confirmSend=true` 后可带 mode、参考 Artifact、尺寸/格式/优化/web search/组图参数执行真实生成并逐 Artifact 排队，不接受任意外部目标 |
| `Data/Entities/ConnectorStreamProjectionEntity.cs` + `Services/ConnectorStreamProjectionSchemaBootstrapper.cs` | `connector_stream_projections` 的 CardKit resource、累计正文、Conversation cursor、操作 sequence、重试与生命周期状态；SQLite 幂等建表升级 |
| `PuddingAgent/Services/ConnectorDeliveryDispatcher.cs` | Connector endpoint 的 durable egress 消费器；独立 claim/ack/指数退避/dead-letter；CardKit 终态 ACK 后同步完成 stream projection，出站故障不重跑 Agent |
| `Tests/PuddingAgent.IntegrationTests/Feishu/FakeFeishuRoundTripTests.cs` + `FeishuInboundImageTests.cs` + `FeishuInboundAudioTests.cs` + `SendVoiceToolTests.cs` | 无外网 Fake 飞书往返验收；覆盖文本/图片/音频/TTS metadata、图片/音频资源稳定落盘与重投复用、Ogg/Opus 入站规范化、CardKit durable delivery，以及 `send_voice` 当前 Turn 路由/禁用保护/终态文字抑制 |
| `PuddingRuntimeTests/Services/ContextPipelineLayerTests.cs` + `LlmStreamObservabilityTests.cs` + `ManagedOggOpusTranscoderTests.cs` + `PuddingPlatformTests/Services/AudioArtifactStorageServiceTests.cs` + `AudioTranscriptionServiceTests.cs` + `PuddingWebApiTests/Tools/AsrToolTests.cs` | 语音输入/输出协议回归；锁定唯一系统提示、原生 `input_audio` 与文本模型不泄漏、双向托管 Opus 转码、PCM Artifact 校验、Provider 自有默认 TTS/ASR 模型和 `asr` Workspace 路径授权 |
| `PuddingPlatformTests/Services/VisualArtifactObservationServiceTests.cs` | 多模态执行预处理回归；覆盖文本主模型强制视觉观察、原生视觉直通、视觉失败阻断、精确音频 capability 双分流，以及媒体 prompt-injection 安全边界 |
| `PuddingCoreTests/MessageFabric/AgentReplyImageDirectiveTests.cs` + `AgentReplyImageGenerationDirectiveTests.cs` + `PuddingRuntimeTests/Services/VolcengineArkImageGenerationProviderTests.cs` + `PuddingPlatformTests/Services/RemoteImageArtifactImportServiceTests.cs` + `ImageGenerationServiceTests.cs` + `MessageGateway/ConversationReplyProjectionWorkerTests.cs` + `Tests/PuddingAgent.IntegrationTests/Feishu/SendImageToolTests.cs` + `FeishuImageUploadPreparationServiceTests.cs` + `Tests/HarnessAgent.Core.Tests/Feishu/FeishuClientReplyTests.cs` | 图片生成/投递回归；覆盖已有图片围栏授权/纯图片/混合追加、HTTPS 导入复用、Pro 参考图/坐标请求、capability 路由、稳定操作复用、工具抑制、可信 Turn 路由、15 MiB 原图的 C# 有界投递副本、飞书 multipart 上传和稳定 uuid reply |
| `Tests/PuddingAgent.IntegrationTests/Feishu/FeishuCommandInterceptionTests.cs` | 飞书系统指令边界回归；验证 channel-owned open_id 白名单、`/whoami` 身份透传、`/status` 非特权共享处理、`/compact` 只回复 Connector 不投递 Agent，以及显式 `ForwardToAgent` 契约 |
| `PuddingPlatformTests/Services/ChannelConfigurationFileServiceTests.cs` | 渠道文件配置回归；覆盖 Secret 不回显、空 Secret 更新保留、旧 Agent 飞书配置迁移和重复 App ID 拒绝 |
| `Tests/HarnessAgent.Cli/Program.cs` (`feishu-echo`) | 独立飞书 SDK Echo 程序；真实长连接收到文本后以稳定 uuid 调 reply API 原样回复，支持 `--once/--timeout-seconds/--config` |
| `Tests/HarnessAgent.Core.Tests/Feishu/FeishuWebSocketInitialPingTests.cs` | 本地真 WebSocket 协议回归；锁定建连后立即 CONTROL/ping，以及 pbbp2 DATA/event 解码、文本投递和成功 ACK |
| `Controllers/Api/MessageQueueController.cs` | Agent 交互队列 API；`includeSystem=false` 为默认用户界面边界 |

### API Controllers（核心）
| Controller | 用途 |
|------------|------|
| `Controllers/Api/SystemCommandsController.cs` | `POST /api/v1/conversations/{id}/system-commands`；系统命令专用端口，不进入 Agent 执行链 |
| `Api/SessionEventsController.cs` | 🔑 Conversation Event live SSE、forward replay 与 `/compact` HTTP 映射；不编排压缩业务 |
| `Api/SessionApiController.cs` | Session CRUD |
| `Api/AgentChatApiController.cs` | Agent 聊天 API |
| `Api/ConversationTurnsController.cs` | ADR-059 Conversation Turn 唯一命令入口：Submit / Cancel；Steering 明确返回 501 |
| `Api/MessageApiController.cs` | ChatMessages 查询 API；返回 Message/Turn/Command 稳定身份供前端历史收敛 |
| `Api/AuthApiController.cs` | 认证（JWT） |
| `Api/WorkspaceApiController.cs` | 工作区管理 |
| `Api/ToolCatalogApiController.cs` | 工具目录 |
| `Api/WorkspaceSkillApiController.cs` | Workspace Skill CRUD；MCP 配置校验/规范化、热重载与 runtime-status 查询 |
| `Api/ChannelProviderApiController.cs` / `Api/WorkspaceChannelApiController.cs` | 渠道服务商目录与 Workspace 渠道实例 API；后者同步维护 Agent channel 引用且不返回 Secret |
| `Api/FeishuVoiceDebugController.cs` | admin-only 飞书真实语音链路调试 API；预览最近可信入站路由，显式确认后经 Message Fabric 排队 typed audio，并查询 delivery 状态；禁止手填任意飞书目标 |
| `Api/MemoryLibraryAdminController.cs` | 记忆图书馆管理 |

---

## 🔑 PuddingMemoryEngine — 记忆引擎

### 核心类
| 文件 | 用途 |
|------|------|
| `Data/MemoryLibrary.cs` | 🔑 记忆图书馆实现（Book/Chapter CRUD, FTS5 搜索） |
| `Data/IMemoryLibrary.cs` | 记忆图书馆接口 |
| `Data/MemoryDbContext.cs` | 核心会话、消息、记忆与事件队列 DbContext |
| `Data/PlatformDbContextFactory.cs` | Platform 数据库统一工厂；singleton options + singleton factory 供后台服务使用，并由同一工厂创建 scoped HTTP/application DbContext |
| `Data/MemoryDbInitializer.cs` | 从 `Schema/init_memory.sql` 显式初始化核心 Schema；与图书馆共享数据库时不使用 `EnsureCreated` |
| `Data/MemoryLibraryDbContext.cs` | 记忆图书馆 DbContext（与核心记忆共享同一 SQLite 文件） |
| `Data/MemoryLibraryDbInitializer.cs` | 核心 Schema 完成后显式初始化图书馆 Schema |
| `Data/BookRegistry.cs` | 标准 Book 注册表（14 本预定义书） |
| `Data/LibraryEntities.cs` | 实体定义（Library, Book, Chapter, ChapterRelation, Pointer） |

### 结构化事实库
| 文件 | 用途 |
|------|------|
| `FactMemoryService.cs` | 结构化事实 CRUD（Fact + Evidence + Context + Freshness） |
| `MemoryEngine.cs` | 记忆引擎（融合召回：Library + Facts + Prefs） |

### 工具类
| 文件 | 用途 |
|------|------|
| `MemoryBoundaryService.cs` | 记忆边界控制 |
| `SessionMemoryStore.cs` | Session 级记忆存储 |
| `WorkspaceMemoryStore.cs` | Workspace 级记忆存储 |
| `MemoryEntry.cs` | 记忆条目模型 |

---

## 🔑 PuddingCore — 核心抽象与契约

### LLM 配置与解析
| 文件 | 用途 |
|------|------|
| `Abstractions/ILlmResolver.cs` | 🔑 LLM 路由解析边界；`ResolveRouteAsync` 原子返回 Provider/Model 身份与 `LlmConfig` 快照 |
| `Abstractions/ILlmConfigService.cs` | LLM Provider/Model 注册表接口；只支持显式 Provider/Model 精确解析，不选择平台默认模型 |
| `Services/FileLlmResolver.cs` | 基于文件注册表的 LLM 路由实现；负责显式路由、唯一纯模型和能力标签，空路由且无能力标签时拒绝解析 |
| `Services/FileLlmConfigService.cs` | 基于文件的 LLM 配置服务 |
| `Contracts/LlmContracts.cs` | LLM 相关契约模型 |

### 工具契约
| 文件 | 用途 |
|------|------|
| `Contracts/PuddingToolContracts.cs` | 🔑 工具契约（ToolAttribute；SubAgentExposure 的 MainAgentOnly/DelegatedSubAgent；配置所有者、委派开关和深度执行上下文） |

### 会话与运行时
| 文件 | 用途 |
|------|------|
| `Abstractions/ISessionStateManager.cs` | 会话状态管理接口（含 Restore 方法） |
| `Platform/SubAgentSessionId.cs` | 子代理会话 ID 唯一生成器；池预留和普通调度共用，不通过创建空 run 获取身份 |
| `Models/SwarmSessionState.cs` | 会话状态枚举 |
| `Services/RuntimeActivity.cs` | 运行时活动记录（Enrich 方法处理 "unknown" 合法阶段） |
| `Configuration/PuddingDataPaths.cs` | 数据路径配置 |
| `Agents/AgentProfileProvider.cs` | 加载自包含 Agent 实例定义：manifest、`config/llm.json`、Markdown 与 permissions；运行时不跨目录读取模板 |
| `Agents/IAgentSelfMaintenanceService.cs` | Agent 自维护端口；只暴露当前实例白名单文档的 inspect/read/update，不暴露任意路径或其他 Agent ID |

### Conversation 受理与可靠事件流
| 文件 | 用途 |
|------|------|
| `Platform/ConversationHandlers.cs` | 4 个应用 Handler 接口：Submit/Cancel/Steering/Compaction |
| `Platform/ConversationTurnContracts.cs` | SubmitTurn、Recipient、ContentPart、AcceptanceResult |
| `Platform/ConversationEventContracts.cs` | Event Envelope、AppendResult、Cursor 与写入条件 |
| `Platform/ExecutionRunContracts.cs` | 冻结契约：ExecutionLease、TurnTerminal、AgentExecutionSnapshot |
| `Platform/IExecutionRunCoordinator.cs` | Execution Kernel 入口契约 |
| `Platform/IExecutionJournal.cs` | 统一 fenced 事件写入 + 原子终态契约 |
| `Platform/IExecutionLeaseStore.cs` | 原子 CAS 领取、续租、释放契约 |
| `Platform/IControlInbox.cs` | 统一控制消息收件箱契约 |
| `Platform/IAgentExecutionSnapshotFactory.cs` | Agent 执行快照工厂契约 |
| `Platform/IConversationAcceptanceStore.cs` | Turn 批次幂等受理事务边界契约 |
| `Platform/IConversationEventStore.cs` | Conversation Event 追加、重放、Head/Sequence 契约 |
| `Platform/IExecutionCommandReader.cs` | Command 只读契约；写入分别归 Acceptance/Lease/Journal |
| `Platform/ConversationContracts.cs` | 状态枚举（CommandStatus/RunStatus/TurnStatus/TurnTerminalKind）和事件类型常量 |
| `Runtime/ITurnExecutor.cs` | Runtime 执行端口；不依赖 HTTP/SSE/Platform DTO |

### Token 预算
| 文件 | 用途 |
|------|------|
| `Contracts/ContextCompactionContracts.cs` | 上下文压缩契约（含 CapacityPrediction 模型） |
| `Contracts/PrefixCacheContracts.cs` | Prefix Cache 契约（Churn 归因） |

---

## 关键流程（调用链路）

### 1. 用户消息 → Agent 响应（Conversation 可靠事件流, ADR-059 Execution Kernel）
```
前端 POST /api/v1/conversations/{conversationId}/turns
  → ConversationTurnsController                 // HTTP 协议层
  → ISubmitTurnHandler                          // 应用处理器
  → IConversationAcceptanceStore                // 原子受理
    → Message + AcceptanceBatch + ConversationTurn + Command + turn.accepted Event + Head
  → ChatExecutionWorker v5                      // 后台 Worker
    → IExecutionLeaseStore.TryAcquireAsync       // 原子 CAS 领取（BEGIN IMMEDIATE + 每 Conv 互斥）
    → 透传 ExecutionLease 给 IExecutionRunCoordinator
      → IAgentRuntimeProfileResolver               // 唯一配置解析边界
      → IAgentExecutionSnapshotFactory             // 无密钥执行快照
      → LlmInvocationProfile                       // Provider/Profile/Model 类型化路由身份
      → ITurnExecutor                             // Agent Loop Runtime
        → TurnExecutorAdapter                     // usage 帧封装为 v2：usage + 不可变 Provider/Profile/Model/Role
      → TurnOutputChunker                         // delta 聚合
      → IExecutionJournal.AppendOutputAsync       // fenced 输出
      → IExecutionJournal.CommitTerminalAsync     // 原子终态（验证 runId/workerId/fence/lease）
        → Turn + Run + Command + Event + Head 同事务更新
  → ICommittedEventSignal
  → SSE / forward replay（同一 canonical envelope）
  → 前端确认服务端 Turn ID，原子替换 optimistic Turn ID
    → `turn.accepted` 可先于 POST continuation 完成身份迁移
  → 前端 Reducer 按 sequence 幂等提交
  → ConversationProjectionWorker 发现 Head > Checkpoint
  → ConversationProjector
    → 按事件 MessageId 幂等物化 ChatMessages
    → 按 EventId 幂等物化 agent_llm TokenUsageEvents（失败不推进 checkpoint）
  → 前端历史对账只允许单调收敛，不得用滞后物化结果降级 SSE 终态

旧 POST /api/workspaces/{workspaceId}/chat/message 已删除，不保留兼容翻译层
```

### 2. Token 预算与自动压缩
```
ContextWindowManager.EnsureCapacity()
  → 解析 Provider Model: maxContextTokens / maxInputTokens / maxOutputTokens
  → effectiveInput = min(maxInputTokens, maxContextTokens - maxOutputTokens - safetyBuffer)
  → ContextHealthEvaluator.Evaluate(usedTokens, effectiveInput)
    ├── < 60% → 不裁剪
    ├── 60-80% → TrimHistory（token 驱动，修剪到 70%）  // 动态计算 maxMessages = budget/2500
    └── >= 80% → TryAutoCompactAsync()  // LLM 压缩
      → ContextCompactionService.CompactAsync()
      → CompactionEventEmitter.EmitAsync()
      → conversation_events → resumable SSE → 前端
  → CapacityPrediction: 剩余 tokens + 预计几轮后触发各阈值
  → LlmRequestBudgetGuard.Prepare(final messages + tools)
    ├── 超限 → 按完整会话单元移除最旧历史并重新计量
    └── Provider 明确输入范围 400 → 校准 tokenizer 比率，同一执行恢复一次
```

### 3. 手动 `/compact` 与新会话切换

```text
Web /compact
  → POST /api/sessions/{conversationId}/compact ───────────────┐
Feishu /compact                                                │
  → MessageGatewayIngress（稳定 ID + channel-owned 白名单）    │
  → ISystemCommandHandler（拦截，不创建 Agent Turn）───────────┤
                                                               ▼
  IRequestCompactionHandler
      → IAgentRuntimeProfileResolver
      → context.compaction.started
      → IContextCompactionService
      → ICompactionSessionSuccessor
          → create successor Session
          → Controller SessionRepository.RebindMainAsync
          → persist Agent mainSessionId
          → register old → new redirect
      → source context.compaction.completed
      → successor context.compaction.completed
  → Feishu durable Connector reply（统计 + 后继 Conversation）
  → 前端按 compactionId 更新独立状态 Turn
  → 清零新 Conversation 的 SSE cursor 并切换
  → Bootstrap.lifecycleEvents 恢复持久压缩状态
  → 前端维护独立 lifecycle Turn 索引
  → Hook 输出边界统一合并 ChatMessages 与 lifecycle Turn
```

### 4. Web/飞书共享 `/status`

```text
Web /status ──→ system-command endpoint ───────────────────────┐
Feishu /status → MessageGatewayIngress（只读，无需白名单）────┤
                                                               ▼
                   ISystemCommandHandler
                         → ISystemStatusSnapshotProvider
                             → IAgentRuntimeProfileResolver
                             → ILlmConfigService
                             → IContextCompactionService.GetHealthAsync
                             → ISessionStateManager
                             → IRuntimeControlService
                         → canonical system transcript
                         → Web system Turn / Feishu Connector reply
                         ╳ no ConversationTurn / ChatExecutionCommand / Agent delivery
```

Compact HTTP 命令只携带 Conversation/Workspace/Agent 身份、压缩级别、原因和
`compactionId`，不得携带 `llmConfig`。压缩事件没有 `turnId`，前端不得把它
归并到最近的 Agent 回复。`snapshotCursor` 覆盖压缩事件时，Bootstrap 必须同时
返回对应 `lifecycleEvents`，前端应用这些事件后才允许推进 SSE cursor。
Controller SessionRepository 是 Main Session 归属的事实源；Agent manifest 只是
运行时镜像，内存 redirect 只负责进程内低延迟跳转，二者都不能替代持久 rebind。
飞书使用稳定 `clientRequestId` 作为 `compactionId`；压缩已把主会话切到后继会话后，
同一飞书消息重投仍按 `clientRequestId + responseMessageId` 跨 Conversation 命中旧结果，
不得执行第二次压缩。

### 5. Smart* 工具 — 子代理薄包装与有界委派模式
```
ExecutionRunCoordinator（Turn 启动时冻结 24h parent hard deadline，并注册 1h meaningful-progress 窗口）
  → Turn / Runtime / Tool contract 逐层透传（只能收紧）
主 Agent 调用 smart_plan(task="...")
  → SmartPlanTool（上限 3600s / 48 rounds，父级预留 120s）
    → Planner 使用显式只读 capability whitelist
    → 不写计划文件、不执行 shell、不继续派生 Smart 子代理
主 Agent 或获授权子代理调用 smart_explore(task="...")
  → SmartExploreTool（上限 1800s / 32 rounds，DelegatedSubAgent）
    → Explorer 使用同类只读 whitelist，不能继续派生子代理
  → 其他 Smart 工具保持 MainAgentOnly
  → PuddingToolRegistry 在执行边界强制 exposure + allow + depth 三项检查

任意 Smart 工具 → spawn_sub_agent(sync, model 或 capability)
    → SubAgentTool.ResolveChildLlmRouteAsync()
      → ILlmResolver（唯一读取 data/config/llm.providers.json）
        → 唯一确定 Provider/Model + LlmConfig
      → ConfigurationAgentInstanceId 保持根 Agent 配置所有者；
        AgentInstanceId/ParentAgentId 表示当前临时执行身份
      → SubAgentTool 仅补充调用语义 ProfileId=subagent.conscious、Role=conscious
    → ISubAgentInvocationService（只映射，不重新解析）
      → SubAgentManager.ValidateLlmRoute()
      → 同步/异步统一以 parent deadline 收紧预算
      → 并发门等待计入同一个绝对 deadline
      → 非池化：生成 SubSessionId；池化：复用预留 SubSessionId
      → 每次执行创建全新 runId
      → SessionStateManager 原子 UPSERT SubSessionId 当前状态（首次创建/复用重置）
      → Runtime 派发
      → RuntimeExecutionIdentity 派生 child execution
      → AgentExecutionService 发出 round/LLM/tool 运行事实
      → FileSubAgentRunStore events.jsonl
      → SubAgentConversationProjectionWorker
      → canonical Conversation Event Store / resumable SSE
      → subAgentReducer / Chat 子代理运行面板
        → RuntimeDispatchRequest.LlmProfile + LlmConfig
    → 子代理执行角色协议并返回 canonical 详细报告
```

### 6. 记忆写入
```
Agent 调用 save_memory / manage_memory
  → MemoryTools.ExecuteAsync()
    → MemoryLibrary.CreateBookAsync()       // 创建 Book（检查重复）
    → MemoryLibrary.AddChapterAsync()       // 添加 Chapter
    → BookRegistry.GetBookIdByAlias()       // 标准名 → BookId 路由
```

### 7. 记忆召回
```
Agent 调用 search_memory / grep_memory
  → MemoryTools / MemoryLibraryTool
    → MemoryLibrary.SearchChaptersFtsAsync()  // FTS5 全文检索
    → MemoryLibrary.SearchBooksFtsAsync()     // Book 级检索
```

### 8. 会话状态持久化恢复
```
重启
  → SessionStateStore.LoadFromDisk()           // 扫描 data/sessions/*.json
  → 遍历持久化状态
    ├── Streaming/Running/Waiting → 恢复为 Stopped（被中断）
    ├── Completed/Closed → 保持原状态
    └── 无持久化记录 → 标记 Stopped（兜底）
  → ISessionStateManager.Restore() → 恢复 _sessionStates
```

---

## Smart 工具角色模型配置

### manifest.json 字段（Agent 实例级）
| 字段 | 用途 |
|------|------|
| `explorerModel` | Explorer 子代理模型（smart_explore/smart_search/smart_query_session_log） |
| `researcherModel` | Researcher 子代理模型（smart_research） |
| `plannerModel` | Planner 子代理模型（smart_plan） |
| `reviewerModel` | Reviewer 子代理模型（smart_review） |
| `developerModel` | Developer 子代理模型（smart_develop） |
| `deployerModel` | Deployer 子代理模型（smart_deploy） |
| `testerModel` | Tester 子代理模型（smart_test） |
| `channelIds` | 绑定的渠道实例 ID；账号、Secret 与渠道策略保存在 `data/channels`，不得嵌入 Agent manifest |

值格式: `"{providerId}/{modelId}"`，如 `"deepseek/deepseek-v4-pro"`。
不配置时 Smart 工具不传 `model`，由 `spawn_sub_agent` 的默认模型策略解析。

### C# 数据模型
- `AgentInstanceManifest`（`PuddingConfigModels.cs`）：7 个 `string?` 属性
- `WorkspaceAgentDto` / `CreateWorkspaceAgentRequest` / `UpdateWorkspaceAgentRequest`：管理 API 的七字段契约
- `WorkspaceAgentFileService`：创建、列表、详情、更新均以 Agent manifest 为唯一配置源；PUT 支持清空角色模型
- `SmartWorkflowToolBase.ResolveRoleModelAsync()`：从 manifest 解析角色模型
- `ILlmResolver.ResolveRouteAsync()`：消费入场 `providerId/modelId` 或能力标签，从
  `ILlmConfigService` 原子解析 `ProviderId + ModelId + LlmConfig`；空路由且无能力标签
  时直接拒绝，不得选择平台默认模型，也不得从 endpoint/key/model 反推 Provider
- `SubAgentTool.ResolveChildLlmRouteAsync()`：只为上述路由补充
  `ProfileId=subagent.conscious` 与 `Role=conscious`
- `SubAgentInvocationRequest` / `SubAgentSpawnRequest`：不再持有冗余 `ModelId`，
  只透传上述不可变快照；同时透传 `MaxRounds` 与 `WorkingDirectory`，后者是文件
  工具根目录而不是 WorkspaceId 的路径映射；`SubAgentManager` 在产生状态/事件前
  校验两个模型 ID 一致
- `SmartWorkflowArgs`：七个 Smart 工具统一以 `task` 作为必填主指令；
  `ScopedSmartWorkflowArgs.scope` 只有在指向真实文件/目录时才冻结为 WorkingDirectory
- `AgentExecutionService.ExecuteAsync()`：同步边界禁止返回 `Running`；function-call 在最后
  一轮耗尽时统一返回 `Failed + MaxRoundsReached`

### 前端 UI
- `workspace/[id]/SmartRoleModelFields.tsx`：加载启用的 LLM 服务商/模型并生成 7 个角色模型下拉，支持批量填充
- `workspace/[id]/WorkspaceAgentSettingsDrawer.tsx`：Workspace Agent 自包含配置编辑器；
  复用全局模板的分组组件，但字段绑定到 Agent DTO，不回查模板；六个互斥分组共用一个 Form store，
  Markdown 和高级运行环境默认折叠
- `workspace/[id]/index.tsx`：加载 Agent 详情、模板创建快照、Provider/Model、Capability
  和 Skill 选项，并负责完整创建/更新请求
- 下拉选项格式：`{服务商名} / {模型名} (上下文大小)`

### Workspace Agent 配置闭环

```text
WorkspaceAgentSettingsDrawer
  → Create/UpdateWorkspaceAgentRequest
  → WorkspaceAgentFileService
  → data/agents/{agentId}/manifest.json + Markdown + config/llm.json
  → AgentProfileProvider
  ├→ AgentExecutionSnapshotFactory
  │   → ExecutionRunCoordinator → TurnExecutionContext → RuntimeDispatchRequest
  └→ SmartWorkflowToolBase
```

- Agent 编辑器覆盖角色、Prompt/Markdown、能力、Skill、主模型、潜意识模型、
  Embedding、Smart 子代理模型和执行护栏（含 `maxReplyTokens`）；渠道账号与密钥在渠道管理维护
- `sourceTemplateId` 创建后只作为来源审计信息，运行时不得据此读取模板
- `maxContextTokens` 不进入 Agent 表单或 Agent 配置，容量只由 Provider Model 解析
- 最大轮次、最大耗时、最大工具调用进入不可变执行快照；默认父 Turn 最大耗时为
  86400 秒最终安全上限，最大轮次与耗时仍受平台 `AgentExecutionGuardrails` 约束；工具调用
  总预算直接采用快照写入的 `RuntimeDispatchRequest.MaxToolCallsTotal`，不再受隐藏全局值裁剪；
  正常停滞由 3600 秒滑动 meaningful-progress 窗口终结

---

## 🧠 Subconscious — 潜意识自改进系统

> 后台异步自循环，5 条专业化管道 + 完整作业队列 + 运行时控制 + 多层可观测性。

### 主要触发与学习入口

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousConsolidationHook** | `PuddingRuntime/Services/Background/SubconsciousConsolidationHook.cs` | AgentLoop Hook：每轮对话结束 → `Channel<ConsolidationJob>` 入队 |
| **SubconsciousJobScheduler** | `PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs` | 定时调度：9 种跳过条件（空闲冷却/并发限制/预算耗尽/DryRun...） → `TryLeaseNextAsync` |
| **SessionCompressedMemoryMaintenanceHook** | `PuddingRuntime/Services/Background/SessionCompressedMemoryMaintenanceHook.cs` | ✅ HOSTED — 订阅 `session.compressed`，把压缩前抢救出的 Memory Notes 以稳定幂等键送入持久潜意识 Job |
| **SubconsciousTriggerTool** | `PuddingRuntime/Tools/BuiltIns/Management/SubconsciousTriggerTool.cs` | 对主 Agent 透明的语义工具：`auto_dream` / `extract_patterns` / `improve_skills` / `consolidate` / `all`；内部按 Agent 的 `subconscious` 角色路由模型 |
| **SubconsciousDebugApiController** | `PuddingPlatform/Controllers/Api/SubconsciousDebugApiController.cs` | 认证调试 API：主动触发 evolution、指定日期 daily summary、`session.compressed` Hook，并查询 Job/Result；DebugApi 开关关闭时返回 404 |
| **AgentDailySummaryBatchService** | `PuddingPlatform/Services/AgentDailySummaryBatchService.cs` | 从实际 Agent 日志生成外部 Markdown 记忆索引；`POST /api/debug/subconscious/daily-summary/trigger` 可按日回填，源哈希保证重跑不重复消费 Token |
| **SubconsciousWorkerService** | `PuddingRuntime/Services/Background/SubconsciousWorkerService.cs` | ✅ HOSTED — 消费持久队列；三个周期循环只负责按时间桶幂等入队；执行时按 Job 所属 Agent 的 `subconscious` 角色解析模型，先持久化 Report envelope 再完成 Job；解析失败不回退 |
| **ConversationSkillEvolutionTrajectorySource** | `PuddingRuntime/Services/Skills/ConversationSkillEvolutionTrajectorySource.cs` | 从规范 `conversation_events` + 成功 Command 读取 workspace/agent 隔离的已验证工具轨迹；有界扫描最近最多 200 条成功 Command，在质量过滤后截取候选，避免近期简单对话饿死较早黄金路径 |
| **AgentSkillEvolutionStore** | `PuddingRuntime/Services/Skills/AgentSkillEvolutionStore.cs` | 自进化适配器：通过 `AgentSkillFileService` 创建/更新真实 SKILL.md、manifest 与 index |
| **SkillEvolutionDeduplicationService** | `PuddingMemoryEngine/Services/SkillEvolutionDeduplicationService.cs` | 自值守 Skill 准入与去重：Flash 提议 create/merge/skip/defer 或重复组，确定性置信度、工具指纹、文本/来源证据复核；合并后禁用重复项而不删除；按 Skill 版本写 dedup/evaluation 水位避免周期空转 |

### 作业队列

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousJobQueue** | `PuddingMemoryEngine/Services/SubconsciousJobQueue.cs` | 持久队列：Enqueue(幂等键) → Lease(超时) → Complete/Fail/DeadLetter；过期 `processing` 租约在统计中转为 pending backlog，避免并发门禁阻止 Worker 重启后自恢复；遥测指标 |
| **SubconsciousJobEntity** | `PuddingMemoryEngine/Entities/SubconsciousEntities.cs` | EF 实体：JobId/Type/IdempotencyKey/Status/RetryCount/LeaseUntil |
| **SubconsciousJobLogEntity** | 同上 | 作业日志：SessionId/Status/FactsExtracted/FactsMerged/ElapsedMs/ErrorMessage |

### 5 条专业化管道

| 管道 | Orchestrator 方法 | 描述 | 报告 |
|------|------|------|------|
| **事实提取** | `ConsolidateAsync` | LLM → 事实/偏好 → Jaccard≥0.8 去重合并 → MemoryFacts/Preferences → Library | `SubconsciousJobLogEntity` |
| **记忆整理** | `AutoDreamAsync` | Flash 分析 Library 快照 → merge/archive/delete（≤5 op, 30d 过期） | `AutoDreamReport` |
| **经验→SKILL** | `ExtractPatternsAsync` | 成功 Command + 规范工具事件 → 来源 Turn 去重 → 黄金路径 → passing/reusable/safe → Flash 语义准入(create/merge/skip/defer) → Agent 私有 Skill | `PatternExtractionReport` |
| **Skill 改进** | `ImproveSkillsAsync` | Flash 语义聚类 + 确定性复核 → 规范 Skill 吸收证据、重复项可恢复禁用 → 对启用 Skill 完整 Markdown 评估与原地修补 | `SkillImprovementReport` |
| **增强召回** | `RecallAugmentedAsync` | LLM 直接阅读全量 MemoryFacts + Preferences，自主判断相关性（不做 LIKE/FTS5）；LLM 判断同样按 Agent 的 `subconscious` 角色路由 | `RecallDiagnostics` |

```text
ConsolidateAsync:  消息 → LLM抽取 → ExtractionPayload(JSON) → 去重(Jaccard) → Facts/Prefs → Library
AutoDreamAsync:    MemorySnapshot → LLM规划(AutoDreamPlan) → merge/archive/delete
ExtractPatternsAsync: conversation_events成功工具链 → source-turn去重 → LLM检测 → 三重质量门禁 → 语义准入 → create/merge/skip/defer
ImproveSkillsAsync: Agent私有auto-generated SKILL.md → 语义聚类+确定性复核 → 可恢复禁用重复项 → LLM评估 → 修补+Bump版本
RecallAugmentedAsync: 用户消息 + 全量Facts → LLM编译 → 截断(maxTokens*4 chars)
```

### 增强召回管道（Track 1）

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousRecallPipeline** | `PuddingRuntime/Services/SubconsciousRecallPipeline.cs` | 关键词提取(纯算法) → 混合搜索(记忆库→日摘要→日志) → Flash 判断排名(单次调用, Temp=0/Seed=42) → 截断注入(≤5条, ~2K tokens)。Session 级状态：话题转换检测 + 连续不召回兜底（每5轮强制）、30s 内存缓存 |

### 运行时控制与可观测性

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousRuntimeControlService** | `PuddingRuntime/Services/Background/SubconsciousRuntimeControlService.cs` | Pause/Resume + GetSnapshot（队列状态+调度配置+诊断） |
| **SubconsciousDiagnosticLog** | `PuddingRuntime/Services/Background/SubconsciousDiagnosticLog.cs` | JSONL 诊断日志：按日分片、1MB 滚动、200 文件保留 |
| **SubconsciousPlanGenerationService** | `PuddingRuntime/Services/SubconsciousPlanGenerationService.cs` | Dry-run 计划生成 → MemoryMaintenancePlan → 校验 → 遥测（Activity + Metric） |
| `/health/subconscious` | `PuddingAgent/Program.cs` | HTTP 健康端点：DB 查询最近 JobLog |
| **SubconsciousRuntimeControlSnapshot** | `PuddingCore/Platform/SubconsciousDtos.cs` | 一站式快照：State/IsPaused/QueueStats/Scheduling/Diagnostics |
| **SubconsciousJobQueueStats** | 同上 | 队列统计：Pending/Retrying/Processing/Completed/DeadLetter + per-workspace/per-session |
| **SchedulingSkipReasons（9种）** | 同上 | Disabled/DryRun/Cooldown/WorkspaceLimit/GlobalLimit/SessionLimit/BudgetExhausted/BackoffNotElapsed/NoEligibleJob |
| 遥测指标 | — | `subconscious_job.enqueue` / `lease` / `complete` / `schedule_skip` |
| 流事件 | — | `StreamingEventBus`：SubconsciousLoad / SubconsciousThink / SubconsciousDone |

### 配置

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousOptions** | `PuddingCore/Configuration/SubconsciousOptions.cs` | 开关：EnableWorker / EnableLegacyConsolidationHook / DebugApiEnabled；主宿主绑定 `AppContext.BaseDirectory` 配置源 |
| **SubconsciousSchedulingOptions** | 同上 | 调度：周期作业开关、默认 workspace/agent、三个首次延迟与周期，以及 IdleCooldown(60s) / MaxGlobalConcurrent(1) / MaxRetryAttempts(3) / BudgetWindow(60min) / MaxJobsPerWorkspacePerHour(20) |
| **AgentLlmRoleIds / ResolveRoleAsync** | `PuddingCore/Abstractions/ILLMConfigResolver.cs` | 稳定语义角色契约；业务调用方只声明角色，provider/model/profile 来自 Agent 实例配置，账单记录保留 workspace/session/stage 归因 |
| **MemoryLibraryConvenience** | `PuddingMemoryEngine/Data/MemoryLibraryConvenience.cs` | 记忆写入的语义去重按所属 Agent 的 `subconscious` 角色调用 LLM；无 workspace/Agent 身份的旧 `SmartSearchAsync` 不在主宿主偷偷启动默认-profile 深度探索，主会话由 role-scoped recall pipeline 完成 Flash 判断 |

### 数据流水线

```text
触发:
  AgentLoopHook → Channel<ConsolidationJob>
  session.compressed → SessionCompressedMemoryMaintenanceHook → ISubconsciousJobQueue → Wiki page update
  SubconsciousJobScheduler → ISubconsciousJobQueue.LeaseNextAsync() → Worker → Orchestrator
  SubconsciousTriggerTool → role(subconscious) → Orchestrator（语义工具）
  SubconsciousDebugApiController → evolution/daily-summary/hook（认证 HTTP 主动调试）
  AgentDailySummaryBatchService → role(subconscious) → memory/daily/*.md + memory/index.json

Orchestrator:
  PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs（1614 行）
  依赖: IMemoryLibrary, IMemoryEngine, IMemoryLlmClient, IEmbeddingService,
        IMemoryDbContextFactory, IMemoryLibrarian, IStreamingEventBus

可观测性栈:
  ILogger → 结构化日志（Debug~Error, 含 SessionId/WorkspaceId）
  SubconsciousDiagnosticLog → JSONL 按日归档
  SubconsciousRuntimeControlSnapshot → 队列+调度+诊断 一站式
  /health/subconscious → HTTP 健康检查
  TelemetryMetricSink → enqueue/lease/complete/schedule_skip 指标
  RuntimeActivitySink → memory_maintenance_plan.validate 活动
  RecallDiagnostics (AsyncLocal) → Rounds/Queries/FoundItems/Latency
```

---

## 注意事项

1. **双轨工具系统**: 正在从 `IAgentSkill`（Legacy）迁移到 `IPuddingTool`（新），两套接口并存
2. **双轨记忆系统**: 传统图书馆（Book/Chapter）+ 结构化事实库（Fact）并存，未来融合
3. **Smart* 工具有界委派模式**: 七个工具统一 `task` 合同；单次共享上限 3600 秒，`smart_plan=3600 秒/48 轮`，`smart_explore=1800 秒/32 轮`；除 `smart_explore=DelegatedSubAgent` 外保持 `MainAgentOnly`，唯一嵌套边为 `smart_plan → smart_explore`，并由 capability whitelist、委派开关和深度硬门共同防循环
4. **能力标签系统 (P2)**: `ILlmResolver.ResolveRouteAsync(requiredCapabilityTags)` 按标签选择唯一配置源中的模型；显式 model 路由优先
5. **Token 预算准确**: Provider usage 不得被更小本地估算覆盖；估算完整计入 reasoning/tool-call payload，并按 session + model 校准；有效输入预算同时受 Provider 输入硬上限、输出预留和安全余量约束
6. **会话持久化**: `SessionStateStore` 在状态变更时异步写入 `data/sessions/{id}.json`，重启后恢复
7. **EF Core Migration**: Platform 用 Code-First Migration，MemoryEngine 用 DbInitializer 手动建表
8. **SSE 双轨迁移**: 新聊天链路以 `ConversationEventStore` 为事实源并按 sequence 重放；`SessionStateManager` 仅保留遗留 Session 流
9. **工具权限**: `ToolPermissionPolicyService` 先执行显式 `AllowedToolNames` 暴露边界，再处理安全区与 Yolo 审批旁路；高危工具需 `InMemoryToolApprovalService` 审批
10. **执行配置边界**: Command 只保存稳定引用；LLM/Tool/Skill 配置由 Worker 执行时通过 SnapshotFactory 快照化
11. **ADR-059 Execution Kernel 已建成**: Worker 原子 CAS 领取；Journal 负责 fenced 输出、原子终态与基础设施失败兜底；SnapshotFactory 负责执行配置；ControlService 负责 Cancel/Control 写入
12. **唯一命令入口**: 前端只调用 `POST /api/v1/conversations/{id}/turns`；旧 ChatApiController 与旧前端发送函数已删除
13. **Command 单一写入权威**: `IChatCommandStore` 已删除；读取使用 `IExecutionCommandReader`，受理/租约/终态分别由 AcceptanceStore/LeaseStore/Journal 写入
14. **Control 安全边界**: Inbox 只读后确认；Cancel 在终态成功后确认；Steering 在 Runtime 消费器完成前返回 501
15. **启动与健康门禁**: 所有环境启用 DI Build/Scope 校验；`/health/live` 与 `/health/ready` 分离
16. **Agent LLM 快照**: `data/agents/{agentId}/manifest.json` 的 `preferredProviderId + preferredModelId` 是主 Agent 执行模型的唯一真相源；`config/llm.json` 仅作为管理兼容镜像，不参与主 Agent 路由，Resolver 对缺失/无效配置返回 `agent_configuration_invalid`，不得回查模板、资源池默认或系统默认模型
17. **Agent 不复制模型容量**: `maxContextTokens`、`maxInputTokens`、模型 `maxOutputTokens` 只从 `llm.providers.json` 的 Provider Model 解析；Agent manifest、Agent DTO 和 `config/llm.json` binding 不保存这些字段，`maxReplyTokens` 仅收紧实例输出并下传 Provider
18. **前端终态游标**: `turn.accepted` 负责尽早迁移 optimistic Turn 身份；终态按 Turn 清除全部关联 messageId，事件只有成功归并后才能推进 cursor
19. **Agent 执行护栏生效链**: Agent manifest → RuntimeProfile → ExecutionSnapshot → TurnExecutionContext → RuntimeDispatchRequest；实例上限不得超过平台 Guardrails
20. **渠道配置独立化**: `data/config/channel.providers.json` 声明已安装 Connector，`data/channels/{channelId}/manifest.json` 保存渠道实例和密钥，Agent manifest 只保存 `channelIds`；管理 API 不回显 Secret，运行时按 channel-owned identity 装配 Connector

---

## 🆕 新增模块 (2026-07-22)

### 多模型协作与显式 Fallback
| 文件 | 用途 |
|------|------|
| `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | 7 个 Smart 工具可声明备用模型链，但默认禁止跨模型重试；仅调用参数 `allow_fallback=true` 且 `IsTransientSmartFailure` 命中时才按 `FallbackModelIds` 显式降级 |
| `PuddingCore/Abstractions/ILlmConfigService.cs` (`ProviderCompatConfig`) | 6 个 Provider 兼容性开关；K3 Gateway 适配：`maxTokensField→max_tokens`、`requiresStringContent`、`useReasoningEffort`、`supportsUsageInStreaming→false`、`requiresReasoningContentInToolMessages` |
| `PuddingGateway/Services/OpenAiLlmGateway.cs` | `BuildRequestBody` 中消费 6 个 compat 字段 |

### 大文件支持
| 文件 | 用途 |
|------|------|
| `PuddingPlatform/Services/FileChunkService.cs` | 大文件分块读取基础 — 支持滑动窗口流式读取 >100KB 文件；为后续大文件工具操作提供基础 |

### Chat 前端 — 交互体验优化 (Phase 1+2+3)
| 文件 | 优化 | 说明 |
|------|------|------|
| `hooks/useSessionEventConnection.ts` | SSE 断流状态条 | `reconnectCountRef` → ChatMain Alert banner |
| `components/AgentMessageBubble.tsx` | TTFB + 停滞检测 + 语音气泡 | 3s/10s 阈值；计时使用服务端 Turn 时间，重挂载不归零；15s 琥珀脉冲；`modality='voice'` 波形 |
| `components/MessageItem.tsx` | 代码懒高亮 + Settle FLIP + Vision Artifact 围栏 | 流式跳过 Prism；200ms transform 平滑切换；小写 `image` 代码块只把当前 Workspace 的 `vision-*`/精确本地 Artifact 路径映射为受控 API 图片，任意路径仍按代码显示 |
| `hooks/useTypewriterStreaming.ts` | 增量扫描 + 自适应打字机 | O(n)→O(delta)；48-200 chars 动态缓冲 |
| `viewport/useMessageViewportRuntime.ts` | 高度缓存 + 滚动锚定 | Map 缓存；500ms 挂起；rAF×2 重试 |
| `components/MessageList.tsx` | 未读 badge + 诊断导出 + 骨架屏 | 红点计数；Alert 诊断复制；Skeleton |
| `components/MessageGroup.tsx` | 发送失败保护 | 红色边框 + 复制内容 + 重试发送 |
| `styles/animations.styles.ts` | 动画复活 | 5 keyframes：messageIn/stepIn/blockCondense/glowSettle/charFadeIn |
| `components/ChatMain.tsx` | React.lazy 懒加载 | DevPanel/SubAgentDock/HistorySearchModal 延迟加载 |
| `components/IntentConsole.tsx` + `VoiceConversationPanel.tsx` | 语音面板集成 | 麦克风 → 530 行语音面板 (ASR+TTS) |

### Chat 前端 — 多模态图片支持
| 文件 | 用途 |
|------|------|
| `components/UserMessageBubble.tsx` | 用户多图气泡：`visionArtifactIds` → GET API → `<img>` 画廊；加载失败回退 |
| `components/IntentConsole.tsx` + `components/visionArtifactImage.ts` | 图片暂存：多选/粘贴/拖放；非 JPEG/PNG/WebP 在浏览器解码后转 PNG → `onSendWithMetadata(visionArtifactIds)` |
| `hooks/useMessageSend.ts` | `submitConversationTurn` 携带 `metadata: { visionArtifactId }` |
| `hooks/useSessionHistoryProjection.ts` | `toTurnsFromHistory` 映射 `item.metadata` → 历史图片渲染 |
| `client/api.ts` | `ChatMessageDto.metadata` + `SubmitConversationTurnRequest.metadata` |
| `PuddingPlatform/Services/ConversationCommandSchemaBootstrapper.cs` | SQLite Schema 升级：`PRAGMA table_info` 幂等补齐 `metadata_json` 列 |

### 死代码审计 (2026-07-22)
| 文件 | 状态 | 说明 |
|------|:--:|------|
| `PuddingMemoryEngine/Class1.cs` | 🗑️ 待删除 | 空白占位类，零引用 |
| `PuddingCore/Swarm/` (10 文件) | 🗑️ 待归档 | Swarm 原型，DI 中零引用 |
| `PuddingCoreTests/Test1.cs` | 🗑️ 待删除 | 占位测试 |
| `PuddingWebApiTests/Test1.cs` | 🗑️ 待删除 | 占位测试 |
| `ILLMConfigResolver.cs:13-32` | `[Obsolete]` | 旧版 ResolveAsync 方法 |
| `AgentTemplateProvider.cs` | `[Obsolete]` | 已迁移到 manifest |

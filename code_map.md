# PuddingAgent CodeMAP

> 顶层快速索引 | 2026-08-09 | 29 项目 | .NET 10 / WPF / React / SQLite / WebView2

## 项目定位

Pudding — Windows 桌面智能助手。ASP.NET Core 是 Desktop 子进程，Console 仅开发入口。详见 `Agents.md`。

## 架构文档

| 文档 | 主题 |
|------|------|
| `Docs/07架构/67ADR-066*.md` | Browser 能力与 Douyin 分层决策 |
| `Docs/07架构/68*.md` | WebView2 自动化分阶段实施规格 |
| `Docs/07架构/69*.md` | Desktop 浏览器工作区/运行中心/存储 |
| `Docs/07架构/70–73*.md` | Phase 2A-1 Bridge 与双标签工作区（✅） |
| `Docs/07架构/74*.md` | Phase 2A-2 Remote Browser + Agent Tools（✅） |
| `Docs/07架构/75–76*.md` | Phase 2A-3 Snapshot/Locator/Interact/Wait（✅） |
| `Docs/07架构/77–79*.md` | Phase 2A-3B/C DeepSeek 验收与闭环 |
| `Docs/07架构/80ADR-069*.md` | MOA 子代理设计委员会编排核心；Phase 1 只编译、不执行 |
| `Docs/QA/QA-2026-08-03*.md` | Qwen 输入上限修复验收 |
| `Agents.md` | 仓库级开发约束 |
| `dev-up.py` | 本地开发监督器 |
| `How-Debuge.md` | 诊断路径 |

## 顶层目录

| 项目 | 说明 | 详细索引 |
|------|------|----------|
| `Source/PuddingAgent/` | 🔑 入口 (Program.cs) | — |
| `Source/PuddingRuntime/` | 🔑 Agent Loop · LLM · 工具 · 上下文管线 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingDesktop/` | 🔑 WPF Launcher · 固定端口 Core 子进程 · Browser 工作区 | [code_map](Source/PuddingDesktop/code_map.md) |
| `Source/PuddingHost/` | 🔑 组合根 · 全网卡 HTTP/本机控制地址 · Browser Bridge · 飞书连接器 | [code_map](Source/PuddingHost/code_map.md) |
| `Source/PuddingCore/` | 🔑 抽象与契约 · 接口 · 模型 | [code_map](Source/PuddingCore/code_map.md) |
| `Source/PuddingPlatform/` | 🔑 Session · API · EF Core · 消息网关 | [code_map](Source/PuddingPlatform/code_map.md) |
| `Source/PuddingMemoryEngine/` | 🔑 Library/Book/Chapter · FTS5 · 潜意识 | [code_map](Source/PuddingMemoryEngine/code_map.md) |
| `Source/PuddingGateway/` | LLM 网关适配 | [code_map](Source/PuddingGateway/code_map.md) |
| `Source/PuddingController/` | 代理控制层 | [code_map](Source/PuddingController/code_map.md) |
| `Source/PuddingCodexService/` | Codex MCP Sidecar | [code_map](Source/PuddingCodexService/code_map.md) |
| `Source/PuddingBrowser.AgentTools/` | 七项 Browser Agent Tools | [code_map](Source/PuddingBrowser.AgentTools/code_map.md) |
| `Source/PuddingBrowser.Abstractions/` | Browser 契约 | [code_map](Source/PuddingBrowser.Abstractions/code_map.md) |
| `Source/PuddingBrowser.WebView2/` | WebView2 Driver | [code_map](Source/PuddingBrowser.WebView2/code_map.md) |
| `Source/PuddingBrowser.Protocol/` | Bridge 线协议（8 .cs） | [code_map](Source/PuddingBrowser.Protocol/code_map.md) |
| `Source/PuddingCodeIntelligence/` | 代码索引/分析 | [code_map](Source/PuddingCodeIntelligence/code_map.md) |
| `Source/PuddingCodeIndexer.Cli/` | 代码索引 CLI | [code_map](Source/PuddingCodeIndexer.Cli/code_map.md) |
| `Source/PuddingFullTextIndex/` | 全文索引引擎 | [code_map](Source/PuddingFullTextIndex/code_map.md) |
| `Source/PuddingGit.Tools/` | Git 20 工具（实现在 Runtime） | [code_map](Source/PuddingGit.Tools/code_map.md) |
| `Source/PuddingPlatformAdmin/` | React 管理前端 · Chat 虚拟视口/渐进消息/状态缓存 | [code_map](Source/PuddingPlatformAdmin/code_map.md) |

## 调用链路

```
Agent Loop → search_tools → Browser Tools (PuddingBrowser.AgentTools)
  → IBrowserRuntime → RemoteBrowserRuntime (Host/BrowserBridge/)
    → WebSocket → DesktopBrowserBridgeClient (Desktop/Browser/)
      → WebView2 (PuddingBrowser.WebView2)

Agent Loop → LlmInvocationService → DirectLlmClient
  → model.protocol=openai → OpenAiLlmGateway (/chat/completions)
  → model.protocol=responses → ResponsesLlmGateway (/responses)
  → model.protocol=anthropic → AnthropicMessagesLlmGateway (/messages)
  → Provider 不保存协议；同一 Provider 的模型可分别选择三种协议

DesignRequest + ExpertGroupDefinition → DesignCouncilPlanCompiler
  → 上下文审计 → 调研 → 独立提案 → 交叉批判 → 主席综合 → 独立终审
  → 输出 Draft + RequiresExplicitActivation；当前阶段不调用子代理

Chat first paint → AgentConversationProjectionService
  → 最近 20 条消息 + active run 最近 64 条过程明细/全量摘要
  → MessageList → MessageViewportRuntime（虚拟化、锚点、贴底）
  → 展开过程摘要时才构建 rounds / trace chips
  → MessageItem 先渲染纯文本，异步加载 Markdown/KaTeX 增强块
  → 子代理检查器、会话诊断 Drawer、摄像头输入仅在首次打开时加载
  → 常驻埋点使用 perfEventRuntime；完整诊断模块仅在 perf/debug 模式加载
```

## 测试项目

| 项目 | 覆盖 |
|------|------|
| `Tests/PuddingCoreTests/` | 工具契约、LLM 网关、MessageFabric |
| `Tests/PuddingRuntimeTests/` | Agent Loop、上下文管线、语音/图片 |
| `Tests/PuddingPlatformTests/` | 渠道配置、Artifact 存储、图片生成 |
| `Tests/PuddingMemoryEngineTests/` | Library/Book/Chapter、FTS5、Skill 去重 |
| `Tests/PuddingMemoryEngineBenchmarks/` | BenchmarkDotNet |
| `Tests/PuddingCodeIntelligenceTests/` | 代码索引 |
| `Tests/PuddingCodexServiceTests/` | Codex MCP Service |
| `Tests/PuddingFullTextIndexTests/` | 全文索引 |
| `Tests/PuddingWebApiTests/` | Web API |
| `Tests/PuddingDesktop.Tests/` | Desktop 进程/配置、Browser Controller/Client（135/135 ✅，Release 2026-08-09） |
| `Tests/PuddingHost.Tests/` | Bridge Endpoint/Remote proxy（56/56 ✅） |
| `Tests/PuddingBrowser.AgentTools.Tests/` | 七项 Agent Tools（10/10 ✅） |

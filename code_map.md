# PuddingAgent CodeMAP

> 顶层快速索引 | 2026-08-07 | 29 项目 | .NET 10 / WPF / React / SQLite / WebView2

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
| `Docs/QA/QA-2026-08-03*.md` | Qwen 输入上限修复验收 |
| `Agents.md` | 仓库级开发约束 |
| `dev-up.py` | 本地开发监督器 |
| `How-Debuge.md` | 诊断路径 |

## 顶层目录

| 项目 | 说明 | 详细索引 |
|------|------|----------|
| `Source/PuddingAgent/` | 🔑 入口 (Program.cs) | — |
| `Source/PuddingRuntime/` | 🔑 Agent Loop · LLM · 工具 · 上下文管线 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingDesktop/` | 🔑 WPF Launcher · Core 子进程 · Browser 工作区 | [code_map](Source/PuddingDesktop/code_map.md) |
| `Source/PuddingHost/` | 🔑 组合根 · Browser Bridge · 飞书连接器 | [code_map](Source/PuddingHost/code_map.md) |
| `Source/PuddingCore/` | 🔑 抽象与契约 · 接口 · 模型 | [code_map](Source/PuddingCore/code_map.md) |
| `Source/PuddingPlatform/` | 🔑 Session · API · EF Core · 消息网关 | [code_map](Source/PuddingPlatform/code_map.md) |
| `Source/PuddingMemoryEngine/` | 🔑 Library/Book/Chapter · FTS5 · 潜意识 | [code_map](Source/PuddingMemoryEngine/code_map.md) |
| `Source/PuddingGateway/` | LLM 网关适配 | — |
| `Source/PuddingController/` | 代理控制层 | — |
| `Source/PuddingCodexService/` | Codex MCP Sidecar | — |
| `Source/PuddingBrowser.AgentTools/` | 七项 Browser Agent Tools | — |
| `Source/PuddingBrowser.Abstractions/` | Browser 契约 | — |
| `Source/PuddingBrowser.WebView2/` | WebView2 Driver | — |
| `Source/PuddingBrowser.Protocol/` | Bridge 线协议（8 .cs） | — |
| `Source/PuddingCodeIntelligence/` | 代码索引/分析 | — |
| `Source/PuddingCodeIndexer.Cli/` | 代码索引 CLI | — |
| `Source/PuddingFullTextIndex/` | 全文索引引擎 | — |
| `Source/PuddingGit.Tools/` | Git 20 工具（实现在 Runtime） | — |
| `Source/PuddingPlatformAdmin/` | React 管理前端 | — |

## 调用链路

```
Agent Loop → search_tools → Browser Tools (PuddingBrowser.AgentTools)
  → IBrowserRuntime → RemoteBrowserRuntime (Host/BrowserBridge/)
    → WebSocket → DesktopBrowserBridgeClient (Desktop/Browser/)
      → WebView2 (PuddingBrowser.WebView2)
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
| `Tests/PuddingDesktop.Tests/` | Browser Controller/Client（102/102 ✅） |
| `Tests/PuddingHost.Tests/` | Bridge Endpoint/Remote proxy（56/56 ✅） |
| `Tests/PuddingBrowser.AgentTools.Tests/` | 七项 Agent Tools（10/10 ✅） |

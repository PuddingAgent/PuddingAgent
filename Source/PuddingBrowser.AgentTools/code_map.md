# PuddingBrowser.AgentTools CodeMAP

> 七项 Browser Agent Tools | Phase 2A-2/3

## 工具（7 项）

| 文件 | 工具 | 用途 |
|------|------|------|
| `BrowserContextTool.cs` | `browser_context` | 获取浏览器上下文 |
| `BrowserTabsTool.cs` | `browser_tabs` | 标签页管理 |
| `BrowserNavigateTool.cs` | `browser_navigate` | 页面导航 |
| `BrowserSnapshotTool.cs` | `browser_snapshot` | 页面快照 / 无障碍树 |
| `BrowserLocateTool.cs` | `browser_locate` | 元素定位 |
| `BrowserInteractTool.cs` | `browser_interact` | 元素交互（点击/输入） |
| `BrowserWaitForTool.cs` | `browser_wait_for` | 等待条件满足 |

## 基础与契约

| 文件 | 用途 |
|------|------|
| `BrowserAgentToolBase.cs` | 工具基类 |
| `BrowserAgentToolIds.cs` | 工具 ID 常量 |
| `BrowserToolContracts.cs` | 工具输入/输出契约 |
| `BrowserLocatorInput.cs` | 定位器输入模型 |
| `BrowserToolRuntimeResolver.cs` | 运行时解析器 |

## 调用链

```
Agent Loop → search_tools → Browser*Tool
  → BrowserToolRuntimeResolver → IBrowserRuntime
```

## 测试

`../Tests/PuddingBrowser.AgentTools.Tests/` — 10/10 ✅

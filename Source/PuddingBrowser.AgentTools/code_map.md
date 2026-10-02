---
title: 工具（7 项）
author: hyfree
date: 2026-08-07
last_reviewed: 2026-08-07
status: active
description: "﻿# PuddingBrowser.AgentTools CodeMAP"
categories: [docs]
tags: [code, map]
related_docs: []
related_files: []
slug: code-map
draft: false
---

﻿# PuddingBrowser.AgentTools CodeMAP

> 七项 Browser Agent Tools | Phase 2A-2/3

## 工具（7 项）

| 文件 | 工具 | 用途 |
|------|------|------|
| `BrowserContextTool.cs` | `browser_context` | 创建/列出/查看/关闭浏览器上下文（create/close 走上下文管理窄端口） |
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

| `BrowserCapabilityFailure.cs` | **唯一一张**能力错误 → 工具错误码映射表（每工具各写一份必然漂移）；版本不符的语义按工具给出（定位=引用过期、快照/页状态=页面已变） |

## 调用链

```
Agent Loop → search_tools → Browser*Tool（七项**全部**已迁移）
  → IDesktopBrowserCapabilitySurface（窄端口）+ IDesktopCapabilityCallContextFactory
    上下文管理（browser_context 的 create/close）另走 IDesktopContextCapabilitySurface
  → 组合根路由：能力通道会话 / Bridge 实现（二选一，不回退）
```

> 七项工具均已脱离进程内 `IBrowserRuntime`；迁移完成后旧的静态解析器（`BrowserToolRuntimeResolver`）已删除。

## 测试

`../Tests/PuddingBrowser.AgentTools.Tests/` — 10/10 ✅

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
| `BrowserSnapshotTool.cs` | `browser_snapshot` | 页面快照；`mode` 默认 **outline**（只请求无障碍树，DOM 树不采集；输出按 50 行/4096 字节收敛，超预算给 `nextCursor`）；`interactive` 只列可交互节点；`full` 排障用（显式开启，沿用调用方预算）；`text`（正文 markdown）**未实现**，返回明确 unsupported 而非静默降级；截断原因区分 `incomplete_scan` 与 `output_budget` |
| `BrowserLocateTool.cs` | `browser_locate` | 元素定位；`include_value=true` 时附带表单当前值（`value`/`selectedValues`）——**默认不带**以守住输出预算，需要确认「fill 是否写入成功」时显式打开（唯一可靠回执，`text` 不含表单值） |
| `BrowserInteractTool.cs` | `browser_interact` | 元素交互（点击/输入）；`expect_value` 声明期望后**一次调用即可确认写入成功**——输出带 `ok`/`completion`/`execution`/`verification`/`assertions`/`mayHaveSideEffects`/`safeToRetry`/`retryDisposition`（判定在 `PuddingBrowser.Automation`，两条传输天然同形）；表单值只以 matched+长度回传。**失败也带回执**（放进失败的 `value`）：「是否已经派发」不能因为失败就丢失，`blocked`（接管/暂停/授权）与 `failed` 分开 |
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

调用链：`Agent Loop → search_tools → Browser*Tool`（七项**全部**已迁移）→ `IDesktopBrowserCapabilitySurface`（窄端口）+ `IDesktopCapabilityCallContextFactory`；
上下文管理（`browser_context` 的 create/close）另走 `IDesktopContextCapabilitySurface`；组合根路由二选一（能力通道会话 / Bridge 实现），**不回退**。

> 七项工具均已脱离进程内 `IBrowserRuntime`；迁移完成后旧的静态解析器（`BrowserToolRuntimeResolver`）已删除。

## 测试

`../Tests/PuddingBrowser.AgentTools.Tests/` — 10/10 ✅

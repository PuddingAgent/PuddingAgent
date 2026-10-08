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
| `BrowserSnapshotTool.cs` | `browser_snapshot` | 页面快照；`mode` 默认 **outline**（只请求无障碍树，DOM 树不采集；输出按 50 行/4096 字节收敛，超预算给 `nextCursor`）；`interactive` 只列可交互节点；`full` 排障用（**显式开启**，沿用调用方预算；**同次默认只返回一种表示**＝无障碍树，要多于一种必须显式勾选 `include_*` 或用 `fields` 指定；一个表示都没选 ⇒ 参数错）；`text`（正文 markdown，§6.1）：**只抓语义块**（DOM/无障碍树都不抓）后由 `ArticleMarkdown` 本地组装，输出 `representation=markdown` 与 `content`，超预算按**块边界**分页给 `nextCursor`（**不透明**且绑定产生它的**表示与页面/版本**：`md:3@p1a2b3c4d-v7`；跨表示、跨页面或旧版本的误用都明确拒绝，而不是静默跳段/接不上），丢弃块以告警如实报出；截断原因区分 `incomplete_scan` 与 `output_budget`。`fields`（输出字段白名单，§151）：核心 `mode/truncated/truncationReason/nextCursor` 恒在，可裁剪三种表示与预算事实——**指定表示时只向驱动请求那一种**（三种一起抓再丢掉是纯浪费），未知字段名明确拒绝。`scope`（locator/ref）：只读该元素子树；ref 直接用（不多一跳），其它定位方式先解析成**唯一** ref——0 命中/多命中都明确拒绝（"大概就是这个"是错位读取的常见来源）。`since_revision` + 输出 `revision`/`changed`（§145）：绑定版本+查询+内容哈希，不可比时 `refresh_required`（**基线缺失不伪造变化**）；没给 `since_revision` 时 `changed` 为 null（不比较就不声称"没变"） |
| `BrowserLocateTool.cs` | `browser_locate` | 元素定位。**输出按设计 §6.2 收敛**：`limit` 默认 10（最多 100）、`text` 与 `name` 相同时省略、bbox 展示最多一位小数（驱动内部保留精度）、`checked` 与 `value`/`selectedValues` 都要**显式请求**（`include_value`/`include_checked`，默认不带以守住预算，需要确认「fill 是否写入成功」时显式打开——唯一可靠回执，`text` 不含表单值）。**失败也带最小修复提示**（`warnings`）：只读、无执行回执，但"真的没命中"与"命中了但被筛掉"的下一步完全不同。`cursor`（原样回传宿主签发的 `nextCursor`，取完为 null）——过期时明确 `cursor_expired` + 「重新读取」提示，而不是把旧游标套在新内容上。`fields`（字段白名单）：**只回要的字段**（核心 ref/tag/visible/enabled 恒在），给了白名单就是权威（列了 value 即要值），未知字段名明确拒绝并回合法清单 |
| `BrowserInteractTool.cs` | `browser_interact` | 元素交互（点击/输入）；`expect_value` 声明期望后**一次调用即可确认写入成功**——输出带 `ok`/`completion`/`execution`/`verification`/`assertions`/`mayHaveSideEffects`/`safeToRetry`/`retryDisposition`（判定在 `PuddingBrowser.Automation`，两条传输天然同形）；表单值只以 matched+长度回传。`input_mode`（`dom` 默认 / `native`）：**native 走 CDP 真实键鼠**（覆盖 click/fill/type/press/check/hover/scroll；滚动只覆盖按位移滚动，到达类目标 to_top/to_bottom/to_ref 明确拒绝），其余动作**明确 unsupported**、绝不静默降级成 DOM；输出 `inputBackend` 报告**实际**用的路径（§7，因为没有回落，成功即代表用的就是这条路径；native 不做动作后回读，回执通常是 `dispatched`）。`scroll_target`（`page/element/to_ref/to_top/to_bottom`，省略时按 locator/位移推导）：输出 `scroll` 事实（容器/前后位置/消耗位移/`atBoundary`/`contentChanged`/`notScrolled`）——**"滚到边界"与"没加载出新内容"都不是失败**，只如实报告（§7）。**失败也带回执**（能力失败与**驱动异常**两条路径都带，放进失败的 `value`）：「是否已经派发」不能因为失败就丢失，`blocked`（接管/暂停/授权）与 `failed` 分开，且不可重试时附最小修复提示 |
| `BrowserWaitForTool.cs` | `browser_wait_for` | 等待条件满足 |

## 基础与契约

| 文件 | 用途 |
|------|------|
| `BrowserAgentToolBase.cs` | 工具基类 |
| `BrowserAgentToolIds.cs` | 工具 ID 常量 |
| `BrowserToolContracts.cs` | 工具输入/输出契约；失败载荷也支持 `value`（失败必须能带结构化证据，不能只有 code/message）；**元素展示投影的唯一实现**（`Element(...)`：text==name 省略、bbox 一位小数、checked/value 显式请求）——两个工具共用，规则不会各写一份 |
| `BrowserOperationFailure.cs` | 驱动异常（`BrowserOperationException`）→ 失败回执：异常码映射成领域错误 + 是否可重试 + **最小修复提示**。§6.2 的动作拒绝分别给建议：`browser_locator_ambiguous` ⇒ 不可重试 + 「用 nth 或更具体的定位」（**绝不静默选第一个**）；`browser_element_not_visible`/`browser_element_disabled`/`browser_element_not_in_viewport`/**`browser_element_obstructed`** ⇒ 不可重试 + 滚入可见/等待可用/调整滚动/撤掉浮层（最后一条=命中测试发现被别的节点挡住，**绝不"照点不误"当成成功**）；`browser_element_not_found` ⇒ **可重试** + 等待或重新观察（零命中不等于"定位方式全坏"）；**`browser_locator_matches_filtered`** ⇒ 不可重试 + 「命中了但被隐藏/视口筛掉：显式传 includeHidden/inViewportOnly 或滚动容器」（把"这次筛选没用对"与"这种定位方式坏了"分开）；`stale_element_reference` ⇒ 不可重试 + 重新观察再定位；未登记异常码保守按 `internal_error`（可能已产生副作用） |
| `BrowserLocatorInput.cs` | 定位器输入模型。⚠️ **嵌套字段必须用 camelCase**（`includeHidden`，不是 `include_hidden`）：工具框架的 snake_case 归一化只作用于**顶层参数**（`PuddingToolContracts.DeserializeArgs` 只重写根对象的属性名） |

| `BrowserCapabilityFailure.cs` | **唯一一张**能力错误 → 工具错误码映射表（每工具各写一份必然漂移）；版本不符的语义按工具给出（定位=引用过期、快照/页状态=页面已变） |

## 调用链

调用链：`Agent Loop → search_tools → Browser*Tool`（七项**全部**已迁移）→ `IDesktopBrowserCapabilitySurface`（窄端口）+ `IDesktopCapabilityCallContextFactory`；
上下文管理（`browser_context` 的 create/close）另走 `IDesktopContextCapabilitySurface`；组合根路由二选一（能力通道会话 / Bridge 实现），**不回退**。

> 七项工具均已脱离进程内 `IBrowserRuntime`；迁移完成后旧的静态解析器（`BrowserToolRuntimeResolver`）已删除。

## 测试

`../Tests/PuddingBrowser.AgentTools.Tests/` — 10/10 ✅

---
title: 浏览器自动化外部 smoke 运行手册（S-03/S-05/S-06/S-07/S-08）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "把外部 smoke 清单里依赖 TestSite fixture 的观察项细化到照着做就能跑：起站命令与固定端口、每个观察项的 fixture 路由、要发的工具调用、期望数字（10 条结果、≤1536 字节、边界 no-op、等新条目等）、以及把结果写回 facts JSON 的方式。手册里的路由与期望数字由测试钉住（TestSiteFixtureTests + BrowserSmokePlanTests），不会与代码漂移。"
status: active
categories: [docs, runbooks]
tags: [browser, smoke, runbook, acceptance, g5, g6, g8]
related_docs: [Docs/14_reports/2026-10-09-浏览器自动化外部smoke清单.md, Docs/14_reports/2026-10-09-浏览器自动化交付验收索引.md, Docs/00_changelog/2026Year/10/2026-10-08-TestSite验收fixture.md]
related_files: [Tests/PuddingBrowser.TestSite/Program.cs, Tests/PuddingBrowser.TestSite/fixtures/serp-zh.html, Tests/PuddingBrowser.TestSite/fixtures/article.html, Tests/PuddingBrowser.TestSite/fixtures/scroll.html, Source/Pudding.DesktopService/BrowserSmokePlan.cs]
slug: runbooks-browser-external-smoke
draft: false
---

# 浏览器自动化外部 smoke 运行手册

本手册只讲**怎么做**（清单讲"看到什么才算过"）。执行者是**外部控制器**：
进程内的 Agent 证明不了这些结论（见 `Agents.md`「验证边界」）。

## 0. 准备

```powershell
# ① 起 TestSite（固定端口，便于外部控制器反复复现）
& dotnet run --project Tests/PuddingBrowser.TestSite/PuddingBrowser.TestSite.csproj `
    --urls http://127.0.0.1:5199
# 期望：/health 返回 {"status":"ok"}
```

| fixture 路由 | 用途 | 期望（结构） |
|---|---|---|
| `/fixtures/serp-zh` | S-05 中文 SERP | **10 条**结果；标题等长（20 汉字） |
| `/fixtures/serp-en` | S-05 英文 SERP | **10 条**结果；标题等长（43 ASCII） |
| `/fixtures/article` | S-06 结构化长文 | h1/h2、列表、表格、代码、链接、最后一段 |
| `/fixtures/scroll` | S-08 滚动 | 3000px 文档、`overflow-y: scroll` 嵌套容器、无限列表（终点 10 条） |

```powershell
# ② 确认 Desktop + 能力通道已就绪（工具调用全部经由它）
#    不在此处回显 Token；凭据由主机侧注入。
```

## 1. S-05 阅读预算（G5）

**步骤**（对两种语言各做一次）：

1. `browser_navigate` → `http://127.0.0.1:5199/fixtures/serp-zh`（英文用 `serp-en`）；
2. `browser_snapshot` → `mode=text`、`format=markdown`（或 `compact_json`）、`max_bytes=1536`、**不传** `fields`；
3. 记下：`content` 是否含**前 10 条标题**、`bytes` 字段值、`truncated`、`nextCursor`。

**期望**：一次调用即拿到前 10 条；`bytes ≤ 1536`；**不需要**额外 `browser_locate`。
**若超预算**：必须声明分页/截断（`truncated=true` + `nextCursor`），不得静默丢内容。

> ✅ **两种表示都应一次给全（round-86 实测）**：1536 字节预算下，markdown 与 `compact_json`
> 都能装下 10 条中文标题（compact_json 实测 **993 字节**）——前提是 compact_json **不对非 ASCII 转义**
> （`\uXXXX` 会让每个汉字 6 字节、把预算翻倍；这一条已在 round-86 修掉）。
> 若将来看到 compact_json 分页，先怀疑"转义策略回退了"（`ArticleCompactJsonFormatTests` 会拦住）。

> 中英标题长度不同（20 汉字 / 43 ASCII），**字节数不可直接比较**，分别观察。

## 2. S-06 正文与分页（G6）

1. `browser_navigate` → `/fixtures/article`；
2. `browser_snapshot` → `mode=text`、`format=markdown`、`max_bytes=1024`（**故意小**，逼出分页）；
3. 用返回的 `nextCursor` 连续取下一页，直到 `nextCursor` 为空；
4. 把各页拼起来，核对结构：h1/h2 标题、列表（≥3 项）、表格两行、代码块、链接、最后一段**各出现一次**。

**期望**：聚合覆盖 **100%**、不重不漏；每页都是**合法输出**（不是半截语法）；
用过期游标（先导航到别的页再回传旧 cursor）必须得到 `cursor_expired`，而不是把旧游标套在新内容上。

> 全文模式（`mode=full`）必须**显式**选择；不选不该返回全量。

## 3. S-08 滚动（G8）

### 3.1 文档级滚动（非边界 vs 边界）

1. `browser_navigate` → `/fixtures/scroll`；
2. `browser_interact` → `action=scroll`、`input_mode=native`、`scroll_target=page`、`delta_y=800`；
3. 再滚一次同样的量，直到不动；
4. 记下每次回执的 `scroll.beforeY/afterY/consumedDeltaY/atBoundary`。

**期望**：非边界有**真实位移**（`afterY > beforeY`）；到边界后 `atBoundary=true`；
**零位移不得报告成滚动成功**（`consumedDeltaY=0` 且 `atBoundary=false` 是矛盾，必须记为失败）。

### 3.2 嵌套容器

1. `browser_locate` → `#inner` 拿 ref；
2. `browser_interact` → `action=scroll`、`input_mode=native`、`scroll_target=element`、`locator=<ref>`、`delta_y=200`；
3. 期望：**内层** `scrollTop` 变化、外层窗口 `scrollY` 基本不变（若外层也动了，说明落点选错）。

### 3.3 到达滚动与停止原因

1. `browser_interact` → `action=scroll`、`input_mode=native`、`scroll_target=to_bottom`；
2. 期望：回执 `scroll.stopReason` 出现（例如 `already at the bottom` / `the scroll position stopped changing`），
   且**位置确实变了**（除非本来就在底部）；
3. 同样做一次 `to_top`；再用 `#inner` 的 ref 做 `to_ref`（期望元素进入视口后停止）。

### 3.4 无限滚动等待（等新条目）

1. 先 `browser_snapshot` 记下 `nodeCount` → 记为 **基线**；
2. `browser_interact` → `action=scroll`、`delta_y=2000`（触发 `/fixtures/scroll` 的追加逻辑）；
3. `browser_wait_for` → `content_grows_above_nodes=<基线>`；
4. 记下：等待是否**成功**（不是超时）、之后快照的 `nodeCount` 是否 **> 基线**。

**期望**："滚到底但没加载出新内容"**不是失败**（如实报告 `contentChanged=false`）；
追加逻辑有确定终点（最多 10 条），因此"一直等不到"必须表现为**超时**而不是死循环。

## 4. 其它不依赖 fixture 的项（简表）

| 项 | 怎么做 | 期望 |
|---|---|---|
| S-03 回执 | `browser_snapshot` 找页面里的按钮，`browser_interact action=click` 一个**不导航**的元素（如 `/fixtures/scroll` 的 `#sentinel`） | 不因"版本未推进"判失败；回执 `execution`/`verification` 与事实一致 |
| S-07 定位与输入 | `browser_locate` 用十种 locator 组合（含 `exact`/重复/隐藏）；对受控输入 `fill` 后**不重新快照**直接看回执 | 受控值一致；密码字段不回显；0×0/禁用/遮挡被拒；一次 fill 回执足以确认非敏感值 |

## 5. 记录与判定

把每个观察项的结果写进 facts 文件（骨架由脚手架生成）：

```powershell
& .\TestScripts\external-acceptance.ps1 -Manifest "temp/test-out/delivery-manifest.json"
# 手工编辑 temp/test-out/external-facts.json：
#   smoke[]   : { checkId, outcome: passed|failed|not-run, evidence: "<你看到的事实>" }
#   lifecycle[]: { step, passed, evidence }
& .\TestScripts\external-acceptance.ps1 -SkipTemplate
# 退出码 0 = 允许 external-lifecycle-verified；2 = 拒绝（逐条打印原因）
```

**只有 `passed` + 非空 evidence 才算通过**；`not-run` **不等于**通过（这是脚本与判定组件的硬规则）。

## 关联

- 清单：[外部 smoke 清单](../14_reports/2026-10-09-浏览器自动化外部smoke清单.md)（"看到什么才算过"）
- 索引：[交付验收索引](../14_reports/2026-10-09-浏览器自动化交付验收索引.md)（本会话总体状态）
- fixture：[TestSite 验收 fixture](../00_changelog/2026Year/10/2026-10-08-TestSite验收fixture.md)

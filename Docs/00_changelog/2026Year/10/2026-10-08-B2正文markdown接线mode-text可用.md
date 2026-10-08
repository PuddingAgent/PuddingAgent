---
title: 2026-10-08 B2 正文 markdown 接线：mode=text 真正可用
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "把上一轮的正文组装器接成可用功能：语义块从驱动经两种传输一路带到工具，mode=text 不再返回 unsupported，而是只抓语义块后本地组装 markdown，输出 representation=markdown / content 与块边界分页游标；不可比与丢弃块都如实标注。顺带修正 §161 的 representation 字段。"
status: archived
categories: [docs, changelog]
tags: [browser, reading, markdown, text-mode, b2]
related_docs: [Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md, Docs/00_changelog/2026Year/10/2026-10-08-B2正文markdown组装与提取脚本.md]
related_files: [Source/PuddingBrowser.AgentTools/BrowserSnapshotTool.cs, Source/PuddingBrowser.Protocol/BrowserBridgeCommandPayloads.cs, Source/Pudding.DesktopSurface.Browser/BrowserRuntimeDesktopSurface.cs]
slug: changelog-b2-text-mode-wired
draft: false
---

# 2026-10-08 B2 正文 markdown 接线：mode=text 真正可用

## 目标 / 背景

上一轮（见 [B2 正文 markdown 组装与提取脚本](2026-10-08-B2正文markdown组装与提取脚本.md)）按组件交付顺序
先做完了两块基础件并**如实登记**「还没接线：`mode=text` 仍返回 unsupported」。本轮接线，让 §6.1 的
`text` 模式成为可用能力。

## 改动

| 层 | 文件 | 改动 |
|---|---|---|
| 契约 | `DesktopSnapshotOptions.IncludeContentBlocks`（并纳入 `HasContent`，否则"只要正文"会被当成无内容调用而拒绝）· `DesktopSnapshot.ContentBlocks` | |
| 运行时 | `SnapshotOptions.IncludeContentBlocks` · `PageSnapshot.ContentBlocks` · 驱动 payload/结果 | 驱动侧契约 |
| 能力通道 | `BrowserRuntimeDesktopSurface` | 传下去、带回来（含 `HasContent` 的语义修正） |
| 桥接 | `PageSnapshotArguments.IncludeContentBlocks` · `BrowserSnapshotDescriptor.ContentBlocks` · `RemoteBrowserPage` · `BrowserWorkspaceController` | 两条传输同形（内容仍由宿主提取） |
| 工具 | `BrowserSnapshotTool` | **删除** `mode=text` 的 unsupported 分支：改为**只请求语义块**（DOM/无障碍树都不抓）→ `ArticleMarkdown.Compose` 本地组装 → 输出 `representation=markdown`、`content`、`nextCursor`、`bytes`、`returnedLines`；丢弃块以 `snapshot_blocks_skipped:N` 告警如实报出 |

顺带把 §161 要求的 `representation` 字段落到输出上（正文是 `markdown`，其余模式是各自文本表示），
这是「同次默认只返回一种表示」的可见前提。

## 验证

| 套件 | 结果 |
|---|---|
| `PuddingBrowser.AgentTools.Tests` | **55 / 55**（新增 2 条正文用例，删除 1 条"未实现"用例） |
| `PuddingHost.Tests` · `PuddingDesktop.Tests` · `DesktopSurface.BrowserTests` · `DesktopServiceTests` · `AutomationTests` · `ContractsTests` | 258/258 · 318/318 · 63/63 · 229/229 · 207/207 · 152/152 |

新增门禁：

- `Snapshot_TextMode_ComposesArticleMarkdown`：`mode=text` ⇒ `representation=markdown`，`content` 含
  标题/段落/列表/表格；**且请求里只有 `includeContentBlocks`**（`includeDom`/`includeAccessibilityTree`/
  `includeHtml` 全为 false）——"只抓语义块"是可验证的事实，不只是输出层裁剪；
- `Snapshot_TextMode_PagesByByteBudgetAndReportsTheCursor`：`max_bytes=80` ⇒ 截断 + 游标；
  续读拼接后 6 段**每段恰好出现一次**（G6 的覆盖 100% 在工具层再钉一遍）。

**取红实测**：把 Text 分支的 `includeContentBlocks` 改成 `false`（模拟"没真去抓正文"）⇒
`Snapshot_TextMode_ComposesArticleMarkdown` **恰好变红**；复原后 55/55 复绿。

## 已知边界（诚实登记）

- **正文选择仍是启发式**（`article`/`main`/`[role=main]`/普通块回退，无语义可读性评分）：
  可能混入少量非正文，但**不会丢掉**正文；真机提取效果需外部 smoke 复核（本环境跑不了 WebView2）。
- **不做块内切分**：单块超预算时整块给出并标注 `output_budget`（游标仍指向下一块）。
- **`cursor` 仍是"第几块"**：`limit`（行数）对正文模式不生效（正文按块与字节收敛）。
- **`format`（compact_json）仍未实现**：`representation` 目前只有文本形态与 `markdown`。
- 图片（`img`）暂不产出 `![alt](src)`；正文模式忽略 `fields` 白名单（它的输出字段是 `content` 等）。

## 影响与回滚

- **行为变化（Agent 可见）**：`browser_snapshot` 的 `mode=text` 从"明确 unsupported"变成
  **可用的正文 markdown**（每页默认 4096 字节、块边界分页续读）；输出新增 `representation`/`content`。
- 已有模式的输出不变（`representation` 是新增字段）。
- 回滚：还原上表 6 个生产文件与 2 个测试文件及 code_map。

## 关联

- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../../../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§6.1、G6、§161）
- 前序：[B2 正文 markdown 组装与提取脚本](2026-10-08-B2正文markdown组装与提取脚本.md)（本轮接线）、
  [B2 快照修订标识与 refresh_required](2026-10-08-B2快照修订标识与refresh_required.md)

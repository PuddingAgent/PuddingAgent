---
title: 2026-10-08 B2 format=compact_json：正文的第二种表示（补全 §151 的 mode+format）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "补上 §151 里一直空着的 format：正文除 markdown 外可要求 compact_json（给程序读的紧凑块结构），两种表示共用同一份块解析与分页决策以保证页边界与游标一致，且截断后仍是合法 JSON。未登记的 format 与用在非正文模式上的 format 都 fail closed。"
status: archived
categories: [docs, changelog]
tags: [browser, reading, format, compact-json, b2]
related_docs: [Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md, Docs/00_changelog/2026Year/10/2026-10-08-B2快照默认只回一种表示.md]
related_files: [Source/PuddingBrowser.Automation/ArticleCompactJson.cs, Source/PuddingBrowser.AgentTools/BrowserSnapshotTool.cs]
slug: changelog-b2-format-compact-json
draft: false
---

# 2026-10-08 B2 format=compact_json：正文的第二种表示（补全 §151 的 mode+format）

## 目标 / 背景

设计 §151：「`mode` 决定读取内容，`format` 决定 markdown/compact_json；**同次默认只返回一种表示**。」
第 33 轮把 `representation` 字段铺好了，第 34 轮收口了"默认只一种"，但 **`format` 本身一直没实现** ——
`text` 模式只有 markdown 一种形状，程序化调用方（要块类型/层级/表格行列而不是渲染文本）没有选择。

## 改动

| 层 | 文件 | 改动 |
|---|---|---|
| Automation | `ArticleCompactJson.cs`（新增） | 正文的紧凑 JSON 表示：`{blocks:[{k,t,d,h,r}],truncated,nextCursor}`。**与 markdown 共用同一份块解析与同一套分页决策**（`ArticleMarkdown.Parse` + 同一预算/游标），因此两种表示**页边界一致、游标可互换续读**；`d` 只对 `li` 写（少一个字段就少一份歧义）。硬性要求：**截断后仍是合法 JSON** —— 先选块、再一次性序列化，括号永远成对（边写边截会产出半截 JSON，调用方根本无法解析） |
| Automation | `ArticleMarkdown.cs` | 暴露 `Parse`（两种表示共用；分页决策只能有一份，否则续读会在表示之间错位） |
| 工具 | `BrowserSnapshotTool.cs` | 新增 `format` 参数：`markdown`（默认）/`compact_json`；**未登记线名与"用在非正文模式上"都 fail closed**（outline/interactive 只有一种文本表示，full 回的是原始表示而不是格式，不假装支持） |

## 验证

| 套件 | 结果 |
|---|---|
| `PuddingBrowser.AutomationTests` | **234 / 234**（原 227 ＋ 7） |
| `PuddingBrowser.AgentTools.Tests` | **60 / 60**（原 58 ＋ 2） |
| `PuddingDesktop.Tests` · `DesktopSurface.BrowserTests` · `ContractsTests` · `DesktopServiceTests` · `Host` | 320/320 · 64/64 · 152/152 · 229/229 · 258/258 |

新增门禁：

- 块保真：`k`/`t`/`d`（仅 li）/`h`/`r`（表格行列）都在，`truncated=false`、`nextCursor=null`；
- **截断后仍可解析**：预算不足时输出仍是合法 JSON，且 `truncated=true`、`nextCursor>0`；
- **页边界与 markdown 一致**：同一预算下 `ReturnedLines` 与 `NextCursor` 相同；续读拼接后每段**恰好出现一次**；
- 字节口径是 UTF-8（中文 3 字节）；畸形块计数不被静默丢弃；空输入不产出"假对象"；
- 工具层：`mode=text&format=compact_json` ⇒ `representation=compact_json` 且 `content` 可被 `JsonDocument.Parse`；
  `format=yaml` ⇒ `browser_invalid_arguments`；`mode=outline&format=compact_json` ⇒ `browser_invalid_arguments`（消息点明 `mode=text`）。

**取红实测**：把 compact_json 的 `nextCursor` 恒写 null（＝截断了却不给续读入口）⇒
`TruncatedOutputIsStillValidJson_AndSaysSoExplicitly` **恰好变红**；复原后 234/234 复绿。

## 已知边界（诚实登记）

- **`format` 只对 `text` 模式生效**：outline/interactive 目前只有一种文本表示，full 回的是原始表示
  （dom/a11y/html）而不是"格式"——两者都**明确拒绝**而不是假装支持。若将来要给列表型输出加 compact_json，
  需要先定义它的块模型（不能直接套正文块）。
- **compact_json 的字节预算与 markdown 同额（默认 4096）但实际字节数不同**：JSON 有结构/转义开销，
  因此同一预算下可容纳的块数可能更少——这是"先按 markdown 决策分页、再序列化"的直接结果。
  若要精确按 JSON 字节分页，需要把分页决策参数化成"成本函数"，属于后续收敛（已在 code_map 记录当前行为）。
- **`ArticleCompactJson` 的行数语义**是"返回的块数"（不是文本行数），与 markdown 的"块数"一致但字段名同为
  `returnedLines`；工具文档需要读作"返回的块/段数"。
- 真机无关（纯组装层），不涉及 WebView2；正文提取本身的正确性仍属"真机未验证"。

## 影响与回滚

- **行为变化（Agent 可见）**：`browser_snapshot` 新增 `format` 参数；`mode=text` 默认仍是 markdown
  （既有调用方不受影响），显式传 `compact_json` 时 `content` 变成紧凑 JSON、`representation=compact_json`。
- 回滚：还原上表 3 个生产文件与 2 个测试文件、frozen 列表与 code_map。

## 关联

- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../../../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§151、§6.1）
- 前序：[B2 快照默认只回一种表示](2026-10-08-B2快照默认只回一种表示.md)（`representation` 与默认表示）、
  [B2 正文 markdown 接线](2026-10-08-B2正文markdown接线mode-text可用.md)（`mode=text` 可用）

---
title: 2026-10-08 修掉 G5 的表示差异：compact_json 不再转义非 ASCII，中文 SERP 从"装不下"变 993 字节
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "round-85 的实测发现 compact_json 因把非 ASCII 转义成 \\uXXXX（每汉字 6 字节）而装不下 1536 预算里的 10 条中文标题。本轮修掉：ArticleCompactJson 的 JSON 写入改用 UnsafeRelaxedJsonEscaping（JSON 允许直接输出 UTF-8；组件输出的是纯数据、不拼 HTML），实测从 >1536 降到 **993 字节**，于是 G5 的两个数字在 markdown 与 compact_json 上都成立。测试相应升级为'一次给全 10 条'的硬断言，运行手册也改回'两种表示都应一次给全'。"
status: archived
categories: [docs, changelog]
tags: [browser, reading, budget, g5, compact-json, fix]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-08-G5预算实测.md, Docs/13_runbooks/浏览器自动化外部smoke-runbook.md, Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md]
related_files: [Source/PuddingBrowser.Automation/ArticleCompactJson.cs, Source/PuddingBrowser.AutomationTests/SerpBudgetFixtureTests.cs]
slug: changelog-compact-json-no-ascii-escape
draft: false
---

# 2026-10-08 修掉 G5 的表示差异

## 目标 / 背景

上一轮实测发现：同一批块（2 块/结果，21 块）、同一 1536 字节预算下，
**markdown 能一次给全 10 条中文标题，`compact_json` 装不下**（21 块只装 19 块）。
根因是 `compact_json` 把非 ASCII 转义成 `\uXXXX`：**每个汉字 6 字节**，是原文 3 字节的两倍。

## 改动

`Source/PuddingBrowser.Automation/ArticleCompactJson.cs`：

- 新增共享的 `CompactWriterOptions`，把 `Encoder` 设为 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`；
- 代码注释写明**为什么**：JSON 允许直接输出 UTF-8；转义只是可选的保守做法，代价是预算翻倍；
  名字里的 "Unsafe" 针对的是"把 JSON 嵌进 HTML/JS"的场景，而本组件输出的是**纯 JSON 数据**。
- 两处 `new JsonWriterOptions { Indented = false }` 统一改用该选项（避免以后再有人只改一处）。

## 验证

| 项 | 结果 |
|---|---|
| **实测** | 中文 SERP、`compact_json`、1536 预算：**993 字节**、`truncated=false`、无游标、10 条标题全在（此前 >1536 且只装 19 块） |
| `PuddingBrowser.AutomationTests` | **315 / 315**（把"应当分页"的宽松断言升级为"**一次给全 10 条**"的硬断言） |
| 其它套件 | DesktopService 310 · AgentTools 70 · Contracts 153 · DesktopSurface.Browser 67 · PuddingDesktop 320 · Host 258（全绿） |
| 运行手册 | 改回"两种表示都应一次给全"，并写明 993 字节与"看到分页先怀疑转义策略回退" |

**取红能力**：`TheChineseSerpFitsTheBudgetOrPagesExplicitly` 现在硬断言"未截断" ⇒ 若转义策略回退或块变重，
立刻变红（本轮它正是先红后修：我特意用它来"捕捉改进发生"）。

## 已知边界（诚实登记）

- **只验证了格式与预算**：993 字节是**在"2 块/结果"的替身抽块粒度**下测的；真机驱动的真实块粒度仍未知
  （若驱动把标题+链接合成一块，会更省；若附带更多字段，可能又超）。
- **不改变语义**：输出仍是合法 JSON，字段名/结构与之前一致；差异只在非 ASCII 是否转义
  （消费者是 JSON 解析器，不依赖转义形式）。
- **未改设计文档**：G5 的 1536 未变；本轮只是让两种表示都能满足它。
- **未真机验证**：整条链路仍未在 WebView2 上跑过。

## 影响与回滚

- **对外可见变化**：`compact_json` 输出里中文由 `\uXXXX` 变为原样 UTF-8（字节数更小、人可读）。
  任何依赖"转义形式"的消费者会受影响（本仓库的消费者是 JSON 解析器与我的测试，均已适配）。
- 回滚：把 `CompactWriterOptions.Encoder` 去掉（即可回到转义形式，但会重新装不下 10 条）。

## 关联

- 前序：[G5 预算实测](2026-10-08-G5预算实测.md)（本轮修的就是它发现的问题）
- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§6.1、门禁 G5）
- 手册：[外部 smoke 运行手册](../../13_runbooks/浏览器自动化外部smoke-runbook.md)

---
title: 2026-10-08 横向：英文 SERP fixture（S-05 要求中英两种）+ 被 PS 写坏 BOM 的更正
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "S-05（阅读预算）要求中文与英文两种 fixture，本轮补上 serp-en.html（正好 10 条、标题固定 43 个 ASCII 字符、每条带链接）+ /fixtures/serp-en 路由 + 结构契约用例，并把英文 fixture 纳入 UTF-8 无 BOM 检查。过程中被自己的门禁抓到两处真实问题：① 我用 PowerShell 改写文件时 -Encoding UTF8 会写入 BOM（PS 5.1 行为），门禁直接报出；② 我按肉眼估计把标题长度写成 40，实际是 43。两处都已更正。"
status: archived
categories: [docs, changelog]
tags: [browser, testsite, fixture, g5, acceptance]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-08-TestSite验收fixture.md, Docs/14_reports/2026-10-09-浏览器自动化外部smoke清单.md]
related_files: [Tests/PuddingBrowser.TestSite/fixtures/serp-en.html, Source/Pudding.DesktopServiceTests/TestSiteFixtureTests.cs]
slug: changelog-testsite-english-serp
draft: false
---

# 2026-10-08 横向：英文 SERP fixture + 被门禁抓到的两处真实问题

## 目标 / 背景

S-05 明确要求"**中文与英文** SERP fixture 各一次 extract"（预算在两种文字下的成本不同）。
上一轮只做了中文版，本轮补齐英文版。

## 改动

| 文件 | 内容 |
|---|---|
| `fixtures/serp-en.html`（新增） | 正好 **10 条**结果；标题长度**固定 43 个 ASCII 字符**；每条带来源链接（`https://example.com/e1..e10`） |
| `Program.cs` | 新增 `/fixtures/serp-en` 路由（同样从文件读，缺文件即 404） |
| `TestSiteFixtureTests` | 新增英文 fixture 用例（10 条、等长、长度=43、10 个链接、两个 SERP 路由都在）；把英文 fixture 纳入 UTF-8/无 BOM/体积检查 |

## 验证

| 项 | 结果 |
|---|---|
| `Pudding.DesktopServiceTests` | **309 / 309**（＋1） |
| **起站真机验证** | `/fixtures/serp-en` ⇒ HTTP **200**、`charset=utf-8`、2374 字符、**服务端解析出 10 个标题**；随后停站 |
| 其它套件 | 未受影响（本轮只改测试站点与测试） |

## 被自己的门禁抓到的两处真实问题（值得记录）

1. **BOM**：我用 PowerShell `Set-Content -Encoding UTF8` 改写 `serp-en.html` 时，**PS 5.1 会写入 BOM**；
   `FixtureFilesAreUtf8AndReasonablySmall` 直接报出 `serp-en.html 带 BOM`。已改用
   `UTF8Encoding($false)` 重写（无 BOM）。
   —— 这条正是上一轮加该用例的目的：**fixture 的编码是验收前提**，不是小事。
2. **长度估计错误**：我按肉眼把标题长度写成 **40**，实际是 **43**
   （"Result title padded to forty ascii chars aa"）。用例报 `Expected 40 / Actual 43`，已更正断言与 fixture 注释。

## 已知边界（诚实登记）

- **fixture ≠ 真机结论**：取到 200 且能解析出 10 个标题只证明**站点提供了这些页面**；
  "阅读管线在真实 WebView2 上一次 extract ≤1536 字节"仍**未验证**（属 S-05 的外部观察）。
- **长度固定但内容不同**：中英两条 fixture 的标题长度分别固定为 20 汉字 / 43 ASCII —— 两者**不可直接比字节数**，
  验收要分别观察。
- **无第三语言**：设计只要求中英两种。
- 本轮起的是**本机临时站点**，不是产品 Shell。

## 影响与回滚

- **无产品行为变化**（测试站点 + 测试）。
- 回滚：删除 `serp-en.html`、`/fixtures/serp-en` 路由与该用例，并还原 UTF-8 检查的文件清单。

## 关联

- 报告：[外部 smoke 清单](../../14_reports/2026-10-09-浏览器自动化外部smoke清单.md)（S-05 的观察对象）
- 前序：[TestSite 验收 fixture](2026-10-08-TestSite验收fixture.md)

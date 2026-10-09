---
title: 2026-10-08 横向：补齐 TestSite 验收 fixture（G5/G6/G8 的观察对象）+ 结构契约门禁
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "外部 smoke 清单里的 S-05/S-06/S-08 需要固定的观察对象，而 TestSite 此前只有三个路由（/health、/frame、/）。本轮补上三个 fixture 页面并以文件形式提供（/fixtures/serp-zh 十条固定长度中文标题、/fixtures/article 标题/列表/表格/代码/链接齐全、/fixtures/scroll 嵌套滚动容器 + 有确定终点的无限滚动），项目文件把 fixture 复制到输出目录；并新增 5 条结构契约测试（10 条结果、标题长度固定、结构齐全、嵌套容器与终点写死、UTF-8 无 BOM），让 fixture 漂移会立刻变红。真机验证：起站后三个路由均 HTTP 200 且 contentType 带 charset=utf-8。"
status: archived
categories: [docs, changelog]
tags: [browser, testsite, fixture, acceptance, g5, g6, g8]
related_docs: [Docs/14_reports/2026-10-09-浏览器自动化外部smoke清单.md, Docs/14_reports/2026-10-09-浏览器自动化交付验收索引.md]
related_files: [Tests/PuddingBrowser.TestSite/fixtures/serp-zh.html, Tests/PuddingBrowser.TestSite/fixtures/article.html, Tests/PuddingBrowser.TestSite/fixtures/scroll.html, Source/Pudding.DesktopServiceTests/TestSiteFixtureTests.cs]
slug: changelog-testsite-acceptance-fixtures
draft: false
---

# 2026-10-08 横向：补齐 TestSite 验收 fixture（G5/G6/G8 的观察对象）

## 目标 / 背景

外部 smoke 清单（S-05 阅读预算、S-06 正文与分页、S-08 滚动）都要求"在固定 fixture 上观察"，
但 `Tests/PuddingBrowser.TestSite` 此前**只有三个路由**（`/health`、`/frame`、`/`）。没有固定观察对象，
这三条验收**无法执行** —— 这正是我此前登记的"TestSite fixture 是验收前提，未完成"。

## 改动

| 文件 | 内容 |
|---|---|
| `fixtures/serp-zh.html`（新增） | G5：**正好 10 条**结果，标题长度**固定为 20 个汉字**（"标题长度明确固定"是 1536 字节预算可复核的前提），每条带来源链接 |
| `fixtures/article.html`（新增） | G6：标题（h1/h2）/列表/表格/代码/链接齐全，且有"最后一段"作为分页边界观察点 |
| `fixtures/scroll.html`（新增） | G8：文档 3000px 占位（文档级滚动有位移）+ **嵌套可滚动容器**（`overflow-y: scroll`）+ 无限滚动列表**写死终点**（最多 10 条，另有 `window.puddingAppendBatch()` 供手工确定性触发） |
| `Program.cs` | 三个路由 `/fixtures/serp-zh`、`/fixtures/article`、`/fixtures/scroll` 从**文件**读取（找不到就 404，不内联造假） |
| `PuddingBrowser.TestSite.csproj` | `<Content Include="fixtures/**/*.html" CopyToOutputDirectory="PreserveNewest" />`（否则运行期 404） |
| `Source/Pudding.DesktopServiceTests/TestSiteFixtureTests.cs`（新增，5 条） | **结构契约门禁**：10 条结果 + 标题等长 + 10 个链接；文章 fixture 结构齐全；滚动 fixture 有嵌套容器且终点写死；站点按文件读且项目复制 fixture；fixture 均为 UTF-8 **无 BOM** 且 < 16 KiB |

**为什么要门禁**：fixture 一旦被随意改动，S-05/S-06/S-08 会**悄悄变得无法执行**（等不到结论或预算对不上）。
门禁把"fixture 漂移"变成会红的测试。

## 验证

| 项 | 结果 |
|---|---|
| `Pudding.DesktopServiceTests` | **308 / 308**（＋5） |
| **起站真机验证（本机）** | `dotnet run --project Tests/PuddingBrowser.TestSite --urls http://127.0.0.1:5199` ⇒ `/health` 返回 `{"status":"ok"}`；三个 fixture 路由**均 HTTP 200**、`contentType=text/html; charset=utf-8`、内容长度 1809 / 880 / 1573 字符；随后停站 |
| 其它套件 | Automation 309/309 · AgentTools 70/70 · Contracts 153/153 · DesktopSurface.Browser 67/67 · PuddingDesktop 320/320 · Host 258/258 |
| 构建 | `PuddingBrowser.TestSite` 已成功生成 |

**既有 flake**：`Host.Tests` 的 handshake 用例本轮**连续失败两次、第三次通过**（与本次改动无关；
它是本会话多轮记录的负载敏感用例）。

## 已知边界（诚实登记）

- **fixture ≠ 真机结论**：起站并取到 200 只证明**站点把这些页面提供出来了**；
  S-05/S-06/S-08 要求的"阅读管线在真实 WebView2 上的表现"仍然**未验证**。
- **只有中文 SERP**：S-05 提到"中文与英文 fixture"，英文版本尚未添加（登记为后续）。
- **无限滚动的触发方式**：fixture 同时支持滚动触发与 `window.puddingAppendBatch()` 手工触发；
  自动滚动触发在真机上是否按预期追加，属外部观察项。
- **结构门禁只覆盖"存在性/数量/长度"**：不校验渲染后的可见性（那需要浏览器）。
- 真机环境限制：本轮起的是**本机临时站点**，不是产品 Shell。

## 影响与回滚

- **无产品行为变化**：只影响测试站点与测试。
- 回滚：删除三个 fixture 文件、`Program.cs` 的三个路由、csproj 的 Content 项与 5 条测试。

## 关联

- 报告：[外部 smoke 清单](../../../14_reports/2026-10-09-浏览器自动化外部smoke清单.md)（S-05/S-06/S-08 的观察对象即本轮的 fixture）
- 前序：[联合演练与验收索引](2026-10-08-联合演练与验收索引.md)（本轮补上它列为"未完成"的 fixture 前提）

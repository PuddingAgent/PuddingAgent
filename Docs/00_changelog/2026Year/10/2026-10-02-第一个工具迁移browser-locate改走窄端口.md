---
title: "第一个工具迁移完成：`browser_locate` 改走窄端口（切片 D 第③步下半开始）"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 加宽清单里卡住 browser_locate 的两处缺口（Ref 早先确认可用、BoundingBox 上一轮补上）都已闭合， 本轮把它真正迁移到窄端口——这是七个工具迁移的第一个，作为其余六个的模板。
categories: [docs, changelog]
tags: [第一个工具迁, 改走窄端口]
related_docs: []
related_files: [Source/PuddingBrowser.AgentTools/BrowserLocateTool.cs, Source/PuddingBrowser.AgentTools/BrowserLocatorInput.cs, Source/PuddingBrowser.AgentTools/PuddingBrowser.AgentTools.csproj, Tests/PuddingBrowser.AgentTools.Tests/BrowserAgentToolsTests.cs, Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-第一个工具迁移browser-locate改走窄端口
draft: false
---

# 第一个工具迁移完成：`browser_locate` 改走窄端口（切片 D 第③步下半开始）

## 目标 / 背景

加宽清单里卡住 `browser_locate` 的两处缺口（`Ref` 早先确认可用、`BoundingBox` 上一轮补上）都已闭合，
本轮把它**真正迁移**到窄端口——这是七个工具迁移的第一个，作为其余六个的模板。

## 改动

| 文件 | 改动 |
|---|---|
| `Source/PuddingBrowser.AgentTools/BrowserLocateTool.cs` | 依赖由 `IBrowserRuntime` 换成 `IDesktopBrowserCapabilitySurface` + `IDesktopCapabilityCallContextFactory`；执行体改为 contexts→target→（Ref 时读一次状态拿基准版本）→`LocateAsync`；新增能力错误码 → 工具错误码映射 |
| `Source/PuddingBrowser.AgentTools/BrowserLocatorInput.cs` | 新增 `ToDesktopLocator`（契约形状），与既有 `ToLocator` 共用同一套 kind 归一化/校验 |
| `Source/PuddingBrowser.AgentTools/PuddingBrowser.AgentTools.csproj` | 新增 `Pudding.Contracts` 引用（计划本意：Contracts 平台无关，消费方直接依赖） |
| `Tests/PuddingBrowser.AgentTools.Tests/BrowserAgentToolsTests.cs` | 新增 `FakeCapabilitySurface`（把请求**委托**给既有 `FakeBrowserRuntime`，而非返回罐头值）+ `FakeCallContextFactory`；3 处构造点改用新构造参数 |

## 行为保持（逐条对照迁移前）

| 面 | 迁移前 | 迁移后 |
|---|---|---|
| 上下文省略 | 取第一个可用上下文 | 同样取第一个（`GetContextsAsync`） |
| Ref 定位的版本 | 运行时不校验版本 | 先读页面状态作基准值，再带上；引用因此有可比较的版本 |
| 陈旧引用 | 运行时抛 `stale_element_reference` | Bridge 路径：异常原样传播，工具既有 catch 映射为同一错误码；能力通道路径：`page_version_mismatch` → **同一错误码** |
| 结果字段 | `Ref/Tag/Role/Name/Text/Visible/Enabled/Checked/BoundingBox` | 同上（`BoundingBox` 由新增的契约字段提供） |
| 截断 | 只取前 100 并标注 `locator_results_truncated` | 同上（并叠加 `LocateResult.Truncated`） |
| 未连接 Desktop | 报"页面找不到" | 报 `browser_not_connected`（**更诚实**；这是有意的失败模式变化） |

## 验证

- `PuddingBrowser.AgentTools` 构建 **0 错误**；`PuddingHost` 与 `PuddingHost.Tests` 构建 **0 错误**；
- `PuddingBrowser.AgentTools.Tests` **15/15**（含陈旧引用用例仍得 `stale_element_reference`）；
- `PuddingHost.Tests` **225/225**。
- **未验证**：真实 Desktop 会话里的端到端行为（需要启用能力通道 + 外部重启窗口）。

## 重要意义（本轮起口径变化）

此前几轮我反复写「运行实例行为不变（端口尚无消费方）」。**从本轮起不再成立**：
`browser_locate` 已是端口的真实消费方，而 DI 把端口注册为 Bridge 实现（第 85 轮）⇒
该工具的执行路径**形状已变**（走窄端口），行为按上表逐条对照保持一致、并由测试覆盖。
⇒ 真正的运行时结论仍需外部重启窗口的端到端验收。

## 未完成 / 留白

- 其余六个工具迁移（模板已就位）；组合根「通道 / Bridge」开关（把现有注册扩成开关即可）；
- 其余加宽项（1/3/4/5/6/7/9）与审计剩余维度（预算/截断、错误码逐项、可空语义）；
- Tool Runtime 权限/审批链；切片 F；外部重启窗口 8 步验收；索引登记。

## 关联

`77b878a`（审计第二批）、`f373f58`（`BoundingBox`）、`f15bc0c`（DI 装配）、`1ce00a4`（上下文工厂）；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.4 / §8.5。

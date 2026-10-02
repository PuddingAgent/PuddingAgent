---
title: "第四个工具迁移：`browser_interact` 改走窄端口"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: fill / type 的空文本（= 清空输入框）在能力通道上表达不了：契约要求 text 非空， 而 proto3 的 string text 无法区分空串与未设。当前选择明确拒绝（browser_invalid_arguments）， 不静默当成清空；若要恢复该用法，需把 text 改为 optional string 或另加显式标志。 已写入 §8.5.1 第 13 项。
categories: [docs, changelog]
tags: [interact, 第四个工具迁]
related_docs: []
related_files: [Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-第四个工具迁移browser-interact
draft: false
---

# 第四个工具迁移：`browser_interact` 改走窄端口

## 改动

| 文件 | 改动 |
|---|---|
| `BrowserInteractTool.cs` | 依赖改为 `IDesktopBrowserCapabilitySurface` + `IDesktopCapabilityCallContextFactory`；动作名 → 契约动作（`check` 按 `checked` 真假映射 Check/Uncheck）；**先读页面状态拿基准版本**（契约对变更类要求固定版本）；结果按 `DesktopInteractionResult` 搬运 |
| `Tests/…/BrowserAgentToolsTests.cs` | 替身端口新增 `InteractAsync`（委托给假页面的 `ClickAsync`/`FillAsync`/…，因此 `LastAction`/`LastValue` 断言继续有效）；2 处构造点更新 |

## 行为保持与有意变化

| 面 | 迁移前 | 迁移后 |
|---|---|---|
| 形状校验 | `browser_invalid_arguments` + 原文案 | **完全相同**（locator/press 的 text/select 的 values/check 的 checked） |
| 变更版本 | 直接作用于当前页 | **先读状态拿基准版本再交互**（契约要求；也让"等待期间页面变化"可被检出 ⇒ 多一次 `page_state` 调用） |
| 受影响元素 | 结果里 `Element` **恒为 null**（当时担心"交互后重查旧 Locator"） | 由端口在**动作之前**解析并回带 ⇒ 不再违反该规则，且与能力通道路径一致 |
| 未连接 Desktop | 报"页面找不到" | `browser_not_connected`（与其它已迁移工具一致） |

## 已知收窄（已登记为缺口 #13）

`fill` / `type` 的**空文本**（= 清空输入框）在能力通道上**表达不了**：契约要求 `text` 非空，
而 proto3 的 `string text` 无法区分空串与未设。当前选择**明确拒绝**（`browser_invalid_arguments`），
**不静默当成清空**；若要恢复该用法，需把 `text` 改为 `optional string` 或另加显式标志。
已写入 §8.5.1 第 13 项。

## 验证

- `PuddingBrowser.AgentTools.Tests` **15/15**；
- `PuddingHost` / `PuddingHost.Tests` 构建 **0 错误**；`PuddingHost.Tests` **226/226**
  （含按调用序列断言的桥接集成测试）；
- **未验证**：真实 Desktop 会话端到端（需启用能力通道 + 外部重启窗口）。

## 迁移进度 4/7

已迁移：`locate`、`snapshot`、`wait_for`、`interact`。剩余三个全是**能力缺口**：

| 工具 | 缺口 |
|---|---|
| `browser_navigate` | #3 导航四动作（可复用"字符串动作 + 冻结线名"模式）、#4 导航 `TimeoutMs`、#5 导航结果三字段 |
| `browser_tabs` | #6 新建标签页 |
| `browser_context` | #1 上下文 create/close |

## 未完成 / 留白

- 缺口 #1/#3/#4/#5/#6/#13；其余三个工具；
- 组合根「通道 / Bridge」开关；Tool Runtime 权限链；切片 F；外部重启窗口验收；索引登记。

## 关联

`ecfca05`（缺口 #7）、`f053909`（第三个工具）、`8912657`、`84a3f3d`、`5a9bb60`；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。

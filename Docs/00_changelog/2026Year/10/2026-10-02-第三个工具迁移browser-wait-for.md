---
title: "第三个工具迁移：`browser_wait_for` 改走窄端口"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 上下文快路径（上一轮的教训）：给了 ContextId 就直接用，只有省略时才列清单取第一个。
categories: [docs, changelog]
tags: [wait, for, 第三个工具迁]
related_docs: []
related_files: [Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-第三个工具迁移browser-wait-for
draft: false
---

# 第三个工具迁移：`browser_wait_for` 改走窄端口

## 改动

| 文件 | 改动 |
|---|---|
| `BrowserWaitForTool.cs` | 依赖改为 `IDesktopBrowserCapabilitySurface` + `IDesktopCapabilityCallContextFactory`；三个可选等待条件 → 契约的单一条件（优先级与运行时字段顺序一致：选择器 → 消失 → URL）；`TimeoutMs` 直接进请求；结果按 `DesktopWaitResult` 搬运 |
| `Tests/…/BrowserAgentToolsTests.cs` | 替身端口新增 `WaitForAsync`（委托给假页面）；2 处构造点更新 |

**上下文快路径**（上一轮的教训）：给了 `ContextId` 就直接用，只有省略时才列清单取第一个。

**标题回退**：`BrowserTabToolValue.Title` 是必填，而 `DesktopPageState.Title` 可以为 `null`
⇒ 未知时回退**空串**（"不知道"不编造标题）；`PageVersion` 未知时回退 `0`。

## 行为保持要点

| 面 | 迁移前 | 迁移后 |
|---|---|---|
| 超时语义 | 运行时 `WaitResult.TimedOut` | 契约 `DesktopWaitResult.TimedOut`（**超时是结果不是失败**，两侧一致） |
| 诊断信息 | `WaitResult.Error` 原样进结果 | 同上（`error` 作为附加说明，不升级为失败） |
| 参数校验 | 超时 1..120000 + 至少一个条件 | 不变 |
| 未连接 Desktop | 报"页面找不到" | `browser_not_connected`（更诚实，与已迁移工具一致） |

## 验证

- `PuddingBrowser.AgentTools.Tests` **15/15**；
- `PuddingHost` / `PuddingHost.Tests` 构建 **0 错误**；`PuddingHost.Tests` **225/225**
  （含按调用序列断言的桥接集成测试 ⇒ 调用形状未被破坏）；
- **未验证**：真实 Desktop 会话端到端（需启用能力通道 + 外部重启窗口）。

## 迁移进度

七个工具已迁移 **3** 个：`locate`、`snapshot`、`wait_for`。

| 剩余工具 | 阻塞缺口 |
|---|---|
| `browser_context` | #1 上下文 create/close |
| `browser_tabs` | #6 新建标签页 |
| `browser_navigate` | #3 导航四动作 + #4 导航 `TimeoutMs` + #5 导航结果三字段 |
| `browser_interact` | #7 交互 `type` |

> 四个剩余工具都是**能力缺口**（不是参数缺口）：`context` 的 create/close、`tabs` 的 new、
> `navigate` 的 back/forward/reload/stop、`interact` 的 type。每项都要动 proto 的 payload oneof
> 与 Desktop 侧实现 ⇒ 每项一轮。

## 未完成 / 留白

- 缺口 #1/#3/#4/#5/#6/#7；其余四个工具；
- 组合根「通道 / Bridge」开关；Tool Runtime 权限链；切片 F；外部重启窗口验收；索引登记。

## 关联

`8912657`（缺口 #12）、`84a3f3d`（第二个工具）、`5a9bb60`（首个工具）；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。

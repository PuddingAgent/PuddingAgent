---
title: "第二个工具迁移：`browser_snapshot` 改走窄端口；集成测试抓出「多余的上下文列举」"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 迁移后 PuddingHost.Tests 的两个桥接集成测试失败：
categories: [docs, changelog]
tags: [第二个工具迁, 与上下文快路]
related_docs: []
related_files: [Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-第二个工具迁移browser-snapshot与上下文快路径
draft: false
---

# 第二个工具迁移：`browser_snapshot` 改走窄端口；集成测试抓出「多余的上下文列举」

## 改动

| 文件 | 改动 |
|---|---|
| `BrowserSnapshotTool.cs` | 依赖由 `IBrowserRuntime` 改为 `IDesktopBrowserCapabilitySurface` + `IDesktopCapabilityCallContextFactory`；快照预算改由 `DesktopSnapshotOptions`（含本轮加宽的四旋钮）表达；结果 `DomText`/`AccessibilityTree`/`Html`/`Truncated`/`NodeCount` 逐字段搬运 |
| `BrowserCapabilityFailure.cs`（新增） | **唯一一张**能力错误 → 工具错误码映射表（避免每个工具各写一份而漂移）；版本不符的语义由调用方给出（定位=引用过期，快照=页面已变） |
| `BrowserLocateTool.cs` | 改用共享映射表（删除自己的那份）＋ 上下文快路径 |
| `Tests/…/BrowserAgentToolsTests.cs` | 替身端口新增 `SnapshotAsync`（委托给假页面，保持引用真实）；2 处构造点更新 |

## 集成测试抓出的真实缺陷：明知上下文却先去列清单

迁移后 `PuddingHost.Tests` 的两个**桥接集成测试**失败：

```
Expected: "context.getInfo"   Actual: "context.list"
```

它们按**调用序列**断言桥接命令流，因此抓出：工具的 `ContextId` 明明给了，我却先调
`GetContextsAsync`（=`context.list`）——**比迁移前多一跳且更重**。

**修复（快路径）**：调用方给了上下文就直接用；**只有省略上下文时**才列清单取第一个（保持既有语义）。
修复后两个集成测试通过。

> **教训（值得记）**：只断言 JSON 的工具单测**看不出**这类回归；
> **按调用序列断言的集成测试才是迁移的行为预言机**。迁移其余五个工具时必须继续跑它，并优先保持调用形状。

## 验证

- `PuddingBrowser.AgentTools.Tests` **15/15**；
- `PuddingHost` / `PuddingHost.Tests` 构建 **0 错误**；`PuddingHost.Tests` 全量 **225/225**；
- **未验证**：真实 Desktop 会话端到端（需启用能力通道 + 外部重启窗口）。

## 顺带观察（非本次改动引入）

`PuddingHost.Tests.BrowserBridge.DesktopBrowserBridgeHandshakeTests.SecondConnection_CannotReplaceAwaitingHelloConnection`
**单独跑会失败**（`OperationCanceledException`），**全量跑通过** ⇒ 该用例依赖时序/顺序。
本次改动只涉及浏览器工具，与该握手用例无交集；记录在此供团队按"不稳定用例"处理。

## 迁移进度

七个工具中已迁移 **2** 个（`locate`、`snapshot`）；下一个可迁移的取决于缺口：
`browser_context`（#1 create/close）、`browser_tabs`（#6 new + #12 标题）、
`browser_navigate`（#3 四动作 + #4 超时 + #12 标题）、`browser_interact`（#7 type + #12 标题）、
`browser_wait_for`（#12 标题）⇒ **补 #12（页状态标题）收益最大：一次解锁四个工具**。

## 未完成 / 留白

- 缺口 #12（`DesktopPageState.Title`）与其余加宽项；其余五个工具；
- 组合根「通道 / Bridge」开关；Tool Runtime 权限链；切片 F；外部重启窗口验收；索引登记。

## 关联

`d2abaa5`（加宽 #9）、`5a9bb60`（首个工具迁移）、`9d58ef5`、`77b878a`、`f373f58`；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。

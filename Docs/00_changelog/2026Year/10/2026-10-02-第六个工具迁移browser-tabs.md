---
title: "第六个工具迁移：`browser_tabs` 改走窄端口（含新建标签页保真改进）"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: PuddingBrowser.AgentTools.Tests 15/15（含 new/list/activate/close 四动作用例）； PuddingHost.Tests 231/231、Pudding.DesktopSurface.BrowserTests 54/54、 Pudding.DesktopServiceTests 170/170； 未验证：真实 Desktop 会话端到端（需
categories: [docs, changelog]
tags: [tabs, 第六个工具迁]
related_docs: []
related_files: [Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-第六个工具迁移browser-tabs
draft: false
---

# 第六个工具迁移：`browser_tabs` 改走窄端口（含新建标签页保真改进）

## 改动

| 文件 | 改动 |
|---|---|
| `BrowserTabsTool.cs` | 四个动作全部改走窄端口：`new` → `BrowserTabsRequest.New(contextId, url, activate)`；`list` → `GetContextsAsync` 后拍平；`activate`/`close` → 先读页状态拿基准版本再 `TabsAsync` |
| `BrowserRuntimeDesktopSurface.cs` / `BridgeBrowserCapabilitySurface.cs` | **保真改进**：新建标签页把初始地址与是否激活**一次性**交给运行时（`PageCreateOptions { InitialUrl, Activate }`），不再"先建后导航再置前"——与迁移前工具行为一致，且少两次往返 |
| `Tests/…/BrowserAgentToolsTests.cs` | 替身端口新增 `TabsAsync`（委托给既有假上下文/假页面）+ `ContextsAsync`/`State` 辅助；3 处构造点更新 |

## 行为保持与有意变化

| 面 | 迁移前 | 迁移后 |
|---|---|---|
| `activate` / `close` | 直接作用于当前页 | **先读状态拿基准版本**（契约要求变更类固定版本；也让"等待期间页面变化"可被检出） |
| `close` 的可见形状 | `{ closed: true }` + ids | **完全相同** |
| `list` | 走运行时的上下文/页面清单 | 走 `GetContextsAsync`（结果同形：context/page/标题/URL/版本） |
| `new` 的上下文省略 | `createIfMissing: true`（会**创建**一个上下文） | 取第一个可用上下文；**若一个都没有则如实报错**——契约没有"创建上下文"的能力（缺口 #1，尚未做） |

> 最后一行的差别是**已知且刻意的**：它把"没上下文时偷偷建一个"换成"如实失败"。
> 恢复该行为需要缺口 #1（上下文 create/close），已登记。

## 验证

- `PuddingBrowser.AgentTools.Tests` **15/15**（含 `new/list/activate/close` 四动作用例）；
- `PuddingHost.Tests` **231/231**、`Pudding.DesktopSurface.BrowserTests` **54/54**、
  `Pudding.DesktopServiceTests` **170/170**；
- **未验证**：真实 Desktop 会话端到端（需启用能力通道 + 外部重启窗口）。

## 过程教训（本轮踩了三次，都靠"编译/放弃"兜住）

1. **脚本里出现三个我自己写的错**：Python 字符串里未转义的 ASCII 引号（脚本直接语法失败）、
   引用了一个不存在的 `temp/tabs_tool_body.cs`、以及给替身端口**重复添加** `TabsAsync`
   （旧 stub 还在 ⇒ CS0111）。⇒ 结论：**替身端口的旧 stub 必须先删再加**，脚本里引用外部文件要先造。
2. 锚点漂移：测试工程里存在**两个**不同的假上下文（一个是我加的委托式替身端口，一个是既有
   真实行为的 `FakeBrowserContext`，后者的 `ListPagesAsync`/`ClosePageAsync`/`NewPageAsync`
   本来就是可用的）⇒ 我最初三条"补假上下文"的补丁其实**不需要**，且因缩进不同而全部落空。
   ⇒ 结论：动手前先**实读目标区域**，不要按记忆猜缩进与实现状态。
3. 好消息：`all-or-nothing` 的脚本设计生效了——三次失败都**没有写盘**，工作树没有被留在半成品状态。

## 迁移进度 6/7

已迁移：`locate`、`snapshot`、`wait_for`、`interact`、`navigate`、`tabs`。

| 剩余工具 | 缺口 |
|---|---|
| `browser_context` | **#1** 上下文 `create`/`close`（最后一个能力缺口，约 3~5 轮：两个新能力 = proto oneof + 联合成员 + 端口两方法 + 目录/准入/探针） |

## 未完成 / 留白

- 缺口 #1 / #13；`browser_context` 迁移；
- Tool Runtime 权限链；切片 F（默认传输切换、退役 Bridge）；外部重启窗口 8 步验收；索引登记。

## 关联

`5230095`（传输路由切换）、`679eccc`（缺口 #6）、`ff5ddfb`（第五个工具）；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。

---
title: "2026-10-02 加宽第 8 项落实为**自身缺陷修复**：Bridge 侧丢掉了 `DeltaX`"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: "按 §8.5.1 的加宽清单开始施工，先挑\"只加字段、不加能力\"的最低风险项。 第一件（交互 DeltaX）动手前先实读线缆与两侧映射，结果发现它根本不是契约缺口。"
categories: [docs, changelog]
tags: [加宽第, 项实为, 缺陷]
related_docs: []
related_files: [Source/Pudding.Rpc.Protocol/Protos/desktop_capability.proto, Source/Pudding.CapabilityBroker/WireMapping.cs, Source/Pudding.DesktopConnection/Mapping.cs, Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs, Tests/PuddingHost.Tests/BrowserBridge/BridgeBrowserCapabilitySurfaceTests.cs, Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-加宽第8项实为bridge侧deltax缺陷
draft: false
---

# 2026-10-02 加宽第 8 项落实为**自身缺陷修复**：Bridge 侧丢掉了 `DeltaX`

## 目标 / 背景

按 §8.5.1 的加宽清单开始施工，先挑"只加字段、不加能力"的最低风险项。
第一件（交互 `DeltaX`）动手前先实读线缆与两侧映射，结果发现**它根本不是契约缺口**。

## 更正（2026-10-02）：第 8 项不是契约缺口，是我自己的适配器缺陷

实读三处：

| 位置 | 事实 |
|---|---|
| `Source/Pudding.Rpc.Protocol/Protos/desktop_capability.proto` | `InteractCommand` **已有** `double delta_x = 8; double delta_y = 9;` |
| `Source/Pudding.CapabilityBroker/WireMapping.cs` | Core→proto 两个轴都写（`DeltaX`/`DeltaY`），幂等键也含两轴 |
| `Source/Pudding.DesktopConnection/Mapping.cs` | proto→DTO 两个轴都读；`BrowserRuntimeDesktopSurface` 把它们一起塞进 `ScrollOptions` |

⇒ 缺口**只存在于我上一轮写的 Bridge 适配器**：`ApplyInteractionAsync` 的 Scroll 分支只映射了
`DeltaY`，把 `DeltaX` 悄悄丢掉。这是**能力通道路径有、Bridge 路径没有**的行为分叉——
恰好是本系列一直在防的那类问题，而制造它的是我自己。

**修复**：`ScrollOptions { DeltaX = request.DeltaX, DeltaY = request.DeltaY }`；
**测试**：`Interact_Scroll_PassesBothDeltaAxesToTheRuntime`（断言两轴都到达运行时；
顺带确认滚动后的版本推进不变量仍成立）。

## 改动

- `Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs`：Scroll 分支传两轴 +
  注释写明这是缺陷修复而非契约缺口。
- `Tests/PuddingHost.Tests/BrowserBridge/BridgeBrowserCapabilitySurfaceTests.cs`：新增 1 项；
  替身 `FakePage.ScrollAsync` 由抛异常改为记录 `ScrollOptions` 并推进版本。
- `Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1 第 8 项就地更正
  （划掉 + 说明不是契约缺口）。

## 验证

- `PuddingHost.Tests` **219/219**（新增 1 项）；
- **教训**：清单里的"契约缺口"必须**逐条实读线缆与两侧映射**后才能定性——
  否则会把自身缺陷记成契约缺口，进而去动根本不必要动的协议（本轮就差点如此）。

## 加宽清单现状

11 项中 **第 8 项完成（且更正为自身缺陷）**；其余 10 项仍待做
（上下文 `create`/`close`、`Persistent`、导航四动作、导航 `TimeoutMs`、导航结果三字段、
标签页 `new`、交互 `type`、快照四参数、定位 `BoundingBox`、等待 `TimeoutMs`）。

## 未完成 / 留白

- 其余 10 项加宽未开始；七个工具迁移未开始 ⇒ **运行实例行为仍然不变**
  （本轮改的是 Bridge 适配器，而它的消费方尚未接入）；
- Tool Runtime 权限/审批链；切片 F；外部重启窗口验收；索引登记。

## 关联

`3a9d652`（加宽清单补齐）、`e799742`（首次引入该缺陷的那一轮）；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。

---
title: 2026-10-02 更正：Ref 定位在 Bridge 侧有等价物；并识别出工具迁移的缺失前置件
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: "上一轮我在 BridgeBrowserCapabilitySurface 里把 DesktopLocatorKind.Ref 当成\"Bridge 侧无等价物\" 而明确拒绝，并写进了 §8.4/§8.5 的不对称清单。这个判断是错的："
categories: [docs, changelog]
tags: [更正, 定位在, 侧有等价物]
related_docs: []
related_files: [Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs, Tests/PuddingHost.Tests/BrowserBridge/BridgeBrowserCapabilitySurfaceTests.cs, Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-更正ref定位在bridge侧有等价物
draft: false
---

# 2026-10-02 更正：Ref 定位在 Bridge 侧有等价物；并识别出工具迁移的缺失前置件

## 更正（2026-10-02）：Bridge 侧**支持** `Ref` 定位

上一轮我在 `BridgeBrowserCapabilitySurface` 里把 `DesktopLocatorKind.Ref` 当成"Bridge 侧无等价物"
而明确拒绝，并写进了 §8.4/§8.5 的不对称清单。**这个判断是错的**：

- 运行时的 `PuddingBrowser.Abstractions.LocatorKind` **本身就含 `Ref`**（枚举第一项）；
- `browser_locate` 工具今天就是靠它解析快照引用的（`BrowserLocatorInput` 支持 `ref` 并
  `Enum.TryParse<LocatorKind>`）。

⇒ 本轮把 `ToRuntimeLocator` 的 `Ref` 映射补上（十种 kind 一一对应），并把那条断言"Ref 会被拒绝"的
测试**改成**断言"Ref 映射到 `LocatorKind.Ref` 且值原样传递"。测试 213/213 全绿。

**教训**：判断"某能力在另一条传输上没有等价物"时，必须**实读对方枚举/接口**，不能凭推断——
我上一次犯的是同一类错误的镜像（那次是"以为换注册就能迁移"）。两次都靠实读纠正。

## 新识别出的前置件：Core 侧缺少「能力调用上下文工厂」

本轮尝试把 `browser_locate`（§8.5 里唯一完整可迁移的工具）改到窄端口时发现：
窄端口九个方法的签名都要求 `DesktopCallContext`（`DesktopInstanceId` + `OperationId` + 期限），
而**工具侧完全没有这些**——工具只有 `ContextId`/`PageId` 等参数。

Desktop 实例 ID 来自握手协商（由 broker 持有），`OperationId` 需要每次调用新生成，期限来自配置。
因此工具迁移前必须先补一个**能力调用上下文工厂**（Core 侧，从当前活动会话取 DesktopId +
`OperationId.NewId()` + 期限），并在 DI 里注册窄端口的 Bridge 实现。

这条已加进 §8.5 的顺序里（位于"加宽契约"与"逐个迁移工具"之间），本轮**未实现**（避免半成品）。

## 改动

- `Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs`：`ToRuntimeLocator` 补 `Ref`
  映射，并改写注释（明确"早先判断已纠正"）。
- `Tests/PuddingHost.Tests/BrowserBridge/BridgeBrowserCapabilitySurfaceTests.cs`：
  把"Ref 被拒绝"的用例改成"Ref 映射正确"。

## 验证

- `PuddingHost.Tests` **213/213**（用例数不变：一条改判、其余不动）。

## 未完成 / 留白

- **能力调用上下文工厂**（新识别的前置件）尚未实现；
- 加宽契约（上下文 create/close、导航四动作、标签页 new、交互 type、超时与快照/滚动参数）尚未开始；
- 七个工具调用点替换未开始 ⇒ **运行实例行为完全不变**；
- Tool Runtime 权限/审批链；切片 F；外部重启窗口验收；索引登记。

## 关联

`ab3e667`（能力面 ⊊ 工具面的实读结论）、`97a189f`（Bridge 侧 9/9）；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.4 / §8.5。

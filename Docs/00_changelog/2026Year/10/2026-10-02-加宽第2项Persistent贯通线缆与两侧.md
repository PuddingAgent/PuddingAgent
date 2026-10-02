---
title: "加宽第 2 项完成：上下文 `Persistent` 贯通线缆与两侧"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 按 §8.5.1 清单施工。先实读线缆与两侧映射定性每一项（上一轮的教训）， 确认第 2 项是真的缺口：proto ContextInfo 只有 context_id/trust/pages 三个字段， 而工具侧 BrowserContextToolValue.Persistent 一直存在 ⇒ 能力通道取不到、Bridge 取得到。
categories: [docs, changelog]
tags: [加宽第, 贯通线缆与两]
related_docs: []
related_files: [Source/Pudding.Rpc.Protocol/Protos/desktop_capability.proto, Source/Pudding.Contracts/Desktop/ContextsContracts.cs, Source/Pudding.DesktopConnection/Mapping.cs, Source/Pudding.CapabilityBroker/WireMapping.cs, Source/Pudding.DesktopSurface.Browser/BrowserRuntimeDesktopSurface.cs, Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs, Source/Pudding.Rpc.ProtocolTests/WireContractSnapshotTests.cs, Tests/PuddingHost.Tests/BrowserBridge/BridgeBrowserCapabilitySurfaceTests.cs, Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-加宽第2项persistent贯通线缆与两侧
draft: false
---

# 加宽第 2 项完成：上下文 `Persistent` 贯通线缆与两侧

## 目标 / 背景

按 §8.5.1 清单施工。先实读线缆与两侧映射**定性**每一项（上一轮的教训），
确认第 2 项是**真的缺口**：proto `ContextInfo` 只有 `context_id`/`trust`/`pages` 三个字段，
而工具侧 `BrowserContextToolValue.Persistent` 一直存在 ⇒ 能力通道取不到、Bridge 取得到。

## 改动（跨侧协议变更，两侧同提交）

| 层 | 文件 | 改动 |
|---|---|---|
| 线缆 | `Source/Pudding.Rpc.Protocol/Protos/desktop_capability.proto` | `ContextInfo` 新增 `bool persistent = 4;` |
| 契约 | `Source/Pudding.Contracts/Desktop/ContextsContracts.cs` | `DesktopContextInfo.Persistent`（init 属性 + 文档） |
| Desktop→proto | `Source/Pudding.DesktopConnection/Mapping.cs` | 两处 `ContextInfo` 构造都写 `Persistent` |
| proto→DTO | `Source/Pudding.CapabilityBroker/WireMapping.cs` | `DecodeContexts` 两处构造都读 `Persistent` |
| Desktop 侧 | `Source/Pudding.DesktopSurface.Browser/BrowserRuntimeDesktopSurface.cs` | 从 `BrowserContextInfo.Persistent` 填充 |
| Bridge 侧 | `Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs` | 从 `ListContextsAsync` 的摘要填充 |
| 线缆快照 | `Source/Pudding.Rpc.ProtocolTests/WireContractSnapshotTests.cs` | 断言 `("persistent", 4)` |
| 适配器测试 | `Tests/PuddingHost.Tests/BrowserBridge/BridgeBrowserCapabilitySurfaceTests.cs` | 断言 `Persistent` 真的流通 |

**契约形态的取舍**：做成 `init` 属性而不是构造函数参数——`DesktopContextInfo` 有 11 处构造点
（含三个测试工程与探针），加必填参数会让它们全部编译失败；而该字段的默认语义
（未声明即非持久）与线缆上的无 presence `bool` **完全一致**，因此 init 属性没有引入新的谎言。
代价是"生产者可以忘记写"——用两侧映射测试 + 线缆快照把这个洞钉住。

## 验证

- 构建：`PuddingHost`、`Pudding.DesktopConnection`、`Pudding.DesktopSurface.Browser` 均 **0 错误**；
- 测试：`Pudding.Rpc.ProtocolTests` **20/20**（含线缆字段号快照）、
  `PuddingHost.Tests` **219/219**、`Pudding.DesktopSurface.BrowserTests` **54/54**；
- **未验证**：真实通道上的端到端往返（需要 Desktop 侧启用能力通道 + 外部重启窗口）。

## 加宽清单现状

11 项中 **第 2、8 项完成**（第 8 项更正为自身缺陷）；其余 9 项待做。

## 未完成 / 留白

- 其余 9 项加宽；七个工具迁移未开始 ⇒ **运行实例行为仍不变**（本字段尚无工具消费方）；
- Tool Runtime 权限/审批链；切片 F；外部重启窗口验收；索引登记。

## 关联

`3a9d652`（加宽清单）、`91f8015`（第 8 项）、`f15bc0c`（DI 装配）；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。

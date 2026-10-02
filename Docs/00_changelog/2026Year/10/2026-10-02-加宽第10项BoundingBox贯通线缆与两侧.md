---
title: "加宽第 10 项完成：元素 `BoundingBox` 贯通线缆与两侧（`locate` 因此可迁移）"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 按 §8.5.1 清单施工。第 10 项是卡住 browser_locate 迁移的最后一处缺口： 工具侧 BrowserElementToolValue.BoundingBox 一直存在，而 DesktopElementRef 与线缆都没有它。
categories: [docs, changelog]
tags: [加宽第, 贯通线缆与两]
related_docs: []
related_files: [Source/Pudding.Rpc.Protocol/Protos/desktop_capability.proto, Source/Pudding.Contracts/Desktop/LocateContracts.cs, Source/Pudding.DesktopConnection/Mapping.cs, Source/Pudding.CapabilityBroker/WireMapping.cs, Source/Pudding.DesktopSurface.Browser/BrowserRuntimeDesktopSurface.cs, Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs, Source/Pudding.Rpc.ProtocolTests/WireContractSnapshotTests.cs, Tests/PuddingHost.Tests/BrowserBridge/BridgeBrowserCapabilitySurfaceTests.cs, Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-加宽第10项boundingbox贯通线缆与两侧
draft: false
---

# 加宽第 10 项完成：元素 `BoundingBox` 贯通线缆与两侧（`locate` 因此可迁移）

## 目标 / 背景

按 §8.5.1 清单施工。第 10 项是**卡住 `browser_locate` 迁移的最后一处缺口**：
工具侧 `BrowserElementToolValue.BoundingBox` 一直存在，而 `DesktopElementRef` 与线缆都没有它。

## 改动（跨侧协议变更，两侧同提交）

| 层 | 文件 | 改动 |
|---|---|---|
| 线缆 | `Source/Pudding.Rpc.Protocol/Protos/desktop_capability.proto` | 新增 `message ElementBox {x,y,width,height}` + `ElementRef.bounding_box = 10`（消息字段 ⇒ 天然有 presence） |
| 契约 | `Source/Pudding.Contracts/Desktop/LocateContracts.cs` | 新增 `DesktopElementBox`；`DesktopElementRef.BoundingBox`（init 属性） |
| Desktop→proto | `Source/Pudding.DesktopConnection/Mapping.cs` | 两处 `ElementRef` 构造写 `ToWireBox(...)` + 新增 `ToWireBox` 辅助 |
| proto→DTO | `Source/Pudding.CapabilityBroker/WireMapping.cs` | 两处构造改对象初始化器写 `FromWireBox(...)` + 新增 `FromWireBox` 辅助 |
| Desktop 侧 | `Source/Pudding.DesktopSurface.Browser/BrowserRuntimeDesktopSurface.cs` | 从 `BrowserElementInfo.BoundingBox` 填充 |
| Bridge 侧 | `Source/PuddingHost/BrowserBridge/BridgeBrowserCapabilitySurface.cs` | 同上（`handle.Info.BoundingBox`） |
| 线缆快照 | `Source/Pudding.Rpc.ProtocolTests/WireContractSnapshotTests.cs` | 断言 `("bounding_box", 10)` |
| 适配器测试 | `Tests/PuddingHost.Tests/BrowserBridge/BridgeBrowserCapabilitySurfaceTests.cs` | 假元素给盒并断言 `X=12 / Height=78` 真的流通 |

**为什么用嵌套消息而不是四个裸 `double`**：proto3 的 `double` 没有 presence，
"没有包围盒"与"包围盒是 0,0,0,0"无法区分；消息字段天然有 presence ⇒ `null` 表示**不知道**，
绝不填 0 假装知道（与本系列"不撒谎"的一贯约定一致）。

**契约形态**：仍用 init 属性（`DesktopElementRef` 有 15 处构造点，含测试与探针），
默认 `null` = 不知道，语义与线缆 presence 一致。

## 验证

- 构建：`PuddingHost`、`Pudding.DesktopConnection`、`Pudding.DesktopSurface.Browser` 均 **0 错误**；
- 测试：`Pudding.Rpc.ProtocolTests` **20/20**、`PuddingHost.Tests` **219/219**、
  `Pudding.DesktopSurface.BrowserTests` **54/54**；
- **未验证**：真实通道端到端往返（需 Desktop 侧启用能力通道 + 外部重启窗口）。

## 过程教训（工具层面）

我用「先校验出现次数、不匹配就整体放弃」的脚本批量改 10 处，结果 4 处因
**这些文件用 LF 而我按 CRLF 拼串**而未命中——脚本按设计**中止并逐条报告**，
没有产生半套改动。重跑时改为按文件探测行尾即全部命中。
⇒ 批量改写必须**逐处校验命中数**，且**行尾要按文件探测**，不能假定全仓库统一。

## 加宽清单现状

11 项中已完成 **2、8、10**；其余 8 项待做。
**`browser_locate` 的所有缺口现已闭合**（`Ref` 早先已确认可用、`BoundingBox` 本轮补上）
⇒ 它是下一个可以直接迁移的工具。

## 未完成 / 留白

- 其余 8 项加宽；七个工具迁移未开始 ⇒ **运行实例行为仍不变**；
- Tool Runtime 权限/审批链；切片 F；外部重启窗口验收；索引登记。

## 关联

`3926f88`（第 2 项 Persistent）、`91f8015`（第 8 项）、`3a9d652`（加宽清单）；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。

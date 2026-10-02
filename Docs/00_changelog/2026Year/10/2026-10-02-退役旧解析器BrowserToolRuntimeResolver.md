---
title: "退役旧解析器 `BrowserToolRuntimeResolver`（七个工具迁移完成后的清理）"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 七个 Browser Agent Tools 全部迁到能力通道的窄端口之后，旧的静态解析器 Source/PuddingBrowser.AgentTools/BrowserToolRuntimeResolver.cs 已无任何调用方。 本轮先核实再删除（不靠记忆）。
categories: [docs, changelog]
tags: [退役旧解析器]
related_docs: []
related_files: [Source/PuddingBrowser.AgentTools/code_map.md, Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-退役旧解析器browsertoolruntimeresolver
draft: false
---

# 退役旧解析器 `BrowserToolRuntimeResolver`（七个工具迁移完成后的清理）

## 背景

七个 Browser Agent Tools 全部迁到能力通道的窄端口之后，旧的静态解析器
`Source/PuddingBrowser.AgentTools/BrowserToolRuntimeResolver.cs` 已**无任何调用方**。
本轮先核实再删除（不靠记忆）。

## 核实（删除前）

- 全仓库搜索 `BrowserToolRuntimeResolver`：**只有它自己那个文件**引用（`internal static class`），
  既没有被任何工具调用，也**没有 DI 注册**（它是静态工具方法类，不是可注入服务）；
- 因此退役 = 删除该文件，无需改动 DI。

## 改动

| 文件 | 改动 |
|---|---|
| `Source/PuddingBrowser.AgentTools/BrowserToolRuntimeResolver.cs` | **删除**（48 行） |
| `Source/PuddingBrowser.AgentTools/code_map.md` | 删除其索引行，并把调用链说明改为现状（七项工具全部走窄端口） |

## 语义影响

**无**。该解析器只被迁移前的工具调用；迁移后工具改用
`IDesktopBrowserCapabilitySurface` / `IDesktopContextCapabilitySurface` + `IDesktopCapabilityCallContextFactory`
（组合根路由到能力通道或 Bridge）。

`BrowserOperationException`（工具侧自有错误类型，定义在 `BrowserLocatorInput.cs` 等）与
`PuddingBrowser.Abstractions` 的引用**仍然需要**，未动。

## 验证

- `PuddingBrowser.AgentTools` 构建 **0 错误**；
- `PuddingBrowser.AgentTools.Tests` **15/15**；
- `PuddingHost.Tests` **231/231**（含经认证 Bridge 的真实往返集成用例）；
- 生产代码里已无 `IBrowserRuntime` 使用点（仅测试替身仍用，用于构造假运行时）；
- **未验证**：无新增待验证面（纯删除 + 索引更新）。

## 未完成 / 留白

- Tool Runtime 权限链（当前授权器只是身份门禁）；
- 切片 F：默认传输切换 + 退役 WebSocket Bridge（需要"能力通道真实启用"的证据，属外部重启窗口）；
- 外部重启窗口 8 步验收（内部 Agent 无法验收承载自身的生命周期）。

## 关联

`85ae6c9`（第七个工具迁移完成，工具迁移 7/7）、`652024d`（被夹具挡住的诊断）、
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。

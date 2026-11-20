---
title: 工具目录接入 host-catalog 工具源
author: hyfree
date: 2026-11-20
last_reviewed: 2026-11-20
status: archived
description: "修复 L1/L9 工具目录丢失 host-catalog 工具源：目录声称 6 个工具而实际可用 40+，导致模型反复 search_tools 试探。"
categories: [docs, changelog]
tags: [context, tools, registry, adr-081]
related_docs: [Docs/10_conventions/组件化交付规程.md]
related_files: [Source/PuddingRuntime/Services/ContextPipelineLayers.cs, Source/PuddingRuntimeTests/Services/ContextPipelineToolCatalogTests.cs]
slug: changelog-20261120-tool-catalog-host-source
draft: false
---

# 工具目录接入 host-catalog 工具源

## 问题

系统提示词里的工具目录只列出 6 个可用工具（`plugin_catalog`、`plugin_diagnostics`、`plugin_search`、`search_tools`、`list_tool_approvals`、`plugin_unload`），而同一时刻 `test_tool` 实测 `shell` 与 `file_read` 都是 `registered=true / is_available=true / exposed_to_agent=true / resolved_by=host-catalog`。

目录与实际投递面不一致，违反 ADR-081（`AgentExecutionService.Streaming.cs:252` 已写明两处必须同口径）。

## 根因

`PuddingToolRegistry.BuildToolSnapshot` 只在收到非空 `ToolExecutionContext` 时才并入上下文工具源：

```csharp
if (context is not null)
    tools = tools.Concat(_contextualToolSources.SelectMany(s => s.ListTools(context)));
```

而 `ContextPipelineLayers.BuildToolsLayerAsync` 调用的是 `ListAvailable(policy, string workspaceId)` 重载，`context` 恒为 `null`，于是 host-service 包提供的工具（`pudding.shell`、`pudding.files` 等）永远进不了目录。

注意契约层的干扰：`IPuddingToolRegistry.ListAvailable(policy, ToolExecutionContext)` **有默认实现**，会退化成 `ListAvailable(policy, context.WorkspaceId)`。任何只覆盖基础重载的实现都会静默丢掉 context，从签名上看不出问题。

## 改动

- `Source/PuddingRuntime/Services/ContextPipelineLayers.cs`
  - 新增 `internal static ToolExecutionContext CreateToolContext(ContextRequest)`：从请求就地构造执行上下文。`ContextRequest` 已带齐 `WorkspaceId` / `SessionId` / `AgentInstanceId` / `Capability` / `WorkingDirectory` / `AgentTemplateId` / `Trace`，**不新增任何契约字段**。
  - 新增 `internal static IReadOnlyList<ToolDescriptor> ResolveToolDescriptors(IPuddingToolRegistry?, ContextRequest)`：目录取数的唯一入口，改为携带 context 的重载。
  - `BuildToolsLayerAsync` 改调 `ResolveToolDescriptors`。
- `Source/PuddingRuntimeTests/Services/ContextPipelineToolCatalogTests.cs`（新增）
  - `RecordingToolRegistry` **显式覆盖** context 重载并记录收到的上下文；若不覆盖，接口默认实现会退化，夹具测不出原始缺陷。
  - 断言：context 非空、六个身份/边界字段逐一映射、`CapabilityPolicy` 原样透传、未退化到 workspaceId 重载。
  - 断言：`Capability` 为 null 时仍能构造可用上下文，不抛异常。

## 验证与限制

PuddingRuntime 构建 0 error（94 个既有 warning）。新增 3 项用例全绿；变异（把调用退回 workspaceId 重载）后 3 项中 1 失败 2 通过，失败用例单独复跑同样失败，复原后文件 SHA-256 与变异前比特一致（`9D668223…CF8D2FB4`）。`PuddingRuntimeTests` 全量 2873 项：通过 2862、失败 0、跳过 11。

未覆盖的部分需诚实说明：本组测试守住的是**调用方必须携带 context**这一契约，不直接证明目录文本中真的出现 `shell`——那取决于运行时 `HostToolContributions` 的挂载与能力策略。该端到端判据留待部署后核对系统提示词的 L9-TOOL-CATALOG 是否列出 `shell` / `file_read`。

同一现象此前已由平台诊断侧面印证：`spawn_sub_agent` 的一次失败原因为 `tool_discovery_stalled: search_tools was called 8 consecutive times without executing a discovered task tool`。未执行 git commit / push。

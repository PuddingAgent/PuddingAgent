---
title: 2026-11-19 修复 /compact 的 missing LlmProfile
author: hyfree
date: 2026-11-19
last_reviewed: 2026-11-19
status: archived
description: 上下文压缩摘要派发时未解析 LLM 路由，导致 RequireInvocationProfile 抛 missing LlmProfile；改为在派发前用 ILlmResolver 解析并自行构造 profile。
categories: [changelog]
tags: [compaction, llm-routing, runtime]
related_docs: [Docs/00_changelog/README.md]
related_files: [Source/PuddingRuntime/Services/AgentContextCompactionSummaryGenerator.cs, Source/PuddingRuntimeTests/Services/StubLlmResolver.cs]
slug: changelog-compaction-llm-profile-2026-11-19
draft: false
---

# 2026-11-19 修复 /compact 的 missing LlmProfile

## 症状

`/compact` 失败并原样返回：

```
Runtime dispatch for agent '<id>' is missing LlmProfile.
The invocation boundary must resolve provider/profile/model before execution.
```

抛点在 `AgentExecution/AgentExecutionLlmInvoker.cs:186` 的 `RequireInvocationProfile()`——`request.LlmProfile` 为 null 即抛。文案本身不含任何定位信息，看不出是哪一层没解析。

## 根因

压缩走的是独立派发路径：`AgentContextCompactionSummaryGenerator` 自己 new 一个 `RuntimeDispatchRequest`，只填了 `LlmConfig`，没填 `LlmProfile`。而两条上游契约（`ContextCompactionRequest`、`ContextCompactionSummaryRequest`）**都只有 `LlmConfig` 字段**，profile 在整条链上没有传递通道。

其它调用方都是对的（`AgentInvocationDispatchFactory` 用 `BuildLlmProfile`、`SubAgentTool` 用 `ResolveRouteAsync().Profile` 语义、`TurnExecutorAdapter` 从 context 取），所以主对话一切正常，只有压缩这条线坏。

## 修复

- 生成器注入 `ILlmResolver`，派发前 `ResolveRouteAsync(request.LlmConfig?.ModelId, requiredCapabilityTags: null, ct)`；
- 用返回的 `ProviderId` / `ModelId` **自行构造** `LlmInvocationProfile`（`ProfileId = agent:{AgentId}:compaction`，`Role = "compaction"`）。
- **两份前置物都要回退**：`RuntimeDispatchRequest` 除了 `LlmProfile` 还要求 `LlmConfig` 不为空（否则下游 `AgentExecutionService.cs:1282` 抛 `Agent LLM config is null`）。两者现在共用同一份回退链：`request.LlmConfig` → `agentProfile.PreferredModelId` / `agentProfile.LlmConfig`。

为什么不能只依赖调用方：`SessionCompactTool.cs:115`（手动 `/compact`）和 `ContextWindowManager.cs:1242`（自动压缩）**都不传 `LlmConfig`**，只带 `AgentId`。所以真正的解析来源是 `IAgentRuntimeProfileResolver.ResolveAsync(workspaceId, agentId)`——运行时容器里早已注册（`MessageDeliveryDispatcher.cs:1507` 就在用）。

为什么不是「沿链透传 profile」：要改两处 record 加字段、三处构造透传，而压缩只是一次辅助调用；相对地，解析器只有一个注入点。

`ResolvedLlmRoute` 刻意**只含身份**（`ProviderId` / `ModelId` / `Config`），其注释明确「Profile 属于调用语义，由上层 invocation boundary 单独赋值」——所以 profile 由调用方构造是符合设计的，不是绕过。

## 验证

- 新增测试桩 `StubLlmResolver`（`Config = new LlmConfig { ModelId = ... }`；`LlmConfig` 全可空 init、无 required 成员）；
- `dotnet test --filter FullyQualifiedName~AgentContextCompactionSummaryGenerator`：**失败 0 / 通过 4 / 总计 4**；
- 变异取红：去掉 `?? agentProfile?.LlmConfig` 后 **失败 1 / 通过 3**，红的正是新加的「只传 AgentId」用例——说明它不是空转；
- commit gate 通过。

## 未完成

端到端 `/compact` 未验证——需要把新程序集部署到运行中的 Core。当前部署通道阻塞：Guardian 的 `controlToken` 在 `system.json` 中是 `{{vault:…}}` 引用，取不到明文；`bootstrap_reboot` 只重启不重建。

部署后应再跑一次 `/compact`，确认返回真实 `<compact_summary>`。

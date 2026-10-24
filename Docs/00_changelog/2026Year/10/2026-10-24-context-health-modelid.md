---
title: 2026-10-24 上下文健康面板补齐 modelId 归因
author: hyfree
date: 2026-10-24
last_reviewed: 2026-10-24
status: active
description: "context_health 之前只说「估算多少、provider 实测多少」，不说这组数字是哪个模型计价出来的。不同模型走不同词表，估算偏高时无法区分是词表差异还是报文改写，两个方向相反的误差混在一起无法归因。本次把实测请求的 modelId 冻结进实测记录并投影到面板。"
categories: [changelog]
tags: [context, tokenizer, diagnostics, llm]
related_docs: [Docs/14_reports/2026-10-24-harness-context-practice-evidence.md]
related_files: [Source/PuddingCore/Platform/LlmOptions.cs, Source/PuddingCore/Runtime/ContextCompactionContracts.cs, Source/PuddingRuntime/Services/ContextCompactionService.cs]
slug: changelog-2026-10-24-context-health-modelid
draft: false
---

# 2026-10-24 上下文健康面板补齐 modelId 归因

## 做了什么

`ContextUsageSnapshot` 增加可选 `ModelId`，与 `RequestSequence` 一起从**冻结的请求记录**透传，再由
`ContextCompactionService` 投影到 `context_health` 输出。

取值规则是 `measuredMain?.ModelId ?? usage.ModelId`：实测记录的 modelId 优先，没有实测才退回当前会话。
反过来取（永远用当前会话的 ModelId）会掩盖「上一笔其实用了别的模型」——辅助调用与路由切换都会让两者不同，
而这正是估算偏高时需要排除的第一个混淆项。

## 为什么需要它

估算偏高的两个候选原因方向相反：

- 词表不同（我们按 A 词表数，服务端按 B 词表编码）→ 系统性偏差
- 报文在估算之后被改写（投影 / 裁剪 / 工具定义重排序）→ 只影响个别请求

两者在「本地估算 ÷ provider 实测」这一个数字上是**不可区分**的。只有把「这次是哪个模型计价」固定下来，
才能把偏差按模型分组，再看组内是否稳定。

## 验证与边界

- 运行中 Core（PID 90940，HEAD `0d7629c`）的 `agent_diagnostics(context_health)` 已返回
  `modelId=deepseek-flash`；重启前该字段不存在。`fallbackReason=None`、词表 identity
  `deepseek-v4.1-flash` 为精确词表。
- `ContextCompaction` 相关 20 条测试通过。
- **未新增独立单测**：钉住「实测优先于当前会话」需要伪造 prepared-request ↔ provider-usage 配对记录，
  成本高于这一只读字段的风险面。该项记为已知缺口，若后续该字段参与任何判定必须先补测试。

## 顺带纠正的一条误判

`ResolveTokenizerKey` 里 `deepseek → o200k_base` 一度被判为「精确词表永不生效」的缺陷。
复核 `GetTokenizer` 后确认：该分支只在 `ModelTokenizers.Resolve` **拿不到**精确词表时才执行，
源码注释亦写明这是有意保留的回退。误判已撤回，未改代码。

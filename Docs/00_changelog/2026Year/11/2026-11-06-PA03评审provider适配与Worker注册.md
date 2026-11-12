---
title: PA-03 评审 provider 适配与 Worker 注册
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "新增 PlatformReviewModelProvider（catalog 解析 + IRuntimeLlmChatClient 端口）与 HostRuntimeLlmChatClient；Host 注册 binder/executor/coordinator/worker 并启用 ReviewWorkerHostedService。3 条测试。"
categories: [docs, changelog]
tags: [review, worker, provider, host, pa03]
related_docs: [Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md]
related_files: [Source/PuddingPlatform/Services/Reviews/PlatformReviewModelProvider.cs, Source/PuddingHost/Services/HostRuntimeLlmChatClient.cs, Source/PuddingHost/Services/ReviewWorkerHostedService.cs, Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs]
slug: changelog-pa03-review-worker-registration-20261106
draft: false
---

# PA-03 评审 provider 适配与 Worker 注册

接通评审执行链最后一段（host gateway → binder → executor → worker）；未 commit、未部署、未调用真实模型。

## 1 provider 适配

- 新增 `IRuntimeLlmChatClient` 端口（Platform）与 `PlatformReviewModelProvider`：
  - 通过 `ILlmModelCatalogView` 解析冻结 model 的配置 provider；缺失 → `model.configuration_invalid`（不调用 gateway）；
  - 每次评审只发起一次有界 chat；usage 缺失保持 null（交由预算层 Unknown），不伪造 0。
- Host 新增 `HostRuntimeLlmChatClient`：经 `ILlmConfigService.Resolve(provider, model)` 取配置，调用共享 `IRuntimeLlmClient`；凭据不离开 Host。

## 2 Worker 注册

- Host DI 注册：`IRuntimeLlmChatClient`、`PlatformReviewModelProvider`、`IReviewModelLeaseSource`、`IReviewEvaluationBinder`、`IReviewExecutor`、`ReviewJobCoordinator`、`ReviewAttemptReconciler`、`ReviewJobWorker`。
- 新增 `ReviewWorkerHostedService`（5s tick、20 条/批、5 分钟租约）：异常只记录，正确性由 durable Job/outbox 保证；取消立即停止。
- **注意**：Host 现在会启用评审 Worker；当前无评审 Job 时不会产生模型调用。

## 3 测试与验证

- `PlatformReviewModelProviderTests` 3/3：provider 解析/usage 映射、未知 model 在 gateway 前 typed 失败、缺失 usage 保持 null。
- 与预算端口回归合计 **5/5**；Platform S5 切片 **143/143**；全解决方案构建 exit 0（restore + --no-restore）；一键脚本 RunId 20261106-r23 exit 0（组件 274 + Platform 143 + 前端 47 + tsc）。
- Host 适配器未单独运行 xUnit：本机 VSTest testhost 在沙箱下 `Win32Exception(5)` 无法启动（环境限制）；Host 构建通过，provider 逻辑由 Platform 测试覆盖。
- 冻结基线：Host DI 文件属 P0/P1 清单，有意变更已审阅并重签指纹（配置 18/18 PASS）。
- 证据：`.work/pa03/review-worker-host/`、`.work/verification/review-worker-solution/`、`.work/pa08/components-20261106-r23/`。

## 4 未完成

- 评审链路的**真实模型 smoke**（08C 未授权）与产品进程内验证（08B）仍 NotRun；Host Worker 启动仅通过构建/离线测试，未在运行中的 Core 验证。
- 插件/后台 Job 授权适配；流式/子代理预算；Goal 自动续跑。

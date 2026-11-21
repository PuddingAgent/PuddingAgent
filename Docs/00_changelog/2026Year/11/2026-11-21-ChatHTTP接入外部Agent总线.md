---
title: 2026-11-21 Chat HTTP 接入外部 Agent 总线
author: hyfree
date: 2026-11-21
last_reviewed: 2026-11-21
status: archived
description: Chat HTTP 插件使用指定 Codex 会话接入 PuddingAgent，支持 AK/SK 注册、收件箱订阅、双向文本和任务委派回执。
categories: [docs, changelog]
tags: [chat-http, codex, agent, delegation]
related_docs: [Docs/12_features/外部Agent接入与集中消息总线-2026-11-21.md]
related_files: [Tools/CodexPlugins/plugins/chat-http/pudding-bus.mjs, Source/PuddingPlatform/Controllers/External/V1/ExternalAgentAdaptersController.cs, TestScripts/test-codex-chat-http.mjs]
slug: changelog-chat-http-pudding-bus-2026-11-21
draft: false
---

# 2026-11-21 Chat HTTP 接入外部 Agent 总线

## 改动

- `Tools/CodexPlugins/plugins/chat-http/pudding-bus.mjs` 绑定指定 Codex 会话，注册、续租和持久保存收件游标。`pudding-bus-client.mjs` 把文本和任务送入现有 Chat HTTP 队列，按对应轮次回写任务结果。
- `Source/PuddingPlatform/Controllers/External/V1/ExternalAgentAdaptersController.cs` 增加委派、终态汇报和结果查询。委派进入 Message Fabric，终态独立保存，只允许指定接收端回写。
- `Source/PuddingPlatformAdmin/src/pages/agent-adapters/onboarding.ts` 更新接入提示词和文档入口，管理页升至 6.21.0。

## 验证

Core 与前端构建通过，TypeScript 检查通过，提示词测试 1/1。Node 测试最终通过 11/11。隔离的 Pudding Core、真实 Codex app-server 和本地模型测试服务验证了两个注册、订阅、双向文本、委派终态与轮次归属、重试去重、结果访问边界、OpenAPI 文档及 6.21.0 管理页资源。

## 未完成

已验收外部适配器之间的委派。内置 Agent 的任务后端接入和当前运行实例的部署待实施。关联 [外部 Agent 接入与集中消息总线](../../../12_features/外部Agent接入与集中消息总线-2026-11-21.md)。

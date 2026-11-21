---
title: 2026-11-21 外部 Agent 管理与 HTTP 接入
author: hyfree
date: 2026-11-21
last_reviewed: 2026-11-21
status: archived
description: 管理员可签发外部 Agent 凭据，适配器可通过 HTTP 注册和收发文本，接口由 Microsoft.AspNetCore.OpenApi 生成文档。
categories: [docs, changelog]
tags: [agent, adapter, http, openapi]
related_docs: [Docs/12_features/外部Agent接入与集中消息总线-2026-11-21.md]
related_files: [Source/PuddingPlatform/Controllers/Api/AdminAgentAdaptersController.cs, Source/PuddingPlatform/Controllers/External/V1/ExternalAgentAdaptersController.cs, Source/PuddingHost/Hosting/AgentAdapterApiDocumentation.cs, Source/PuddingPlatformAdmin/src/pages/agent-adapters/index.tsx]
slug: changelog-external-agent-http-onboarding-2026-11-21
draft: false
---

# 2026-11-21 外部 Agent 管理与 HTTP 接入

## 改动

- `Source/PuddingPlatform/Controllers/Api/AdminAgentAdaptersController.cs` 管理工作区内的外部 Agent 绑定，签发一次性展示的 AK/SK，复用已有令牌撤销接口。
- `Source/PuddingPlatform/Controllers/External/V1/ExternalAgentAdaptersController.cs` 提供注册、租约管理、同工作区目录、文本投递和持久收件箱。复用 Message Fabric 与外部 API 幂等存储。
- `Source/PuddingHost/Hosting/AgentAdapterApiDocumentation.cs` 使用 `Microsoft.AspNetCore.OpenApi` 生成 6 个外部 Agent 端点的 OpenAPI JSON。
- `Source/PuddingPlatformAdmin/src/pages/agent-adapters/` 增加外部 Agent 管理页、复制接入提示词和 OpenAPI 文档入口。前端版本升至 6.20.0。

## 验证

Core 构建通过，前端 TypeScript 检查、接入提示词测试 1/1 和构建通过。在独立数据目录启动 Core，确认文档含 6 个端点和 Bearer 认证，Swagger 页面返回 404。关闭外部 API 时文档返回 404。两个工作区的四个测试 Agent 验证签发、注册、目录分页、租约隔离、文本投递、幂等重试、收件箱游标、断开和撤销。重启 Core 后可续用原身份、领取离线消息并重放发送回执。浏览器验证 6.20.0 管理页的在线状态、文档链接和剪贴板按钮。

## 未完成

Chat HTTP 适配器接线、与内置 Agent 的双向消息、任务委派及处理结果回执待实施。本次验证在独立 Core 进行。关联 [外部 Agent 接入与集中消息总线](../../../12_features/外部Agent接入与集中消息总线-2026-11-21.md)。

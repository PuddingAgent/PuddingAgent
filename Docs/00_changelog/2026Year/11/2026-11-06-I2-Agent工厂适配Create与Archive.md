---
title: I2 Agent 工厂适配（Create/Archive）
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "IAgentAuthoringBackend + PlatformAgentFactoryApi/工厂/控制器；Create 复用配置权威且不启动推理，Archive 在既有 manifest 上以文件 revision 围栏落终态；5 条测试。"
categories: [docs, changelog]
tags: [agents, factory, create, archive, i2, pa02]
related_docs: [Docs/12_features/基础设施组件化收敛实施计划-2026-11-06.md, Docs/14_reports/2026-11-06-基础设施组件化评估与接入缺口.md]
related_files: [Source/PuddingPlatform/Services/Agents/PlatformAgentFactoryApi.cs, Source/PuddingPlatform/Services/Agents/WorkspaceAgentAuthoringBackend.cs, Source/PuddingPlatform/Services/WorkspaceAgentFileService.Directory.cs, Source/PuddingPlatform/Controllers/Api/AgentFactoryController.cs]
slug: changelog-i2-agent-factory-20261106
draft: false
---

# I2 Agent 工厂适配（Create/Archive）

补齐第一个公共闭环的“取得/创建 Agent”部分；未 commit、未部署、未调用真实模型。

## 1 工厂

- `IAgentAuthoringBackend` + `WorkspaceAgentAuthoringBackend`：
  - Create 复用 `WorkspaceAgentFileService.CreateAgentAsync`（既有配置权威），只物化实例，**不启动推理、不创建 Work、不计费**；
  - Archive 在既有 manifest 上写 `IsEnabled=false`/`IsFrozen=true`，以 **manifest 文件 revision**（mtime ticks）做 CAS；revision 不匹配先于“已归档”判定返回错误。
- `PlatformAgentFactoryApi` + `AgentFactoryApiFactory`：仅管理 principal 绑定 actor；请求边界校验。
- `AgentFactoryController`：`POST /api/workspaces/{w}/agents`、`POST .../agents/{id}/archive`。
- 目录的 `Revision` 也改为 manifest mtime ticks，与归档 CAS 一致；`Archived` 状态 = `IsFrozen || !IsEnabled`。
- Host 注册 `IAgentAuthoringBackend` + `AgentFactoryApiFactory`。

## 2 测试与验证

- `PlatformAgentFactoryApiTests` 3/3：Create/Archive 经 backend、外部/匿名拒绝、无界请求拒绝。
- `WorkspaceAgentAuthoringBackendTests` 1/1：真实 manifest 归档翻转终态；过期 revision 被拒且不修改。
- Platform S5 切片 **161/161**；全解决方案构建 exit 0（restore + --no-restore）；一键脚本 RunId 20261106-r29 exit 0（组件 276 + Platform 161 + 前端 47 + tsc）。
- 冻结基线：Host DI 文件属 P0/P1 清单，注册属有意变更，已审阅重签指纹（配置 18/18 PASS）。
- 证据：`.work/i0/factory/`、`.work/verification/i2-factory-solution/`、`.work/pa08/components-20261106-r29/`。

## 3 未完成

- Create 真实适配的测试未覆盖（`CreateAgentAsync` 依赖默认记忆图书馆服务）；Host 构建与实现已接通，但公共闭环的 Create 仍缺端到端证据。
- **Submit → Observe → Cancel** 未接公共门面（下一步）。
- 子代理池/提交端口、工具/授权/MCP/上下文/工作区/插件消费者迁移、Goal 自动评审接线、08B/08C（NotRun）保持原状。

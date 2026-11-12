---
title: PA-06 Agent manifest 模型策略 schema 与映射
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "AgentInstanceManifest 增加可选 ModelPolicy（复用既有配置权威），新增 AgentModelPolicyMapper：instance 层到冻结契约、CAS set/clear/inherit 往返；4 条测试。"
categories: [docs, changelog]
tags: [models, policy, config, pa06]
related_docs: [Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md, Docs/14_reports/2026-11-06-Agent组件化状态阻塞与待决策清单.md]
related_files: [Source/PuddingCore/Configuration/AgentModelPolicyModels.cs, Source/PuddingCore/Configuration/PuddingConfigModels.cs, Source/PuddingPlatform/Services/Configuration/AgentModelPolicyMapper.cs, Source/PuddingPlatformTests/Services/AgentModelPolicyMapperTests.cs]
slug: changelog-pa06-agent-manifest-model-policy-20261106
draft: false
---

# PA-06 Agent manifest 模型策略 schema 与映射

按裁定“复用既有配置权威、不新增第二套配置文件”实现 purpose 模型的 manifest schema 与纯映射层；未 commit、未部署、未调用真实模型。

## 1 schema

- `AgentInstanceManifest` 增加可选 `ModelPolicy`（null 表示无实例覆盖）：
  - `AgentModelPolicy(Revision, PolicyGeneration, Purposes)`；
  - 每个 purpose：`ModelId` 或显式 `InheritPrimary`（null ModelId 不等于继承）。
- 复用 manifest 的原子写入与既有 writer；不新增第二个策略文件。

## 2 映射层

- 新增 `AgentModelPolicyMapper`：
  - `ToSnapshot(policy)`：manifest instance 层 → `AgentConfigurationSnapshot`（Source=Instance，Workspace/System 为 null）；
  - `ToPolicy(snapshot)`：仅保留 instance 层 set/inherit；
  - `Apply(policy, command, out writeResult)`：委托 `ModelPolicyPatchApplier` 做 CAS 与校验，set/clear/inherit 往返；
  - 过期 `ExpectedRevision` → `model.config_revision_conflict`；重复 purpose → `model.config_field_unknown`。

## 3 测试与验证

- `AgentModelPolicyMapperTests` 4/4：instance 层映射、CAS set/clear/inherit 往返、过期 revision 冲突、重复 purpose 拒绝。
- Platform S5 切片 **130/130**；全解决方案构建 exit 0（restore + --no-restore）；一键脚本 RunId 20261106-r19 exit 0（组件 274 + Platform 130 + 前端 47 + tsc）。
- 证据：`.work/pa06/policy-mapper/`、`.work/verification/policy-mapper-solution/`、`.work/pa08/components-20261106-r19/`。

## 4 未完成

- 冻结基线：`Source/PuddingCore/Configuration/PuddingConfigModels.cs` 属 P0/P1 清单，本次为**新增可选字段**的有意变更；主代理审阅后已更新 `Agent配置API-P0P1-核心文件指纹.json`（配置冻结 18/18 PASS）。\n- **真实 `IAgentModelPolicyStore` 文件适配**：`WorkspaceAgentFileService` 的读写入口（含调用方权限校验）尚未实现；当前只有 manifest schema + 纯映射层。
- `IAgentModelPolicyStore` 写权限（HTTP/插件/后台 principal）仍待按裁定接入授权；live `IReviewModelLeaseSource` 与 Review Worker 注册仍缺。
- 流式/子代理预算、Goal 自动续跑、08B/08C（NotRun）保持原状。

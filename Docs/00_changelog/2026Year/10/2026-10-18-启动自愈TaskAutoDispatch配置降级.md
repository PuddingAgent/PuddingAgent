---
title: 2026-10-18 启动自愈TaskAutoDispatch配置降级
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-18
status: archived
description: "产品启动硬化：真实库历史配置 authoritative + Goals 关闭会让新 Core Options 校验拒绝启动；新增启动校验器仅对该前置缺失自愈降级 Mode=disabled 并记录 Error，其余非法配置仍 fail-closed；真实 D:\\data 验证可进入 Ready。"
categories: [docs, changelog]
tags: [startup, scheduler, configuration, hardening]
related_docs: [Docs/14_reports/会话加载优化阶段D端到端验证-2026-10-18.md]
related_files: [Source/PuddingHost/Services/TaskAutoDispatchOptionsStartupValidator.cs, Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs]
slug: changelog-2026-10-18-task-autodispatch-startup-self-heal
draft: false
---

# 2026-10-18 启动自愈TaskAutoDispatch配置降级

## 背景

真实开发库 `D:\data\config\system.json` 存在历史配置：`taskAutoDispatch.Enabled=true + Mode=authoritative`，但 `GoalRuns.Enabled=false`（旧版本遗留）。现行源码的 `TaskAutoDispatchOptions.Validate` 对 authoritative 要求 Goal 前置，`ValidateOnStart` 使新 Core **直接启动失败**，用户既无法进入页面也无法修复配置。

## 改动

- 新增 `TaskAutoDispatchOptionsStartupValidator`（Host 单例 `IValidateOptions<TaskAutoDispatchOptions>`）：
  - 只对「Authoritative 缺 TaskBoundGoals/GoalRuns 前置」这一条：记录 **Error**，把 `Mode` 降级为 `disabled`，返回校验成功；
  - 其他非法配置（ScanInterval、CandidateLimit 等）仍返回失败，保持 fail-closed；
  - 降级后调度完全关闭，不会在 Goals 关闭时偷跑任务；修复配置后需重启。
- Host 组合注册移除原先的硬 `.Validate(...)` 链，改用该启动校验器 + `ValidateOnStart`。
- 新增 Host 测试 3 例（前置缺失自愈、合法 authoritative 保持、其他非法仍失败）。
- 更新 Host code_map。

## 验证

- `PuddingHost.Tests`：新增校验器 3 例 + Host composition 2 例，**5/5 通过**。
- `PuddingAgent` 构建 0 error。
- **真实 `D:\data` 启动验证**（不设置 `TaskAutoDispatch__Mode` 覆盖，仅设 Heartbeat 阈值 600s 防自动心跳）：Core 成功启动并 `[StartupPhase] ready total=24364ms`，`TaskAutoDispatchWorker` 以 `mode=disabled` 运行；此前同一配置直接 `OptionsValidationException` 启动失败。
- 未正式部署 Desktop/产品、未改 `D:\data` 配置；验证后已停止 Core。

## 未完成与影响

- 该自愈只保证**可启动**；调度仍关闭，需要用户/维护者修复 `GoalRuns` 配置后重启才能恢复 authoritative 自动调度。
- 启动验证仍触发了已排队的 Agent 执行（上一次中断回合），发现后立即停止 Core；未删除用户数据。
- 产品部署、页角版本徽标、真实 WebView2 全链路验收仍属阶段 F；D2c3/阶段 E 剩余项仍未完成。

---
title: 工具目录查询接入真实 Tool Catalog
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "PuddingToolCatalogAdapter 将 PA-05B 目录查询接到既有 IPuddingToolCatalogService：搜索/分页/分类/副作用、schema/state；Hidden 不泄露；Unavailable 显式；Host 注册 IToolCatalogSource；3 条测试。"
categories: [docs, changelog]
tags: [tools, catalog, adapter, i3, pa05]
related_docs: [Docs/12_features/基础设施组件化收敛实施计划-2026-11-06.md, Docs/14_reports/2026-11-06-基础设施组件化评估与接入缺口.md]
related_files: [Source/PuddingPlatform/Services/Tools/PuddingToolCatalogAdapter.cs, Source/PuddingPlatformTests/Services/PuddingToolCatalogAdapterTests.cs]
slug: changelog-tool-catalog-adapter-20261106
draft: false
---

# 工具目录查询接入真实 Tool Catalog

按 I3.1“工具查询接真实目录”先做只读消费者；未 commit、未部署、未调用真实模型，也未新增工具执行入口。

## 1 适配器

- `PuddingToolCatalogAdapter`（`IToolCatalogSource`）：
  - Search 走 `IPuddingToolCatalogService.ListTools()`，支持文本/分类/副作用过滤、按 ToolId 排序与游标分页；
  - SideEffect 由既有 `ToolSafetyFlags` 推导（Shell→Process、文件写/破坏→Write、网络→Network、只读→Read）；
  - Schema 序列化既有 `ToolParameterSchema`；State 保留真实 `RuntimeStatus`，Unavailable 带 `tool.<status>` 原因；
  - **Hidden 工具在搜索/schema/state 中都不泄露存在性**；
  - 只有查询，没有执行入口（执行仍由 Runtime 的 `IPuddingToolExecutionService` 承担）。
- Host 注册 `IToolCatalogSource`（未注册 `ToolFacade` 以免引入未使用的执行表面）。

## 2 测试与验证

- `PuddingToolCatalogAdapterTests` 3/3：过滤/分页/副作用映射；Hidden 零泄露；Unavailable 显式状态与 schema。
- Platform S5 切片 **177/177**；SDK 25/25；全解决方案构建 exit 0（restore + --no-restore）；一键脚本 RunId 20261106-r38 exit 0（组件 276 + Platform 177 + 前端 47 + tsc）。
- 冻结：配置 18/18、Work 16/16、Plugin 18/18 PASS。
- 证据：`.work/i3/tool-catalog/`、`.work/verification/tool-catalog-solution/`、`.work/pa08/components-20261106-r38/`。

## 3 未完成

- 工具**执行**仍未接新授权决策器；审批/权限仍由既有执行链承担（不新增第二套）。
- MCP/上下文/工作区消费者未迁移；子代理端口、插件事件 sink、主执行链预算身份、Goal 自动续跑、08B/08C（NotRun）保持原状。

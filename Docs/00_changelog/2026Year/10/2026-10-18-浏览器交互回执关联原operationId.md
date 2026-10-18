---
title: 2026-10-18 浏览器交互回执关联原 operationId（F5 关联 id）
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-18
status: archived
description: "browser_interact 回执改用本次调用的原 operationId 并回传 ToolCallId，不再生成无法关联的新 Guid。"
categories: [docs, changelog]
tags: [browser, receipt, correlation, operation-id]
related_docs: [Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md, Docs/12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md]
related_files: [Source/PuddingBrowser.AgentTools/BrowserInteractTool.cs, Source/PuddingBrowser.AgentTools/BrowserToolContracts.cs]
slug: changelog-2026-10-18-browser-interaction-operation-id
draft: false
---

# 2026-10-18 浏览器交互回执关联原 operationId（F5 关联 id）

## 目标与结论

审阅报告 F5 的一项事实：交互回执的 `operationId` 由工具层**新生成 Guid**，与本次工具调用的 `DesktopCallContext.OperationId` 无关联，Core/Desktop 两侧无法按同一 id 对账（Desktop 活动记录已带 ToolCallId 可 join，但回执没有搬运）。

本轮做最小关联修复：

- `BrowserInteractTool` 成功路径的 `BrowserInteractionEvidence.OperationId` 改用 `call.OperationId`（本次调用原 id），不再 `Guid.NewGuid()`；
- `BrowserInteractionToolValue` 增加 `OperationId` 与 `ToolCallId`（后者来自 Origin，缺失为 null），回执可直接与 Desktop 活动/账本关联；
- 测试：`Interact_ExpectValueAgainstAPreActionObservation_IsReportedUnverified` 增加断言——回执 `operationId` 必须等于本次调用生成的 operationId（`FakeCallContextFactory.LastOperationId`）。

## 验证

- `dotnet test Tests/PuddingBrowser.AgentTools.Tests` ⇒ **99/99 通过**（含新增断言；fake call-context 工厂暴露 `LastOperationId`）。
- 旧实现不可通过该断言（回执 operationId 为独立 Guid，与调用 id 不同；且工具值没有 `operationId` 字段）。
- 未运行产品/真实模型；本切片只修回执关联，不改变执行语义。

## 未完成与影响

- 失败路径的回执仍走各自投影（`BrowserOperationFailure`），尚未统一搬运原 operationId；属下一片。
- 回执仍以 `Guid` 形式的 operationId/ToolCallId 对外暴露；**没有**把它们加入跨进程协议字段，跨传输关联需协议切片。
- 回滚 = revert 本提交；无配置/数据变更。

## 关联

任务：goal round 8；审阅报告 F5；ADR-097 D5（回执区分未执行/已派发/已观察/未知并保持关联）。

---
title: 2026-10-18 浏览器交互失败回执关联原 operationId（F5 关联补齐）
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-18
status: archived
description: "失败/异常路径的交互回执同样使用本次调用的原 operationId，不再生成无法关联的新 Guid。"
categories: [docs, changelog]
tags: [browser, receipt, correlation, failure]
related_docs: [Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md, Docs/12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md]
related_files: [Source/PuddingBrowser.AgentTools/BrowserOperationFailure.cs, Source/PuddingBrowser.AgentTools/BrowserInteractTool.cs, Source/PuddingBrowser.AgentTools/BrowserToolContracts.cs]
slug: changelog-2026-10-18-browser-interaction-failure-operation-id
draft: false
---

# 2026-10-18 浏览器交互失败回执关联原 operationId（F5 关联补齐）

## 目标与结论

上一片已让**成功**回执使用本次调用的原 `operationId`；失败/异常路径仍生成新 Guid，导致"失败也要能对账、能关联 Desktop 活动"这一半缺失。

本轮补齐失败路径：

- `BrowserReceiptToolValue` 增加 `OperationId`；
- `BrowserOperationFailure.Receipt` 与工具内 `Failure(...)` 接收可选的 `OperationId`（缺省才生成新 id），并写入失败载荷；
- `BrowserInteractTool` 在调用上下文创建后记住 `call.OperationId`，能力失败路径与驱动异常路径都传入；异常路径通过 `callOperationId` 局部变量跨越 try/catch 作用域；
- 测试：`Interact_Failure_OnAGateDenial_IsBlocked_NotJustFailed` 增加断言——失败回执 `operationId` 必须等于本次调用 id（fake 工厂暴露 `LastOperationId`）。

## 验证

- `dotnet test Tests/PuddingBrowser.AgentTools.Tests` ⇒ **99/99 通过**。
- 旧实现无法通过新断言（失败回执 operationId 为独立 Guid / 无该字段）。
- 未运行产品/真实模型；本切片只改回执关联，不改变失败语义与重试裁定。

## 未完成与影响

- 其它工具的失败回执（snapshot/locate/navigate 等）尚未统一搬运 operationId；本片聚焦 `browser_interact`。
- 回执关联仍只在工具层可见；跨进程协议字段与 Desktop 活动 join 的端到端验证属后续切片。
- 回滚 = revert 本提交；无配置/数据变更。

## 关联

任务：goal round 9；承接 `36b2d30`（成功路径关联）与 `ad85bd8`（动作前观察诚实化）。

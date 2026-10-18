---
title: 2026-10-18 浏览器八工具 canonical 事实目录纳入截图（F6）
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-18
status: archived
description: "BrowserAgentToolIds.All / BrowserAgentCapabilityIds.All 由七项改为八项并包含 screenshot，工具与能力目录同形同序。"
categories: [docs, changelog]
tags: [browser, tools, capability-catalog]
related_docs: [Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md, Docs/12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md, Docs/14_reports/Agent浏览器控制架构与实现审阅-2026-10-18.md]
related_files: [Source/PuddingBrowser.AgentTools/BrowserAgentToolIds.cs, Tests/PuddingBrowser.AgentTools.Tests/BrowserDelegationTasksTests.cs]
slug: changelog-2026-10-18-browser-eight-tool-catalog
draft: false
---

# 2026-10-18 浏览器八工具 canonical 事实目录纳入截图（F6）

## 目标与结论

审阅报告 F6 指出：`BrowserAgentToolIds.All` / `BrowserAgentCapabilityIds.All` 仍为七项，`browser_screenshot` 只在“可写委派 required 列表”里追加，和“截图属 Read 观察”的设计不一致。ADR-097 D1 要求八个 `browser_*` 工具进入**同一事实目录**，注册、发现、提示词、测试与状态展示从该目录及运行时支持集生成。

本切片把两个目录都改为八项（`Screenshot` 排在 `Snapshot` 之后，两目录同序），并明确注释“工具存在 ≠ 该动作被支持”：运行时支持集仍由 Host/Desktop 能力握手如实表达。

## 改动

- `Source/PuddingBrowser.AgentTools/BrowserAgentToolIds.cs`：两个 All 集合加入 `browser_screenshot` / `cap-browser-screenshot`；更新类文档为八项事实目录。
- `Tests/PuddingBrowser.AgentTools.Tests/BrowserDelegationTasksTests.cs`：新增 canonical 目录八项/同形断言，以及“只读委派注入集合包含 canonical 全集（含 screenshot）”用例。
- `Source/PuddingBrowser.AgentTools/code_map.md`：更新当前语义。

## 验证

- `dotnet test Tests/PuddingBrowser.AgentTools.Tests` ⇒ **98 通过 / 0 失败**（含 2 条新增用例）。
- 变化性质可静态判定：旧两集合为 7 项且不含 Screenshot，新断言 `Count == 8` 在旧实现上必然失败（本轮未重复跑“旧码红”以避免测试工程引用新常量导致编译失败）。
- 未运行 PuddingDesktop/真实模型；工具目录变化不改变任何工具执行语义。

## 未完成与影响

- 目录变化**不等于**截图产品可用：默认 Bridge 截图仍取决于执行器接线（本日已做内部侧）与真实 WebView2/视觉模型验收（I3/I4）。
- 子代理委派仍按“只读=快照 / 可写=导航+交互+截图”注入必需工具；本轮只是把 screenshot 纳入 canonical 全集，不改变“子代理一期后置”的定位。
- 回滚 = revert 本提交；无配置/数据变更。

## 关联

任务：按 ADR-097 / 一期方案继续推进实施；审阅报告 F6；方案 §4 八工具支持矩阵。

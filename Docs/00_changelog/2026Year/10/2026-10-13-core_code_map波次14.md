---
title: 2026-10-13 PuddingCore code_map 波次 14：Goal 域 + 模型表列式改造
author: hyfree
date: 2026-10-13
last_reviewed: 2026-10-13
status: active
description: "PuddingCore 的「Goal 域」13 行与「模型（Models/）」22 行改为 v2 五列；该文件 error 81→70、warn 44→36、header-repeat 归零；LlmOptions 的 F1 归因细目下沉为表后段落以同时守住 token 零丢失与行长 300。"
categories: [docs, changelog]
tags: [code-map, condensation, v2]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-13-core_code_map波次13.md]
related_files: [Source/PuddingCore/code_map.md, Tools/Docs/code_map_check.py]
slug: changelog-2026-10-13-puddingcore-code-map-wave14
draft: false
---

# PuddingCore code_map 波次 14

## 改了什么

`Source/PuddingCore/code_map.md` 两节共 **35 行**改为 v2 五列：

| 节 | 行数 | 说明 |
|---|---|---|
`## Goal 域（Goals/ · ADR-074 G0 合同层）` | 13 | 合同类型总集、状态机、事件目录、命令文本 parser、命令/查询契约、配置节、continuation/verification/TaskBound 合同 + Scheduling/Tasks 三处跨节引用 |
| `## 模型（Models/）` | 22 | 消息模型与多模态归一、LLM 响应/选项/续写状态、工具调用与权限、Agent 回复指令、记忆与任务规划/实时/视觉/语音/音频模型、消息序列规范化 |

**长枚举的承载方式**：`LlmOptions` 行的 F1 归因细节（`MeasureLlmRequest` / `CaptureLlmRequest` / `currentPreparedRequest` / `RecordProviderUsage` / `LlmCallPurposes` / `MeasuredRequestUsage` / `lastMeasuredRequest` / `TryGetLastAuxiliaryMeasurement` / `Total` / `Prompt`）**下沉为表后换行段落**（一行 ≤300，逐字保留），使表格行同时满足「token 零丢失」与「行长 ≤300」。这是波次 11 验证过的模式。

## 验证

| 指标 | 波次 13 后 | 波次 14 后 |
|---|---|---|
error | 81 | **70** |
warn | 44 | 36 |
line-too-long | 18 | 17 |
field-too-long | 63 | 53 |
header-repeat(warn) | 1 | **0** |
已改造节 | 4 | 6（+2） |

**PuddingCore 两波累计：error 88 → 70、warn 48 → 36。** 断言四条照旧（行数守恒 13/13 与 22/22、旧行 **57 个反引号 token 逐字零丢失**、列长、行长 ≤300）。
本轮 dry-run 实报并修掉两处：**Goal 行 8 约束 161>160**、**`模型` 表实际 22 行而我首轮只读到 18 行**（读被截断 ⇒ 行数断言当场拦下，补读 4 行后清零）。`git diff --numstat` = 42 插入 / 40 删除。

## 未做（诚实登记）

- PuddingCore 仍 error 70 / warn 36，剩余 12 节未改造，按规模：判定算子与分类契约(11)、抽象层(12)、LLM 网关(11)、配置 & 序列化(10)、Agent 定义(7)、子代理编排(4)、检索合同(4)、自动调度契约(3)、其余小节各 1；`missing-fingerprint`(1 warn) 仍未处理。

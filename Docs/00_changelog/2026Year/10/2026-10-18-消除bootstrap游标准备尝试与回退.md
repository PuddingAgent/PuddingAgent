---
title: 2026-10-18 消除bootstrap游标准备尝试与回退
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-18
status: archived
description: "实验性尝试：在 projection-owned 主会话的 SSE/历史游标准备中等待快照≤800ms 以跳过 /bootstrap；真实浏览器验证 bootstrap 仍 1 次（另有调用方），且引入潜在延迟，已回退到 6.9.4。结论：需改调用链或服务端，不是前端等待。"
categories: [docs, changelog]
tags: [chat, frontend, experiment, reverted]
related_docs: [Docs/14_reports/会话加载优化阶段F浏览器冒烟-2026-10-18.md]
related_files: [Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts]
slug: changelog-2026-10-18-bootstrap-wait-experiment-reverted
draft: false
---

# 2026-10-18 消除bootstrap游标准备尝试与回退

## 实验内容

在 6.9.5/6.9.6 中尝试：

- 将 `syncCompletedHistoryEventCursor` 的 projection-owned 包装同时用于 SSE 与历史投影；
- 包装内等待统一快照游标最多 800ms（每 100ms 重试），成功则返回 C，超时才回退真实 `/bootstrap`；
- 410 `snapshot_required`（resetCursor）仍走真实重取。

## 真实浏览器结果（6.9.6，临时 Core + 真实 D:\data，2 样本）

| 指标 | 结果 |
|---|---|
| badge | `v6.9.6+dirty`（验证了构建确实更新） |
| `/bootstrap?messageLimit=1` | **仍 1 次** |
| `/conversation-open` | 2 次（顺序重复仍存在） |
| 首屏 `/content` | 1 次 |
| 点击展开 | 1 次请求、剩余 6 个长消息预览 |
| 首内容可读 | 944ms / 1,034ms |
| 5xx | 0 |

说明 bootstrap 的调用方不止 SSE 游标准备（历史投影/会话链路仍有调用），前端等待没有消除它，且会给游标准备增加最多 800ms 的潜在延迟。

## 处理

- **已回退** `useChatState.ts` 与 `package.json` 到 6.9.4（`f62d700`），不保留未证实收益的等待逻辑。
- 更新阶段 F 报告：记录实验失败与根因判断；bootstrap 消除需要调整调用链（先快照、后开流/历史投影）或让服务端 `/bootstrap` 对 projection-owned 会话直接返回快照 C，而不是前端等待。

## 验证

- 工作树中本轮前端改动已回退（无残留）。
- 阶段 F 报告中记录 6.9.6 实验证据；无代码提交（回退不产生 diff）。
- 目标状态：bootstrap/重复 open 仍为阶段 D/F 未完成项，未以隐藏方式标记通过。

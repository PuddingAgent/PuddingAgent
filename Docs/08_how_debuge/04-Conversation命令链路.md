---
title: Conversation 命令链路
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: 当前主链路：
categories: [docs, how-debug]
tags: [命令链路, how_debuge]
related_docs: [Docs/08_how_debuge/README.md]
related_files: []
slug: how-debuge-04-conversation命令链路
draft: false
---

# Conversation 命令链路

> 本文档是 [How-Debuge 调试与诊断手册](README.md)（主索引）的主题分册，由原根目录 `How-Debuge.md` 于 2026-10-02 按主题拆分而来。
> 新增本主题的经验请直接追加到本文件；跨主题内容请回到主索引选择分册。

## 5. Conversation 命令链路

当前主链路：

```text
POST /api/v1/conversations/{conversationId}/turns
    ↓
SubmitTurnHandler / ConversationAcceptanceStore
    ↓ 原子持久化
User Message + Turn + Command + turn.accepted
    ↓
ChatExecutionWorker 领取 Lease
    ↓
ExecutionRunCoordinator
    ↓
AgentExecutionSnapshotFactory
    ↓
ITurnExecutor / AgentExecutionService
    ↓
IExecutionJournal.CommitTerminalAsync
    ↓
Conversation Event Store
    ↓
ConversationProjectionWorker / ConversationProjector
    ↓
SSE replay/live + 历史消息 API
    ↓
前端单调状态合并
```

### 5.1 每一阶段应看到的证据

| 阶段 | 关键证据 | 缺失意味着 |
|---|---|---|
| HTTP 受理 | POST 返回 202；响应包含稳定 ID | 路由、认证、请求契约或受理事务失败 |
| 事件写入 | `[ConversationEventStore] Appended ...` | 命令没有进入持久事实层 |
| Worker 领取 | `[LeaseStore] Acquired cmd=... turn=... runId=... fence=...` | Worker 未运行、Command 不可领取或 Lease CAS 失败 |
| 执行开始 | `turn.started`，Coordinator 开始运行 | 快照组装或执行前置条件失败 |
| LLM/工具 | `llm_gateway`、`tool_runner`、`runtime_activity` | Provider、网络、上下文或工具阶段阻塞 |
| 终态提交 | `turn.completed`、`turn.failed` 或 `turn.cancelled` | 执行结果没有原子提交 |
| 投影 | `[ConversationProjector] Projected conv=... checkpoint=A->B` | Event Store 与读模型之间存在积压或投影失败 |
| SSE/历史 | 相同稳定 `messageId/turnId/commandId` | 前后端身份或游标不一致 |

`ConversationProjector` 的 `events=0` 不一定是错误。部分事件只推进 checkpoint，不产生聊天消息投影。真正的异常是 checkpoint 长时间落后、重复失败或终态事件存在但消息读模型始终缺失。

### 5.2 一次请求必须保持的身份

- `conversationId`：浏览器观察、POST 命令、Event Store 和投影必须使用同一个值。
- `clientRequestId`：命令幂等键。
- `clientMessageId`：客户端用户消息身份。
- `turnId`：一次用户回合。
- `commandId`：可领取、可恢复的执行命令。
- `runId + fenceToken`：当前执行尝试及其写入权限。
- `assistantMessageId`：助手消息从开始、流式片段、终态到历史投影保持不变。

不要在 Controller、Worker 或投影器中重新生成这些 ID。


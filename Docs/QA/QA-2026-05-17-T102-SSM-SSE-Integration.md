# QA 审阅报告 — T-102 前端 SSM SSE 接入

| 项 | 值 |
|----|-----|
| 日期 | 2026-05-17 |
| 任务 ID | T-102 |
| 审阅模型 | DeepSeek-V4-Pro (QA) |
| 变更范围 | ChatApiController.cs / useChatState.ts / api.ts — 前端 Chat 页从双 SSE 合并为持久 SSE |
| 审阅结论 | **FAIL** |

> **阻断原因**: 2 个 P0 功能缺陷（用户中止流式生成不可用、background Task.Run 异常前端不可见）必须修复后重审。

---

## 变更摘要

将前端 Chat 页实时流从 `ChatApiController` 的临时 Channel（POST/SSE 双连接）切换到 `SessionEventsController` 的持久 Channel。后端 `SendMessage` 端点改为 fire-and-forget（立即返回 `{messageId, sessionId}`），帧通过 `SessionEventHub` → SSE 持久通道推送。前端 `sendMessage` 重构为 `sendChatMessage`（POST）+ 持久 SSE 自动接收。

---

## 问题清单

### P0 — 阻断

#### P0-1: 用户无法中止流式生成（功能缺陷）

**位置**: [useChatState.ts](Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts#L1012-L1057) — `sendMessage`

**问题**: 新流程 `sendChatMessage` 是 fire-and-forget POST，`await` 完成后 `abortRef.current` 被设为 `null`（line 1055）。当用户在 AI 生成过程中按 Enter 试图中止时，`handleKeyDown` 执行 `abortRef.current?.abort()` —— 此时 `abortRef` 已经是 `null`，没有任何效果。用户无法取消正在进行的 LLM 生成。

旧流程中 `abortRef.abort()` 会中止 POST/SSE fetch，触发 `AbortError` → `setLoading(false)`。新流程中 SSE 由独立的 `sessionEventsAbortRef` 管理，与 `abortRef` 完全解耦，但没有在 abort 逻辑中联动 `stopSessionEventStream()`。

**修复方向**:
1. `handleKeyDown` 中调用 `stopSessionEventStream()` 断开 SSE
2. 需要一个后端 cancel 端点（`POST /api/workspaces/{wid}/chat/cancel`）通知 Runtime 停止生成
3. 或者保留 `abortRef` 用于中止 POST，同时在 `sendMessage` 的 catch 中也断开 SSE

---

#### P0-2: Fire-and-forget Task.Run 异常前台不可见

**位置**: [ChatApiController.cs](Source/PuddingPlatform/Controllers/Api/ChatApiController.cs#L143-L179) — `SendMessage` Task.Run lambda

**问题**: `Task.Run` 内 `apiClient.SendMessageStreamAsync` 可能在任何阶段失败——参数校验失败、网络断开、Runtime 异常等。catch 块只记录日志 `LogWarning`，HTTP 已返回 `200 { messageId, sessionId }`。前端收到成功响应，loading=true 永久悬挂。

**具体场景**:
- 如果异常发生在写入任何帧之前：前端收到 `{messageId: "pending", sessionId: "pending"}`，SSE 无任何帧到达，loading 永不解除
- 如果异常发生在写入 metadata 之后、done 之前：前端收到真实 messageId/sessionId 并绑定 turnId，但 done 事件永远不会到达，`mapEventToTurn` 的 done 分支永不触发，`setLoading(false)` 永不执行

**修复方向**: 至少应在 Task.Run 异常时通过 EventHub 写入一个 `error` 帧（含 sessionId/streamMessageId），让前端 SSE 收到后关闭 loading。

---

### P1 — 严重

#### P1-1: `session.closed` 事件不会推送给活跃 SSE 连接

**位置**: 
- [SessionStateManager.cs](Source/PuddingPlatform/Services/SessionStateManager.cs#L367-L376) — `MarkSessionClosedAsync`
- [SessionEventsController.cs](Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs#L63-L68) — `EventsStream`

**问题**: `MarkSessionClosedAsync` 仅设置状态为 `Closed` 并安排 TTL 延迟清理 Channel，**不推送 `session.closed` SSE 帧到 EventHub**。`session.closed` 帧只在 `SessionEventsController.EventsStream` 中当 `Subscribe` 返回 `null`（Channel 不存在 + 状态为 Closed）时发送。这意味着：

- **活跃 SSE 连接**: Channel 存在 → `subscribeSessionEvents` 读取循环自然结束（Channel 被 TTL 清理），**不触发** `session.closed` 事件
- **重连场景**: Channel 已清理 + 状态 Closed → `Subscribe` 返回 null → 收到 `session.closed`

前端 `applySessionEvent` 依赖 `session.closed` 作为最后的 `setLoading(false)` 兜底（line 357）。如果 `done`/`error`/`cancelled` 事件正常到达，此缺陷不触发。但若这些事件因任何原因丢失（网络中断、部分帧被 DropOldest 丢弃），loading 永久悬挂。

**修复方向**: 在 `MarkSessionClosedAsync` 中向 EventHub Channel 写入 `session.closed` 帧，或在 TTL 清理前先写入。

---

#### P1-2: Metadata 轮询的 "pending" ID 与后续真实 ID 冲突

**位置**: [ChatApiController.cs](Source/PuddingPlatform/Controllers/Api/ChatApiController.cs#L181-L210) — metadata 等待循环

**问题**: 15s 超时后返回 `messageId = "pending"`（line 208）。前端 `sendMessage` 将 `"pending"` 映射到 turnId（line 1030）。如果 metadata 帧在 15s 后到达并写入 EventHub，前端 SSE 收到真实 `messageId` 时会重复映射：
- `messageIdToTurnIdRef` 中有两个条目：`"pending"` → turnId 和 `realId` → turnId
- 后续事件的 `resolveEventTurnId` 可能产生不一致的路由
- 更严重：`streamMessageId` 从 `null` 变为真实值后，`finalMessageId` 不会更新（HTTP 已返回），但 SSE 中的 metadata 帧会触发 `mapEventToTurn` 的 metadata 分支

**修复方向**: 超时时返回更明确的标识（如空字符串或带前缀的 `timeout-xxx`），前端检查 `messageId === "pending"` 时不建立映射，等待 SSE metadata 帧建立。

---

#### P1-3: SSE 静默断开后最大 8 秒事件丢失窗口

**位置**: [useChatState.ts](Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts#L439-L441) — 补偿轮询间隔

**问题**: `subscribeSessionEvents` 的 `.catch(() => {})` 吞咽所有错误。当 SSE 连接因网络原因静默断开（非 abort），补偿轮询每 8 秒执行一次。在最坏情况下，断开后 8 秒内的事件通过轮询补回，但轮询依赖序列号去重（`seq <= lastSequenceNumRef.current`），如果事件序列号存在但 `applySessionEvent` 被跳过（例如因为 `turnId` 解析失败），会导致看似已处理但实际未处理。

**影响**: 普通文本 delta 丢失最多造成显示不完整（用户可见但不阻断）；工具调用结果丢失可能导致 UI 状态不一致。

---

### P2 — 改进

| # | 位置 | 问题 | 建议 |
|---|------|------|------|
| P2-1 | [useChatState.ts](Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts#L18) | `sendAdminChatMessageStream` 导入但不再使用，仅在注释（line 997）引用 | 移除导入和注释中的旧流程引用（或保留作为降级方案） |
| P2-2 | [useChatState.ts](Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts#L1037) | `console.log` 调试日志残留 | 改为结构化日志或移除 |
| P2-3 | [api.ts](Source/PuddingPlatformAdmin/src/services/platform/api.ts#L1376-L1386) | `sendChatMessage` 返回类型使用 `as` 断言而非独立类型定义 | 定义专用返回类型 `SendChatMessageResponse` 增强类型安全 |
| P2-4 | [ChatApiController.cs](Source/PuddingPlatform/Controllers/Api/ChatApiController.cs#L244) | `SendMessageStream`（旧流式端点）仍存在，与 T-102 目标合并双连接为单一连接的描述不完全一致 | 确认旧端点是否仍被使用，若已废弃应标记 `[Obsolete]` |
| P2-5 | [useChatState.ts](Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts#L356) | 注释说"不再过滤 delta/thinking/tool_call/tool_result"，但 `mapEventToTurn` 中仍处理这些事件类型 | 注释准确但可能引起误解——建议改为"所有事件类型均通过统一通道到达" |

---

## 数据流完整性检查

| 链路节点 | 状态 | 备注 |
|---------|------|------|
| 用户输入 → `sendChatMessage` POST | ✅ | 正确传递 messageText/sessionId/agentId |
| POST → `ChatApiController.SendMessage` | ✅ | 解析 LLM 配置、技能包、工具定义 |
| `SendMessage` → Task.Run → `SendMessageStreamAsync` | ⚠️ P0-2 | 异常不可见 |
| 帧写入 `SessionEventHub` | ⚠️ P1-1 | session.closed 不推送 |
| SSE → `subscribeSessionEvents` → `applySessionEvent` | ✅ | 正确解析事件 |
| `applySessionEvent` → `mapEventToTurn` | ✅ | 全部事件类型覆盖 |
| 前端显示回复（delta 累积） | ✅ | 去重逻辑正确 |
| 用户中止生成 | ❌ P0-1 | 完全不可用 |
| loading 状态生命周期 | ⚠️ P0-2 + P1-1 | 两处悬挂风险 |

---

## 架构合规性

| 检查项 | 结果 | 说明 |
|--------|------|------|
| 依赖方向 | ✅ | Platform → Controller（HTTP），前端 SSE → SessionEventsController，无逆向引用 |
| 关键链路日志 | ✅ | 后端有 REQUEST/Returned/framesWritten 日志；前端有 Perf:SSM console.log（建议结构化） |
| 异常处理 | ❌ P0-2 | Task.Run 异常仅记录日志不复现到前端 |
| OWASP Top 10 | ✅ | `[Authorize]` + Bearer Token 认证；无敏感数据裸露 |

---

## 测试验证

未执行 — 项目 `PuddingPlatformAdmin` 前端暂无自动化测试套件（为已知状况，见 context.md "E2E-Playwright DEFERRED"）。

建议手动验证：
1. 发送消息后立即按 Enter → 确认生成中止且 loading 关闭
2. 断网 15 秒后恢复 → 确认事件补回且 loading 正确关闭
3. 发送消息后关闭 Runtime → 确认前端显示错误且 loading 关闭
4. 快速连续发送 3 条消息 → 确认每个 turn 独立且无串扰

---

## 总结

| 严重度 | 数量 | 关键项 |
|--------|------|--------|
| P0 阻断 | 2 | 中止不可用、Task.Run 异常不可见 |
| P1 严重 | 3 | session.closed 不推送、pending ID 冲突、SSE 8s 丢失窗口 |
| P2 改进 | 5 | 死代码导入、console.log、类型断言等 |

**结论: FAIL** — P0-1（中止不可用）和 P0-2（异常不可见）为功能缺陷，必须修复后重审。

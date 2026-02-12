# ADR-053 前端会话引用生命周期与 SSE 清理边界

> 状态：Proposed  
> 日期：2026-06-27  
> 范围：Admin Chat、useChatState、session replay、session SSE、Agent main session

## Context

Admin Chat 旧状态管理同时维护 `selectedSessionId`、`mainSessionId`、`sessionIdRef`、`sseSessionIdRef` 等多个 session 引用。删除、归档或后端返回 404 后，部分引用和 replay poll timer 可能继续持有旧 session，导致前端持续请求已不存在的 `/api/sessions/{id}/replay` 或 SSE stream。

同时，简单地"清空所有 session runtime ref"会误伤当前正在查看的其他 session，例如删除侧栏中的 session A 时 abort 当前 session B 的 SSE。

## Decision

前端引入明确的 session-not-found 处理边界：

1. 404、删除、归档统一进入 `handleSessionNotFound(sessionId, reason)`。
2. SSE/replay runtime 清理必须按 sessionId 作用域执行。
3. 只有当 `sseSessionIdRef.current === sessionId` 时，才允许 abort 当前 SSE、清 replay/reconnect timer、清 active message refs。
4. 如果只有 `sessionIdRef.current === sessionId`，只清该 ref 和 projection ownership，不得 abort 当前 SSE。
5. `mainSessionId` 必须通过 ref 镜像参与异步回调判断，避免 SSE/replay timer 使用过期闭包。
6. 当前 main session 被删除或 404 后，前端清理 `mainSessionId` 和 `agents[].mainSessionId`，并短暂抑制自动 ensure，避免刚删除又自动创建新主线。
7. SSE HTTP 404（session 不存在/已删除）或 410（session 已冻结/归档）进入 `handleSessionNotFound` 终态清理；SSE 其他错误（500/网络波动等）进入 reconnect，不清 UI session 状态。

## Consequences

正向影响：

- 删除/归档非当前 session 不再中断当前 SSE。
- replay poll 收到 404 后停止调度，不再形成幻影轮询。
- main session 缓存和 Agent 列表缓存能随 session invalidation 清理。
- SSE 500/网络错误恢复为重连路径，不被误判为 session 删除。

代价：

- `useChatState` 仍保留 legacy 状态组合，P0 只是止血，不是最终状态机。
- 需要继续迁移到独立的 `useSessionEventStream`，让 `useEffect` cleanup 成为 timer/SSE 生命周期唯一出口。
- 测试必须串行运行 Umi/Jest，避免 `.umi-test` 临时目录竞争。

## Implementation Notes

新增前端 helper：

- `SessionNotFoundError`
- `isSessionNotFoundError`
- `disposeCurrentSessionRuntime(sessionId, refs, reason)` — 仅 `sseSessionIdRef` 匹配时 abort SSE
- `clearDeletedSessionReferences(sessionId, refs)` — 只清 projectionOwned/sessionIdRef，不碰 SSE

`handleSessionNotFound` 内部规则：

```ts
if (isCurrentStream) {
  disposeCurrentSessionRuntime(sessionId, runtimeRefs, reason);
  stopSessionEventStream();
}
// isSelected → React UI 清理（selectedSessionId/turns/events/subAgentCards）
// isMain → 清 mainSessionId + agents[].mainSessionId + 抑制一次 ensure
```

main session 自动重建抑制：

```ts
const suppressMainSessionEnsureRef = useRef(false);

// handleSessionNotFound isMain 分支
suppressMainSessionEnsureRef.current = true;

// ensure effect 入口
if (suppressMainSessionEnsureRef.current) {
  suppressMainSessionEnsureRef.current = false;
  return; // 跳过本次 ensure
}
```

显式重置点（`resetMainSessionEnsureSuppression`）：

- `ensureAgentMainSession` 开始前 → reason: `'explicit-ensure'`
- `sendMessage` 有效路由确认后 → reason: `'send-message'`

`subscribeSessionEvents` 新增 `onError` 回调：

```ts
options?: { onError?: (error: Error, httpStatus?: number) => void }
```

调用方规则：

- `httpStatus === 404`（deleted）或 `httpStatus === 410`（frozen/archived）：`handleSessionNotFound(sessionId, 'sse-${httpStatus}')`
- 其他错误（含 abort/session 变更守卫）：`scheduleReconnect()`

## Acceptance Criteria

1. 删除非当前 session，不 abort 当前 SSE。
2. 删除当前 selected session，清 selected、turns、runtime events、subAgentCards。
3. 删除或 404 当前 main session，清 `mainSessionId` 和 `agents[].mainSessionId`。
4. replay 404/410 后不再继续 schedule replay poll。
5. SSE 404（deleted）或 410（frozen/archived）进入 session-not-found 清理。
6. SSE 500 或网络错误调度 reconnect，不清 selected session。
7. 以下测试通过：
   - `sessionRuntimeCleanup.test.ts`
   - `useChatState.selection.test.tsx`
   - `useChatState.recovery.test.ts`
   - `api.sessionEvents.test.ts`

## Next

P0 完成后进入 P1：

- 抽出 `useSessionEventStream`
- 把 `startSessionEventStream`、replay poll、online reconnect、AbortController cleanup 从 `useChatState.ts` 迁出
- 让 `useEffect([sessionId])` cleanup 成为 SSE/replay 生命周期唯一入口

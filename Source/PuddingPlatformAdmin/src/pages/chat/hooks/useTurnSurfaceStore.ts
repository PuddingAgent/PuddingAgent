// ── useTurnSurfaceStore：TurnSurfaceStore 的 React 胶水（2026-08-24）────────
//
// 职责：
//  1. 消费会话投影（AgentConversationView 轮询快照）：为每个 agent turn 建立
//     canonical turnId ↔ messageId/runId 别名，并同步 turn 状态。
//  2. 完成 turn 的过程明细懒水合：processSummary.hasDetails 且该消息尚未水合
//     时，调用单消息明细接口，把 text/thinking/tool/delegation 项归一为
//     ExecutionFlowEvent 流入 store——刷新/终态后轨迹永久可恢复。
//  3. activeRun 快照项也进入同一事件流（eventId 幂等去重，与历史明细互斥），
//     补齐 agent-client 架构下无 session SSE 时的过程事实。
//
// 渲染层通过 getSurfaceProjection(turnId|alias) 消费；与 useChatState 的
// live 投影（getTurnProjection）互补，优先 surface（覆盖历史 turn）。
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { getAgentMessageProcessItems } from '../client/agentChatApi';
import type {
  AgentConversationView,
  ConversationMessageView,
} from '../client/types';
import {
  processItemsToFlowEvents,
  TurnSurfaceStore,
  type TurnSurfaceStatus,
} from '../projections/turnSurfaceStore';

const surfaceStatusFromMessage = (
  status: ConversationMessageView['status'],
): TurnSurfaceStatus => {
  switch (status) {
    case 'streaming':
    case 'sending':
    case 'sent':
      return 'running';
    case 'failed':
      return 'failed';
    case 'cancelled':
      return 'cancelled';
    default:
      return 'completed';
  }
};

export interface UseTurnSurfaceStoreArgs {
  workspaceId?: string | null;
  agentId?: string | null;
  conversationView?: AgentConversationView | null;
}

export interface UseTurnSurfaceStoreResult {
  /** canonical turnId（或别名 messageId/runId）→ 水合投影；无则 undefined。 */
  getSurfaceProjection: (
    turnIdOrAlias?: string | null,
  ) => ReturnType<TurnSurfaceStore['getProjection']>;
  /** 已水合/合流的 turn 数（诊断用）。 */
  hydratedTurnCount: number;
  /**
   * store 修订号：每次事件合流/水合落盘递增。下游把消费函数（如
   * getTurnProjection 组合）的 useCallback 依赖它，可打破 MessageRow 级
   * memo 的「函数身份恒定 → 水合后不重渲染」死锁。
   */
  revision: number;
}

export function useTurnSurfaceStore({
  workspaceId,
  agentId,
  conversationView,
}: UseTurnSurfaceStoreArgs): UseTurnSurfaceStoreResult {
  const storeRef = useRef<TurnSurfaceStore | null>(null);
  if (storeRef.current === null) storeRef.current = new TurnSurfaceStore();
  const store = storeRef.current;
  const [revision, setRevision] = useState(0);
  const hydratedMessageIdsRef = useRef<Set<string>>(new Set());
  const inFlightRef = useRef<Set<string>>(new Set());

  // 会话切换：就地清空 store（实例身份恒定，闭包不会捕获到孤儿 store）。
  const conversationId = conversationView?.mainSessionId ?? null;
  const boundConversationRef = useRef<string | null>(null);
  useEffect(() => {
    if (boundConversationRef.current === conversationId) return;
    boundConversationRef.current = conversationId;
    hydratedMessageIdsRef.current.clear();
    inFlightRef.current.clear();
    store.reset();
    setRevision(0);
  }, [conversationId, store]);
  const notifyMutated = useCallback(() => {
    setRevision(store.getRevision());
  }, [store]);

  // 1) 投影快照 → 别名/状态/activeRun 事件合流。
  useEffect(() => {
    if (!conversationView) return;
    const view = conversationView;
    let mutated = false;
    for (const message of view.messages) {
      const turnId = message.turnId || message.runId || message.messageId;
      if (!turnId) continue;
      const surface = store.linkAlias(turnId, message.messageId);
      if (message.runId) store.linkAlias(turnId, message.runId);
      if (message.role === 'agent' && message.messageType !== 'agent_input') {
        const next = surfaceStatusFromMessage(message.status);
        if (surface.status !== next && !(surface.status !== 'running' && next === 'running')) {
          surface.status = next;
          surface.revision += 1;
          mutated = true;
        }
      }
    }
    const activeRun = view.activeRun;
    if (activeRun) {
      const turnId =
        store.resolveTurnId(activeRun.commandClientId) ??
        store.resolveTurnId(activeRun.runId);
      if (turnId) {
        store.linkAlias(turnId, activeRun.runId);
        if (activeRun.commandClientId)
          store.linkAlias(turnId, activeRun.commandClientId);
        const events = processItemsToFlowEvents(
          activeRun.outputSnapshot.processItems ?? [],
          { turnId },
        );
        const result = store.applyEvents(events, { turnIdHint: turnId });
        if (result.applied > 0) mutated = true;
      }
    }
    if (mutated) notifyMutated();
  }, [conversationView, store, notifyMutated]);

  // 2) 完成 turn 懒水合（每 messageId 一次；失败静默，下次视图刷新重试）。
  useEffect(() => {
    if (!workspaceId || !agentId || !conversationView) return;
    const view = conversationView;
    const targets = view.messages.filter((message) => {
      if (message.role !== 'agent') return false;
      if (!message.processSummary?.hasDetails) return false;
      if (message.status === 'streaming') return false;
      const turnId = message.turnId || message.runId || message.messageId;
      if (!turnId) return false;
      if (hydratedMessageIdsRef.current.has(message.messageId)) return false;
      if (inFlightRef.current.has(message.messageId)) return false;
      return true;
    });
    for (const message of targets) {
      inFlightRef.current.add(message.messageId);
      const turnId = message.turnId || message.runId || message.messageId;
      void getAgentMessageProcessItems(workspaceId, agentId, message.messageId)
        .then((details) => {
          hydratedMessageIdsRef.current.add(message.messageId);
          if (!details.processItems?.length) return;
          const events = processItemsToFlowEvents(details.processItems, {
            turnId,
            // 历史明细 base 取负高段：projector 的终态单调守卫会忽略
            // sequence 大于终态的迟到事件，水合事实必须排在 live 事件之前；
            // 明细按 canonical sequence 升序返回，负段递增保持相对顺序，
            // 与 live 事实的重叠靠 eventId 去重互斥（同 canonical EventId）。
            baseSequence: -1_000_000,
          });
          const result = store.applyEvents(events, { turnIdHint: turnId });
          if (result.applied > 0) notifyMutated();
        })
        .catch(() => {
          // 水合失败（网络/权限）：移出 in-flight，待下次视图轮询重试。
          inFlightRef.current.delete(message.messageId);
        });
    }
  }, [workspaceId, agentId, conversationView, store, notifyMutated]);

  const getSurfaceProjection = useCallback(
    (turnIdOrAlias?: string | null) =>
      store.getProjection(turnIdOrAlias),
    [store],
  );

  const hydratedTurnCount = useMemo(
    () => store.getStats().turns,
    [store, revision],
  );

  return { getSurfaceProjection, hydratedTurnCount, revision };
}

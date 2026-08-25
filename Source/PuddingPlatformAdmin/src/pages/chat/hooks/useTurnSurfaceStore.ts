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
  /**
   * 注册「近视口回合」：MessageRow 挂载（虚拟化窗口内可见）时上报 turnId，
   * 懒水合只对已注册回合 + 活跃回合发起，替代首屏全量并发拉取。
   */
  registerVisibleTurn: (turnId?: string | null) => void;
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
  const [registerRevision, setRegisterRevision] = useState(0);
  const [hydrationDrainRevision, setHydrationDrainRevision] = useState(0);
  const hydratedMessageIdsRef = useRef<Set<string>>(new Set());
  const inFlightRef = useRef<Set<string>>(new Set());
  const failedMessageIdsRef = useRef<Set<string>>(new Set());
  const visibleTurnIdsRef = useRef<Set<string>>(new Set());

  // 会话切换：就地清空 store（实例身份恒定，闭包不会捕获到孤儿 store）。
  const conversationId = conversationView?.mainSessionId ?? null;
  const boundConversationRef = useRef<string | null>(null);
  useEffect(() => {
    if (boundConversationRef.current === conversationId) return;
    boundConversationRef.current = conversationId;
    hydratedMessageIdsRef.current.clear();
    inFlightRef.current.clear();
    failedMessageIdsRef.current.clear();
    store.reset();
    setRevision(0);
  }, [conversationId, store]);
  // 新一轮服务端投影到达后允许失败项重试；同一轮 drain 内先跳过，防止网络
  // 故障造成无界立即重试，同时不阻塞队列里的其他可见消息。
  useEffect(() => {
    failedMessageIdsRef.current.clear();
  }, [conversationView]);
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

  // 2) 完成 turn 懒水合（有界）：只水合「近视口回合」（MessageRow 挂载时
  //    registerVisibleTurn 上报；虚拟化窗口即近视口过滤器）与活跃回合，
  //    单批最多 2 个并发——替代旧版首屏全量并发拉取（实测 8 回合 5288 事件
  //    导致 4.5k DOM 节点）。未注册回合滚动进入视口时由注册触发补拉。
  useEffect(() => {
    if (!workspaceId || !agentId || !conversationView) return;
    const view = conversationView;
    const targets = view.messages.filter((message) => {
      if (message.role !== 'agent') return false;
      if (!message.processSummary?.hasDetails) return false;
      if (message.status === 'streaming') return false;
      const turnId = message.turnId || message.runId || message.messageId;
      if (!turnId) return false;
      if (!visibleTurnIdsRef.current.has(turnId)) return false;
      if (hydratedMessageIdsRef.current.has(message.messageId)) return false;
      if (inFlightRef.current.has(message.messageId)) return false;
      if (failedMessageIdsRef.current.has(message.messageId)) return false;
      return true;
    }).slice(0, 2);
    for (const message of targets) {
      inFlightRef.current.add(message.messageId);
      const turnId = message.turnId || message.runId || message.messageId;
      const requestConversationId = view.mainSessionId;
      void getAgentMessageProcessItems(workspaceId, agentId, message.messageId)
        .then((details) => {
          // 会话切换后到达的旧请求不得污染新 store。
          if (boundConversationRef.current !== requestConversationId) return;
          hydratedMessageIdsRef.current.add(message.messageId);
          if (!details.processItems?.length) return;
          const events = processItemsToFlowEvents(details.processItems, {
            turnId,
          });
          const result = store.applyEvents(events, { turnIdHint: turnId });
          if (result.applied > 0) notifyMutated();
        })
        .catch(() => {
          // 网络/权限失败保持未水合；后续投影轮询/重新进入视口可重试。
          failedMessageIdsRef.current.add(message.messageId);
        })
        .finally(() => {
          inFlightRef.current.delete(message.messageId);
          // slice(0, 2) 是并发窗口而不是总量上限：任一槽位完成即调度下一条
          // 已注册的可见消息。否则初始列表只有最早两条有轨迹，最新消息永远
          // 停留在“底部整段正文”的旧 UI。
          if (boundConversationRef.current === requestConversationId) {
            setHydrationDrainRevision((n) => n + 1);
          }
        });
    }
  }, [
    workspaceId,
    agentId,
    conversationView,
    store,
    notifyMutated,
    registerRevision,
    hydrationDrainRevision,
  ]);

  const getSurfaceProjection = useCallback(
    (turnIdOrAlias?: string | null) =>
      store.getProjection(turnIdOrAlias),
    [store],
  );

  const hydratedTurnCount = useMemo(
    () => store.getStats().turns,
    [store, revision],
  );

  const registerVisibleTurn = useCallback(
    (turnId?: string | null) => {
      if (!turnId || visibleTurnIdsRef.current.has(turnId)) return;
      visibleTurnIdsRef.current.add(turnId);
      // 触发水合 effect 重跑（revision 只增不减，setRevision 同值会被 React
      // 忽略，故用自增计数器）。
      setRegisterRevision((n) => n + 1);
    },
    [],
  );

  return { getSurfaceProjection, hydratedTurnCount, revision, registerVisibleTurn };
}

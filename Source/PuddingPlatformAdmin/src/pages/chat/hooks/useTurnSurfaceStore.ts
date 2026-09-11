// ── useTurnSurfaceStore：TurnSurfaceStore 的 React 胶水（2026-08-24）────────
//
// 职责：
//  1. 消费会话投影（AgentConversationView 轮询快照）：为每个 agent turn 建立
//     canonical turnId ↔ messageId/runId 别名，并同步 turn 状态。
//  2. 完成 turn 的过程明细懒水合：processSummary.hasDetails 且该消息尚未水合
//     时，经 DetailHydrationScheduler 调用单消息明细接口，把 text/thinking/
//     tool/delegation 项归一为 ExecutionFlowEvent 流入 store——刷新/终态后
//     轨迹永久可恢复。并发上限、请求 key、generation 与失败预算全部由
//     runtime/detailHydrationScheduler 持有，本 hook 不维护并发状态。
//  3. activeRun 快照项也进入同一事件流（eventId 幂等去重，与历史明细互斥），
//     补齐 agent-client 架构下无 session SSE 时的过程事实。
//  4. 调度器生命周期（F01/AU-F01-3）：创建 effect 是调度器实例的创建与释放
//     所有者——setup 创建并经 state 下发，cleanup 只释放自己创建的实例；
//     StrictMode setup→cleanup→setup 后，新实例经重渲染驱动水合 effect 从
//     现有可见集合与快照恢复期望工作。
//  5. 恢复接线（F01/AU-F01-4）：失败状态按 messageId 投影（getHydration-
//     Failure），认证恢复 notifyAuthRecovered 与手动重试 retryAll 读当前
//     ref 转发调度器，供认证事件源与明细 UI 接线。
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
import {
  DetailHydrationScheduler,
  type DetailFailureKind,
  type DetailHydrationRequest,
} from '../runtime/detailHydrationScheduler';

/** 明细请求有界超时：到时由 API adapter 主动 abort，真实终止 transport。 */
const DETAIL_HYDRATION_TIMEOUT_MS = 15_000;

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

/** 明细水合失败投影（F01/AU-F01-4）：UI 据此区分「已失败 / 明细不存在 /
 *  认证失败」，不再把失败渲染成永久空轨迹。 */
export interface TurnHydrationFailure {
  kind: DetailFailureKind;
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
   * 注册「近视口回合」：MessageRow 进入滚动容器的预取区时上报 turnId，
   * 懒水合只对已注册回合 + 活跃回合发起，替代首屏全量并发拉取。
   */
  registerVisibleTurn: (turnId?: string | null) => void;
  /**
   * 注销近视口回合（引用计数）：MessageRow 离开预取区/卸载时上报，
   * 停止尚未开始的预取；已取得的明细不回滚。
   */
  unregisterVisibleTurn: (turnId?: string | null) => void;
  /** 查询消息明细水合失败状态；null 表示未失败（尚未加载/进行中/已水合）。 */
  getHydrationFailure: (messageId: string) => TurnHydrationFailure | null;
  /** 认证恢复事件入口（真实登录/认证版本变化时由认证层调用）。 */
  notifyAuthRecovered: () => void;
  /** 手动重试：对已耗尽/认证等待的可见明细重新开启预算。 */
  retryAll: () => void;
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
  const hydratedMessageIdsRef = useRef<Set<string>>(new Set());
  /** 近视口回合引用计数：MessageRow 进入/离开预取区（或卸载）配对增减。 */
  const visibleTurnIdsRef = useRef<Map<string, number>>(new Map());
  const boundConversationRef = useRef<string | null>(null);
  const conversationId = conversationView?.mainSessionId ?? null;

  const notifyMutated = useCallback(() => {
    setRevision(store.getRevision());
  }, [store]);

  // 明细水合失败投影（F01/AU-F01-4）：按 messageId 记录最近失败分类。
  const [hydrationFailures, setHydrationFailures] = useState<
    Map<string, DetailFailureKind>
  >(new Map());

  // 调度器实例（F01/AU-F01-3）：由下方创建 effect 持有生命周期；ref 始终
  // 指向当前存活实例，回调与恢复入口只读 ref，不捕获上一代已 disposed 实例。
  const schedulerRef = useRef<DetailHydrationScheduler | null>(null);
  const [scheduler, setScheduler] = useState<DetailHydrationScheduler | null>(
    null,
  );

  // 创建 effect：调度器实例的唯一创建与释放所有者。setup 创建当前实例；
  // cleanup 仅当 ref 仍指向自己创建的实例时释放（不误伤后继实例）。
  // StrictMode setup→cleanup→setup：A 被释放、B 接管，setScheduler(B) 驱动
  // 依赖 scheduler 的 effect 重跑，从现有可见集合与快照恢复期望工作。
  useEffect(() => {
    const instance = new DetailHydrationScheduler({
      // 取消链（F01/AU-F01-1）：调度器 signal 直达 HTTP transport；
      // 会话切换与卸载共用同一 AbortController 路径。
      fetch: (request, signal) =>
        getAgentMessageProcessItems(
          request.workspaceId,
          request.agentId,
          request.messageId,
          { signal, timeoutMs: DETAIL_HYDRATION_TIMEOUT_MS },
        ),
      onSuccess: (request, details) => {
        // 调度器已保证请求仍属当前代；此处再挡一道旧会话结果进新 store。
        if (boundConversationRef.current !== request.conversationId) return;
        hydratedMessageIdsRef.current.add(request.messageId);
        setHydrationFailures((prev) => {
          if (!prev.has(request.messageId)) return prev;
          const next = new Map(prev);
          next.delete(request.messageId);
          return next;
        });
        if (!details.processItems?.length) return;
        const turnId = request.turnId || request.messageId;
        const activeStore = storeRef.current;
        if (!activeStore) return;
        const result = activeStore.applyEvents(
          processItemsToFlowEvents(details.processItems, { turnId }),
          { turnIdHint: turnId },
        );
        if (result.applied > 0) notifyMutated();
      },
      onFailure: (request, kind) => {
        // 失败预算（401 等待 / 404 终止 / transient 退避）由调度器持有；
        // 失败分类投影给 UI，区分「已失败/明细不存在/认证失败」。
        if (boundConversationRef.current !== request.conversationId) return;
        setHydrationFailures((prev) => {
          const next = new Map(prev);
          next.set(request.messageId, kind);
          return next;
        });
      },
    });
    schedulerRef.current = instance;
    setScheduler(instance);
    return () => {
      if (schedulerRef.current !== instance) return;
      schedulerRef.current = null;
      instance.dispose();
    };
  }, [notifyMutated]);

  // 会话切换：就地清空 store（实例身份恒定，闭包不会捕获到孤儿 store），
  // 并递增调度器 generation 终止旧会话的一切在飞/排队工作；失败投影
  // 一并清空（新会话全新失败预算）。
  useEffect(() => {
    if (!scheduler) return;
    if (boundConversationRef.current === conversationId) return;
    boundConversationRef.current = conversationId;
    hydratedMessageIdsRef.current.clear();
    visibleTurnIdsRef.current.clear();
    store.reset();
    scheduler.beginGeneration();
    setRevision(0);
    setHydrationFailures(new Map());
  }, [conversationId, store, scheduler]);

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

  // 2) 完成 turn 懒水合（有界）：把「近视口 + 可水合」的期望集合交给调度器
  //    对账。调度器保证全页未完成请求 ≤ capacity，任一槽位结束自动补位，
  //    不依赖 React 重渲染驱动排空。effect 重放（含 StrictMode 第二次
  //    setup、scheduler 实例切换）即从当前可见集合与快照恢复期望工作。
  useEffect(() => {
    if (!scheduler || !workspaceId || !agentId || !conversationView) return;
    const view = conversationView;
    const desired: DetailHydrationRequest[] = [];
    for (const message of view.messages) {
      if (message.role !== 'agent') continue;
      if (!message.processSummary?.hasDetails) continue;
      if (message.status === 'streaming') continue;
      const turnId = message.turnId || message.runId || message.messageId;
      if (!turnId) continue;
      if (!visibleTurnIdsRef.current.has(turnId)) continue;
      if (hydratedMessageIdsRef.current.has(message.messageId)) continue;
      desired.push({
        workspaceId,
        agentId,
        conversationId: view.mainSessionId,
        messageId: message.messageId,
        turnId,
      });
    }
    scheduler.sync(desired);
  }, [workspaceId, agentId, conversationView, scheduler, registerRevision]);

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
      if (!turnId) return;
      const counts = visibleTurnIdsRef.current;
      counts.set(turnId, (counts.get(turnId) ?? 0) + 1);
      // 触发水合 effect 重跑（revision 只增不减，setRevision 同值会被 React
      // 忽略，故用自增计数器）。
      setRegisterRevision((n) => n + 1);
    },
    [],
  );

  const unregisterVisibleTurn = useCallback(
    (turnId?: string | null) => {
      if (!turnId) return;
      const counts = visibleTurnIdsRef.current;
      if (!counts.has(turnId)) return;
      const next = (counts.get(turnId) ?? 0) - 1;
      if (next > 0) counts.set(turnId, next);
      else counts.delete(turnId);
      setRegisterRevision((n) => n + 1);
    },
    [],
  );

  const getHydrationFailure = useCallback(
    (messageId: string) => {
      const kind = hydrationFailures.get(messageId);
      return kind ? { kind } : null;
    },
    [hydrationFailures],
  );

  // 恢复入口（F01/AU-F01-4）：读当前 ref 转发，不捕获已 disposed 实例。
  const notifyAuthRecovered = useCallback(() => {
    schedulerRef.current?.notifyAuthRecovered();
  }, []);

  const retryAll = useCallback(() => {
    schedulerRef.current?.retryAll();
  }, []);

  return {
    getSurfaceProjection,
    hydratedTurnCount,
    revision,
    registerVisibleTurn,
    unregisterVisibleTurn,
    getHydrationFailure,
    notifyAuthRecovered,
    retryAll,
  };
}

import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useMemo, useRef, useState } from 'react';
import type {
  AdminChatStreamEvent,
  TokenUsageDto,
  WorkspaceAgentDto,
} from '@/services/platform/api';
import { recordPerfEvent, writeDebugTrace } from '@/utils/perfEventRuntime';
import { sanitizeProcessText } from '../components/processPreview';
import {
  projectSubAgentRunsToCards,
  reduceSubAgentRunEvent,
  type SubAgentRunMap,
} from '../reducer/subAgentReducer';
import type {
  ApprovalCardData,
  PlanCardData,
  PlanStepData,
} from '../client/types';
import type { ChatSource, ChatTurn } from '../types';
import type { ChatInteractionRuntimeEvent } from '../types/chatStateTypes';
import {
  formatChatErrorDiagnostic,
  isChatStreamErrorEvent,
  logChatDiag,
} from '../utils/chatDiagnostics';
import {
  canBindUnknownMetadataToTurn,
  confirmOptimisticTurn,
  createAssistant,
  formatCompactSuccessMessage,
  getStepMessage,
  getStepTone,
  getTrackedActiveMessageIds,
  isSubAgentConversationEvent,
  isActiveAssistantTurn,
  isReasoningStep,
  normalizeUsage,
  removeTrackedActiveMessageIdsForTurn,
  resolveTerminalAssistantMarkdown,
  resolveTurnIdForEvent,
  shouldAdvanceSequenceForSessionEvent,
  shouldResetSequenceForSessionChange,
  stringToColor,
  toChatInteractionRuntimeEvent,
  tryExtractDelta,
} from '../utils/chatStateUtils';
import {
  buildTimelineItemIdentity,
  getCanonicalEventId,
  getCanonicalOccurredAtMs,
  recordEventProtocolError,
  requireToolCallId,
} from '../utils/canonicalEvents';
import type { CompactionLifecycleOptions } from './useCompaction';

interface ProjectionIdentityPort {
  agentId?: string;
  selectedAgent?: WorkspaceAgentDto;
  mainSessionId: string | null;
  selectedSessionId: string | null;
  sseSessionIdRef: MutableRefObject<string | null>;
  selectedSessionIdRef: MutableRefObject<string | null>;
  sessionIdRef: MutableRefObject<string | undefined>;
}

interface ProjectionTurnsPort {
  turnsRef: MutableRefObject<ChatTurn[]>;
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  completedTurnsRef: MutableRefObject<Set<string>>;
  latestTurnIdRef: MutableRefObject<string | null>;
  messageIdToTurnIdRef: MutableRefObject<Map<string, string>>;
  lastSequenceNumRef: MutableRefObject<number>;
  activeMessageIdsRef: MutableRefObject<Set<string>>;
}

interface ProjectionBufferPort {
  pendingDeltaRef: MutableRefObject<Map<string, string>>;
  pendingThinkingRef: MutableRefObject<Map<string, string>>;
  enqueueDelta: (turnId: string, delta: string) => void;
  enqueueThinking: (
    turnId: string,
    delta: string,
    facts?: { eventId?: string; occurredAtMs?: number },
  ) => void;
  flushPendingDeltas: () => void;
  flushPendingThinking: () => void;
  resetSessionEventBuffers: () => void;
}

interface ProjectionIntegrationPort {
  setLoading: Dispatch<SetStateAction<boolean>>;
  appendRuntimeEvent: (event: ChatInteractionRuntimeEvent) => void;
  markSteeringInjected: (event: {
    steeringId: string;
    injectedAt: number;
    injectedRound?: number;
    sessionId?: string;
    agentId?: string;
    messageChars?: number;
  }) => void;
  handleCompactionLifecycleEvent: (
    event: AdminChatStreamEvent,
    options?: CompactionLifecycleOptions,
  ) => void;
}

interface UseSessionEventProjectionOptions {
  identity: ProjectionIdentityPort;
  turns: ProjectionTurnsPort;
  buffers: ProjectionBufferPort;
  integrations: ProjectionIntegrationPort;
}

/** Owns canonical live-event to ChatTurn/SubAgent/status projection. */
export function useSessionEventProjection({
  identity,
  turns,
  buffers,
  integrations,
}: UseSessionEventProjectionOptions) {
  const {
    agentId,
    selectedAgent,
    mainSessionId,
    selectedSessionId,
    sseSessionIdRef,
    selectedSessionIdRef,
    sessionIdRef,
  } = identity;
  const {
    turnsRef,
    setTurns,
    completedTurnsRef,
    latestTurnIdRef,
    messageIdToTurnIdRef,
    lastSequenceNumRef,
    activeMessageIdsRef,
  } = turns;
  const {
    pendingDeltaRef,
    pendingThinkingRef,
    enqueueDelta,
    enqueueThinking,
    flushPendingDeltas,
    flushPendingThinking,
    resetSessionEventBuffers,
  } = buffers;
  const {
    setLoading,
    appendRuntimeEvent: appendChatInteractionRuntimeEvent,
    markSteeringInjected,
    handleCompactionLifecycleEvent,
  } = integrations;

  const [workingAgentIds, setWorkingAgentIds] = useState<string[]>([]);
  const [subAgentRuns, setSubAgentRuns] = useState<SubAgentRunMap>({});
  const subAgentCards = useMemo(
    () => projectSubAgentRunsToCards(subAgentRuns),
    [subAgentRuns],
  );
  const [latestUsage, setLatestUsage] = useState<TokenUsageDto | undefined>();
  const [sessionCacheHitTokens, setSessionCacheHitTokens] = useState(0);
  const [sessionCacheMissTokens, setSessionCacheMissTokens] = useState(0);
  const hydrateSessionReplayRef = useRef(false);
  const duplicateDeltaReplayOffsetRef = useRef<Map<string, number>>(new Map());
  const eventCountsRef = useRef<Map<string, number>>(new Map());
  const streamStartAtRef = useRef<Map<string, number>>(new Map());
  const messageIdToAgentIdsRef = useRef<Map<string, string[]>>(new Map());
  const sessionIdToAgentIdsRef = useRef<Map<string, string[]>>(new Map());

  const setAgentIdsWorking = useCallback(
    (agentIds: Iterable<string | undefined>, isWorking: boolean) => {
      const normalized = Array.from(
        new Set(
          [...agentIds]
            .map((id) => id?.trim())
            .filter((id): id is string => Boolean(id)),
        ),
      );
      if (normalized.length === 0) return;

      setWorkingAgentIds((prev) => {
        const next = new Set(prev);
        normalized.forEach((id) => {
          if (isWorking) next.add(id);
          else next.delete(id);
        });
        const nextList = Array.from(next);
        if (prev.length === nextList.length && prev.every((id) => next.has(id)))
          return prev;
        return nextList;
      });
    },
    [],
  );

  const clearWorkingAgentsForMessage = useCallback(
    (messageId: string | null, targetTurnId?: string | null) => {
      let ids = messageId
        ? messageIdToAgentIdsRef.current.get(messageId)
        : undefined;
      if ((!ids || ids.length === 0) && targetTurnId) {
        const turn = turnsRef.current.find(
          (item) => item.turnId === targetTurnId,
        );
        ids =
          turn?.source?.sourceType === 'agent'
            ? [turn.source.sourceId]
            : undefined;
      }
      if ((!ids || ids.length === 0) && agentId) ids = [agentId];
      if (ids && ids.length > 0) setAgentIdsWorking(ids, false);
      if (messageId) messageIdToAgentIdsRef.current.delete(messageId);
    },
    [agentId, setAgentIdsWorking],
  );

  const reconcileSessionWorkingAgents = useCallback(
    (sessionId: string, nextAgentIds: string[], isWorking: boolean) => {
      const previousAgentIds =
        sessionIdToAgentIdsRef.current.get(sessionId) ?? [];
      if (previousAgentIds.length > 0)
        setAgentIdsWorking(previousAgentIds, false);
      if (!isWorking || nextAgentIds.length === 0) {
        sessionIdToAgentIdsRef.current.delete(sessionId);
        return;
      }
      sessionIdToAgentIdsRef.current.set(sessionId, nextAgentIds);
      setAgentIdsWorking(nextAgentIds, true);
    },
    [setAgentIdsWorking],
  );

  const updateLastSequence = useCallback((ev: unknown) => {
    if (!ev || typeof ev !== 'object') return;
    const raw = (ev as Record<string, unknown>).sequenceNum;
    if (raw === undefined || raw === null) return;
    const seq = typeof raw === 'number' ? raw : Number(raw);
    if (Number.isFinite(seq) && seq > lastSequenceNumRef.current) {
      lastSequenceNumRef.current = seq;
    }
  }, []);

  const resetStreamCursorForSessionChange = useCallback(
    (previousSessionId?: string | null, nextSessionId?: string | null) => {
      if (
        !shouldResetSequenceForSessionChange(previousSessionId, nextSessionId)
      )
        return;
      lastSequenceNumRef.current = 0;
      activeMessageIdsRef.current.clear();
      streamStartAtRef.current.clear();
      duplicateDeltaReplayOffsetRef.current.clear();
      resetSessionEventBuffers();
    },
    [resetSessionEventBuffers],
  );

  const pruneTrackedActiveMessages = useCallback((reason: string): boolean => {
    const before = activeMessageIdsRef.current.size;
    const tracked = getTrackedActiveMessageIds(
      activeMessageIdsRef.current,
      messageIdToTurnIdRef.current,
      turnsRef.current,
    );
    if (tracked.length !== before) {
      const keep = new Set(tracked);
      const removed = [...activeMessageIdsRef.current].filter(
        (messageId) => !keep.has(messageId),
      );
      activeMessageIdsRef.current = keep;
      recordPerfEvent(
        'chat.activeMessages.pruned',
        {
          reason,
          before,
          after: tracked.length,
          removedMessageIds: removed,
        },
        { throttleMs: 1_000 },
      );
    }
    return tracked.length > 0;
  }, []);

  const resolveEventTurnId = useCallback(
    (ev: AdminChatStreamEvent): string | null => {
      const latestTurn = latestTurnIdRef.current
        ? turnsRef.current.find(
            (turn) => turn.turnId === latestTurnIdRef.current,
          )
        : undefined;
      return resolveTurnIdForEvent(
        ev as Record<string, unknown>,
        messageIdToTurnIdRef.current,
        latestTurnIdRef.current,
        canBindUnknownMetadataToTurn(latestTurn),
      );
    },
    [],
  );

  // ── mapEventToTurn ──────────────────────────────────────────
  const mapEventToTurn = useCallback(
    (turnId: string, ev: AdminChatStreamEvent) => {
      const hydrateReplay = hydrateSessionReplayRef.current;
      setTurns((prev) =>
        prev.map((turn) => {
          if (turn.turnId !== turnId) return turn;
          if (
            completedTurnsRef.current.has(turnId) &&
            (ev.type === 'message.content.appended' ||
              ev.type === 'message.thinking_summary.appended' ||
              ev.type === 'usage.recorded')
          ) {
            return turn;
          }
          if (ev.type === 'metadata') {
            // T-102: 从 metadata 帧推断消息来源（持久 SSE 通道）
            // T-103: 兼容两种命名——SessionRouter 帧输出 source_id(source_name) snake_case，
            // WebSocket connector metadata 使用 sourceId camelCase。
            const anyMeta = ev as unknown as Record<string, unknown>;
            const sourceId = String(
              anyMeta.source_id || anyMeta.sourceId || 'agent',
            );
            const sourceType = String(
              anyMeta.source_type || anyMeta.sourceType || 'agent',
            ) as ChatSource['sourceType'];
            const sourceName = String(
              anyMeta.source_name ||
                anyMeta.sourceName ||
                (sourceType === 'system_command' ? 'System' : 'AI 助手'),
            );
            const sourceMeta =
              anyMeta.source_id ||
              anyMeta.sourceId ||
              anyMeta.source_type ||
              anyMeta.sourceType;
            const source: ChatSource | undefined = sourceMeta
              ? {
                  sourceId,
                  sourceType,
                  displayName: sourceName,
                  avatarEmoji:
                    sourceType === 'websocket'
                      ? ('🔌' as const)
                      : sourceType === 'webhook'
                        ? ('🪝' as const)
                        : sourceType === 'email'
                          ? ('📧' as const)
                          : sourceType === 'system_command'
                            ? ('⚙' as const)
                            : ('🤖' as const),
                  avatarColor: stringToColor(sourceId),
                  avatarUrl:
                    String(
                      anyMeta.avatar_url ||
                        anyMeta.avatarUrl ||
                        selectedAgent?.avatarUrl ||
                        '',
                    ) || undefined,
                }
              : undefined;
            return {
              ...turn,
              source: source || turn.source,
              userMessage: { ...turn.userMessage, status: 'success' as const },
            };
          }
          if (ev.type === 'approval.requested') {
            const raw = ev as unknown as Record<string, unknown>;
            const approvalId =
              typeof raw.approvalId === 'string' ? raw.approvalId : '';
            if (!approvalId) return turn;
            const riskLevel = ['low', 'medium', 'high', 'critical'].includes(
              String(raw.riskLevel),
            )
              ? (String(raw.riskLevel) as ApprovalCardData['riskLevel'])
              : 'medium';
            const approvalCard: ApprovalCardData = {
              approvalId,
              toolName: String(raw.toolName ?? raw.tool_name ?? ''),
              description: String(raw.description ?? ''),
              riskLevel,
              arguments:
                raw.arguments &&
                typeof raw.arguments === 'object' &&
                !Array.isArray(raw.arguments)
                  ? (raw.arguments as Record<string, unknown>)
                  : undefined,
              status: 'pending',
              requestedAt:
                typeof raw.requestedAt === 'string'
                  ? raw.requestedAt
                  : new Date().toISOString(),
              expiresAt:
                typeof raw.expiresAt === 'string' ? raw.expiresAt : undefined,
            };
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                approvalCard,
                renderMode: 'structured' as const,
              },
            };
          }
          if (ev.type === 'plan.proposal') {
            const raw = ev as unknown as Record<string, unknown>;
            const planId = typeof raw.planId === 'string' ? raw.planId : '';
            if (!planId) return turn;
            const rawSteps = Array.isArray(raw.steps) ? raw.steps : [];
            const steps: PlanStepData[] = rawSteps
              .map((step, index): PlanStepData | null => {
                if (!step || typeof step !== 'object') return null;
                const record = step as Record<string, unknown>;
                const stepId =
                  typeof record.id === 'string' && record.id.trim()
                    ? record.id.trim()
                    : `step-${index + 1}`;
                const title =
                  typeof record.title === 'string' && record.title.trim()
                    ? record.title.trim()
                    : typeof record.summary === 'string' &&
                        record.summary.trim()
                      ? record.summary.trim()
                      : `步骤 ${index + 1}`;
                const description =
                  typeof record.description === 'string'
                    ? record.description
                    : '';
                return { id: stepId, title, description };
              })
              .filter((step): step is PlanStepData => step !== null);
            const planCard: PlanCardData = {
              planId,
              summary:
                typeof raw.summary === 'string' ? raw.summary : undefined,
              steps,
              status: 'pending',
              requestedAt:
                typeof raw.requestedAt === 'string'
                  ? raw.requestedAt
                  : new Date().toISOString(),
            };
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                planCard,
                renderMode: 'structured' as const,
              },
            };
          }
          if (ev.type === 'message.content.appended') {
            const rawDelta = typeof ev.delta === 'string' ? ev.delta : '';
            if (!rawDelta) return turn;
            // ADR-InkBloom: 去重逻辑保留，通过 enqueueDelta 批处理更新
            const current = turn.assistant.answerMarkdown;
            const replayOffset =
              duplicateDeltaReplayOffsetRef.current.get(turn.turnId) ?? 0;
            if (
              current.length > 0 &&
              replayOffset < current.length &&
              current.slice(replayOffset).startsWith(rawDelta)
            ) {
              const nextOffset = replayOffset + rawDelta.length;
              if (nextOffset >= current.length) {
                duplicateDeltaReplayOffsetRef.current.delete(turn.turnId);
              } else {
                duplicateDeltaReplayOffsetRef.current.set(
                  turn.turnId,
                  nextOffset,
                );
              }
              return turn;
            }
            duplicateDeltaReplayOffsetRef.current.delete(turn.turnId);
            let delta = rawDelta;
            const maxOverlap = Math.min(current.length, delta.length, 10);
            for (let n = maxOverlap; n > 0; n--) {
              if (current.endsWith(delta.substring(0, n))) {
                delta = delta.substring(n);
                break;
              }
            }
            if (!delta) return turn;
            if (hydrateReplay) {
              return {
                ...turn,
                assistant: {
                  ...turn.assistant,
                  renderMode: 'structured' as const,
                  answerMarkdown: current + delta,
                },
              };
            }
            enqueueDelta(turn.turnId, delta);
            return turn;
          }
          if (ev.type === 'message.thinking_summary.appended') {
            const thinkingDelta = typeof ev.delta === 'string' ? ev.delta : '';
            if (!sanitizeProcessText(thinkingDelta, { compact: false }))
              return turn;
            enqueueThinking(turn.turnId, thinkingDelta, {
              eventId: getCanonicalEventId(ev) ?? undefined,
              occurredAtMs: getCanonicalOccurredAtMs(ev) ?? undefined,
            });
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'thinking' as const,
                renderMode: 'structured' as const,
              },
            };
          }
          if (ev.type === 'tool.call.requested') {
            // TR-01/CU-02：toolCallId 是工具事件必填字段；缺失记协议错误且不渲染工具行。
            const toolCallId = requireToolCallId(ev);
            if (!toolCallId) return turn;
            const identity = buildTimelineItemIdentity(ev, 'tool-call');
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                renderMode: 'structured' as const,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: identity.id,
                    eventId: getCanonicalEventId(ev) ?? undefined,
                    type: 'tool_call' as const,
                    status: 'tool_call',
                    toolCallId,
                    parentToolCallId: ev.parentToolCallId,
                    presentation: ev.presentation,
                    name: ev.name,
                    arguments: ev.arguments,
                    message: `🔧 调用工具: ${ev.name}\n参数: ${ev.arguments}`,
                    timestamp: identity.timestamp,
                    collapsed: false,
                  },
                ],
              },
            };
          }
          if (
            ev.type === 'tool.call.completed' ||
            ev.type === 'tool.call.failed'
          ) {
            // TR-01/CU-02：toolCallId 是工具事件必填字段；缺失记协议错误且不渲染工具行。
            const toolCallId = requireToolCallId(ev);
            if (!toolCallId) return turn;
            const identity = buildTimelineItemIdentity(ev, 'tool-result');
            const isSuccess =
              ev.type === 'tool.call.completed' && ev.exitCode === 0;
            const exitLabel = isSuccess ? '✓' : '✗';
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                renderMode: 'structured' as const,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: identity.id,
                    eventId: getCanonicalEventId(ev) ?? undefined,
                    type: 'tool_result' as const,
                    status: isSuccess ? 'success' : 'error',
                    toolCallId,
                    parentToolCallId: ev.parentToolCallId,
                    durationMs: ev.durationMs,
                    presentation: ev.presentation,
                    name: ev.name,
                    output: ev.output,
                    exitCode: ev.exitCode,
                    message: `🔧 ${ev.name} ${exitLabel}\n${ev.output || ev.error || '(empty)'}`,
                    timestamp: identity.timestamp,
                    collapsed: false,
                  },
                ],
              },
            };
          }
          if (ev.type === 'subconscious_step') {
            const identity = buildTimelineItemIdentity(ev, 'subconscious-step');
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                renderMode: 'structured' as const,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: identity.id,
                    eventId: getCanonicalEventId(ev) ?? undefined,
                    type: 'subconscious_step' as const,
                    status: ev.status === 'done' ? 'done' : 'thinking',
                    message: `🧠 ${ev.message}`,
                    timestamp: identity.timestamp,
                    collapsed: false,
                  },
                ],
              },
            };
          }
          if (ev.type === 'context.compaction.started') {
            const items = turn.assistant.timelineItems ?? [];
            const last = items.length > 0 ? items[items.length - 1] : null;
            if (
              last?.type === 'subconscious_step' &&
              last.status === 'compacting'
            ) {
              return {
                ...turn,
                assistant: {
                  ...turn.assistant,
                  status: 'executing' as const,
                  isStreaming: true,
                  renderMode: 'structured' as const,
                },
              };
            }
            const compactStartIdentity = buildTimelineItemIdentity(
              ev,
              'compaction-started',
            );
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'executing' as const,
                isStreaming: true,
                renderMode: 'structured' as const,
                timelineItems: [
                  ...items,
                  {
                    id: compactStartIdentity.id,
                    eventId: getCanonicalEventId(ev) ?? undefined,
                    type: 'subconscious_step' as const,
                    status: 'compacting',
                    message: '正在压缩上下文…',
                    timestamp: compactStartIdentity.timestamp,
                    collapsed: false,
                  },
                ],
              },
            };
          }
          if (ev.type === 'context.compaction.completed') {
            // 适配新旧两种返回形状：旧版字段直接平铺，新版包裹在 compaction 对象中
            const compactData = (ev as any).compaction ?? ev;
            const before =
              typeof compactData.beforeTokens === 'number'
                ? compactData.beforeTokens
                : 0;
            const after =
              typeof compactData.afterTokens === 'number'
                ? compactData.afterTokens
                : 0;
            const count =
              typeof compactData.compactedMessageCount === 'number'
                ? compactData.compactedMessageCount
                : 0;
            const newSessionId =
              typeof ev.newSessionId === 'string' ? ev.newSessionId : null;
            const answerMarkdown =
              formatCompactSuccessMessage({
                beforeTokens: before,
                afterTokens: after,
                compactedMessageCount: count,
              }) +
              (newSessionId ? `\n\n新会话已创建：\`${newSessionId}\`` : '');
            const items = turn.assistant.timelineItems ?? [];
            const alreadyCompleted = items.some(
              (item) =>
                item.type === 'subconscious_step' &&
                item.status === 'success' &&
                item.message === '上下文压缩完成',
            );
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'success' as const,
                isStreaming: false,
                renderMode: 'structured' as const,
                answerMarkdown,
                timelineItems: alreadyCompleted
                  ? items
                  : [
                      ...items,
                      {
                        id: buildTimelineItemIdentity(
                          ev,
                          'compaction-completed',
                        ).id,
                        eventId: getCanonicalEventId(ev) ?? undefined,
                        type: 'subconscious_step' as const,
                        status: 'success',
                        message: '上下文压缩完成',
                        timestamp: buildTimelineItemIdentity(
                          ev,
                          'compaction-completed',
                        ).timestamp,
                        collapsed: false,
                      },
                    ],
              },
            };
          }
          if (ev.type === 'context.compaction.failed') {
            const items = turn.assistant.timelineItems ?? [];
            const message = String(ev.error || '上下文压缩失败');
            const alreadyFailed = items.some(
              (item) =>
                item.type === 'subconscious_step' &&
                item.status === 'error' &&
                item.message === message,
            );
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'error' as const,
                isStreaming: false,
                renderMode: 'structured' as const,
                answerMarkdown: message,
                timelineItems: alreadyFailed
                  ? items
                  : [
                      ...items,
                      {
                        id: buildTimelineItemIdentity(ev, 'compaction-failed')
                          .id,
                        eventId: getCanonicalEventId(ev) ?? undefined,
                        type: 'subconscious_step' as const,
                        status: 'error',
                        message,
                        timestamp: buildTimelineItemIdentity(
                          ev,
                          'compaction-failed',
                        ).timestamp,
                        collapsed: false,
                      },
                    ],
              },
            };
          }
          if (ev.type === 'step') {
            const status = String(ev.status || 'executing');
            const message = getStepMessage(ev);
            const stepIdentity = buildTimelineItemIdentity(ev, 'step');
            if (isReasoningStep(status)) {
              const items = turn.assistant.timelineItems ?? [];
              const last = items.length > 0 ? items[items.length - 1] : null;
              if (last?.type === 'thinking') {
                return {
                  ...turn,
                  assistant: {
                    ...turn.assistant,
                    status: 'thinking' as const,
                    renderMode: 'structured' as const,
                    timelineItems: [
                      ...items.slice(0, -1),
                      { ...last, text: `${last.text ?? ''}\n${message}` },
                    ],
                  },
                };
              }
              return {
                ...turn,
                assistant: {
                  ...turn.assistant,
                  status: 'thinking' as const,
                  renderMode: 'structured' as const,
                  timelineItems: [
                    ...items,
                    {
                      id: stepIdentity.id,
                      eventId: getCanonicalEventId(ev) ?? undefined,
                      type: 'thinking' as const,
                      text: message,
                      timestamp: stepIdentity.timestamp,
                      collapsed: true,
                    },
                  ],
                },
              };
            }
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: getStepTone(status) === 'error' ? 'error' : 'executing',
                renderMode: 'structured' as const,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: stepIdentity.id,
                    eventId: getCanonicalEventId(ev) ?? undefined,
                    type: 'subconscious_step' as const,
                    status,
                    message,
                    timestamp: stepIdentity.timestamp,
                    collapsed: false,
                  },
                ],
              },
            };
          }
          if (ev.type === 'usage.recorded') {
            return {
              ...turn,
              assistant: { ...turn.assistant, usage: normalizeUsage(ev.usage) },
            };
          }
          if (ev.type === 'turn.completed') {
            completedTurnsRef.current.add(turnId);
            duplicateDeltaReplayOffsetRef.current.delete(turnId);
            if (ev.traceId) writeDebugTrace(ev.traceId);
            const isErrorEvent = isChatStreamErrorEvent(ev);
            // 埋点：done 事件到达，记录 turn 完成状态
            console.debug('[Pudding Chat] done event applied', {
              turnId,
              messageId: (ev as Record<string, unknown>).messageId,
              replyLen: typeof ev.reply === 'string' ? ev.reply.length : 0,
              currentAnswerLen: turn.assistant.answerMarkdown.length,
              isStreaming: turn.assistant.isStreaming,
              hydrateReplay,
            });
            logChatDiag('event.done.applied', {
              turnId,
              messageId: (ev as Record<string, unknown>).messageId,
              sessionId: sseSessionIdRef.current,
              replyLen: typeof ev.reply === 'string' ? ev.reply.length : 0,
              currentAnswerLen: turn.assistant.answerMarkdown.length,
              isStreaming: turn.assistant.isStreaming,
              hydrateReplay,
            });
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: isErrorEvent
                  ? ('error' as const)
                  : ('success' as const),
                isStreaming: false,
                answerMarkdown: isErrorEvent
                  ? formatChatErrorDiagnostic(ev, {
                      sessionId: sseSessionIdRef.current,
                      turnId,
                    })
                  : resolveTerminalAssistantMarkdown(
                      turn.assistant.answerMarkdown,
                      ev.reply,
                    ),
                usage: normalizeUsage(ev.usage) ?? turn.assistant.usage,
                voice: (ev as Record<string, unknown>).voice as
                  | { enabled?: boolean; tts_text?: string }
                  | undefined,
              },
            };
          }
          if (ev.type === 'turn.cancelled') {
            const cancelledIdentity = buildTimelineItemIdentity(
              ev,
              'turn-cancelled',
            );
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'cancelled' as const,
                isStreaming: false,
                timelineItems: ev.message
                  ? [
                      ...(turn.assistant.timelineItems ?? []),
                      {
                        id: cancelledIdentity.id,
                        eventId: getCanonicalEventId(ev) ?? undefined,
                        type: 'subconscious_step' as const,
                        status: 'cancelled',
                        message: ev.message,
                        timestamp: cancelledIdentity.timestamp,
                        collapsed: false,
                      },
                    ]
                  : (turn.assistant.timelineItems ?? []),
              },
            };
          }
          if (ev.type === 'turn.failed') {
            const diagnosticMarkdown = formatChatErrorDiagnostic(ev, {
              sessionId: sseSessionIdRef.current,
              turnId,
            });
            const failedIdentity = buildTimelineItemIdentity(ev, 'turn-failed');
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'error' as const,
                isStreaming: false,
                answerMarkdown: diagnosticMarkdown,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: failedIdentity.id,
                    eventId: getCanonicalEventId(ev) ?? undefined,
                    type: 'subconscious_step' as const,
                    status: 'error',
                    message: ev.message || '请求失败',
                    timestamp: failedIdentity.timestamp,
                    collapsed: false,
                  },
                ],
              },
            };
          }
          return turn;
        }),
      );
    },
    [enqueueDelta, enqueueThinking, selectedAgent],
  );

  const applySessionEvent = useCallback(
    (ev: AdminChatStreamEvent) => {
      const applyStart = performance.now();
      const eventType = String(ev.type);
      const anyEv = ev as Record<string, unknown>;
      const isSubAgentEvent = isSubAgentConversationEvent(eventType);
      if (isSubAgentEvent) {
        setSubAgentRuns((current) =>
          reduceSubAgentRunEvent(current, {
            ...anyEv,
            type: eventType,
          }),
        );
      }
      const messageId =
        typeof anyEv.messageId === 'string' ? anyEv.messageId : null;
      const count = (eventCountsRef.current.get(eventType) ?? 0) + 1;
      eventCountsRef.current.set(eventType, count);

      // ADR-060: 子代理事实只进入独立 reducer / 运行坞。它们不属于主 Agent
      // 消息内容；缺少 parentTurnId 的历史事件尤其不能回退绑定到最新 Turn。
      if (isSubAgentEvent) {
        updateLastSequence(ev);
        recordPerfEvent(
          'chat.subagent.event.apply',
          {
            eventType,
            count,
            runId:
              typeof anyEv.runId === 'string'
                ? anyEv.runId
                : typeof anyEv.run_id === 'string'
                  ? anyEv.run_id
                  : undefined,
            sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
            applyMs: Math.round(performance.now() - applyStart),
          },
          { throttleMs: 500 },
        );
        return;
      }

      const runtimeEvent = toChatInteractionRuntimeEvent(ev, agentId);
      if (runtimeEvent) {
        appendChatInteractionRuntimeEvent(runtimeEvent);
      }

      // Compaction is a conversation lifecycle fact, not an Agent Turn event.
      // It has no turnId/messageId and must never fall through to the normal
      // message resolver, otherwise it mutates whichever Agent Turn happened
      // to be latest when the event arrived.
      if (
        eventType === 'context.compaction.started' ||
        eventType === 'context.compaction.completed' ||
        eventType === 'context.compaction.failed'
      ) {
        handleCompactionLifecycleEvent(ev);
        updateLastSequence(ev);
        return;
      }

      // turn.accepted 是服务端 Turn 身份的首个持久事实。必须在这里完成
      // optimisticTurn -> serverTurnId 迁移，不能只等待 POST continuation，
      // 否则快速失败终态可能先到达并被当作 staleTarget 丢弃。
      if (eventType === 'turn.accepted') {
        const confirmedTurnId =
          typeof anyEv.turnId === 'string' ? anyEv.turnId : null;
        const confirmedUserMessageId =
          typeof anyEv.userMessageId === 'string'
            ? anyEv.userMessageId
            : messageId;
        const optimisticTurn = confirmedUserMessageId
          ? turnsRef.current.find(
              (turn) => turn.userMessage.id === confirmedUserMessageId,
            )
          : undefined;

        if (
          confirmedTurnId &&
          confirmedUserMessageId &&
          optimisticTurn &&
          optimisticTurn.turnId !== confirmedTurnId
        ) {
          const optimisticTurnId = optimisticTurn.turnId;
          const confirmedTurns = confirmOptimisticTurn(
            turnsRef.current,
            optimisticTurnId,
            confirmedTurnId,
            confirmedUserMessageId,
          );
          turnsRef.current = confirmedTurns;
          setTurns((current) =>
            confirmOptimisticTurn(
              current,
              optimisticTurnId,
              confirmedTurnId,
              confirmedUserMessageId,
            ),
          );

          const migrateTurnKey = <T>(map: Map<string, T>) => {
            if (!map.has(optimisticTurnId)) return;
            const value = map.get(optimisticTurnId) as T;
            map.delete(optimisticTurnId);
            map.set(confirmedTurnId, value);
          };
          migrateTurnKey(pendingDeltaRef.current);
          migrateTurnKey(pendingThinkingRef.current);
          migrateTurnKey(duplicateDeltaReplayOffsetRef.current);
          if (completedTurnsRef.current.delete(optimisticTurnId))
            completedTurnsRef.current.add(confirmedTurnId);
          if (latestTurnIdRef.current === optimisticTurnId)
            latestTurnIdRef.current = confirmedTurnId;
        }

        if (confirmedTurnId && confirmedUserMessageId) {
          messageIdToTurnIdRef.current.set(
            confirmedUserMessageId,
            confirmedTurnId,
          );
        }
        updateLastSequence(ev);
        return;
      }

      if (
        eventType === 'metadata' &&
        messageId &&
        !messageIdToTurnIdRef.current.has(messageId)
      ) {
        const fanoutIndex = Number(anyEv.fanout_index ?? 0);
        const latestTurn = latestTurnIdRef.current
          ? turnsRef.current.find(
              (turn) => turn.turnId === latestTurnIdRef.current,
            )
          : undefined;
        if (
          (Number.isFinite(fanoutIndex) && fanoutIndex > 0) ||
          !canBindUnknownMetadataToTurn(latestTurn)
        ) {
          const sourceAgentId = String(
            anyEv.agent_id ||
              anyEv.agentId ||
              anyEv.source_id ||
              anyEv.sourceId ||
              agentId ||
              `agent-${fanoutIndex || 'replay'}`,
          );
          const previousTurn = turnsRef.current[turnsRef.current.length - 1];
          // TR-01/CU-02：恢复的 Turn 身份优先取 canonical 信封 turnId，
          // 缺失时退化为服务端事实（messageId）派生的确定性键，不本地随机生成。
          const envelopeTurnId =
            typeof anyEv.turnId === 'string' && anyEv.turnId.trim()
              ? anyEv.turnId
              : null;
          if (!envelopeTurnId) {
            recordEventProtocolError('missing-turn-id-on-metadata', {
              eventType,
              messageId,
              eventId: getCanonicalEventId(ev),
              sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
            });
          }
          const recoveredTurnId =
            envelopeTurnId ?? `turn:recovered:${messageId}`;
          const recoveredOccurredAtMs = getCanonicalOccurredAtMs(ev) ?? 0;
          const recoveredTurn: ChatTurn = {
            turnId: recoveredTurnId,
            source: {
              sourceId: sourceAgentId,
              sourceType: String(
                anyEv.source_type || anyEv.sourceType || 'agent',
              ) as ChatSource['sourceType'],
              displayName: String(
                anyEv.source_name || anyEv.sourceName || sourceAgentId,
              ),
              avatarEmoji: '🤖' as const,
              avatarColor: stringToColor(sourceAgentId),
              avatarUrl:
                String(anyEv.avatar_url || anyEv.avatarUrl || '') || undefined,
            },
            userMessage: {
              id: `umsg:recovered:${messageId}`,
              text: previousTurn?.userMessage.text ?? '',
              timestamp: recoveredOccurredAtMs,
              status: 'success',
            },
            assistant: createAssistant(
              `amsg:recovered:${messageId}`,
              'structured',
              'thinking',
              true,
            ),
          };
          messageIdToTurnIdRef.current.set(messageId, recoveredTurnId);
          activeMessageIdsRef.current.add(messageId);
          latestTurnIdRef.current = recoveredTurnId;
          turnsRef.current = [...turnsRef.current, recoveredTurn];
          setTurns((prev) => [...prev, recoveredTurn]);
          setLoading(true);
        }
      }

      // ADR-057: When the POST response pre-populates messageIdToTurnId
      // (via post.returned.afterApply) before metadata arrives, the metadata
      // handler above is skipped. Restore active tracking here.
      if (
        eventType === 'metadata' &&
        messageId &&
        messageIdToTurnIdRef.current.has(messageId) &&
        !activeMessageIdsRef.current.has(messageId)
      ) {
        const mappedTurnId = messageIdToTurnIdRef.current.get(messageId);
        if (!mappedTurnId) return;
        const mappedTurn = turnsRef.current.find(
          (t) => t.turnId === mappedTurnId,
        );
        if (
          mappedTurn?.assistant &&
          mappedTurn.assistant.status !== 'success'
        ) {
          activeMessageIdsRef.current.add(messageId);
          setLoading(true);
        }
      }

      let targetTurnId = resolveEventTurnId(ev);
      if (messageId && targetTurnId) {
        messageIdToTurnIdRef.current.set(messageId, targetTurnId);
      }

      if (eventType === 'session.closed') {
        recordPerfEvent('chat.event.sessionClosed', {
          sessionId: sseSessionIdRef.current,
          sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
        });
        updateLastSequence(ev);
        setLoading(false);
        return;
      }
      if (eventType === 'steering.injected') {
        const steeringId =
          typeof anyEv.steeringId === 'string' ? anyEv.steeringId : undefined;
        const injectedAt =
          typeof anyEv.injectedAt === 'number'
            ? anyEv.injectedAt
            : (getCanonicalOccurredAtMs(ev) ?? 0);
        const injectedRound =
          typeof anyEv.round === 'number' ? anyEv.round : undefined;
        const messageChars =
          typeof anyEv.messageChars === 'number'
            ? anyEv.messageChars
            : undefined;
        if (steeringId) {
          markSteeringInjected({
            steeringId,
            sessionId:
              typeof anyEv.sessionId === 'string' ? anyEv.sessionId : undefined,
            agentId:
              typeof anyEv.agentId === 'string' ? anyEv.agentId : undefined,
            injectedRound,
            messageChars,
            injectedAt,
          });
        }
        updateLastSequence(ev);
        return;
      }
      if (eventType === 'steering.created') {
        updateLastSequence(ev);
        return;
      }
      // P0#1: 审批决议是会话级事实，可能不与卡片所在 Turn 绑定。
      // 全局扫描所有 Turn，按 approvalId 匹配并更新审批卡片。
      if (eventType === 'approval.resolved') {
        const approvalId =
          typeof anyEv.approvalId === 'string' ? anyEv.approvalId : '';
        if (approvalId) {
          const updateApprovalCards = (current: ChatTurn[]): ChatTurn[] =>
            current.map((turn) => {
              const card = turn.assistant.approvalCard;
              if (!card || card.approvalId !== approvalId) return turn;
              const resolvedStatus: ApprovalCardData['status'] =
                String(anyEv.status) === 'denied' ||
                String(anyEv.decision) === 'deny'
                  ? 'denied'
                  : 'approved';
              const resolvedDecision =
                anyEv.decision === 'allow_once' ||
                anyEv.decision === 'always_allow' ||
                anyEv.decision === 'deny'
                  ? (anyEv.decision as ApprovalCardData['decision'])
                  : card.decision;
              return {
                ...turn,
                assistant: {
                  ...turn.assistant,
                  approvalCard: {
                    ...card,
                    status: resolvedStatus,
                    decision: resolvedDecision,
                    reason:
                      typeof anyEv.reason === 'string'
                        ? anyEv.reason
                        : card.reason,
                  },
                },
              };
            });
          turnsRef.current = updateApprovalCards(turnsRef.current);
          setTurns(updateApprovalCards);
        }
        updateLastSequence(ev);
        return;
      }
      // P1#5: 计划决议同样是会话级事实（approve_and_build / manual / keep_planning），
      // 全局扫描所有 Turn，按 planId 匹配并更新计划卡片为终态。
      if (eventType === 'plan.finalized') {
        const planId = typeof anyEv.planId === 'string' ? anyEv.planId : '';
        if (planId) {
          const updatePlanCards = (current: ChatTurn[]): ChatTurn[] =>
            current.map((turn) => {
              const card = turn.assistant.planCard;
              if (!card || card.planId !== planId) return turn;
              const finalizedDecision: PlanCardData['decision'] =
                anyEv.decision === 'approve_and_build' ||
                anyEv.decision === 'manual' ||
                anyEv.decision === 'keep_planning'
                  ? (anyEv.decision as PlanCardData['decision'])
                  : card.decision;
              const finalizedSteps: PlanStepData[] =
                Array.isArray(anyEv.steps) && anyEv.steps.length > 0
                  ? (anyEv.steps as PlanStepData[])
                      .map((step, index) => {
                        if (!step || typeof step !== 'object') return null;
                        const record = step as unknown as Record<
                          string,
                          unknown
                        >;
                        const stepId =
                          typeof record.id === 'string' && record.id.trim()
                            ? record.id.trim()
                            : `step-${index + 1}`;
                        return {
                          id: stepId,
                          title:
                            typeof record.title === 'string'
                              ? record.title
                              : '',
                          description:
                            typeof record.description === 'string'
                              ? record.description
                              : undefined,
                        } as PlanStepData;
                      })
                      .filter(
                        (step): step is PlanStepData =>
                          step !== null && step.title.trim().length > 0,
                      )
                  : card.steps;
              return {
                ...turn,
                assistant: {
                  ...turn.assistant,
                  planCard: {
                    ...card,
                    status: 'finalized',
                    decision: finalizedDecision,
                    steps: finalizedSteps,
                    decidedAt:
                      typeof anyEv.decidedAt === 'string'
                        ? anyEv.decidedAt
                        : card.decidedAt,
                  },
                },
              };
            });
          turnsRef.current = updatePlanCards(turnsRef.current);
          setTurns(updatePlanCards);
        }
        updateLastSequence(ev);
        return;
      }
      if (!targetTurnId) {
        if (shouldAdvanceSequenceForSessionEvent(eventType, false))
          updateLastSequence(ev);
        recordPerfEvent(
          'chat.event.unmapped',
          {
            eventType,
            messageId,
            sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
          },
          { throttleMs: 500 },
        );
        // 埋点：terminal 事件找不到目标 turn 是消息被吞的常见原因
        if (
          eventType === 'turn.completed' ||
          eventType === 'turn.failed' ||
          eventType === 'turn.cancelled'
        ) {
          console.warn(
            '[Pudding Chat] terminal event unmapped (no targetTurnId) — 消息可能被吞',
            {
              eventType,
              messageId,
              sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
              currentTurns: turnsRef.current.length,
              latestTurnId: latestTurnIdRef.current,
              activeMessageIds: Array.from(activeMessageIdsRef.current),
              messageIdToTurnId: Object.fromEntries(
                messageIdToTurnIdRef.current,
              ),
            },
          );
          logChatDiag('event.terminal.unmapped', {
            eventType,
            messageId,
            sessionId: sseSessionIdRef.current,
            selectedSessionId: selectedSessionIdRef.current,
            sessionIdRef: sessionIdRef.current,
            sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
            currentTurns: turnsRef.current.length,
            latestTurnId: latestTurnIdRef.current,
            activeMessageIds: Array.from(activeMessageIdsRef.current),
            messageIdToTurnId: Object.fromEntries(messageIdToTurnIdRef.current),
          });
        }
        return;
      }
      let targetTurnExists = turnsRef.current.some(
        (turn) => turn.turnId === targetTurnId,
      );
      if (!targetTurnExists) {
        recordPerfEvent(
          'chat.event.staleTarget',
          {
            eventType,
            messageId,
            targetTurnId,
            sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
          },
          { throttleMs: 500 },
        );
        // terminal 事件的目标 turn 不存在 → 尝试恢复
        if (
          eventType === 'turn.completed' ||
          eventType === 'turn.failed' ||
          eventType === 'turn.cancelled'
        ) {
          // 尝试 1: 通过 messageId→turnId 映射查找
          let recoveryTurnId: string | null = null;
          if (messageId && messageIdToTurnIdRef.current.has(messageId)) {
            recoveryTurnId =
              messageIdToTurnIdRef.current.get(messageId) ?? null;
          }
          // 尝试 2: 查找当前正在流式输出的 turn
          if (!recoveryTurnId) {
            const streamingTurn = turnsRef.current.find(
              (t) => t.assistant.isStreaming,
            );
            if (streamingTurn) {
              recoveryTurnId = streamingTurn.turnId;
            }
          }
          if (recoveryTurnId) {
            console.warn(
              '[Pudding Chat] terminal event staleTarget — recovered',
              {
                eventType,
                messageId,
                oldTarget: targetTurnId,
                recoveryTurnId,
                recoveryMethod:
                  messageId && messageIdToTurnIdRef.current.has(messageId)
                    ? 'messageIdMap'
                    : 'isStreaming',
              },
            );
            logChatDiag('event.terminal.recovered', {
              eventType,
              messageId,
              oldTarget: targetTurnId,
              recoveryTurnId,
              sessionId: sseSessionIdRef.current,
            });
            targetTurnId = recoveryTurnId;
            targetTurnExists = true;
            // 继续后续处理（fall through）
          } else {
            console.warn(
              '[Pudding Chat] terminal event staleTarget — unrecoverable (消息被吞)',
              {
                eventType,
                messageId,
                targetTurnId,
                sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
                currentTurns: turnsRef.current.length,
                streamingTurns: turnsRef.current.filter(
                  (t) => t.assistant.isStreaming,
                ).length,
              },
            );
            logChatDiag('event.terminal.staleTarget', {
              eventType,
              messageId,
              targetTurnId,
              sessionId: sseSessionIdRef.current,
              selectedSessionId: selectedSessionIdRef.current,
              sessionIdRef: sessionIdRef.current,
              sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
              currentTurns: turnsRef.current.length,
            });
            return;
          }
        } else {
          return;
        }
      }

      if (ev.type === 'usage.recorded' && ev.usage) setLatestUsage(ev.usage);
      if (ev.type === 'turn.completed' && ev.usage) setLatestUsage(ev.usage);

      // T-CACHE-008: Accumulate cache hit/miss for the main session
      if (ev.type === 'turn.completed' && ev.usage) {
        const hitTokens = ev.usage.promptCacheHitTokens || 0;
        const missTokens = ev.usage.promptCacheMissTokens || 0;
        if (hitTokens > 0 || missTokens > 0) {
          const currentStreamSessionId = sseSessionIdRef.current;
          if (
            currentStreamSessionId === mainSessionId ||
            (!mainSessionId && currentStreamSessionId === selectedSessionId)
          ) {
            setSessionCacheHitTokens((prev) => prev + hitTokens);
            setSessionCacheMissTokens((prev) => prev + missTokens);
          }
        }
      }

      // T-102: 持久 SSE 已合并为单一通道 — 不再过滤 delta/thinking/tool_call/tool_result。
      // 所有事件统一路由到 mapEventToTurn 处理。

      // ADR-InkBloom: 终端事件前 flush 所有 pending delta，不丢最后一段
      if (
        eventType === 'session.closed' ||
        eventType === 'turn.completed' ||
        eventType === 'turn.failed' ||
        eventType === 'turn.cancelled'
      ) {
        flushPendingDeltas();
        flushPendingThinking();
      }

      // T-102: 终端事件管理 loading 状态
      if (
        eventType === 'turn.completed' ||
        eventType === 'turn.failed' ||
        eventType === 'turn.cancelled'
      ) {
        logChatDiag('event.terminal.apply', {
          eventType,
          messageId,
          targetTurnId,
          sessionId: sseSessionIdRef.current,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          activeMessageCountBeforeDelete: activeMessageIdsRef.current.size,
          turnCount: turnsRef.current.length,
        });
        // Acceptance tracks the client/user messageId, while output and terminal
        // events use the assistant messageId. Clear every active message bound
        // to this Turn instead of assuming both identities are equal.
        removeTrackedActiveMessageIdsForTurn(
          activeMessageIdsRef.current,
          messageIdToTurnIdRef.current,
          targetTurnId,
          messageId,
        );
        clearWorkingAgentsForMessage(messageId, targetTurnId);
        const hasActiveMessages = pruneTrackedActiveMessages('terminal-event');
        const hasOtherActiveTurn = turnsRef.current.some(
          (turn) => turn.turnId !== targetTurnId && isActiveAssistantTurn(turn),
        );
        if (!messageId || (!hasActiveMessages && !hasOtherActiveTurn)) {
          setLoading(false);
        }
      }
      mapEventToTurn(targetTurnId, ev);
      updateLastSequence(ev);

      const streamStart = messageId
        ? streamStartAtRef.current.get(messageId)
        : undefined;
      recordPerfEvent(
        'chat.event.apply',
        {
          eventType,
          count,
          messageId,
          targetTurnId,
          sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
          deltaChars:
            typeof (ev as { delta?: unknown }).delta === 'string'
              ? (ev as { delta: string }).delta.length
              : undefined,
          applyMs: Math.round(performance.now() - applyStart),
          streamElapsedMs: streamStart
            ? Math.round(performance.now() - streamStart)
            : undefined,
        },
        eventType === 'message.content.appended' ||
          eventType === 'message.thinking_summary.appended'
          ? { throttleMs: 500 }
          : undefined,
      );
    },
    [
      agentId,
      mapEventToTurn,
      resolveEventTurnId,
      updateLastSequence,
      flushPendingDeltas,
      flushPendingThinking,
      pruneTrackedActiveMessages,
      markSteeringInjected,
      clearWorkingAgentsForMessage,
      mainSessionId,
      selectedSessionId,
      appendChatInteractionRuntimeEvent,
      handleCompactionLifecycleEvent,
    ],
  );

  return {
    workingAgentIds,
    subAgentRuns,
    setSubAgentRuns,
    subAgentCards,
    latestUsage,
    setLatestUsage,
    sessionCacheHitTokens,
    setSessionCacheHitTokens,
    sessionCacheMissTokens,
    setSessionCacheMissTokens,
    hydrateSessionReplayRef,
    duplicateDeltaReplayOffsetRef,
    eventCountsRef,
    streamStartAtRef,
    messageIdToAgentIdsRef,
    sessionIdToAgentIdsRef,
    setAgentIdsWorking,
    reconcileSessionWorkingAgents,
    resetStreamCursorForSessionChange,
    pruneTrackedActiveMessages,
    applySessionEvent,
  };
}

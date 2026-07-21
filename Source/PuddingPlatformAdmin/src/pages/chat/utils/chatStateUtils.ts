// ── 聊天状态纯函数 ──────────────────────────────────────
// 从 useChatState.ts 提取的模块级纯函数、常量和类型。
// 这些函数不依赖 React hooks，仅依赖参数和 import。
// ADR-062 P0-1
// ─────────────────────────────────────────────────────────────
import dayjs from 'dayjs';
import type {
  AdminChatStreamEvent,
  AgentMessageQueueItem,
  ContextCompactionResult,
  EnsureMainSessionRequest,
  SessionRecord,
  TokenUsageDto,
  WorkspaceAgentDto,
  WorkspaceWithPermDto,
} from '@/services/platform/api';
import type {
  AssistantStatus,
  ChatTurn,
  SessionGroup,
  SessionListItem,
  SubAgentCardMap,
} from '../types';
import { assistantStatusLabel } from '../types';
import {
  ACTIVE_SESSION_REPLAY_POLL_INTERVAL_MS,
  CHAT_INTERACTION_RUNTIME_EVENT_TYPES,
  type ChatInteractionQueueItem,
  type ChatInteractionRuntimeEvent,
  type ChatRouteSelection,
  IDLE_SESSION_REPLAY_POLL_INTERVAL_MS,
  SESSION_EVENT_PAGE_SIZE,
  SSE_HEALTHY_REPLAY_SUPPRESSION_MS,
} from '../types/chatStateTypes';

// ── 内部辅助函数 ──────────────────────────────────────────

export const getStringValue = (value: unknown): string | undefined =>
  typeof value === 'string' && value.trim() ? value.trim() : undefined;

export function parseObjectJson(
  value: unknown,
): Record<string, unknown> | null {
  if (!value || typeof value !== 'string' || !value.trim()) return null;
  try {
    const parsed = JSON.parse(value) as unknown;
    return parsed && typeof parsed === 'object'
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

export function findMatchingRecentUserTurn(
  loadedTurns: ChatTurn[],
  currentTurn: ChatTurn,
): ChatTurn | undefined {
  const text = currentTurn.userMessage.text.trim();
  if (!text) return loadedTurns[loadedTurns.length - 1];
  const lowerBound = currentTurn.userMessage.timestamp - 60_000;
  return loadedTurns.find(
    (turn) =>
      turn.userMessage.text.trim() === text &&
      turn.userMessage.timestamp >= lowerBound,
  );
}

export function countCompletedAssistantTurns(turns: ChatTurn[]): number {
  return turns.filter((turn) => turn.assistant.answerMarkdown.trim().length > 0)
    .length;
}

export function tryExtractDelta(ev: {
  data?: string;
  delta?: string;
}): string | null {
  if (ev.delta) return ev.delta;
  if (ev.data) {
    try {
      const d = JSON.parse(ev.data);
      return d?.delta ?? null;
    } catch {
      return null;
    }
  }
  return null;
}

export const createId = () =>
  `msg-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
export const COMPACT_COMMAND = '/compact';
export const COMPACTION_TURN_PREFIX = 'compaction:';

export function compactionTurnId(compactionId: string): string {
  return `${COMPACTION_TURN_PREFIX}${compactionId}`;
}

export const createAssistant = (
  id: string,
  renderMode: 'legacy' | 'structured',
  status: AssistantStatus,
  isStreaming: boolean,
): ChatTurn['assistant'] => ({
  id,
  status,
  timelineItems: [],
  answerMarkdown: '',
  isStreaming,
  renderMode,
});

export const normalizeUsage = (
  usage?: TokenUsageDto,
): TokenUsageDto | undefined =>
  usage
    ? {
        promptTokens: usage.promptTokens,
        completionTokens: usage.completionTokens,
        totalTokens: usage.totalTokens,
        contextWindowTokens: usage.contextWindowTokens,
        promptCacheHitTokens: usage.promptCacheHitTokens,
        promptCacheMissTokens: usage.promptCacheMissTokens,
      }
    : undefined;

export const isReasoningStep = (status?: string) => {
  const key = (status || '').toLowerCase();
  return key.startsWith('thinking') || key.startsWith('reasoning');
};

export const getStepTone = (
  status?: string,
): 'executing' | 'success' | 'error' => {
  const key = (status || '').toLowerCase();
  if (key.includes('error') || key.includes('fail') || key.includes('cancel'))
    return 'error';
  if (
    key.includes('done') ||
    key.includes('success') ||
    key.includes('complete')
  )
    return 'success';
  if (key.includes('tool_call')) return 'executing';
  return 'executing';
};

export const getStepMessage = (payload: {
  message?: string;
  [key: string]: unknown;
}) => {
  if (typeof payload.message === 'string' && payload.message.trim())
    return payload.message;
  const fallback = Object.entries(payload)
    .filter(
      ([k, v]) =>
        k !== 'status' &&
        k !== 'type' &&
        v !== undefined &&
        v !== null &&
        v !== '',
    )
    .map(([k, v]) => `${k}: ${typeof v === 'string' ? v : JSON.stringify(v)}`)
    .join(' | ');
  return fallback || '执行步骤更新';
};

export const formatTime = (ts: number) => {
  const diff = dayjs().diff(dayjs(ts), 'minute');
  if (diff < 1) return '刚刚';
  if (diff < 60) return `${diff}分钟前`;
  return dayjs(ts).format('MM-DD HH:mm');
};

// ── 导出纯函数 ────────────────────────────────────────────

export function confirmOptimisticTurn(
  turns: ChatTurn[],
  optimisticTurnId: string,
  confirmedTurnId: string,
  confirmedMessageId: string,
): ChatTurn[] {
  return turns.map((turn) =>
    turn.turnId !== optimisticTurnId
      ? turn
      : {
          ...turn,
          turnId: confirmedTurnId,
          userMessage: {
            ...turn.userMessage,
            id: confirmedMessageId,
            status: 'success' as const,
          },
        },
  );
}

export function parseSessionEventTimestampMs(
  value: unknown,
  fallback = Date.now(),
): number {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value !== 'string' || !value.trim()) return fallback;
  const numeric = Number(value);
  if (Number.isFinite(numeric)) return numeric;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

export const stringToColor = (str: string) => {
  let hash = 0;
  for (let i = 0; i < str.length; i++)
    hash = str.charCodeAt(i) + ((hash << 5) - hash);
  const colors = [
    'var(--avatar-0)',
    'var(--avatar-1)',
    'var(--avatar-2)',
    'var(--avatar-3)',
    'var(--avatar-4)',
    'var(--avatar-5)',
    'var(--avatar-6)',
    'var(--avatar-7)',
    'var(--avatar-8)',
    'var(--avatar-9)',
  ];
  return colors[Math.abs(hash) % colors.length];
};

export const getAgentName = (a: WorkspaceAgentDto) =>
  a.displayName || a.name || 'Agent';

export const formatCompactSuccessMessage = (
  result: Pick<
    ContextCompactionResult,
    'beforeTokens' | 'afterTokens' | 'compactedMessageCount'
  > &
    Partial<ContextCompactionResult>,
  successor?: {
    newSessionId?: string | null;
    newSessionTitle?: string | null;
  },
) => {
  const diagnostics = result.diagnostics;
  const tokenLine =
    result.beforeTokens > 0
      ? `\n\nToken 估算：${result.beforeTokens} → ${result.afterTokens}`
      : '';
  const hasSummary =
    diagnostics === undefined ||
    Boolean(result.summaryMessageId) ||
    Boolean(result.summaryPreview?.trim()) ||
    diagnostics.summaryCharacterCount > 0;
  const headline =
    result.compactedMessageCount > 0
      ? `上下文已压缩，覆盖 ${result.compactedMessageCount} 条历史消息。`
      : hasSummary
        ? '已生成当前会话摘要。'
        : '当前没有可压缩的会话内容。';

  if (!diagnostics) return `${headline}${tokenLine}`;

  const nextSessionId =
    successor?.newSessionId ?? diagnostics.newSessionId ?? null;
  const lines = [
    `${headline}${tokenLine}`,
    '',
    '### 压缩诊断',
    `- Compaction ID：\`${diagnostics.compactionId}\``,
    `- 旧 Session：\`${diagnostics.previousSessionId}\``,
    diagnostics.previousLastMessageId
      ? `- 最后消息：\`${diagnostics.previousLastMessageId}\``
      : null,
    `- 旧 Session 大小：${diagnostics.beforeTokens} tokens / ${diagnostics.activeMessageCountBefore} messages`,
    `- 摘要大小：${diagnostics.summaryCharacterCount} chars / ${diagnostics.summaryEstimatedTokens} tokens`,
    diagnostics.summaryGenerator
      ? `- 摘要生成器：\`${diagnostics.summaryGenerator}\``
      : null,
    nextSessionId ? `- 新 Session：\`${nextSessionId}\`` : null,
    `- 完成时间：\`${diagnostics.completedAtUtc}\``,
  ].filter((line): line is string => line !== null);
  return lines.join('\n');
};

export function removeInjectedSteeringQueueItem(
  queue: ChatInteractionQueueItem[],
  steeringId: string,
): ChatInteractionQueueItem[] {
  return queue.filter(
    (item) =>
      item.steeringId !== steeringId || item.status !== 'steering_injected',
  );
}

export function resolveSubAgentTaskSummary(
  event: Record<string, unknown>,
): string {
  return (
    getStringValue(event.task_summary) ??
    getStringValue(event.task) ??
    getStringValue(event.taskSummary) ??
    getStringValue(event.template) ??
    '处理中...'
  );
}

export function toChatInteractionQueueItem(
  item: AgentMessageQueueItem,
): ChatInteractionQueueItem {
  return {
    id: item.deliveryId,
    text: item.content,
    createdAt: item.createdAt,
    status: item.status,
    source: 'backend_message_queue',
    error: item.lastError,
    metadata: {
      deliveryId: item.deliveryId,
      messageId: item.messageId,
      priority: String(item.priority),
      attemptCount: String(item.attemptCount),
      roomId: item.roomId ?? '',
    },
  };
}

export function resolveSubAgentTerminalOutput(
  event: Record<string, unknown>,
): string {
  return (
    getStringValue(event.result_summary) ??
    getStringValue(event.resultSummary) ??
    getStringValue(event.reply) ??
    getStringValue(event.error) ??
    ''
  );
}

export function toChatInteractionRuntimeEvent(
  event: AdminChatStreamEvent,
  agentId?: string,
): ChatInteractionRuntimeEvent | null {
  if (!agentId || !CHAT_INTERACTION_RUNTIME_EVENT_TYPES.has(event.type))
    return null;
  const anyEvent = event as Record<string, unknown>;
  const status = getStringValue(anyEvent.status)?.toLowerCase();
  if (!status) return null;

  const now = Date.now();
  if (event.type === 'voice_capture_status') {
    return {
      type: event.type,
      agentId,
      status,
      sessionId:
        getStringValue(anyEvent.voiceSessionId) ??
        getStringValue(anyEvent.sessionId),
      now,
    };
  }
  if (event.type === 'voice_playback_status') {
    return {
      type: event.type,
      agentId,
      status,
      deliveryId:
        getStringValue(anyEvent.deliveryId) ??
        getStringValue(anyEvent.voiceSessionId) ??
        getStringValue(anyEvent.sessionId),
      now,
    };
  }
  if (event.type === 'camera_capture_status') {
    return {
      type: event.type,
      agentId,
      status,
      sessionId:
        getStringValue(anyEvent.cameraSessionId) ??
        getStringValue(anyEvent.sessionId),
      artifactId:
        getStringValue(anyEvent.artifactId) ??
        getStringValue(anyEvent.visionArtifactId),
      now,
    };
  }
  if (event.type === 'visual_reasoning_status') {
    return {
      type: event.type,
      agentId,
      status,
      sessionId:
        getStringValue(anyEvent.visionSessionId) ??
        getStringValue(anyEvent.sessionId),
      now,
    };
  }
  return null;
}

export function getChatRouteSelectionFromSearch(
  search: string,
): ChatRouteSelection {
  const params = new URLSearchParams(search);
  const workspaceId = params.get('workspaceId')?.trim() || undefined;
  const agentId = params.get('agentId')?.trim() || undefined;
  const sessionId = params.get('sessionId')?.trim() || undefined;
  return {
    ...(workspaceId ? { workspaceId } : {}),
    ...(agentId ? { agentId } : {}),
    ...(sessionId ? { sessionId } : {}),
  };
}

export function resolveInitialWorkspaceId(
  workspaces: WorkspaceWithPermDto[],
  requestedWorkspaceId?: string,
): string | undefined {
  if (
    requestedWorkspaceId &&
    workspaces.some(
      (workspace) => workspace.workspaceId === requestedWorkspaceId,
    )
  ) {
    return requestedWorkspaceId;
  }
  return (
    workspaces.find(
      (workspace) =>
        workspace.workspaceId === 'default' &&
        workspace.isEnabled &&
        !workspace.isFrozen,
    )?.workspaceId ??
    workspaces.find((workspace) => workspace.workspaceId === 'default')
      ?.workspaceId ??
    workspaces.find((workspace) => workspace.isEnabled && !workspace.isFrozen)
      ?.workspaceId ??
    workspaces[0]?.workspaceId
  );
}

export function resolveInitialAgentId(
  agents: WorkspaceAgentDto[],
  requestedAgentId?: string,
): string | undefined {
  if (
    requestedAgentId &&
    agents.some((agent) => agent.agentId === requestedAgentId)
  )
    return requestedAgentId;
  return (
    agents.find((agent) => agent.isEnabled && !agent.isFrozen)?.agentId ??
    agents.find((agent) => agent.isEnabled)?.agentId ??
    agents[0]?.agentId
  );
}

export function buildAgentMainSessionRequest(
  workspaceId: string | undefined,
  agent: WorkspaceAgentDto | undefined,
): EnsureMainSessionRequest | null {
  if (!workspaceId || !agent?.agentId) return null;
  return {
    workspaceId,
    principalKind: 'agent',
    principalId: agent.agentId,
    agentTemplateId: agent.sourceTemplateId || `global:${agent.agentId}`,
    title: getAgentName(agent),
  };
}

export function toSessionListItem(
  session: SessionRecord,
  fallbackTitle = '对话',
): SessionListItem {
  return {
    sessionId: session.sessionId,
    title:
      session.title?.trim() ||
      session.agentTemplateId?.replace('global:', '') ||
      fallbackTitle,
    timestamp:
      new Date(session.lastActiveAt || session.createdAt).getTime() ||
      Date.now(),
    agentTemplateId: session.agentTemplateId,
    channelId: session.channelId,
    sessionRole: session.sessionRole,
    principalKind: session.principalKind,
    principalId: session.principalId,
  };
}

export const groupSessions = (raw: SessionListItem[]): SessionGroup[] => {
  const now = dayjs();
  const groups: Record<string, SessionListItem[]> = {};
  for (const s of raw) {
    const d = dayjs(s.timestamp);
    let key: string;
    if (d.isSame(now, 'day')) key = '今天';
    else if (d.isSame(now.subtract(1, 'day'), 'day')) key = '昨天';
    else if (d.isAfter(now.subtract(7, 'day'))) key = '本周';
    else key = '更早';
    const items = groups[key] ?? [];
    items.push(s);
    groups[key] = items;
  }
  return ['今天', '昨天', '本周', '更早']
    .filter((k) => groups[k]?.length)
    .map((label) => ({
      label,
      items: (groups[label] ?? []).sort((a, b) => b.timestamp - a.timestamp),
    }));
};

export function shouldAdvanceSequenceForSessionEvent(
  type: string,
  hasTargetTurn: boolean,
): boolean {
  if (CHAT_INTERACTION_RUNTIME_EVENT_TYPES.has(type)) return true;
  if (type === 'steering.created' || type === 'steering.injected') return true;
  return hasTargetTurn;
}

export function shouldReplayEventsAfterHistory(turns: ChatTurn[]): boolean {
  const latest = turns[turns.length - 1];
  if (!latest) return true;
  const assistant = latest.assistant;
  return (
    assistant.isStreaming ||
    assistant.status === 'thinking' ||
    assistant.status === 'executing' ||
    assistant.status === 'streaming' ||
    assistant.answerMarkdown.trim().length === 0
  );
}

export function isActiveAssistantTurn(turn: ChatTurn): boolean {
  return (
    turn.assistant.isStreaming ||
    turn.assistant.status === 'thinking' ||
    turn.assistant.status === 'executing' ||
    turn.assistant.status === 'streaming'
  );
}

export function hasBlockingActiveTurn(
  turns: ChatTurn[],
  activeMessageIds: Iterable<string>,
  messageIdToTurnId: ReadonlyMap<string, string>,
): boolean {
  return (
    turns.some(isActiveAssistantTurn) ||
    hasTrackedActiveSessionMessages(activeMessageIds, messageIdToTurnId, turns)
  );
}

export function getTrackedActiveMessageIds(
  activeMessageIds: Iterable<string>,
  messageIdToTurnId: ReadonlyMap<string, string>,
  turns: ChatTurn[],
): string[] {
  const activeTurnIds = new Set(
    turns.filter(isActiveAssistantTurn).map((turn) => turn.turnId),
  );
  const tracked: string[] = [];
  for (const messageId of activeMessageIds) {
    const turnId = messageIdToTurnId.get(messageId);
    if (turnId && activeTurnIds.has(turnId)) {
      tracked.push(messageId);
    }
  }
  return tracked;
}

export function hasTrackedActiveSessionMessages(
  activeMessageIds: Iterable<string>,
  messageIdToTurnId: ReadonlyMap<string, string>,
  turns: ChatTurn[],
): boolean {
  return (
    getTrackedActiveMessageIds(activeMessageIds, messageIdToTurnId, turns)
      .length > 0
  );
}

export function removeTrackedActiveMessageIdsForTurn(
  activeMessageIds: Set<string>,
  messageIdToTurnId: ReadonlyMap<string, string>,
  turnId: string,
  terminalMessageId?: string | null,
): number {
  let removed = 0;
  for (const [trackedMessageId, trackedTurnId] of messageIdToTurnId) {
    if (trackedTurnId !== turnId) continue;
    if (activeMessageIds.delete(trackedMessageId)) removed++;
  }
  if (terminalMessageId && activeMessageIds.delete(terminalMessageId))
    removed++;
  return removed;
}

export function getHistoryReconcileBlockReason(
  currentTurns: ChatTurn[],
  loadedTurns: ChatTurn[],
): string | null {
  const currentLatest = currentTurns[currentTurns.length - 1];
  if (!currentLatest) return null;

  const currentHasActiveTurn = currentTurns.some(isActiveAssistantTurn);
  const loadedCurrentLatest = findMatchingRecentUserTurn(
    loadedTurns,
    currentLatest,
  );
  const loadedHasCurrentLatest = loadedCurrentLatest != null;

  if (currentHasActiveTurn && !loadedHasCurrentLatest) {
    return 'active-turn-not-materialized';
  }

  const currentAnswer = currentLatest.assistant.answerMarkdown?.trim() ?? '';
  const loadedAnswer =
    loadedCurrentLatest?.assistant.answerMarkdown?.trim() ?? '';
  if (
    currentLatest.assistant.status === 'success' &&
    currentAnswer.length > 0 &&
    loadedAnswer !== currentAnswer
  ) {
    return 'completed-turn-not-materialized';
  }

  if (currentTurns.length > loadedTurns.length && !loadedHasCurrentLatest) {
    return 'history-older-than-visible-turns';
  }

  if (currentTurns.length > loadedTurns.length && loadedHasCurrentLatest) {
    return 'frontend-ahead-of-server';
  }

  return null;
}

export const HISTORICAL_REPLAY_TERMINAL_EVENTS = new Set([
  'done',
  'error',
  'cancelled',
  'session.closed',
  'context.compaction.completed',
  'context.compaction.failed',
]);

export function shouldHydrateSessionEventReplay(
  events: Array<{ type: string }>,
): boolean {
  return events.some((event) =>
    HISTORICAL_REPLAY_TERMINAL_EVENTS.has(event.type),
  );
}

export function resolveTerminalAssistantMarkdown(
  currentMarkdown: string,
  terminalReply?: string | null,
): string {
  const current = currentMarkdown ?? '';
  const reply = terminalReply ?? '';
  if (!current) return reply || '(无回复)';
  if (!reply) return current;
  if (current.includes(reply)) return current;
  if (reply.includes(current)) return reply;
  if (reply.startsWith(current)) return reply;
  const maxOverlap = Math.min(current.length, reply.length);
  for (let n = maxOverlap; n > 0; n--) {
    if (current.endsWith(reply.slice(0, n))) {
      return current + reply.slice(n);
    }
  }
  const separator =
    current.endsWith('\n') || reply.startsWith('\n') ? '' : '\n\n';
  return `${current}${separator}${reply}`;
}

export function applyBufferedDeltaToTurn(
  turn: ChatTurn,
  delta: string,
): ChatTurn {
  if (!delta) return turn;
  return {
    ...turn,
    assistant: {
      ...turn.assistant,
      renderMode: 'structured' as const,
      answerMarkdown: turn.assistant.answerMarkdown + delta,
    },
  };
}

export function shouldResetSequenceForSessionChange(
  previousSessionId?: string | null,
  nextSessionId?: string | null,
): boolean {
  return Boolean(
    previousSessionId && nextSessionId && previousSessionId !== nextSessionId,
  );
}

export function buildSessionEventReplayUrl(
  sessionId: string,
  from: number,
  limit: number,
): string {
  const afterExclusive = Math.max(0, from - 1);
  return `/api/sessions/${encodeURIComponent(sessionId)}/events?from=${encodeURIComponent(String(afterExclusive))}&limit=${encodeURIComponent(String(limit))}`;
}

export function getSessionEventSequenceNum(item: unknown): number | null {
  if (!item || typeof item !== 'object') return null;
  const obj = item as Record<string, unknown>;
  const direct = Number(
    obj.sequence ?? obj.Sequence ?? obj.sequenceNum ?? obj.SequenceNum,
  );
  if (Number.isFinite(direct)) return direct;

  const payload =
    typeof obj.payload === 'object' && obj.payload
      ? (obj.payload as Record<string, unknown>)
      : typeof obj.Payload === 'object' && obj.Payload
        ? (obj.Payload as Record<string, unknown>)
        : parseObjectJson(obj.payload ?? obj.Payload);
  const payloadSeq = Number(payload?.sequenceNum ?? payload?.SequenceNum);
  if (Number.isFinite(payloadSeq)) return payloadSeq;

  const dataJson = parseObjectJson(obj.dataJson ?? obj.DataJson);
  const dataJsonSeq = Number(dataJson?.sequenceNum ?? dataJson?.SequenceNum);
  if (Number.isFinite(dataJsonSeq)) return dataJsonSeq;

  const data =
    typeof obj.data === 'object' && obj.data
      ? (obj.data as Record<string, unknown>)
      : typeof obj.Data === 'object' && obj.Data
        ? (obj.Data as Record<string, unknown>)
        : parseObjectJson(obj.data ?? obj.Data);
  const dataSeq = Number(data?.sequenceNum ?? data?.SequenceNum);
  return Number.isFinite(dataSeq) ? dataSeq : null;
}

export function resolveSessionReplayCursorSequence(page: {
  events?: unknown[];
  Events?: unknown[];
  maxSequence?: unknown;
  MaxSequence?: unknown;
  totalEventCount?: unknown;
  TotalEventCount?: unknown;
}): number | null {
  const rawMax = page.maxSequence ?? page.MaxSequence;
  const max = Number(rawMax);
  if (Number.isFinite(max) && max >= 0) return max;

  const rawTotal = page.totalEventCount ?? page.TotalEventCount;
  const total = Number(rawTotal);
  if (Number.isFinite(total) && total >= 0) return total;

  const events = Array.isArray(page.events)
    ? page.events
    : Array.isArray(page.Events)
      ? page.Events
      : [];
  const maxSeq = events
    .map(getSessionEventSequenceNum)
    .filter((seq): seq is number => seq !== null)
    .reduce((max, seq) => Math.max(max, seq), -Infinity);
  return Number.isFinite(maxSeq) ? maxSeq : null;
}

export function resolveActiveSessionReplayFromSequence(
  lastSequenceNum: number,
  pageSize: number,
): number {
  const cursor = Number.isFinite(lastSequenceNum)
    ? Math.max(0, Math.floor(lastSequenceNum))
    : 0;
  const size = Number.isFinite(pageSize)
    ? Math.max(1, Math.floor(pageSize))
    : SESSION_EVENT_PAGE_SIZE;
  return Math.max(1, cursor - size + 1);
}

export function resolveSessionReplayPollInterval(
  hasActiveMessages: boolean,
): number {
  return hasActiveMessages
    ? ACTIVE_SESSION_REPLAY_POLL_INTERVAL_MS
    : IDLE_SESSION_REPLAY_POLL_INTERVAL_MS;
}

export function shouldRunSessionReplayCompensation(input: {
  hasActiveMessages: boolean;
  lastSseEventAt: number | null | undefined;
  now: number;
  healthyWindowMs?: number;
}): boolean {
  if (!input.hasActiveMessages) return false;
  if (input.lastSseEventAt === null || input.lastSseEventAt === undefined)
    return true;
  const healthyWindowMs =
    input.healthyWindowMs ?? SSE_HEALTHY_REPLAY_SUPPRESSION_MS;
  return input.now - input.lastSseEventAt >= healthyWindowMs;
}

export function resolveTurnIdForEvent(
  event: {
    type?: unknown;
    turnId?: unknown;
    messageId?: unknown;
    fanout_index?: unknown;
  },
  messageIdToTurnId: ReadonlyMap<string, string>,
  latestTurnId: string | null,
  latestTurnCanReceiveUnknownMetadata = true,
): string | null {
  const directTurnId = typeof event.turnId === 'string' ? event.turnId : null;
  if (directTurnId) return directTurnId;

  const messageId =
    typeof event.messageId === 'string' ? event.messageId : null;
  if (messageId) {
    const mapped = messageIdToTurnId.get(messageId);
    if (mapped) return mapped;
    const fanoutIndex = Number(event.fanout_index ?? 0);
    if (
      event.type === 'metadata' &&
      Number.isFinite(fanoutIndex) &&
      fanoutIndex > 0
    )
      return null;
    return event.type === 'metadata' && latestTurnCanReceiveUnknownMetadata
      ? latestTurnId
      : null;
  }

  return latestTurnId;
}

export function canBindUnknownMetadataToTurn(
  turn: ChatTurn | undefined,
): boolean {
  if (!turn) return false;
  return (
    turn.assistant.answerMarkdown.trim().length === 0 ||
    turn.assistant.isStreaming ||
    turn.assistant.status === 'thinking' ||
    turn.assistant.status === 'executing' ||
    turn.assistant.status === 'streaming'
  );
}

export function inferParentSessionIdFromSubSessionId(
  subSessionId?: string | null,
): string | null {
  if (!subSessionId) return null;
  const markerIndex = subSessionId.lastIndexOf('-sub-');
  if (markerIndex <= 0) return null;
  return subSessionId.slice(0, markerIndex);
}

export function filterSubAgentCardsForSession(
  cards: SubAgentCardMap,
  sessionId: string | null | undefined,
): SubAgentCardMap {
  if (!sessionId) return {};
  return Object.fromEntries(
    Object.entries(cards).filter(([, card]) => {
      const parentSessionId =
        card.parentSessionId ??
        inferParentSessionIdFromSubSessionId(card.subSessionId);
      return parentSessionId === sessionId;
    }),
  );
}

export function mergeHistoryWithLifecycleTurns(
  historyTurns: ChatTurn[],
  currentTurns: ChatTurn[],
): ChatTurn[] {
  const lifecycleTurns = currentTurns.filter((turn) =>
    turn.turnId.startsWith(COMPACTION_TURN_PREFIX),
  );
  if (lifecycleTurns.length === 0) return historyTurns;

  const historyIds = new Set(historyTurns.map((turn) => turn.turnId));
  return [
    ...lifecycleTurns.filter((turn) => !historyIds.has(turn.turnId)),
    ...historyTurns,
  ];
}

export { assistantStatusLabel };

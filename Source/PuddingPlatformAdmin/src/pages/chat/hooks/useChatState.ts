// ── 聊天页状态管理 Hook ──────────────────────────────────────
// 
// ADR-057 迁移计划：
//   · activeMessageIds / messageIdToTurnId / completedTurnsRef → 替换为 conversationStore selectors
//   · reconcileCompletedSessionMessages / replay poll → 替换为 gapRecoveryEngine
//   · SSE 生命周期 → 替换为 useConversation hook
//   · 新模块（state/conversationStore, connection/gapRecoveryEngine, hooks/useConversation）
//     已就绪，渐进迁移中。
// ─────────────────────────────────────────────────────────────
import { App, Form, notification } from 'antd';
import dayjs from 'dayjs';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ScrollIntent } from '../viewport/types';
import {
  dequeueCommand,
  enqueueCommand,
  flushOutbox,
  markSending,
} from '../outbox/commandOutbox';
import {
  type AdminChatStreamEvent,
  type AgentMessageQueueItem,
  archiveSession,
  type CompactSessionResponse,
  type ContextCompactionResult,
  compactSession,
  createChatSteeringMessage,
  createSession,
  createWorkspace,
  createWorkspaceAgent,
  deleteSession,
  type EnsureMainSessionRequest,
  ensureMainSession,
  executeConversationSystemCommand,
  getAgentMessageQueue,
  getConversationBootstrap,
  listSessionMessages,
  listSessions,
  listTeams,
  listWorkspaceAgents,
  listWorkspaces,
  type MessageListResponse,
  normalizeConversationEventType,
  renameSession,
  type SessionRecord,
  submitConversationTurn,
  subscribeSessionEvents,
  subscribeWorkspaceNotifications,
  type TokenUsageDto,
  type WorkspaceAgentDto,
  type WorkspaceNotification,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import {
  installPerfDiagnostics,
  markPerf,
  measurePerf,
  recordPerfEvent,
  recordPerfStep,
  writeDebugSessionState,
  writeDebugTrace,
} from '@/utils/debug';
import {
  getThinkingRawText,
  sanitizeProcessText,
} from '../components/processPreview';
import {
  createSessionTerminalEvent,
  reduceSessionLifecycle,
} from '../runtime/sessionLifecycleStore';
import type {
  AssistantStatus,
  ChatSource,
  ChatTurn,
  SessionGroup,
  SessionListItem,
  SubAgentCard,
  SubAgentCardMap,
  TimelineItem,
} from '../types';
import { assistantStatusLabel } from '../types';
import type { AgentAvatarRuntimeEvent } from './agentAvatarRuntime';
import {
  getChatRouteLabel,
  type ResolvedChatRoute,
  resolveChatRoute,
} from './chatRouting';
import {
  clearDeletedSessionReferences,
  disposeCurrentSessionRuntime,
  isSessionNotFoundError,
  SessionNotFoundError,
  type SessionRuntimeRefs,
} from './sessionRuntimeCleanup';
const MESSAGE_PAGE_SIZE = 20;
const SESSION_EVENT_PAGE_SIZE = 50;
const ACTIVE_SESSION_REPLAY_POLL_INTERVAL_MS = 900;
const IDLE_SESSION_REPLAY_POLL_INTERVAL_MS = 8000;
const SSE_HEALTHY_REPLAY_SUPPRESSION_MS = 2500;
const MAX_CHAT_INTERACTION_RUNTIME_EVENTS = 16;
export const STEERING_INJECTED_QUEUE_RETENTION_MS = 8000;
const CHAT_DIAG_STORAGE_KEY = 'pudding_chat_diag_events';
const CHAT_DIAG_MAX_EVENTS = 200;

interface SessionEventPageResponse {
  events?: unknown[];
  Events?: unknown[];
  hasMore?: boolean;
  HasMore?: boolean;
  maxSequence?: unknown;
  MaxSequence?: unknown;
  totalEventCount?: unknown;
  TotalEventCount?: unknown;
}

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

export interface ChatRouteSelection {
  workspaceId?: string;
  agentId?: string;
  sessionId?: string;
}

export interface ChatSendOptions {
  metadata?: Record<string, string>;
}

export type ChatInteractionQueueStatus =
  | 'queued'
  | 'delivering'
  | 'retrying'
  | 'delivered'
  | 'dead_letter'
  | 'failed'
  | 'cancelled'
  | 'expired'
  | 'steering_pending'
  | 'steering_injected'
  | 'steering_failed';

export interface ChatInteractionQueueItem {
  id: string;
  text: string;
  createdAt: number;
  status: ChatInteractionQueueStatus | string;
  source?: 'backend_message_queue' | 'steering';
  metadata?: Record<string, string>;
  steeringId?: string;
  submittedAt?: number;
  injectedAt?: number;
  injectedRound?: number;
  injectionLatencyMs?: number;
  error?: string;
}

export function removeInjectedSteeringQueueItem(
  queue: ChatInteractionQueueItem[],
  steeringId: string,
): ChatInteractionQueueItem[] {
  return queue.filter(
    (item) =>
      item.steeringId !== steeringId || item.status !== 'steering_injected',
  );
}

export type ChatInteractionRuntimeType =
  | 'voice_capture_status'
  | 'voice_playback_status'
  | 'camera_capture_status'
  | 'visual_reasoning_status';

export type ChatInteractionRuntimeEvent = Extract<
  AgentAvatarRuntimeEvent,
  { type: ChatInteractionRuntimeType }
>;

const CHAT_INTERACTION_RUNTIME_EVENT_TYPES = new Set<string>([
  'voice_capture_status',
  'voice_playback_status',
  'camera_capture_status',
  'visual_reasoning_status',
]);

const getStringValue = (value: unknown): string | undefined =>
  typeof value === 'string' && value.trim() ? value.trim() : undefined;

type ChatDiagPayload = Record<string, unknown>;
type ChatDiagWindow = Window & {
  __PUDDING_CHAT_DIAG__?: Array<Record<string, unknown>>;
};

function toChatDiagValue(value: unknown, depth = 0): unknown {
  if (value == null) return value;
  if (typeof value === 'string')
    return value.length > 300 ? `${value.slice(0, 300)}...` : value;
  if (typeof value === 'number' || typeof value === 'boolean') return value;
  if (value instanceof Error)
    return { name: value.name, message: value.message };
  if (Array.isArray(value))
    return depth >= 2
      ? `[array:${value.length}]`
      : value.slice(0, 12).map((item) => toChatDiagValue(item, depth + 1));
  if (typeof value === 'object') {
    if (depth >= 2) return '[object]';
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .slice(0, 24)
        .map(([key, item]) => [key, toChatDiagValue(item, depth + 1)]),
    );
  }
  return String(value);
}

function logChatDiag(label: string, payload: ChatDiagPayload = {}) {
  const entry = {
    at: new Date().toISOString(),
    label,
    payload: toChatDiagValue(payload),
  };
  const line = `[Pudding ChatDiag] ${JSON.stringify(entry)}`;
  console.warn(line);
  if (typeof window === 'undefined') return;
  try {
    const w = window as ChatDiagWindow;
    const current = Array.isArray(w.__PUDDING_CHAT_DIAG__)
      ? w.__PUDDING_CHAT_DIAG__
      : [];
    const next = [...current, entry].slice(-CHAT_DIAG_MAX_EVENTS);
    w.__PUDDING_CHAT_DIAG__ = next;
    window.sessionStorage.setItem(CHAT_DIAG_STORAGE_KEY, JSON.stringify(next));
  } catch {
    // Diagnostics must never affect chat behavior.
  }
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
    (groups[key] ??= []).push(s);
  }
  return ['今天', '昨天', '本周', '更早']
    .filter((k) => groups[k]?.length)
    .map((label) => ({
      label,
      items: groups[label]!.sort((a, b) => b.timestamp - a.timestamp),
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

function findMatchingRecentUserTurn(
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

  // 修复：当前端 turns 数量 > 服务端且最新消息一致时，
  // 说明前端有额外数据（来自 event replay 的中间 turns），不应被覆盖
  if (currentTurns.length > loadedTurns.length && loadedHasCurrentLatest) {
    return 'frontend-ahead-of-server';
  }

  return null;
}

const HISTORICAL_REPLAY_TERMINAL_EVENTS = new Set([
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

function parseObjectJson(value: unknown): Record<string, unknown> | null {
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

function countCompletedAssistantTurns(turns: ChatTurn[]): number {
  return turns.filter((turn) => turn.assistant.answerMarkdown.trim().length > 0)
    .length;
}

/** 从 subagent.* 事件的 data 字段中提取 delta 文本 */
function tryExtractDelta(ev: { data?: string; delta?: string }): string | null {
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

const createId = () =>
  `msg-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
const COMPACT_COMMAND = '/compact';
const COMPACTION_TURN_PREFIX = 'compaction:';

function compactionTurnId(compactionId: string): string {
  return `${COMPACTION_TURN_PREFIX}${compactionId}`;
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

const createAssistant = (
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

const normalizeUsage = (usage?: TokenUsageDto): TokenUsageDto | undefined =>
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

const isReasoningStep = (status?: string) => {
  const key = (status || '').toLowerCase();
  return key.startsWith('thinking') || key.startsWith('reasoning');
};

const getStepTone = (status?: string): 'executing' | 'success' | 'error' => {
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

const getStepMessage = (payload: {
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

const formatTime = (ts: number) => {
  const diff = dayjs().diff(dayjs(ts), 'minute');
  if (diff < 1) return '刚刚';
  if (diff < 60) return `${diff}分钟前`;
  return dayjs(ts).format('MM-DD HH:mm');
};

export interface UseChatStateReturn {
  // workspace
  workspaces: WorkspaceWithPermDto[];
  workspaceId: string | undefined;
  workspaceLoading: boolean;
  setWorkspaceId: (v: string | undefined) => void;
  setWorkspaces: (v: WorkspaceWithPermDto[]) => void;
  // agent
  agents: WorkspaceAgentDto[];
  agentId: string | undefined;
  agentLoading: boolean;
  setAgentId: (v: string | undefined) => void;
  selectedAgent: WorkspaceAgentDto | undefined;
  // session
  sidebarOpen: boolean;
  setSidebarOpen: (v: boolean) => void;
  sessions: SessionListItem[];
  selectedSessionId: string | null;
  sessionsLoading: boolean;
  groups: SessionGroup[];
  // chat
  turns: ChatTurn[];
  chatInteractionRuntimeEvents: ChatInteractionRuntimeEvent[];
  historyLoading: boolean;
  hasMoreMessages: boolean;
  loadingMore: boolean;
  inputValue: string;
  setInputValue: (v: string) => void;
  loading: boolean;
  workingAgentIds: string[];
  interactionQueue: ChatInteractionQueueItem[];
  error: string | null;
  setError: (v: string | null) => void;
  latestUsage: TokenUsageDto | undefined;
  // sub-agent
  subAgentCards: SubAgentCardMap;
  // T-201: workspace notifications
  sessionUnreadCounts: Record<string, number>;
  startWorkspaceNotificationStream: (workspaceId: string) => void;
  stopWorkspaceNotificationStream: () => void;
  clearSessionUnread: (sessionId: string) => void;
  // token
  tLimit: number;
  tUsed: number;
  tPct: number;
  // cache
  mainSessionId: string | null;
  sessionCacheHitTokens: number;
  sessionCacheMissTokens: number;
  cacheHitRate?: number;
  handleSetMainSession: (sessionId: string) => void;
  // modals
  createSceneOpen: boolean;
  setCreateSceneOpen: (v: boolean) => void;
  createSceneLoading: boolean;
  createSceneForm: ReturnType<typeof Form.useForm<{ name: string }>>[0];
  renameModalOpen: boolean;
  setRenameModalOpen: (v: boolean) => void;
  renameTitle: string;
  setRenameTitle: (v: string) => void;
  renameSessionId: string | null;
  // actions
  handleSelectSession: (
    sid: string,
    options?: { agentId?: string },
  ) => Promise<number | undefined>;
  handleDeleteSession: (sid: string) => Promise<void>;
  handleArchiveSession: (sid: string) => Promise<void>;
  handleRenameStart: (sid: string, title: string) => void;
  handleRenameSubmit: () => Promise<void>;
  ensureAgentMainSession: (
    nextWorkspaceId?: string,
    nextAgentId?: string,
    options?: { isCurrent?: () => boolean; selectSession?: boolean },
  ) => Promise<string | undefined>;
  sendMessage: (text: string, options?: ChatSendOptions) => Promise<void>;
  submitInteraction: (text: string, options?: ChatSendOptions) => Promise<void>;
  enqueueInteraction: (
    text: string,
    options?: ChatSendOptions,
  ) => string | null;
  updateQueuedInteraction: (id: string, text: string) => void;
  deleteQueuedInteraction: (id: string) => void;
  sendQueuedInteractionNow: (id: string) => Promise<void>;
  steerQueuedInteraction: (id: string) => Promise<void>;
  handleKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  loadMoreMessages: () => Promise<void>;
  resetConversation: (
    nextWorkspaceId?: string,
    nextAgentId?: string,
  ) => Promise<string | undefined>;
  handleExport: () => void;
  onDeleteTurn: (turnId: string) => void;
  onToggleReasoning: (turnId: string, blockId: string) => void;
  // refs
  messageListRef: React.RefObject<HTMLDivElement | null>;
  listEndRef: React.RefObject<HTMLDivElement | null>;
  abortRef: React.RefObject<AbortController | null>;
  // utils
  formatTime: (ts: number) => string;
  getStepTone: (status?: string) => 'executing' | 'success' | 'error';
  assistantStatusLabel: Record<AssistantStatus, string>;
  // helper
  getAgentName: (a: WorkspaceAgentDto) => string;
  stringToColor: (str: string) => string;
  // select options
  wsOpts: { value: string; label: string; disabled: boolean }[];
  agOpts: { value: string; label: React.ReactNode; disabled: boolean }[];
  creatingSession: boolean;
  viewportScrollIntent: ScrollIntent;
  clearViewportScrollIntent: () => void;
}

export function useChatState(routeSearch?: string): UseChatStateReturn {
  const { message: messageApi } = App.useApp();
  const routeSelection = useMemo(
    () =>
      getChatRouteSelectionFromSearch(
        routeSearch ??
          (typeof window === 'undefined' ? '' : window.location.search),
      ),
    [routeSearch],
  );

  const [workspaces, setWorkspaces] = useState<WorkspaceWithPermDto[]>([]);
  const [workspaceId, setWorkspaceId] = useState<string>();
  const [workspaceLoading, setWorkspaceLoading] = useState(false);
  const [agents, setAgents] = useState<WorkspaceAgentDto[]>([]);
  const [agentId, setAgentId] = useState<string>();
  const [agentLoading, setAgentLoading] = useState(false);

  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [sessions, setSessions] = useState<SessionListItem[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(
    null,
  );
  const [sessionsLoading, setSessionsLoading] = useState(false);

  const [turns, setTurns] = useState<ChatTurn[]>([]);
  const [viewportScrollIntent, setViewportScrollIntent] = useState<ScrollIntent>({ type: 'none' });
  const clearViewportScrollIntent = useCallback(() => {
    setViewportScrollIntent({ type: 'none' });
  }, []);
  const [chatInteractionRuntimeEvents, setChatInteractionRuntimeEvents] =
    useState<ChatInteractionRuntimeEvent[]>([]);
  const [subAgentCards, setSubAgentCards] = useState<SubAgentCardMap>({});
  const [historyLoading, setHistoryLoading] = useState(false);
  const [hasMoreMessages, setHasMoreMessages] = useState(false);
  const [oldestMessageCursor, setOldestMessageCursor] = useState<number | null>(
    null,
  );
  const [loadingMore, setLoadingMore] = useState(false);
  const [creatingSession, setCreatingSession] = useState(false);

  const [inputValue, setInputValue] = useState('');
  const [loading, setLoading] = useState(false);
  const [workingAgentIds, setWorkingAgentIds] = useState<string[]>([]);
  const [serverInteractionQueue, setServerInteractionQueue] = useState<
    ChatInteractionQueueItem[]
  >([]);
  const [steeringInteractionQueue, setSteeringInteractionQueue] = useState<
    ChatInteractionQueueItem[]
  >([]);
  const [error, setError] = useState<string | null>(null);
  const [latestUsage, setLatestUsage] = useState<TokenUsageDto | undefined>();
  const [mainSessionId, setMainSessionId] = useState<string | null>(null);
  const [sessionCacheHitTokens, setSessionCacheHitTokens] = useState(0);
  const [sessionCacheMissTokens, setSessionCacheMissTokens] = useState(0);
  const sessionIdRef = useRef<string | undefined>(undefined);
  const forceNewSessionRef = useRef<boolean>(false);
  const abortRef = useRef<AbortController | null>(null);
  const historyAbortRef = useRef<AbortController | null>(null);
  const messageListRef = useRef<HTMLDivElement>(null);
  const listEndRef = useRef<HTMLDivElement>(null);
  const completedTurnsRef = useRef<Set<string>>(new Set());
  const latestTurnIdRef = useRef<string | null>(null);
  const messageIdToTurnIdRef = useRef<Map<string, string>>(new Map());
  const lastSequenceNumRef = useRef<number>(0);
  const sessionEventsAbortRef = useRef<AbortController | null>(null);
  const sessionEventsPollTimerRef = useRef<number | null>(null);
  const sessionEventsReconnectTimerRef = useRef<number | null>(null);
  const projectionOwnedSessionIdsRef = useRef<Set<string>>(new Set());
  const sseSessionIdRef = useRef<string | null>(null);
  const mainSessionIdRef = useRef<string | null>(null);

  // ADR-fix1-004 Phase 1: 收敛 4 个 session ref 为单一身份对象（逐步迁移）
  interface SessionIdentity {
    route: string | undefined;
    selected: string | null;
    stream: string | null;
    main: string | null;
  }
  const sessionIdentityRef = useRef<SessionIdentity>({
    route: undefined, selected: null, stream: null, main: null,
  });
  const syncSessionIdentity = useCallback(() => {
    sessionIdentityRef.current = {
      route: sessionIdRef.current,
      selected: selectedSessionIdRef.current,
      stream: sseSessionIdRef.current,
      main: mainSessionIdRef.current,
    };
  }, []);

  const suppressMainSessionEnsureRef = useRef(false);
  const lastSseEventAtRef = useRef<number | null>(null);
  // Ref to break circular dependency: applySessionEvent → startSessionEventStream
  const startSessionEventStreamRef = useRef<(sessionId: string) => void>(
    () => {},
  );
  const compactSessionSwitchRef = useRef<
    (sessionId: string, title?: string | null) => void
  >(() => {});
  const compactLifecycleEventRef = useRef<
    (
      event: AdminChatStreamEvent,
      options?: { allowSessionSwitch?: boolean; notify?: boolean },
    ) => void
  >(() => {});
  const compactionTurnIdsRef = useRef<Map<string, string>>(new Map());
  const compactionLifecycleTurnsRef = useRef<Map<string, ChatTurn>>(new Map());
  const activeCompactionTurnIdRef = useRef<string | null>(null);
  const pendingCompactSessionSwitchRef = useRef<{
    sessionId: string;
    title?: string | null;
  } | null>(null);
  const loadingRef = useRef(false);
  const selectedSessionIdRef = useRef<string | null>(null);
  const hydrateSessionReplayRef = useRef(false);
  const turnsRef = useRef<ChatTurn[]>([]);
  const steeringInjectedDismissTimersRef = useRef<Map<string, number>>(
    new Map(),
  );

  // T-201: 工作区通知 SSE（独立于会话 SSE）
  const [sessionUnreadCounts, setSessionUnreadCounts] = useState<
    Record<string, number>
  >({});
  const workspaceNotifyAbortRef = useRef<AbortController | null>(null);
  const workspaceNotifyReconnectRef = useRef<number | null>(null);
  const workspaceNotifyWsIdRef = useRef<string | null>(null);

  // ADR-InkBloom: delta 批处理 refs
  const pendingDeltaRef = useRef<Map<string, string>>(new Map());
  const deltaFlushTimerRef = useRef<number | null>(null);
  const deltaHasFlushedRef = useRef(false);
  const pendingThinkingRef = useRef<Map<string, string>>(new Map());
  const thinkingFlushTimerRef = useRef<number | null>(null);
  const duplicateDeltaReplayOffsetRef = useRef<Map<string, number>>(new Map());
  const eventCountsRef = useRef<Map<string, number>>(new Map());
  const streamStartAtRef = useRef<Map<string, number>>(new Map());
  const activeMessageIdsRef = useRef<Set<string>>(new Set());
  const messageIdToAgentIdsRef = useRef<Map<string, string[]>>(new Map());
  const sessionIdToAgentIdsRef = useRef<Map<string, string[]>>(new Map());

  const [createSceneOpen, setCreateSceneOpen] = useState(false);
  const [createSceneLoading, setCreateSceneLoading] = useState(false);
  const [createSceneForm] = Form.useForm<{ name: string }>();
  const [renameModalOpen, setRenameModalOpen] = useState(false);
  const [renameTitle, setRenameTitle] = useState('');
  const [renameSessionId, setRenameSessionId] = useState<string | null>(null);

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

  const selectedAgent = agents.find((a) => a.agentId === agentId);

  useEffect(() => {
    installPerfDiagnostics();
  }, []);

  useEffect(() => {
    turnsRef.current = turns;
  }, [turns]);

  useEffect(() => {
    loadingRef.current = loading;
  }, [loading]);

  useEffect(() => {
    selectedSessionIdRef.current = selectedSessionId;
    syncSessionIdentity();
    logChatDiag('selectedSessionId.synced', {
      selectedSessionId,
      sessionIdRef: sessionIdRef.current,
      mainSessionId,
      routeSessionId: routeSelection.sessionId,
      agentId,
      turnCount: turnsRef.current.length,
      url: typeof window === 'undefined' ? undefined : window.location.href,
    });
  }, [agentId, mainSessionId, routeSelection.sessionId, selectedSessionId]);

  useEffect(() => {
    mainSessionIdRef.current = mainSessionId;
    syncSessionIdentity();
  }, [mainSessionId]);

  useEffect(() => {
    const flushPendingCommands = () => {
      void flushOutbox(async (record) => {
        await submitConversationTurn(
          record.workspaceId,
          record.conversationId,
          {
            clientRequestId: record.id,
            clientMessageId: record.clientMessageId,
            recipients: { type: 'agent', agentIds: record.agentIds },
            content: [{ type: 'text', text: record.messageText }],
          },
        );
      });
    };

    flushPendingCommands();
    window.addEventListener('online', flushPendingCommands);
    return () => window.removeEventListener('online', flushPendingCommands);
  }, []);

  const clearSessionEventTimers = useCallback(() => {
    if (sessionEventsPollTimerRef.current != null) {
      window.clearTimeout(sessionEventsPollTimerRef.current);
      sessionEventsPollTimerRef.current = null;
    }
    if (sessionEventsReconnectTimerRef.current != null) {
      window.clearTimeout(sessionEventsReconnectTimerRef.current);
      sessionEventsReconnectTimerRef.current = null;
    }
  }, []);

  // ADR-InkBloom: 合并 delta 到 pending 缓冲，首帧立即 flush（0ms），后续 80ms 批刷新
  const enqueueDelta = useCallback((turnId: string, delta: string) => {
    pendingDeltaRef.current.set(
      turnId,
      (pendingDeltaRef.current.get(turnId) ?? '') + delta,
    );
    if (deltaFlushTimerRef.current != null) return;
    const scheduledAt = performance.now();
    const delayMs = deltaHasFlushedRef.current ? 80 : 0;
    deltaFlushTimerRef.current = window.setTimeout(() => {
      deltaHasFlushedRef.current = true;
      const flushStart = performance.now();
      const pending = new Map(pendingDeltaRef.current);
      const chars = [...pending.values()].reduce(
        (sum, value) => sum + value.length,
        0,
      );
      pendingDeltaRef.current.clear();
      deltaFlushTimerRef.current = null;
      setTurns((prev) => {
        let changed = false;
        const next = prev.map((turn) => {
          const d = pending.get(turn.turnId);
          if (!d) return turn;
          changed = true;
          return applyBufferedDeltaToTurn(turn, d);
        });
        return changed ? next : prev;
      });
      recordPerfEvent('chat.delta.flush', {
        turns: pending.size,
        chars,
        waitMs: Math.round(flushStart - scheduledAt),
        applyMs: Math.round(performance.now() - flushStart),
      });
    }, delayMs);
  }, []);

  const flushPendingDeltas = useCallback(() => {
    if (deltaFlushTimerRef.current != null) {
      window.clearTimeout(deltaFlushTimerRef.current);
      deltaFlushTimerRef.current = null;
    }
    if (pendingDeltaRef.current.size === 0) return;
    const flushStart = performance.now();
    const pending = new Map(pendingDeltaRef.current);
    const chars = [...pending.values()].reduce(
      (sum, value) => sum + value.length,
      0,
    );
    pendingDeltaRef.current.clear();
    setTurns((prev) => {
      let changed = false;
      const next = prev.map((turn) => {
        const d = pending.get(turn.turnId);
        if (!d) return turn;
        changed = true;
        return applyBufferedDeltaToTurn(turn, d);
      });
      return changed ? next : prev;
    });
    recordPerfEvent('chat.delta.flushNow', {
      turns: pending.size,
      chars,
      applyMs: Math.round(performance.now() - flushStart),
    });
  }, []);

  const stopSessionEventStream = useCallback(() => {
    flushPendingDeltas();
    clearSessionEventTimers();
    sessionEventsAbortRef.current?.abort();
    sessionEventsAbortRef.current = null;
    sseSessionIdRef.current = null;
    lastSseEventAtRef.current = null;
    syncSessionIdentity();
  }, [clearSessionEventTimers, flushPendingDeltas, syncSessionIdentity]);

  // P0 v2: 有作用域的 session runtime 清理 + 统一通知入口
  const runtimeRefs: SessionRuntimeRefs = useMemo(
    () => ({
      sessionIdRef,
      sseSessionIdRef,
      lastSequenceNumRef,
      messageIdToTurnIdRef,
      activeMessageIdsRef,
      projectionOwnedSessionIdsRef,
      turnsRef,
      completedTurnsRef,
      latestTurnIdRef,
      forceNewSessionRef,
      sessionEventsAbortRef,
      sessionEventsPollTimerRef,
      sessionEventsReconnectTimerRef,
      deltaFlushTimerRef,
      thinkingFlushTimerRef,
      pendingDeltaRef,
      pendingThinkingRef,
      streamStartAtRef,
      messageIdToAgentIdsRef,
      duplicateDeltaReplayOffsetRef,
      eventCountsRef,
    }),
    [],
  );

  /**
   * P0 v2 统一入口：处理 session 不再存在（删除/归档/404）。
   *
   * 所有路径（replay 404 / SSE 404 / handleDeleteSession / handleArchiveSession）最终都走这里。
   * 根据 sessionId 与当前 selected/stream/main 的匹配关系，做不同层次的清理。
   */
  const handleSessionNotFound = useCallback(
    (sessionId: string, reason: string) => {
      const isSelected = selectedSessionIdRef.current === sessionId;
      const isCurrentStream = sseSessionIdRef.current === sessionId;
      const isMain = mainSessionIdRef.current === sessionId;

      // ADR-054: lifecycle 事件已创建，后续 MR 需要：
      // 1. 用 useState/useRef 维护 lifecycleState
      // 2. 用 reduceSessionLifecycle(lifecycleState, event) 更新状态
      // 3. 让清理逻辑依赖 lifecycleState.phase 而非手动 ref 检查
      const _terminalEvent = createSessionTerminalEvent(sessionId, reason);
      void _terminalEvent; // TODO(ADR-054): dispatch to lifecycle state machine

      console.debug('[Pudding Chat] handleSessionNotFound', {
        sessionId,
        reason,
        isSelected,
        isCurrentStream,
        isMain,
        currentSelected: selectedSessionIdRef.current,
        currentSse: sseSessionIdRef.current,
        currentMain: mainSessionIdRef.current,
      });

      // 1. 如果是当前流 → 停止 SSE + 清除 active refs
      // 注意：只有 isCurrentStream 才能 abort SSE，isSelected 不能（可能 SSE 已切到别的 session）
      if (isCurrentStream) {
        disposeCurrentSessionRuntime(sessionId, runtimeRefs, reason);
        stopSessionEventStream();
      }

      // 2. 清除非流引用（projectionOwned 等）
      clearDeletedSessionReferences(sessionId, runtimeRefs);

      // 3. React 层清理
      if (isSelected) {
        setSelectedSessionId(null);
        turnsRef.current = [];
        setTurns([]);
        setChatInteractionRuntimeEvents([]);
        setSubAgentCards({});
        setHasMoreMessages(false);
        setOldestMessageCursor(null);
        messageIdToTurnIdRef.current.clear();
        lastSequenceNumRef.current = 0;
      }

      // 4. 如果是 main session → 清除 mainSessionId 和 agent 缓存，并抑制自动重建
      if (isMain) {
        suppressMainSessionEnsureRef.current = true;
        setMainSessionId(null);
        setAgents((prev) =>
          prev.map((a) =>
            a.mainSessionId === sessionId
              ? { ...a, mainSessionId: undefined }
              : a,
          ),
        );
      }

      // 5. 从列表移除（或标记 stale）
      setSessions((prev) => prev.filter((s) => s.sessionId !== sessionId));

      logChatDiag('session.notFound', {
        sessionId,
        reason,
        isSelected,
        isCurrentStream,
        isMain,
      });
    },
    [runtimeRefs, stopSessionEventStream],
  );

  const resetMainSessionEnsureSuppression = useCallback((reason: string) => {
    if (!suppressMainSessionEnsureRef.current) return;
    suppressMainSessionEnsureRef.current = false;
    logChatDiag('main.ensure.suppressionCleared', { reason });
  }, []);

  const clearInjectedSteeringDismissTimer = useCallback(
    (steeringId: string) => {
      const timer = steeringInjectedDismissTimersRef.current.get(steeringId);
      if (timer != null) {
        window.clearTimeout(timer);
        steeringInjectedDismissTimersRef.current.delete(steeringId);
      }
    },
    [],
  );

  const clearInjectedSteeringDismissTimers = useCallback(() => {
    steeringInjectedDismissTimersRef.current.forEach((timer) =>
      window.clearTimeout(timer),
    );
    steeringInjectedDismissTimersRef.current.clear();
  }, []);

  const scheduleInjectedSteeringDismiss = useCallback(
    (steeringId: string) => {
      clearInjectedSteeringDismissTimer(steeringId);
      const timer = window.setTimeout(() => {
        steeringInjectedDismissTimersRef.current.delete(steeringId);
        setSteeringInteractionQueue((prev) =>
          removeInjectedSteeringQueueItem(prev, steeringId),
        );
        recordPerfEvent('chat.steering.dismissed', {
          steeringId,
          reason: 'injected-retention-elapsed',
          retentionMs: STEERING_INJECTED_QUEUE_RETENTION_MS,
        });
      }, STEERING_INJECTED_QUEUE_RETENTION_MS);
      steeringInjectedDismissTimersRef.current.set(steeringId, timer);
    },
    [clearInjectedSteeringDismissTimer],
  );

  useEffect(
    () => () => {
      clearInjectedSteeringDismissTimers();
    },
    [clearInjectedSteeringDismissTimers],
  );

  // T-201: 清空会话未读标记
  const clearSessionUnread = useCallback((sessionId: string) => {
    setSessionUnreadCounts((prev) => {
      if (!prev[sessionId]) return prev;
      const next = { ...prev };
      delete next[sessionId];
      return next;
    });
  }, []);

  const updateLastSequence = useCallback((ev: unknown) => {
    if (!ev || typeof ev !== 'object') return;
    const raw = (ev as Record<string, unknown>).sequenceNum;
    if (raw === undefined || raw === null) return;
    const seq = typeof raw === 'number' ? raw : Number(raw);
    if (Number.isFinite(seq) && seq > lastSequenceNumRef.current) {
      lastSequenceNumRef.current = seq;
    }
  }, []);

  const applyPendingThinking = useCallback((pending: Map<string, string>) => {
    setTurns((prev) =>
      prev.map((turn) => {
        const thinkingDelta = pending.get(turn.turnId);
        if (!thinkingDelta) return turn;
        if (completedTurnsRef.current.has(turn.turnId)) return turn;

        const items = turn.assistant.timelineItems ?? [];
        const nextItems = [
          ...items,
          {
            id: createId(),
            type: 'thinking' as const,
            text: thinkingDelta,
            status: 'streaming',
            timestamp: Date.now(),
            collapsed: true,
          },
        ];

        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: 'thinking' as const,
            renderMode: 'structured' as const,
            timelineItems: nextItems,
          },
        };
      }),
    );
  }, []);

  const enqueueThinking = useCallback(
    (turnId: string, thinkingDelta: string) => {
      pendingThinkingRef.current.set(
        turnId,
        (pendingThinkingRef.current.get(turnId) ?? '') + thinkingDelta,
      );
      if (thinkingFlushTimerRef.current != null) return;
      const scheduledAt = performance.now();
      thinkingFlushTimerRef.current = window.setTimeout(() => {
        const flushStart = performance.now();
        const pending = new Map(pendingThinkingRef.current);
        pendingThinkingRef.current.clear();
        thinkingFlushTimerRef.current = null;
        if (pending.size > 0) applyPendingThinking(pending);
        recordPerfEvent('chat.thinking.flush', {
          turns: pending.size,
          chars: [...pending.values()].reduce(
            (sum, value) => sum + value.length,
            0,
          ),
          waitMs: Math.round(flushStart - scheduledAt),
          applyMs: Math.round(performance.now() - flushStart),
        });
      }, 120);
    },
    [applyPendingThinking],
  );

  const flushPendingThinking = useCallback(() => {
    if (thinkingFlushTimerRef.current != null) {
      window.clearTimeout(thinkingFlushTimerRef.current);
      thinkingFlushTimerRef.current = null;
    }
    if (pendingThinkingRef.current.size === 0) return;
    const flushStart = performance.now();
    const pending = new Map(pendingThinkingRef.current);
    pendingThinkingRef.current.clear();
    applyPendingThinking(pending);
    recordPerfEvent('chat.thinking.flushNow', {
      turns: pending.size,
      chars: [...pending.values()].reduce(
        (sum, value) => sum + value.length,
        0,
      ),
      applyMs: Math.round(performance.now() - flushStart),
    });
  }, [applyPendingThinking]);

  const resetStreamCursorForSessionChange = useCallback(
    (previousSessionId?: string | null, nextSessionId?: string | null) => {
      if (
        !shouldResetSequenceForSessionChange(previousSessionId, nextSessionId)
      )
        return;
      lastSequenceNumRef.current = 0;
      activeMessageIdsRef.current.clear();
      streamStartAtRef.current.clear();
      pendingDeltaRef.current.clear();
      pendingThinkingRef.current.clear();
      duplicateDeltaReplayOffsetRef.current.clear();
      if (deltaFlushTimerRef.current != null) {
        window.clearTimeout(deltaFlushTimerRef.current);
        deltaFlushTimerRef.current = null;
      }
      if (thinkingFlushTimerRef.current != null) {
        window.clearTimeout(thinkingFlushTimerRef.current);
        thinkingFlushTimerRef.current = null;
      }
    },
    [],
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

  const normalizeSessionEvent = useCallback(
    (
      raw: unknown,
    ):
      | (AdminChatStreamEvent & { sequenceNum?: number; recordedAt?: string })
      | null => {
      if (!raw || typeof raw !== 'object') return null;
      const obj = raw as Record<string, unknown>;

      const rawSeq =
        obj.sequence ?? obj.Sequence ?? obj.sequenceNum ?? obj.SequenceNum;
      const sequenceNum = rawSeq == null ? undefined : Number(rawSeq);
      const rawRecordedAt =
        obj.recordedAt ??
        obj.RecordedAt ??
        obj.recordedAtUtc ??
        obj.RecordedAtUtc;
      const recordedAt =
        typeof rawRecordedAt === 'string' && rawRecordedAt.trim()
          ? rawRecordedAt
          : undefined;

      let payload: Record<string, unknown> = {};
      const rawPayload = obj.payload ?? obj.Payload;
      if (rawPayload && typeof rawPayload === 'object') {
        payload = rawPayload as Record<string, unknown>;
      } else if (typeof rawPayload === 'string' && rawPayload.trim()) {
        try {
          payload = JSON.parse(rawPayload) as Record<string, unknown>;
        } catch {
          payload = {};
        }
      }

      const rawDataJson = obj.dataJson ?? obj.DataJson;
      if (
        Object.keys(payload).length === 0 &&
        typeof rawDataJson === 'string' &&
        rawDataJson.trim()
      ) {
        try {
          payload = JSON.parse(rawDataJson) as Record<string, unknown>;
        } catch {
          payload = {};
        }
      }

      const rawData = obj.data ?? obj.Data;
      if (
        Object.keys(payload).length === 0 &&
        typeof rawData === 'string' &&
        rawData.trim()
      ) {
        try {
          payload = JSON.parse(rawData) as Record<string, unknown>;
        } catch {
          payload = {};
        }
      } else if (
        Object.keys(payload).length === 0 &&
        rawData &&
        typeof rawData === 'object'
      ) {
        payload = rawData as Record<string, unknown>;
      }

      const rawType = obj.type ?? obj.Type;
      const wrapperEventType = obj.eventType ?? obj.EventType;
      const canonicalType = String(
        (rawType === 'event' && wrapperEventType
          ? wrapperEventType
          : rawType) ??
          wrapperEventType ??
          payload.type ??
          '',
      ).trim();
      if (!canonicalType) return null;
      const type = normalizeConversationEventType(canonicalType);

      return {
        ...obj,
        ...(payload as Record<string, unknown>),
        type,
        ...(Number.isFinite(sequenceNum) ? { sequenceNum } : {}),
        ...(recordedAt ? { recordedAt } : {}),
      } as AdminChatStreamEvent & { sequenceNum?: number; recordedAt?: string };
    },
    [],
  );

  const listSessionEventsPage = useCallback(
    async (
      sessionId: string,
      from: number,
      limit = SESSION_EVENT_PAGE_SIZE,
      signal?: AbortSignal,
    ): Promise<SessionEventPageResponse> => {
      const token = localStorage.getItem('pudding_token');
      const headers: Record<string, string> = {};
      if (token) headers.Authorization = `Bearer ${token}`;
      const url = buildSessionEventReplayUrl(sessionId, from, limit);
      const resp = await fetch(url, { method: 'GET', headers, signal });
      if (!resp.ok) {
        // P0: 404 (deleted) / 410 (frozen/archived) → SessionNotFoundError，让调用方停止轮询
        if (resp.status === 404 || resp.status === 410) {
          throw new SessionNotFoundError(
            sessionId,
            `replay HTTP ${resp.status}`,
          );
        }
        throw new Error(`listSessionEvents failed: ${resp.status}`);
      }
      return (await resp.json()) as SessionEventPageResponse;
    },
    [],
  );

  const syncCompletedHistoryEventCursor = useCallback(
    async (sessionId: string, signal?: AbortSignal) => {
      try {
        const bootstrap = await getConversationBootstrap(sessionId, 1);
        if (signal?.aborted) return;
        for (const rawEvent of bootstrap.lifecycleEvents ?? []) {
          const event = normalizeSessionEvent(rawEvent);
          if (!event) continue;
          compactLifecycleEventRef.current(event, {
            allowSessionSwitch: false,
            notify: false,
          });
        }
        const cursor = Number(bootstrap.snapshotCursor);
        if (!Number.isFinite(cursor) || cursor < 0) return;
        lastSequenceNumRef.current = Math.max(
          lastSequenceNumRef.current,
          cursor,
        );
        recordPerfEvent('chat.replay.cursorSynced', {
          sessionId,
          lastSequenceNum: lastSequenceNumRef.current,
        });
      } catch (error) {
        recordPerfEvent(
          'chat.replay.cursorSyncFailed',
          {
            sessionId,
            aborted: signal?.aborted === true,
            error: error instanceof Error ? error.message : String(error),
          },
          { throttleMs: 1_000 },
        );
      }
    },
    [normalizeSessionEvent],
  );

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
            (ev.type === 'delta' ||
              ev.type === 'thinking' ||
              ev.type === 'usage')
          ) {
            return turn;
          }
          if (ev.type === 'metadata') {
            // T-102: 从 metadata 帧推断消息来源（持久 SSE 通道）
            // T-103: 兼容两种命名——SessionRouter 帧输出 source_id(source_name) snake_case，
            // WebSocket connector metadata 使用 sourceId camelCase。
            const anyMeta = ev as Record<string, unknown>;
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
          if (ev.type === 'delta') {
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
          if (ev.type === 'thinking') {
            const thinkingDelta = typeof ev.delta === 'string' ? ev.delta : '';
            if (!sanitizeProcessText(thinkingDelta, { compact: false }))
              return turn;
            enqueueThinking(turn.turnId, thinkingDelta);
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'thinking' as const,
                renderMode: 'structured' as const,
              },
            };
          }
          if (ev.type === 'tool_call') {
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                renderMode: 'structured' as const,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: createId(),
                    type: 'tool_call' as const,
                    status: 'tool_call',
                    name: ev.name,
                    arguments: ev.arguments,
                    message: `🔧 调用工具: ${ev.name}\n参数: ${ev.arguments}`,
                    timestamp: Date.now(),
                    collapsed: false,
                  },
                ],
              },
            };
          }
          if (ev.type === 'tool_result') {
            const exitLabel = ev.exitCode === 0 ? '✓' : '✗';
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                renderMode: 'structured' as const,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: createId(),
                    type: 'tool_result' as const,
                    status: ev.exitCode === 0 ? 'success' : 'error',
                    name: ev.name,
                    output: ev.output,
                    exitCode: ev.exitCode,
                    message: `🔧 ${ev.name} ${exitLabel}\n${ev.output || ev.error || '(empty)'}`,
                    timestamp: Date.now(),
                    collapsed: false,
                  },
                ],
              },
            };
          }
          if (ev.type === 'subconscious_step') {
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                renderMode: 'structured' as const,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: createId(),
                    type: 'subconscious_step' as const,
                    status: ev.status === 'done' ? 'done' : 'thinking',
                    message: `🧠 ${ev.message}`,
                    timestamp: Date.now(),
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
                    id: createId(),
                    type: 'subconscious_step' as const,
                    status: 'compacting',
                    message: '正在压缩上下文…',
                    timestamp: Date.now(),
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
                        id: createId(),
                        type: 'subconscious_step' as const,
                        status: 'success',
                        message: '上下文压缩完成',
                        timestamp: Date.now(),
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
                        id: createId(),
                        type: 'subconscious_step' as const,
                        status: 'error',
                        message,
                        timestamp: Date.now(),
                        collapsed: false,
                      },
                    ],
              },
            };
          }
          // 子代理仍保留独立卡片承载完整输出；父 Agent timeline 只写入轻量活动预览，
          // 让默认气泡能展示“当前正在和哪个子代理交互”，避免长耗时任务看起来像阻塞。
          if (ev.type.startsWith('subagent.')) {
            const mappedType = ev.type.substring('subagent.'.length);
            const saData = ev as any;
            const subAgentId = saData.sub_agent_id || saData.id || 'sub';
            if (!subAgentId || subAgentId === 'sub') return turn;
            const cardId = `sa-${subAgentId}`;
            const eventTimestamp = parseSessionEventTimestampMs(
              saData.recordedAt ?? saData.timestamp,
            );
            const parentSessionId =
              typeof saData.parent_session_id === 'string'
                ? saData.parent_session_id
                : (inferParentSessionIdFromSubSessionId(subAgentId) ??
                  sessionIdRef.current ??
                  selectedSessionId ??
                  undefined);
            const taskSummary = resolveSubAgentTaskSummary(saData);
            const appendOrUpdateSubAgentActivity = (
              items: TimelineItem[],
              next: TimelineItem,
              appendOutput?: string,
            ): TimelineItem[] => {
              const idx = items.findIndex(
                (item) =>
                  item.name === subAgentId &&
                  (item.type === 'subagent_spawned' ||
                    item.type === 'subagent_progress') &&
                  next.type !== 'subagent_spawned',
              );
              if (idx < 0) return [...items, next];
              const existing = items[idx];
              const mergedOutput = sanitizeProcessText(
                `${existing.output ?? ''}${appendOutput ?? next.output ?? ''}`,
                { compact: false },
              );
              const updated: TimelineItem = {
                ...existing,
                ...next,
                output:
                  mergedOutput.length > 900
                    ? mergedOutput.slice(mergedOutput.length - 900)
                    : mergedOutput,
                arguments: next.arguments || existing.arguments,
                timestamp: next.timestamp,
              };
              return [...items.slice(0, idx), updated, ...items.slice(idx + 1)];
            };

            // 子代理 delta → 追加到独立卡片 output
            if (mappedType === 'delta') {
              const innerText = tryExtractDelta(saData);
              if (!innerText) return turn;
              setSubAgentCards((prev) => {
                const existing = prev[cardId];
                if (existing) {
                  return {
                    ...prev,
                    [cardId]: {
                      ...existing,
                      output: (existing.output ?? '') + innerText,
                    },
                  };
                }
                return {
                  ...prev,
                  [cardId]: {
                    turnId: cardId,
                    subSessionId: subAgentId,
                    parentSessionId,
                    taskSummary,
                    status: 'running',
                    spawnedAt: eventTimestamp,
                    output: innerText,
                  },
                };
              });
              return {
                ...turn,
                assistant: {
                  ...turn.assistant,
                  status: 'executing' as const,
                  renderMode: 'structured' as const,
                  timelineItems: appendOrUpdateSubAgentActivity(
                    turn.assistant.timelineItems ?? [],
                    {
                      id: `subagent-progress-${subAgentId}`,
                      type: 'subagent_progress' as const,
                      status: 'running',
                      name: subAgentId,
                      arguments: taskSummary,
                      output: innerText,
                      timestamp: eventTimestamp,
                      collapsed: false,
                    },
                    innerText,
                  ),
                },
              };
            }
            // 子代理 spawned → 创建卡片
            if (mappedType === 'spawned') {
              setSubAgentCards((prev) => ({
                ...prev,
                [cardId]: {
                  turnId: cardId,
                  subSessionId: subAgentId,
                  parentSessionId,
                  templateId: saData.template,
                  modelId: saData.model,
                  taskSummary,
                  status: 'running',
                  spawnedAt: eventTimestamp,
                },
              }));
              return {
                ...turn,
                assistant: {
                  ...turn.assistant,
                  status: 'executing' as const,
                  renderMode: 'structured' as const,
                  timelineItems: appendOrUpdateSubAgentActivity(
                    turn.assistant.timelineItems ?? [],
                    {
                      id: `subagent-spawned-${subAgentId}`,
                      type: 'subagent_spawned' as const,
                      status: 'running',
                      name: subAgentId,
                      arguments: taskSummary,
                      message: taskSummary,
                      timestamp: eventTimestamp,
                      collapsed: false,
                    },
                  ),
                },
              };
            }
            // 子代理 completed → 更新卡片
            if (mappedType === 'completed') {
              const terminalOutput = resolveSubAgentTerminalOutput(saData);
              setSubAgentCards((prev) => {
                const existing = prev[cardId];
                if (!existing) return prev;
                return {
                  ...prev,
                  [cardId]: {
                    ...existing,
                    status: saData.success ? 'completed' : 'failed',
                    parentSessionId:
                      existing.parentSessionId ?? parentSessionId,
                    completedAt: eventTimestamp,
                    success: !!saData.success,
                    output: existing.output || terminalOutput,
                  },
                };
              });
              return {
                ...turn,
                assistant: {
                  ...turn.assistant,
                  status: turn.assistant.status,
                  renderMode: 'structured' as const,
                  timelineItems: appendOrUpdateSubAgentActivity(
                    turn.assistant.timelineItems ?? [],
                    {
                      id: `subagent-completed-${subAgentId}`,
                      type: 'subagent_completed' as const,
                      status: saData.success ? 'success' : 'error',
                      name: subAgentId,
                      arguments: taskSummary,
                      output: terminalOutput,
                      message: terminalOutput || taskSummary,
                      timestamp: eventTimestamp,
                      collapsed: false,
                    },
                  ),
                },
              };
            }
            return turn;
          }
          if (ev.type === 'step') {
            const status = String(ev.status || 'executing');
            const message = getStepMessage(ev);
            const now = Date.now();
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
                      { ...last, text: (last.text ?? '') + '\n' + message },
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
                      id: createId(),
                      type: 'thinking' as const,
                      text: message,
                      timestamp: now,
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
                    id: createId(),
                    type: 'subconscious_step' as const,
                    status,
                    message,
                    timestamp: now,
                    collapsed: false,
                  },
                ],
              },
            };
          }
          if (ev.type === 'usage') {
            return {
              ...turn,
              assistant: { ...turn.assistant, usage: normalizeUsage(ev.usage) },
            };
          }
          if (ev.type === 'done') {
            completedTurnsRef.current.add(turnId);
            duplicateDeltaReplayOffsetRef.current.delete(turnId);
            if (ev.traceId) writeDebugTrace(ev.traceId);
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
                status: 'success' as const,
                isStreaming: false,
                answerMarkdown: resolveTerminalAssistantMarkdown(
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
          if (ev.type === 'cancelled') {
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
                        id: createId(),
                        type: 'subconscious_step' as const,
                        status: 'cancelled',
                        message: ev.message,
                        timestamp: Date.now(),
                        collapsed: false,
                      },
                    ]
                  : (turn.assistant.timelineItems ?? []),
              },
            };
          }
          if (ev.type === 'error') {
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'error' as const,
                isStreaming: false,
                timelineItems: [
                  ...(turn.assistant.timelineItems ?? []),
                  {
                    id: createId(),
                    type: 'subconscious_step' as const,
                    status: 'error',
                    message: ev.message || '请求失败',
                    timestamp: Date.now(),
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
      const messageId =
        typeof anyEv.messageId === 'string' ? anyEv.messageId : null;
      const count = (eventCountsRef.current.get(eventType) ?? 0) + 1;
      eventCountsRef.current.set(eventType, count);

      const runtimeEvent = toChatInteractionRuntimeEvent(ev, agentId);
      if (runtimeEvent) {
        setChatInteractionRuntimeEvents((prev) =>
          [...prev, runtimeEvent].slice(-MAX_CHAT_INTERACTION_RUNTIME_EVENTS),
        );
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
        compactLifecycleEventRef.current(ev);
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

          const migrateTurnKey = <T,>(map: Map<string, T>) => {
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
          const recoveredTurnId = createId();
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
              id: createId(),
              text: previousTurn?.userMessage.text ?? '',
              timestamp: Date.now(),
              status: 'success',
            },
            assistant: createAssistant(
              createId(),
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
        const mappedTurnId = messageIdToTurnIdRef.current.get(messageId)!;
        const mappedTurn = turnsRef.current.find(
          (t) => t.turnId === mappedTurnId,
        );
        if (mappedTurn?.assistant && mappedTurn.assistant.status !== 'success') {
          activeMessageIdsRef.current.add(messageId);
          setLoading(true);
        }
      }

      const targetTurnId = resolveEventTurnId(ev);
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
          typeof anyEv.injectedAt === 'number' ? anyEv.injectedAt : Date.now();
        const injectedRound =
          typeof anyEv.round === 'number' ? anyEv.round : undefined;
        const messageChars =
          typeof anyEv.messageChars === 'number'
            ? anyEv.messageChars
            : undefined;
        if (steeringId) {
          setSteeringInteractionQueue((prev) =>
            prev.map((item) =>
              item.steeringId !== steeringId
                ? item
                : {
                    ...item,
                    status: 'steering_injected' as const,
                    injectedAt,
                    injectedRound,
                    injectionLatencyMs: item.submittedAt
                      ? Math.max(0, injectedAt - item.submittedAt)
                      : undefined,
                  },
            ),
          );
          recordPerfEvent('chat.steering.injected', {
            steeringId,
            sessionId:
              typeof anyEv.sessionId === 'string' ? anyEv.sessionId : undefined,
            agentId:
              typeof anyEv.agentId === 'string' ? anyEv.agentId : undefined,
            round: injectedRound,
            messageChars,
            injectedAt,
          });
          scheduleInjectedSteeringDismiss(steeringId);
        }
        updateLastSequence(ev);
        return;
      }
      if (eventType === 'steering.created') {
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
          eventType === 'done' ||
          eventType === 'error' ||
          eventType === 'cancelled'
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
      const targetTurnExists = turnsRef.current.some(
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
        // 埋点：terminal 事件的目标 turn 已不存在
        if (
          eventType === 'done' ||
          eventType === 'error' ||
          eventType === 'cancelled'
        ) {
          console.warn(
            '[Pudding Chat] terminal event staleTarget (turn gone) — 消息可能被吞',
            {
              eventType,
              messageId,
              targetTurnId,
              sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
              currentTurns: turnsRef.current.length,
              currentTurnIds: turnsRef.current.map((t) => t.turnId),
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
            currentTurnIds: turnsRef.current.map((t) => t.turnId),
          });
        }
        return;
      }

      if (ev.type === 'usage' && ev.usage) setLatestUsage(ev.usage);
      if (ev.type === 'done' && ev.usage) setLatestUsage(ev.usage);

      // T-CACHE-008: Accumulate cache hit/miss for the main session
      if (ev.type === 'done' && ev.usage) {
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
        eventType === 'done' ||
        eventType === 'error' ||
        eventType === 'cancelled'
      ) {
        flushPendingDeltas();
        flushPendingThinking();
      }

      // T-102: 终端事件管理 loading 状态
      if (
        eventType === 'done' ||
        eventType === 'error' ||
        eventType === 'cancelled'
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
          const pendingCompactSwitch = pendingCompactSessionSwitchRef.current;
          if (pendingCompactSwitch) {
            pendingCompactSessionSwitchRef.current = null;
            setTimeout(() => {
              compactSessionSwitchRef.current(
                pendingCompactSwitch.sessionId,
                pendingCompactSwitch.title,
              );
            }, 0);
          }
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
        eventType === 'delta' || eventType === 'thinking'
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
      scheduleInjectedSteeringDismiss,
      clearWorkingAgentsForMessage,
      messageApi,
      mainSessionId,
      selectedSessionId,
    ],
  );

  const formatCompactAnswer = useCallback((result: ContextCompactionResult) => {
    return formatCompactSuccessMessage(result);
  }, []);

  const appendCompactTurn = useCallback(
    (
      text: string,
      status: AssistantStatus,
      result?: ContextCompactionResult,
      stableTurnId?: string,
      placeAtStart = false,
    ) => {
      const now = Date.now();
      const turnId = stableTurnId ?? createId();
      const existing = turnsRef.current.find(
        (turn) => turn.turnId === turnId,
      );
      if (existing) {
        if (existing.turnId.startsWith(COMPACTION_TURN_PREFIX)) {
          compactionLifecycleTurnsRef.current.set(existing.turnId, existing);
        }
        return existing.turnId;
      }
      const compactTurn: ChatTurn = {
        turnId,
        userMessage: {
          id: createId(),
          text: '',
          timestamp: now,
          status: 'success',
        },
        assistant: {
          id: createId(),
          status,
          timelineItems: [
            {
              id: createId(),
              type: 'subconscious_step' as const,
              status:
                status === 'error'
                  ? 'error'
                  : status === 'success'
                    ? 'success'
                    : 'compacting',
              message: text,
              timestamp: now,
              collapsed: false,
            },
          ],
          answerMarkdown: result ? formatCompactAnswer(result) : text,
          isStreaming: status === 'executing' || status === 'thinking',
          renderMode: 'structured',
        },
      };
      const nextTurns: ChatTurn[] = placeAtStart
        ? [compactTurn, ...turnsRef.current]
        : [...turnsRef.current, compactTurn];
      if (compactTurn.turnId.startsWith(COMPACTION_TURN_PREFIX)) {
        compactionLifecycleTurnsRef.current.set(
          compactTurn.turnId,
          compactTurn,
        );
      }
      turnsRef.current = nextTurns;
      setTurns(nextTurns);
      return turnId;
    },
    [formatCompactAnswer],
  );

  const updateCompactTurn = useCallback(
    (
      turnId: string,
      status: AssistantStatus,
      message: string,
      result?: ContextCompactionResult,
    ) => {
      const nextTurns = turnsRef.current.map((turn) => {
          if (turn.turnId !== turnId) return turn;
          const items = turn.assistant.timelineItems ?? [];
          const itemStatus =
            status === 'error'
              ? 'error'
              : status === 'success'
                ? 'success'
                : 'compacting';
          const compactItemIndex = items.findIndex(
            (item) => item.type === 'subconscious_step',
          );
          const nextItem = {
            id:
              compactItemIndex >= 0
                ? items[compactItemIndex].id
                : createId(),
            type: 'subconscious_step' as const,
            status: itemStatus,
            message,
            timestamp: Date.now(),
            collapsed: false,
          };
          const nextItems =
            compactItemIndex >= 0
              ? items.map((item, index) =>
                  index === compactItemIndex ? nextItem : item,
                )
              : [...items, nextItem];
          return {
            ...turn,
            assistant: {
              ...turn.assistant,
              status,
              isStreaming: status === 'executing' || status === 'thinking',
              renderMode: 'structured' as const,
              answerMarkdown: result ? formatCompactAnswer(result) : message,
              timelineItems: nextItems,
            },
          };
        });
      const updatedLifecycleTurn = nextTurns.find(
        (turn) =>
          turn.turnId === turnId &&
          turn.turnId.startsWith(COMPACTION_TURN_PREFIX),
      );
      if (updatedLifecycleTurn) {
        compactionLifecycleTurnsRef.current.set(turnId, updatedLifecycleTurn);
      }
      turnsRef.current = nextTurns;
      setTurns(nextTurns);
    },
    [formatCompactAnswer],
  );

  compactLifecycleEventRef.current = (
    event: AdminChatStreamEvent,
    options?: { allowSessionSwitch?: boolean; notify?: boolean },
  ) => {
    const raw = event as Record<string, unknown>;
    const compactionId =
      typeof raw.compactionId === 'string' && raw.compactionId
        ? raw.compactionId
        : 'unidentified-compaction';
    let compactTurnId =
      compactionTurnIdsRef.current.get(compactionId) ??
      activeCompactionTurnIdRef.current ??
      compactionTurnId(compactionId);
    const eventConversationId =
      typeof raw.conversationId === 'string' ? raw.conversationId : null;
    const sourceSessionId =
      typeof raw.sourceSessionId === 'string' ? raw.sourceSessionId : null;
    const placeAtStart =
      event.type === 'context.compaction.completed' &&
      eventConversationId !== null &&
      sourceSessionId !== null &&
      eventConversationId !== sourceSessionId;

    if (!turnsRef.current.some((turn) => turn.turnId === compactTurnId)) {
      compactTurnId = appendCompactTurn(
        event.type === 'context.compaction.failed'
          ? String(raw.error || '上下文压缩失败')
          : '正在压缩上下文…',
        event.type === 'context.compaction.failed' ? 'error' : 'executing',
        undefined,
        compactTurnId,
        placeAtStart,
      );
    }
    compactionTurnIdsRef.current.set(compactionId, compactTurnId);
    activeCompactionTurnIdRef.current = compactTurnId;

    if (event.type === 'context.compaction.started') {
      setLoading(true);
      updateCompactTurn(compactTurnId, 'executing', '正在压缩上下文…');
      if (options?.notify !== false) {
        messageApi.loading({
          content: '正在压缩上下文…',
          key: 'compaction-status',
          duration: 0,
        });
      }
      return;
    }

    setLoading(false);
    if (options?.notify !== false) {
      messageApi.destroy('compaction-status');
    }
    if (event.type === 'context.compaction.failed') {
      const errorMessage = String(raw.error || '上下文压缩失败');
      updateCompactTurn(compactTurnId, 'error', errorMessage);
      activeCompactionTurnIdRef.current = null;
      if (options?.notify !== false) {
        messageApi.error(errorMessage, 4);
      }
      return;
    }

    const compacted =
      raw.compaction && typeof raw.compaction === 'object'
        ? (raw.compaction as ContextCompactionResult)
        : undefined;
    updateCompactTurn(
      compactTurnId,
      'success',
      '上下文压缩完成',
      compacted,
    );
    activeCompactionTurnIdRef.current = null;

    const newSessionId =
      typeof raw.newSessionId === 'string' ? raw.newSessionId : null;
    const newSessionTitle =
      typeof raw.newSessionTitle === 'string'
        ? raw.newSessionTitle
        : '新会话';
    if (
      options?.allowSessionSwitch !== false &&
      newSessionId &&
      sessionIdRef.current !== newSessionId
    ) {
      messageApi.success(
        `上下文压缩完成，已切换到「${newSessionTitle}」`,
        4,
      );
      compactSessionSwitchRef.current(newSessionId, newSessionTitle);
    }
  };

  const replayMissedSessionEvents = useCallback(
    async (
      sessionId: string,
      signal?: AbortSignal,
      options?: { backfillActiveTerminalEvents?: boolean },
    ) => {
      const backfillActiveTerminalEvents =
        options?.backfillActiveTerminalEvents === true;
      let from = backfillActiveTerminalEvents
        ? resolveActiveSessionReplayFromSequence(
            lastSequenceNumRef.current,
            SESSION_EVENT_PAGE_SIZE,
          )
        : Math.max(0, lastSequenceNumRef.current + 1);
      const startedAt = performance.now();
      const initialFrom = from;
      let pageCount = 0;
      let eventCount = 0;
      let appliedCount = 0;
      let hasMore = true;
      try {
        // 修复：当 cursor 为 0（session 刚切换）时，先遍历所有页面找到真正的 maxSeq。
        // 否则只跳过第一页的事件会导致旧 done 事件（seq 51+）覆盖当前 optimistic turn。
        if (lastSequenceNumRef.current === 0) {
          let offset = 1;
          const maxPages = 20; // 安全上限：最多遍历 20 页（1000 events）
          for (let p = 0; p < maxPages; p++) {
            const page = await listSessionEventsPage(
              sessionId,
              offset,
              SESSION_EVENT_PAGE_SIZE,
              signal,
            );
            const pageList = (
              Array.isArray(page.events)
                ? page.events
                : Array.isArray(page.Events)
                  ? page.Events
                  : []
            ) as unknown[];
            if (pageList.length === 0) break;
            const maxSeqInPage = pageList
              .map(getSessionEventSequenceNum)
              .filter((x): x is number => x !== null)
              .reduce((m, x) => Math.max(m, x), 0);
            if (maxSeqInPage > 0) {
              lastSequenceNumRef.current = Math.max(
                lastSequenceNumRef.current,
                maxSeqInPage,
              );
              offset = maxSeqInPage + 1;
            }
            if (!(page.hasMore ?? page.HasMore)) break;
          }
          from =
            lastSequenceNumRef.current > 0 ? lastSequenceNumRef.current + 1 : 1;
          console.debug(
            '[Pudding Chat] replay cursor initialized (session switch)',
            {
              sessionId,
              cursor: lastSequenceNumRef.current,
              from,
              pagesScanned: Math.min(
                Math.ceil(lastSequenceNumRef.current / SESSION_EVENT_PAGE_SIZE),
                maxPages,
              ),
            },
          );
        }
        while (hasMore) {
          const pageStartedAt = performance.now();
          const page = await listSessionEventsPage(
            sessionId,
            from,
            SESSION_EVENT_PAGE_SIZE,
            signal,
          );
          pageCount++;
          const list = (
            Array.isArray(page.events)
              ? page.events
              : Array.isArray(page.Events)
                ? page.Events
                : []
          ) as unknown[];
          eventCount += list.length;
          recordPerfEvent('chat.replay.page', {
            sessionId,
            from,
            limit: SESSION_EVENT_PAGE_SIZE,
            events: list.length,
            hasMore: Boolean(page.hasMore ?? page.HasMore),
            elapsedMs: Math.round(performance.now() - pageStartedAt),
          });
          if (list.length === 0) break;

          for (const item of list) {
            const normalized = normalizeSessionEvent(item);
            if (!normalized) continue;
            const seq = normalized.sequenceNum;
            if (
              typeof seq === 'number' &&
              Number.isFinite(seq) &&
              seq <= lastSequenceNumRef.current
            ) {
              // SSE 可能在最后的 done/error/cancelled 之前断开，但 cursor 已被后续中间帧推进。
              // 活跃 turn 的补偿 replay 必须允许回扫 terminal 事件，否则 UI 会一直停在最近一次工具结果。
              if (
                !backfillActiveTerminalEvents ||
                !HISTORICAL_REPLAY_TERMINAL_EVENTS.has(normalized.type)
              ) {
                continue;
              }
            }
            applySessionEvent(normalized);
            appliedCount++;
          }

          const maxSeqInPage = list
            .map(getSessionEventSequenceNum)
            .filter((x): x is number => x !== null)
            .reduce((m, x) => Math.max(m, x), Number.NaN);

          if (Number.isFinite(maxSeqInPage)) {
            from = Math.max(from, Number(maxSeqInPage) + 1);
          } else {
            hasMore = false;
          }

          hasMore = hasMore && Boolean(page.hasMore ?? page.HasMore);
        }
        recordPerfEvent('chat.replay.complete', {
          sessionId,
          from: initialFrom,
          nextFrom: from,
          pages: pageCount,
          events: eventCount,
          applied: appliedCount,
          lastSequenceNum: lastSequenceNumRef.current,
          elapsedMs: Math.round(performance.now() - startedAt),
        });
        // 埋点：replay 完成统计
        console.debug('[Pudding Chat] replay complete', {
          sessionId,
          from: initialFrom,
          nextFrom: from,
          pages: pageCount,
          events: eventCount,
          applied: appliedCount,
          lastSequenceNum: lastSequenceNumRef.current,
        });
        logChatDiag('events.replay.complete', {
          sessionId,
          from: initialFrom,
          nextFrom: from,
          pages: pageCount,
          events: eventCount,
          applied: appliedCount,
          lastSequenceNum: lastSequenceNumRef.current,
        });
      } catch (error) {
        recordPerfEvent('chat.replay.error', {
          sessionId,
          from: initialFrom,
          pages: pageCount,
          events: eventCount,
          applied: appliedCount,
          aborted: signal?.aborted === true,
          error: error instanceof Error ? error.message : String(error),
          elapsedMs: Math.round(performance.now() - startedAt),
        });
        // 异常日志：replay 失败可能导致消息被吞
        console.warn('[Pudding Chat] replay ERROR', {
          sessionId,
          from: initialFrom,
          pages: pageCount,
          events: eventCount,
          applied: appliedCount,
          aborted: signal?.aborted === true,
          error: error instanceof Error ? error.message : String(error),
        });
        logChatDiag('events.replay.error', {
          sessionId,
          from: initialFrom,
          pages: pageCount,
          events: eventCount,
          applied: appliedCount,
          aborted: signal?.aborted === true,
          error,
        });
        throw error;
      }
    },
    [applySessionEvent, listSessionEventsPage, normalizeSessionEvent],
  );

  const replayMissedSessionEventsIfNeeded = useCallback(
    async (
      sessionId: string,
      options: {
        signal?: AbortSignal;
        reason: string;
        hasActiveMessages?: boolean;
      },
    ) => {
      const hasActiveMessages =
        options.hasActiveMessages ??
        pruneTrackedActiveMessages(`replay-${options.reason}`);
      const lastSseEventAt =
        sseSessionIdRef.current === sessionId
          ? lastSseEventAtRef.current
          : null;
      const now = performance.now();
      if (
        !shouldRunSessionReplayCompensation({
          hasActiveMessages,
          lastSseEventAt,
          now,
        })
      ) {
        recordPerfEvent(
          'chat.replay.skipped',
          {
            sessionId,
            reason: options.reason,
            hasActiveMessages,
            activeMessageCount: activeMessageIdsRef.current.size,
            sinceLastSseEventMs:
              lastSseEventAt == null ? null : Math.round(now - lastSseEventAt),
          },
          { throttleMs: 2_000 },
        );
        // 埋点：replay 被跳过
        console.debug('[Pudding Chat] replay skipped', {
          reason: options.reason,
          sessionId,
          hasActiveMessages,
          activeMessageCount: activeMessageIdsRef.current.size,
          sinceLastSseEventMs:
            lastSseEventAt == null ? null : Math.round(now - lastSseEventAt),
        });
        logChatDiag('events.replay.skipped', {
          reason: options.reason,
          sessionId,
          hasActiveMessages,
          activeMessageCount: activeMessageIdsRef.current.size,
          sinceLastSseEventMs:
            lastSseEventAt == null ? null : Math.round(now - lastSseEventAt),
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
        });
        return false;
      }

      await replayMissedSessionEvents(sessionId, options.signal, {
        backfillActiveTerminalEvents: hasActiveMessages,
      });
      return true;
    },
    [pruneTrackedActiveMessages, replayMissedSessionEvents],
  );

  const replayLatestTurnSessionEvents = useCallback(
    async (
      sessionId: string,
      loadedTurns: ChatTurn[],
      signal?: AbortSignal,
    ) => {
      const completedAssistantTurns = countCompletedAssistantTurns(loadedTurns);
      const normalizedEvents: Array<
        AdminChatStreamEvent & { sequenceNum?: number }
      > = [];
      let from = 1;
      let hasMore = true;

      while (hasMore) {
        const page = await listSessionEventsPage(
          sessionId,
          from,
          SESSION_EVENT_PAGE_SIZE,
          signal,
        );
        const list = (
          Array.isArray(page.events)
            ? page.events
            : Array.isArray(page.Events)
              ? page.Events
              : []
        ) as unknown[];
        if (list.length === 0) break;

        let maxSeqInPage = Number.NaN;
        for (const item of list) {
          const normalized = normalizeSessionEvent(item);
          if (!normalized) continue;
          normalizedEvents.push(normalized);
          if (
            typeof normalized.sequenceNum === 'number' &&
            Number.isFinite(normalized.sequenceNum)
          ) {
            maxSeqInPage = Math.max(maxSeqInPage, normalized.sequenceNum);
          }
        }

        if (Number.isFinite(maxSeqInPage)) {
          from = Number(maxSeqInPage) + 1;
        } else {
          hasMore = false;
        }
        hasMore = hasMore && Boolean(page.hasMore ?? page.HasMore);
      }

      let doneCount = 0;
      let tailStart = 0;
      for (let i = 0; i < normalizedEvents.length; i++) {
        if (normalizedEvents[i].type === 'done') {
          doneCount++;
          if (doneCount <= completedAssistantTurns) {
            tailStart = i + 1;
          }
        }
      }

      // 埋点：replayLatestTurn 对齐诊断（doneCount 错位会导致 optimistic turn 被吞）
      console.debug('[Pudding Chat] replayLatestTurn align', {
        sessionId,
        completedAssistantTurns,
        doneCount,
        totalEvents: normalizedEvents.length,
        tailStart,
        replayTailLen: normalizedEvents.length - tailStart,
      });

      const previous = normalizedEvents[tailStart - 1];
      if (
        previous &&
        typeof previous.sequenceNum === 'number' &&
        Number.isFinite(previous.sequenceNum)
      ) {
        lastSequenceNumRef.current = Math.max(
          lastSequenceNumRef.current,
          previous.sequenceNum,
        );
      }

      const replayTail = normalizedEvents.slice(tailStart);
      const shouldHydrate = shouldHydrateSessionEventReplay(replayTail);
      const previousHydrateMode = hydrateSessionReplayRef.current;
      hydrateSessionReplayRef.current = shouldHydrate;
      try {
        for (const event of replayTail) {
          if (
            typeof event.sequenceNum === 'number' &&
            Number.isFinite(event.sequenceNum) &&
            event.sequenceNum <= lastSequenceNumRef.current
          ) {
            continue;
          }
          applySessionEvent(event);
        }
      } finally {
        hydrateSessionReplayRef.current = previousHydrateMode;
      }
    },
    [applySessionEvent, listSessionEventsPage, normalizeSessionEvent],
  );

  const startSessionEventStream = useCallback(
    (sessionId: string) => {
      if (!sessionId) return;
      const previousStreamSessionId = sseSessionIdRef.current;
      stopSessionEventStream();
      resetStreamCursorForSessionChange(previousStreamSessionId, sessionId);
      sseSessionIdRef.current = sessionId;
      syncSessionIdentity();
      recordPerfEvent('chat.sse.start', { sessionId });
      logChatDiag('sse.start', {
        sessionId,
        previousStreamSessionId,
        selectedSessionId: selectedSessionIdRef.current,
        sessionIdRef: sessionIdRef.current,
        lastSequenceNum: lastSequenceNumRef.current,
        activeMessageCount: activeMessageIdsRef.current.size,
        turnCount: turnsRef.current.length,
      });

      const ctrl = new AbortController();
      sessionEventsAbortRef.current = ctrl;

      const scheduleReconnect = () => {
        if (sessionEventsReconnectTimerRef.current != null) return;
        recordPerfEvent('chat.sse.reconnectScheduled', { sessionId });
        sessionEventsReconnectTimerRef.current = window.setTimeout(async () => {
          sessionEventsReconnectTimerRef.current = null;
          if (sseSessionIdRef.current !== sessionId) return;
          try {
            await replayMissedSessionEvents(sessionId);
          } catch {
            // 网络波动期间忽略，等待下次补偿
          }
          if (sseSessionIdRef.current === sessionId) {
            startSessionEventStream(sessionId);
          }
        }, 1200);
      };

      const onOnline = () => {
        scheduleReconnect();
      };

      window.addEventListener('online', onOnline);

      // 周期性补偿：流式生成时短间隔追帧，空闲时回到低频兜底，避免用户看到 8 秒一跳。
      const scheduleReplayPoll = () => {
        if (sseSessionIdRef.current !== sessionId || ctrl.signal.aborted)
          return;
        const hasActiveMessages = pruneTrackedActiveMessages(
          'replay-poll-schedule',
        );
        const delayMs = resolveSessionReplayPollInterval(hasActiveMessages);
        recordPerfEvent(
          'chat.replay.pollScheduled',
          {
            sessionId,
            delayMs,
            activeMessageCount: activeMessageIdsRef.current.size,
            activeTurn: hasActiveMessages,
          },
          { throttleMs: 2_000 },
        );
        sessionEventsPollTimerRef.current = window.setTimeout(async () => {
          sessionEventsPollTimerRef.current = null;
          if (sseSessionIdRef.current !== sessionId || ctrl.signal.aborted)
            return;
          const pollStartedAt = performance.now();
          let shouldContinue = true;
          try {
            const ranReplay = await replayMissedSessionEventsIfNeeded(
              sessionId,
              {
                signal: ctrl.signal,
                reason: 'poll',
                hasActiveMessages: pruneTrackedActiveMessages(
                  'replay-poll-execute',
                ),
              },
            );
            recordPerfEvent('chat.replay.pollComplete', {
              sessionId,
              delayMs,
              activeMessageCount: activeMessageIdsRef.current.size,
              skipped: !ranReplay,
              elapsedMs: Math.round(performance.now() - pollStartedAt),
            });
          } catch (error) {
            recordPerfEvent('chat.replay.pollError', {
              sessionId,
              delayMs,
              activeMessageCount: activeMessageIdsRef.current.size,
              aborted: ctrl.signal.aborted,
              error: error instanceof Error ? error.message : String(error),
              elapsedMs: Math.round(performance.now() - pollStartedAt),
            });
            // P0 v2: session 404 → 停止轮询，并通过统一入口清理 UI
            if (isSessionNotFoundError(error)) {
              console.debug(
                '[Pudding Chat] replay poll stopped: session not found',
                {
                  sessionId,
                  reason: error.message,
                },
              );
              logChatDiag('events.replay.sessionNotFound', {
                sessionId,
                error: error.message,
              });
              handleSessionNotFound(sessionId, 'replay-poll-404');
              shouldContinue = false;
            }
            // 其他错误：下次轮询重试（shouldContinue 保持 true）
          } finally {
            // P0 v2: 只有 shouldContinue && 流仍活跃时才继续调度
            if (
              shouldContinue &&
              !ctrl.signal.aborted &&
              sseSessionIdRef.current === sessionId
            ) {
              scheduleReplayPoll();
            }
          }
        }, delayMs);
      };
      scheduleReplayPoll();

      try {
        subscribeSessionEvents(
          sessionId,
          (ev) => {
            if (ctrl.signal.aborted || sseSessionIdRef.current !== sessionId)
              return;
            lastSseEventAtRef.current = performance.now();
            const rawEv = ev as Record<string, unknown>;
            const messageId =
              typeof rawEv.messageId === 'string' ? rawEv.messageId : null;
            if (messageId && !streamStartAtRef.current.has(messageId)) {
              streamStartAtRef.current.set(messageId, performance.now());
              recordPerfEvent('chat.sse.firstEvent', {
                sessionId,
                messageId,
                eventType: ev.type,
                sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
              });
              logChatDiag('sse.firstEvent', {
                sessionId,
                messageId,
                eventType: ev.type,
                sequenceNum: (ev as { sequenceNum?: number }).sequenceNum,
                selectedSessionId: selectedSessionIdRef.current,
                sessionIdRef: sessionIdRef.current,
              });
            }
            applySessionEvent(ev);
          },
          ctrl.signal,
          {
            // P0 v2: SSE 连接异常（404/网络错误等）→ 通过统一入口清理
            onError: (_error, httpStatus) => {
              if (ctrl.signal.aborted || sseSessionIdRef.current !== sessionId)
                return;

              // P0 v2: 404 (deleted) / 410 (frozen/archived) → 终态，不重连
              if (httpStatus === 404 || httpStatus === 410) {
                console.debug(
                  '[Pudding Chat] SSE session terminal status, disposing',
                  { sessionId, httpStatus },
                );
                logChatDiag('sse.sessionTerminal', { sessionId, httpStatus });
                handleSessionNotFound(sessionId, `sse-${httpStatus}`);
                return;
              }

              // 非终态错误（500/网络波动等）→ 调度重连
              logChatDiag('sse.error.reconnect', { sessionId, httpStatus });
              scheduleReconnect();
            },
          },
        );
      } catch {
        scheduleReconnect();
      }

      const originalAbort = ctrl.abort.bind(ctrl);
      ctrl.abort = () => {
        window.removeEventListener('online', onOnline);
        recordPerfEvent('chat.sse.stop', { sessionId });
        logChatDiag('sse.stop', {
          sessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
        });
        originalAbort();
      };
    },
    [
      applySessionEvent,
      handleSessionNotFound,
      pruneTrackedActiveMessages,
      replayMissedSessionEvents,
      replayMissedSessionEventsIfNeeded,
      resetStreamCursorForSessionChange,
      stopSessionEventStream,
    ],
  );

  // Keep ref in sync for applySessionEvent compaction switch (breaks circular dependency)
  startSessionEventStreamRef.current = startSessionEventStream;

  // ── toTurnsFromHistory ─────────────────────────────────────
  const toTurnsFromHistory = useCallback(
    (res: MessageListResponse): ChatTurn[] => {
      const mapped: ChatTurn[] = [];
      let pendingUserIndex: number | null = null;
      for (const item of res.items || []) {
        if (item.role === 'user') {
          mapped.push({
            turnId: item.turnId || `hist-turn-${item.id}`,
            userMessage: {
              id: item.messageId || `hist-user-${item.id}`,
              text: item.content,
              timestamp: item.createdAt,
              status: 'success',
              dbMessageId: item.id,
            },
            assistant: createAssistant(
              `hist-assistant-${item.id}`,
              'legacy',
              'success',
              false,
            ),
          });
          pendingUserIndex = mapped.length - 1;
          continue;
        }
        const thinkingItems: TimelineItem[] = (item.thinking || []).map(
          (t: { text: string }, idx: number) => ({
            id: `hist-think-${item.id}-${idx}`,
            type: 'thinking' as const,
            text: t.text,
            timestamp: item.createdAt,
            collapsed: true,
          }),
        );
        let targetIndex = pendingUserIndex;
        if (targetIndex === null) {
          mapped.push({
            turnId: `hist-turn-orphan-${item.id}`,
            userMessage: {
              id: `hist-user-orphan-${item.id}`,
              text: '',
              timestamp: item.createdAt,
              status: 'success',
            },
            assistant: createAssistant(
              `hist-assistant-orphan-${item.id}`,
              'legacy',
              'success',
              false,
            ),
          });
          targetIndex = mapped.length - 1;
        }
        mapped[targetIndex] = {
          ...mapped[targetIndex],
          turnId: item.turnId || mapped[targetIndex].turnId,
          source:
            item.sourceType === 'system_command'
              ? {
                  sourceId: item.sourceId || 'system',
                  sourceType: 'system_command',
                  displayName: item.sourceName || 'System',
                  avatarEmoji: '⚙',
                  avatarColor: stringToColor(item.sourceId || 'system'),
                }
              : mapped[targetIndex].source,
          assistant: {
            ...mapped[targetIndex].assistant,
            id: item.messageId || mapped[targetIndex].assistant.id,
            status: 'success',
            isStreaming: false,
            usage: normalizeUsage(item.usage),
            answerMarkdown: item.content,
            timelineItems: thinkingItems,
            renderMode: thinkingItems.length > 0 ? 'structured' : 'legacy',
          },
        };
        pendingUserIndex = null;
      }
      return mapped;
    },
    [],
  );

  const reconcileCompletedSessionMessages = useCallback(
    async (sessionId: string) => {
      const res = await listSessionMessages(
        sessionId,
        undefined,
        MESSAGE_PAGE_SIZE,
      );
      const loadedTurns = toTurnsFromHistory(res);
      if (
        loadedTurns.length === 0 ||
        shouldReplayEventsAfterHistory(loadedTurns)
      )
        return;
      const blockReason = getHistoryReconcileBlockReason(
        turnsRef.current,
        loadedTurns,
      );
      if (blockReason) {
        recordPerfEvent('chat.history.reconcile.skipped', {
          reason: blockReason,
          sessionId,
          currentTurns: turnsRef.current.length,
          loadedTurns: loadedTurns.length,
          currentLatestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          loadedLatestUser:
            loadedTurns[loadedTurns.length - 1]?.userMessage.text,
        });
        // 埋点：对账被阻止，记录原因（消息被吞的关键诊断点）
        console.debug('[Pudding Chat] reconcile skipped', {
          reason: blockReason,
          sessionId,
          currentTurns: turnsRef.current.length,
          loadedTurns: loadedTurns.length,
          currentLatestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          loadedLatestUser:
            loadedTurns[loadedTurns.length - 1]?.userMessage.text,
        });
        logChatDiag('history.reconcile.skipped', {
          reason: blockReason,
          sessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          currentTurns: turnsRef.current.length,
          loadedTurns: loadedTurns.length,
          currentLatestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          loadedLatestUser:
            loadedTurns[loadedTurns.length - 1]?.userMessage.text,
        });
        return;
      }

      // 埋点：对账将替换 turns，记录替换前后的关键信息（消息被吞的关键诊断点）
      console.warn('[Pudding Chat] reconcile REPLACING turns', {
        sessionId,
        before: {
          count: turnsRef.current.length,
          latestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          latestStatus:
            turnsRef.current[turnsRef.current.length - 1]?.assistant.status,
          latestIsStreaming:
            turnsRef.current[turnsRef.current.length - 1]?.assistant
              .isStreaming,
        },
        after: {
          count: loadedTurns.length,
          latestUser: loadedTurns[loadedTurns.length - 1]?.userMessage.text,
          latestStatus: loadedTurns[loadedTurns.length - 1]?.assistant.status,
        },
      });
      logChatDiag('history.reconcile.replace', {
        sessionId,
        selectedSessionId: selectedSessionIdRef.current,
        sessionIdRef: sessionIdRef.current,
        beforeCount: turnsRef.current.length,
        afterCount: loadedTurns.length,
        beforeLatestUser:
          turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
        afterLatestUser: loadedTurns[loadedTurns.length - 1]?.userMessage.text,
        beforeLatestStatus:
          turnsRef.current[turnsRef.current.length - 1]?.assistant.status,
        afterLatestStatus:
          loadedTurns[loadedTurns.length - 1]?.assistant.status,
      });
      const reconciledTurns = mergeHistoryWithLifecycleTurns(
        loadedTurns,
        Array.from(compactionLifecycleTurnsRef.current.values()),
      );
      setTurns(reconciledTurns);
      turnsRef.current = reconciledTurns;
      latestTurnIdRef.current =
        reconciledTurns[reconciledTurns.length - 1]?.turnId ?? null;
      setLoading(false);
      await syncCompletedHistoryEventCursor(sessionId);
    },
    [syncCompletedHistoryEventCursor, toTurnsFromHistory],
  );

  // ── resetConversation ──────────────────────────────────────
  const resetConversation = useCallback(
    async (nextWorkspaceId?: string, nextAgentId?: string) => {
      if (creatingSession) return undefined;
      flushPendingDeltas();
      abortRef.current?.abort();
      abortRef.current = null;
      stopSessionEventStream();
      turnsRef.current = [];
      setTurns([]);
      setError(null);
      setLatestUsage(undefined);
      setLoading(false);
      setChatInteractionRuntimeEvents([]);
      setHasMoreMessages(false);
      setOldestMessageCursor(null);
      lastSequenceNumRef.current = 0;
      latestTurnIdRef.current = null;
      messageIdToTurnIdRef.current.clear();
      compactionTurnIdsRef.current.clear();
      compactionLifecycleTurnsRef.current.clear();
      activeCompactionTurnIdRef.current = null;
      const targetWorkspaceId = nextWorkspaceId ?? workspaceId;
      const targetAgentId = nextAgentId ?? agentId;
      if (!targetWorkspaceId || !targetAgentId) return undefined;
      setCreatingSession(true);
      try {
        const selected = agents.find((a) => a.agentId === targetAgentId);
        const agName = selected ? getAgentName(selected) : '新对话';
        const templateId =
          selected?.sourceTemplateId || `global:${targetAgentId}`;
        const session = await createSession(
          targetWorkspaceId,
          templateId,
          undefined,
          agName,
        );
        sessionIdRef.current = session.sessionId;
        forceNewSessionRef.current = false;
        setSelectedSessionId(session.sessionId);
        setSessions((prev) => [toSessionListItem(session, agName), ...prev]);
        return session.sessionId;
      } catch {
        messageApi.error('创建会话失败');
        return undefined;
      } finally {
        setCreatingSession(false);
      }
    },
    [
      workspaceId,
      agentId,
      agents,
      creatingSession,
      messageApi,
      stopSessionEventStream,
    ],
  );

  // ── Effects: load workspaces ───────────────────────────────
  useEffect(() => {
    let a = true;
    (async () => {
      setWorkspaceLoading(true);
      try {
        const items = await listWorkspaces();
        if (!a) return;
        setWorkspaces(items);
        const wid = resolveInitialWorkspaceId(
          items,
          routeSelection.workspaceId,
        );
        setWorkspaceId(wid);
        if (!wid) setError('无可用工作空间');
      } catch (e: unknown) {
        if (a) setError(e instanceof Error ? e.message : '加载失败');
      } finally {
        if (a) setWorkspaceLoading(false);
      }
    })();
    return () => {
      a = false;
    };
  }, [routeSelection.workspaceId]);

  // ── Effects: load agents ───────────────────────────────────
  useEffect(() => {
    let a = true;
    (async () => {
      if (!workspaceId) {
        setAgents([]);
        setAgentId(undefined);
        return;
      }
      setAgentLoading(true);
      try {
        const items = await listWorkspaceAgents(workspaceId);
        if (!a) return;
        if (items.length === 0) {
          try {
            const c = await createWorkspaceAgent(workspaceId, {
              name: 'Pudding 助手',
              displayName: '布丁',
              sourceTemplateId: 'global:general-assistant',
            });
            setAgents([c]);
            setAgentId(c.agentId);
          } catch {
            setAgents([]);
            setAgentId(undefined);
          }
        } else {
          setAgents(items);
          setAgentId(resolveInitialAgentId(items, routeSelection.agentId));
        }
      } catch (e: unknown) {
        if (a) setError(e instanceof Error ? e.message : '加载Agent失败');
      } finally {
        if (a) setAgentLoading(false);
      }
    })();
    return () => {
      a = false;
    };
  }, [workspaceId]);

  useEffect(() => {
    if (!routeSelection.agentId || agents.length === 0) return;
    if (agentId) return;
    const nextAgentId = resolveInitialAgentId(agents, routeSelection.agentId);
    if (nextAgentId && nextAgentId !== agentId) setAgentId(nextAgentId);
  }, [routeSelection.agentId, agents, agentId]);

  // ── refreshSessions ────────────────────────────────────────
  const refreshSessions = useCallback(
    async (options?: { preserveSessionId?: string }) => {
      if (!workspaceId) return;
      try {
        const list = await listSessions(workspaceId);
        const serverMapped: SessionListItem[] = (list || [])
          .filter((s: SessionRecord) => s.status !== 'Frozen')
          .map((s: SessionRecord) => toSessionListItem(s))
          .sort((a, b) => b.timestamp - a.timestamp);

        setSessions((prev) => {
          // ADR: merge 模式 — 如果服务端缺少乐观会话项，保留本地项不被覆盖
          if (
            options?.preserveSessionId &&
            !serverMapped.some((s) => s.sessionId === options.preserveSessionId)
          ) {
            const localItem = prev.find(
              (s) => s.sessionId === options.preserveSessionId,
            );
            if (localItem) return [localItem, ...serverMapped];
          }
          return serverMapped;
        });
      } catch {
        /* 刷新失败保留现有列表，不清空 */
      }
    },
    [workspaceId],
  );

  // ADR: 移除 turns.length 触发，会话列表由 sendMessage 同步插入 + 后台刷新补偿
  useEffect(() => {
    refreshSessions();
  }, [refreshSessions]);

  const switchToCompactedSessionPreservingTurns = useCallback(
    (sessionId: string, title?: string | null) => {
      if (!sessionId) return;
      const previousSessionId =
        sessionIdRef.current ?? selectedSessionIdRef.current ?? null;
      if (
        previousSessionId === sessionId &&
        selectedSessionIdRef.current === sessionId
      ) {
        logChatDiag('compact.switch.noop', {
          sessionId,
          title,
          previousSessionId,
          selectedSessionId: selectedSessionIdRef.current,
        });
        return;
      }
      logChatDiag('compact.switch.apply', {
        sessionId,
        title,
        previousSessionId,
        selectedSessionId: selectedSessionIdRef.current,
        sessionIdRef: sessionIdRef.current,
        turnCount: turnsRef.current.length,
      });

      sessionIdRef.current = sessionId;
      selectedSessionIdRef.current = sessionId;
      setSelectedSessionId(sessionId);
      setMainSessionId(sessionId);
      forceNewSessionRef.current = false;
      // Conversation event sequences are scoped per conversation. Carrying the
      // source cursor into the successor would skip its compaction-origin event
      // and every early event whose sequence is lower than the old cursor.
      lastSequenceNumRef.current = 0;
      messageIdToTurnIdRef.current.clear();
      projectionOwnedSessionIdsRef.current.delete(sessionId);
      setSessions((prev) => {
        const idx = prev.findIndex((item) => item.sessionId === sessionId);
        const timestamp = Date.now();
        if (idx >= 0) {
          const updated = { ...prev[idx], timestamp, mainSessionId: sessionId };
          if (title && (!updated.title || updated.title === '对话')) {
            updated.title = title;
          }
          return [updated, ...prev.slice(0, idx), ...prev.slice(idx + 1)];
        }
        return [
          {
            sessionId,
            title: title || '压缩后的新会话',
            timestamp,
            mainSessionId: sessionId,
          },
          ...prev,
        ];
      });
      startSessionEventStream(sessionId);
      refreshSessions({ preserveSessionId: sessionId });
    },
    [refreshSessions, startSessionEventStream],
  );

  compactSessionSwitchRef.current = switchToCompactedSessionPreservingTurns;

  // T-201: 停止工作区通知 SSE
  const stopWorkspaceNotificationStream = useCallback(() => {
    if (workspaceNotifyReconnectRef.current != null) {
      window.clearTimeout(workspaceNotifyReconnectRef.current);
      workspaceNotifyReconnectRef.current = null;
    }
    workspaceNotifyAbortRef.current?.abort();
    workspaceNotifyAbortRef.current = null;
    workspaceNotifyWsIdRef.current = null;
  }, []);

  // T-201: 启动工作区通知 SSE（独立于会话 SSE）
  const startWorkspaceNotificationStream = useCallback(
    (workspaceId: string) => {
      if (!workspaceId) return;
      stopWorkspaceNotificationStream();
      workspaceNotifyWsIdRef.current = workspaceId;

      const ctrl = new AbortController();
      workspaceNotifyAbortRef.current = ctrl;

      const scheduleReconnect = () => {
        if (workspaceNotifyReconnectRef.current != null) return;
        workspaceNotifyReconnectRef.current = window.setTimeout(() => {
          workspaceNotifyReconnectRef.current = null;
          if (workspaceNotifyWsIdRef.current !== workspaceId) return;
          startWorkspaceNotificationStream(workspaceId);
        }, 5000);
      };

      subscribeWorkspaceNotifications(
        workspaceId,
        (ev: WorkspaceNotification) => {
          if (
            ctrl.signal.aborted ||
            workspaceNotifyWsIdRef.current !== workspaceId
          )
            return;

          // 子代理完成 → 对应 sessionId 未读计数+1 + Toast
          if (ev.type === 'notification.sub_agent_completed') {
            setSessionUnreadCounts((prev) => ({
              ...prev,
              [ev.sessionId]: (prev[ev.sessionId] || 0) + 1,
            }));
            notification.info({
              message: '子代理完成',
              description: ev.sessionTitle
                ? `会话「${ev.sessionTitle}」的子代理任务已完成`
                : `会话 ${ev.sessionId} 的子代理任务已完成`,
              placement: 'bottomRight',
              duration: 4,
            });
          }

          // 新会话创建通知 → 刷新会话列表
          if (ev.type === 'notification.session_created') {
            refreshSessions();
          }
        },
        ctrl.signal,
      );
    },
    [stopWorkspaceNotificationStream, refreshSessions],
  );

  // 记录最新 turn，供持久 SSE 的异步事件路由使用。
  useEffect(() => {
    if (turns.length === 0) {
      latestTurnIdRef.current = null;
      return;
    }
    latestTurnIdRef.current = turns[turns.length - 1].turnId;
  }, [turns.length]);

  // ── handleSelectSession ────────────────────────────────────
  const handleSelectSession = useCallback(
    async (sid: string, options?: { agentId?: string }) => {
      if (sid === selectedSessionId && turns.length > 0) {
        logChatDiag('session.select.noop', {
          sid,
          selectedSessionId,
          sessionIdRef: sessionIdRef.current,
          turnCount: turns.length,
        });
        return turns.length;
      }
      const traceId = `session-select-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
      const selectStartedAt = performance.now();
      const sessionAgentId =
        options?.agentId ??
        sessions.find((session) => session.sessionId === sid)?.principalId ??
        agentId;
      const shouldRestartSameSessionStream = sid === selectedSessionId;
      logChatDiag('session.select.start', {
        traceId,
        sid,
        previousSelectedSessionId: selectedSessionId,
        previousSessionIdRef: sessionIdRef.current,
        mainSessionId,
        routeSessionId: routeSelection.sessionId,
        sessionAgentId,
        previousTurnCount: turnsRef.current.length,
        restartStream: shouldRestartSameSessionStream,
      });
      clearSessionUnread(sid);
      historyAbortRef.current?.abort();
      stopSessionEventStream();
      projectionOwnedSessionIdsRef.current.delete(sid);
      setSelectedSessionId(sid);
      sessionIdRef.current = sid;
      forceNewSessionRef.current = false;
      lastSequenceNumRef.current = 0;
      messageIdToTurnIdRef.current.clear();
      compactionTurnIdsRef.current.clear();
      compactionLifecycleTurnsRef.current.clear();
      activeCompactionTurnIdRef.current = null;
      latestTurnIdRef.current = null;
      turnsRef.current = [];
      setTurns([]);
      setChatInteractionRuntimeEvents([]);
      setHasMoreMessages(false);
      setOldestMessageCursor(null);
      setHistoryLoading(true);
      recordPerfStep('session.select', 'state.reset', selectStartedAt, {
        traceId,
        sessionId: sid,
        agentId: sessionAgentId,
        previousSessionId: selectedSessionId,
        restartStream: shouldRestartSameSessionStream,
      });
      const ctrl = new AbortController();
      historyAbortRef.current = ctrl;
      try {
        const listStartedAt = performance.now();
        const res: MessageListResponse = await listSessionMessages(
          sid,
          undefined,
          MESSAGE_PAGE_SIZE,
        );
        recordPerfStep(
          'session.select',
          'api.listSessionMessages',
          listStartedAt,
          {
            traceId,
            sessionId: sid,
            agentId: sessionAgentId,
            messageCount: res.items?.length ?? 0,
            hasMore: res.hasMore,
            oldestCreatedAt: res.oldestCreatedAt ?? null,
          },
        );
        if (ctrl.signal.aborted) return;
        const mapStartedAt = performance.now();
        const loadedTurns = toTurnsFromHistory(res);
        logChatDiag('session.select.history.loaded', {
          traceId,
          sid,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          messageCount: res.items?.length ?? 0,
          loadedTurns: loadedTurns.length,
          loadedLatestUser:
            loadedTurns[loadedTurns.length - 1]?.userMessage.text,
          loadedLatestStatus:
            loadedTurns[loadedTurns.length - 1]?.assistant.status,
        });
        recordPerfStep('session.select', 'history.toTurns', mapStartedAt, {
          traceId,
          sessionId: sid,
          agentId: sessionAgentId,
          turnCount: loadedTurns.length,
          activeTurnCount: loadedTurns.filter(isActiveAssistantTurn).length,
        });
        // 修复：如果有活跃 turn（正在流式传输的 optimistic turn），不覆盖 turns。
        // 否则 setTurns(loadedTurns) 会移除 SSE 流正在更新的 optimistic turn，
        // 导致 done 事件找不到目标 turn，消息被吞。
        const activeCount = turnsRef.current.filter(
          isActiveAssistantTurn,
        ).length;
        if (activeCount > 0) {
          console.debug(
            '[Pudding Chat] session select preserving active turns',
            {
              sid,
              activeCount,
              loadedTurnCount: loadedTurns.length,
            },
          );
          logChatDiag('session.select.preserveActiveTurns', {
            traceId,
            sid,
            activeCount,
            loadedTurns: loadedTurns.length,
            currentLatestUser:
              turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
            loadedLatestUser:
              loadedTurns[loadedTurns.length - 1]?.userMessage.text,
          });
          // 合并：保留活跃 turn，后端已完成的 turn 用于 lastSequenceNumRef
          await syncCompletedHistoryEventCursor(sid, ctrl.signal);
          return;
        }
        turnsRef.current = loadedTurns;
        setTurns(loadedTurns);
        latestTurnIdRef.current =
          loadedTurns.length > 0
            ? loadedTurns[loadedTurns.length - 1].turnId
            : null;
        reconcileSessionWorkingAgents(
          sid,
          sessionAgentId ? [sessionAgentId] : [],
          loadedTurns.some(isActiveAssistantTurn),
        );
        setLoading(loadedTurns.some(isActiveAssistantTurn));
        setHasMoreMessages(res.hasMore);
        if (res.oldestCreatedAt != null)
          setOldestMessageCursor(res.oldestCreatedAt);
        if (shouldReplayEventsAfterHistory(loadedTurns)) {
          const replayStartedAt = performance.now();
          await replayLatestTurnSessionEvents(sid, loadedTurns, ctrl.signal);
          recordPerfStep(
            'session.select',
            'events.replayLatestTurn',
            replayStartedAt,
            {
              traceId,
              sessionId: sid,
              agentId: sessionAgentId,
              turnCount: loadedTurns.length,
            },
          );
        } else {
          const cursorStartedAt = performance.now();
          await syncCompletedHistoryEventCursor(sid, ctrl.signal);
          recordPerfStep(
            'session.select',
            'events.syncCompletedCursor',
            cursorStartedAt,
            {
              traceId,
              sessionId: sid,
              agentId: sessionAgentId,
              turnCount: loadedTurns.length,
            },
          );
        }
        recordPerfStep('session.select', 'select.finish', selectStartedAt, {
          traceId,
          sessionId: sid,
          agentId: sessionAgentId,
          turnCount: turnsRef.current.length,
        });
        logChatDiag('session.select.finish', {
          traceId,
          sid,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          turnCount: turnsRef.current.length,
          latestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          latestStatus:
            turnsRef.current[turnsRef.current.length - 1]?.assistant.status,
        });
        setViewportScrollIntent({ type: 'manual-bottom', behavior: 'auto' });
        return turnsRef.current.length;
      } catch (error) {
        logChatDiag('session.select.error', {
          traceId,
          sid,
          aborted: ctrl.signal.aborted,
          error,
        });
        recordPerfStep('session.select', 'select.error', selectStartedAt, {
          traceId,
          sessionId: sid,
          agentId: sessionAgentId,
          status: ctrl.signal.aborted ? 'aborted' : 'error',
          error: error instanceof Error ? error.message : String(error),
        });
        if (!ctrl.signal.aborted) messageApi.error('加载历史消息失败');
        return undefined;
      } finally {
        const isActiveHistoryRequest = historyAbortRef.current === ctrl;
        if (isActiveHistoryRequest) {
          historyAbortRef.current = null;
          setHistoryLoading(false);
        }
        if (
          shouldRestartSameSessionStream &&
          !ctrl.signal.aborted &&
          sessionIdRef.current === sid
        ) {
          startSessionEventStream(sid);
        }
      }
    },
    [
      selectedSessionId,
      turns.length,
      sessions,
      toTurnsFromHistory,
      messageApi,
      stopSessionEventStream,
      startSessionEventStream,
      clearSessionUnread,
      replayLatestTurnSessionEvents,
      syncCompletedHistoryEventCursor,
      reconcileSessionWorkingAgents,
      agentId,
      mainSessionId,
      routeSelection.sessionId,
    ],
  );

  // ── handleCompactCommand ──────────────────────────────────
  const handleCompactCommand = useCallback(async () => {
    const currentSessionId = sessionIdRef.current ?? selectedSessionId;
    if (!currentSessionId || !workspaceId) {
      messageApi.info('当前没有可压缩的会话');
      return;
    }
    if (loading) {
      messageApi.info('当前会话正在执行，请稍后再压缩');
      return;
    }

    setError(null);
    setLoading(true);
    const compactionId = createId();
    const compactTurnId = appendCompactTurn(
      '正在压缩上下文…',
      'executing',
      undefined,
      compactionTurnId(compactionId),
    );
    compactionTurnIdsRef.current.set(compactionId, compactTurnId);
    activeCompactionTurnIdRef.current = compactTurnId;
    latestTurnIdRef.current = compactTurnId;
    try {
      const response = await compactSession(currentSessionId, {
        workspaceId,
        agentId,
        level: 'Full',
        reason: 'manual slash command',
        compactionId,
      });
      setLoading(false);
      const result = response.compaction;
      const responseTurnId =
        compactionTurnIdsRef.current.get(response.compactionId) ??
        compactTurnId;
      updateCompactTurn(
        responseTurnId,
        'success',
        '上下文压缩完成',
        result,
      );
      activeCompactionTurnIdRef.current = null;

      if (
        response.newSessionId &&
        sessionIdRef.current !== response.newSessionId
      ) {
        switchToCompactedSessionPreservingTurns(
          response.newSessionId,
          response.newSessionTitle,
        );
      }
    } catch (e: unknown) {
      setLoading(false);
      const msg = e instanceof Error ? e.message : '上下文压缩失败';
      setError(msg);
      updateCompactTurn(compactTurnId, 'error', msg);
      activeCompactionTurnIdRef.current = null;
      messageApi.error(msg);
    }
  }, [
    agentId,
    appendCompactTurn,
    loading,
    messageApi,
    selectedSessionId,
    switchToCompactedSessionPreservingTurns,
    updateCompactTurn,
    workspaceId,
  ]);

  useEffect(() => {
    const requestedSessionId = routeSelection.sessionId;
    if (
      !requestedSessionId ||
      !workspaceId ||
      selectedSessionId === requestedSessionId
    )
      return;
    if (routeSelection.agentId && agentId && routeSelection.agentId !== agentId)
      return;
    if (
      sessions.length > 0 &&
      !sessions.some((session) => session.sessionId === requestedSessionId)
    )
      return;
    void handleSelectSession(requestedSessionId, {
      agentId: routeSelection.agentId ?? agentId,
    });
  }, [
    agentId,
    handleSelectSession,
    routeSelection.agentId,
    routeSelection.sessionId,
    selectedSessionId,
    sessions,
    workspaceId,
  ]);

  const ensureAgentMainSession = useCallback(
    async (
      nextWorkspaceId?: string,
      nextAgentId?: string,
      options?: { isCurrent?: () => boolean; selectSession?: boolean },
    ) => {
      const isCurrent = options?.isCurrent ?? (() => true);
      const shouldSelectSession = options?.selectSession ?? true;
      const targetWorkspaceId = nextWorkspaceId ?? workspaceId;
      const targetAgentId = nextAgentId ?? agentId;
      const targetAgent = agents.find((item) => item.agentId === targetAgentId);
      const request = buildAgentMainSessionRequest(
        targetWorkspaceId,
        targetAgent,
      );
      if (!request) return undefined;
      const traceId = `agent-main-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
      const ensureStartedAt = performance.now();

      resetMainSessionEnsureSuppression('explicit-ensure');
      setCreatingSession(true);
      try {
        const apiStartedAt = performance.now();
        const session = await ensureMainSession(request);
        logChatDiag('main.ensure.returned', {
          traceId,
          workspaceId: targetWorkspaceId,
          agentId: targetAgentId,
          returnedSessionId: session.sessionId,
          returnedTitle: session.title,
          previousSelectedSessionId: selectedSessionIdRef.current,
          previousSessionIdRef: sessionIdRef.current,
          routeSessionId: routeSelection.sessionId,
          shouldSelectSession,
        });
        recordPerfStep(
          'agent.mainSession',
          'api.ensureMainSession',
          apiStartedAt,
          {
            traceId,
            workspaceId: targetWorkspaceId,
            agentId: targetAgentId,
            sessionId: session.sessionId,
            title: session.title ?? null,
          },
        );
        if (!isCurrent()) return undefined;
        const title =
          session.title?.trim() || request.title || targetAgentId || '主线';
        setMainSessionId(session.sessionId);
        if (targetAgentId) {
          setAgents((prev) =>
            prev.map((item) =>
              item.agentId === targetAgentId
                ? { ...item, mainSessionId: session.sessionId }
                : item,
            ),
          );
        }
        setSessionCacheHitTokens(0);
        setSessionCacheMissTokens(0);
        const mainSessionItem = toSessionListItem(session, title);
        setSessions((prev) => {
          const idx = prev.findIndex(
            (item) => item.sessionId === session.sessionId,
          );
          if (idx >= 0)
            return [
              mainSessionItem,
              ...prev.slice(0, idx),
              ...prev.slice(idx + 1),
            ];
          return [mainSessionItem, ...prev];
        });
        if (!isCurrent()) return undefined;
        if (!shouldSelectSession) {
          projectionOwnedSessionIdsRef.current.add(session.sessionId);
          sessionIdRef.current = session.sessionId;
          setSelectedSessionId(session.sessionId);
          setTurns([]);
          turnsRef.current = [];
          setHistoryLoading(false);
          setHasMoreMessages(false);
          setOldestMessageCursor(null);
          logChatDiag('main.ensure.selectSkipped', {
            traceId,
            sessionId: session.sessionId,
            reason: 'agent projection owns message loading',
          });
          recordPerfStep(
            'agent.mainSession',
            'session.selectSkipped',
            ensureStartedAt,
            {
              traceId,
              workspaceId: targetWorkspaceId,
              agentId: targetAgentId,
              sessionId: session.sessionId,
              reason: 'agent projection owns message loading',
            },
          );
          recordPerfStep(
            'agent.mainSession',
            'ensure.finish',
            ensureStartedAt,
            {
              traceId,
              workspaceId: targetWorkspaceId,
              agentId: targetAgentId,
              sessionId: session.sessionId,
              reason: 'session selection skipped',
            },
          );
          return session.sessionId;
        }
        const selectStartedAt = performance.now();
        const loadedTurnCount = await handleSelectSession(session.sessionId, {
          agentId: targetAgentId,
        });
        logChatDiag('main.ensure.selectMain.finish', {
          traceId,
          sessionId: session.sessionId,
          loadedTurnCount: loadedTurnCount ?? null,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          isCurrent: isCurrent(),
        });
        recordPerfStep(
          'agent.mainSession',
          'session.selectMain',
          selectStartedAt,
          {
            traceId,
            workspaceId: targetWorkspaceId,
            agentId: targetAgentId,
            sessionId: session.sessionId,
            loadedTurnCount: loadedTurnCount ?? null,
            status: isCurrent() ? 'ok' : 'stale',
          },
        );
        if (!isCurrent()) return undefined;
        recordPerfStep('agent.mainSession', 'ensure.finish', ensureStartedAt, {
          traceId,
          workspaceId: targetWorkspaceId,
          agentId: targetAgentId,
          sessionId: session.sessionId,
          loadedTurnCount: loadedTurnCount ?? null,
        });
        return session.sessionId;
      } catch (error) {
        logChatDiag('main.ensure.error', {
          traceId,
          workspaceId: targetWorkspaceId,
          agentId: targetAgentId,
          error,
        });
        recordPerfStep('agent.mainSession', 'ensure.error', ensureStartedAt, {
          traceId,
          workspaceId: targetWorkspaceId,
          agentId: targetAgentId,
          status: 'error',
          error: error instanceof Error ? error.message : String(error),
        });
        messageApi.error('打开主线会话失败');
        return undefined;
      } finally {
        setCreatingSession(false);
      }
    },
    [
      agentId,
      agents,
      handleSelectSession,
      messageApi,
      resetMainSessionEnsureSuppression,
      routeSelection.sessionId,
      workspaceId,
    ],
  );

  useEffect(() => {
    if (!workspaceId || !agentId || routeSelection.sessionId) return;
    if (suppressMainSessionEnsureRef.current) {
      suppressMainSessionEnsureRef.current = false;
      logChatDiag('main.ensure.effectSkipped.suppressed', {
        workspaceId,
        agentId,
        reason: 'session-not-found',
      });
      return;
    }
    if (selectedSessionId) {
      logChatDiag('main.ensure.effectSkipped.selectedSession', {
        workspaceId,
        agentId,
        selectedSessionId,
        sessionIdRef: sessionIdRef.current,
        mainSessionId,
        routeSessionId: routeSelection.sessionId,
        turns: turnsRef.current.length,
      });
      return;
    }
    void ensureAgentMainSession(workspaceId, agentId);
  }, [
    agentId,
    ensureAgentMainSession,
    mainSessionId,
    routeSelection.sessionId,
    selectedSessionId,
    workspaceId,
  ]);

  // ── handleSetMainSession ──────────────────────────────────
  const handleSetMainSession = useCallback((sessionId: string) => {
    setMainSessionId(sessionId);
    setSessionCacheHitTokens(0);
    setSessionCacheMissTokens(0);
  }, []);

  // 持久 SSE：跟随当前会话生命周期自动切换。
  useEffect(() => {
    if (!selectedSessionId) {
      stopSessionEventStream();
      return;
    }
    if (projectionOwnedSessionIdsRef.current.has(selectedSessionId)) {
      stopSessionEventStream();
      recordPerfEvent(
        'chat.sse.skipped',
        {
          sessionId: selectedSessionId,
          reason: 'agent projection owns message loading',
        },
        { throttleMs: 1_000 },
      );
      return;
    }
    startSessionEventStream(selectedSessionId);
    return () => {
      stopSessionEventStream();
    };
  }, [selectedSessionId, startSessionEventStream, stopSessionEventStream]);

  // ── loadMoreMessages ───────────────────────────────────────
  const loadMoreMessages = useCallback(async () => {
    if (
      !selectedSessionId ||
      !hasMoreMessages ||
      loadingMore ||
      oldestMessageCursor == null
    )
      return;
    setLoadingMore(true);
    try {
      const res: MessageListResponse = await listSessionMessages(
        selectedSessionId,
        oldestMessageCursor,
        MESSAGE_PAGE_SIZE,
      );
      const olderTurns = toTurnsFromHistory(res);
      setTurns((prev) => [...olderTurns, ...prev]);
      setHasMoreMessages(res.hasMore);
      if (res.oldestCreatedAt != null)
        setOldestMessageCursor(res.oldestCreatedAt);
    } catch {
      messageApi.error('加载更早消息失败');
    } finally {
      setLoadingMore(false);
    }
  }, [
    selectedSessionId,
    hasMoreMessages,
    loadingMore,
    oldestMessageCursor,
    toTurnsFromHistory,
    messageApi,
  ]);

  // ── handleDeleteSession ────────────────────────────────────
  const handleDeleteSession = useCallback(
    async (sid: string) => {
      try {
        await deleteSession(sid);
        messageApi.success('会话已删除');

        // P0 v2: 走统一入口，避免误伤非当前 session
        handleSessionNotFound(sid, 'delete');
      } catch {
        messageApi.error('删除失败');
      }
    },
    [messageApi, handleSessionNotFound],
  );

  // ── handleArchiveSession ───────────────────────────────────
  const handleArchiveSession = useCallback(
    async (sid: string) => {
      try {
        await archiveSession(sid);
        messageApi.success('会话已归档');

        // P0 v2: 走统一入口
        handleSessionNotFound(sid, 'archive');
      } catch {
        messageApi.error('归档失败');
      }
    },
    [messageApi, handleSessionNotFound],
  );

  // ── handleRenameStart / handleRenameSubmit ─────────────────
  const handleRenameStart = useCallback((sid: string, title: string) => {
    setRenameSessionId(sid);
    setRenameTitle(title);
    setRenameModalOpen(true);
  }, []);

  const handleRenameSubmit = useCallback(async () => {
    const trimmed = renameTitle.trim();
    if (!renameSessionId || !trimmed) return;
    try {
      await renameSession(renameSessionId, trimmed);
      messageApi.success('重命名成功');
      setSessions((prev) =>
        prev.map((s) =>
          s.sessionId === renameSessionId ? { ...s, title: trimmed } : s,
        ),
      );
      setRenameModalOpen(false);
    } catch {
      messageApi.error('重命名失败');
    }
  }, [renameSessionId, renameTitle, messageApi]);

  // ── sendMessage ────────────────────────────────────────────
  // T-102: fire-and-forget POST + 持久 SSE 方案。
  const sendMessage = useCallback(
    async (text: string, options?: ChatSendOptions) => {
      if (!text || !workspaceId || !agentId) return;
      const localBusy =
        loadingRef.current ||
        hasBlockingActiveTurn(
          turnsRef.current,
          activeMessageIdsRef.current,
          messageIdToTurnIdRef.current,
        );
      if (localBusy) {
        recordPerfEvent(
          'chat.post.localBusyForwarded',
          {
            loading,
            activeMessageCount: activeMessageIdsRef.current.size,
            turnCount: turnsRef.current.length,
          },
          { throttleMs: 1_000 },
        );
      }
      if (text.trim().toLowerCase() === COMPACT_COMMAND) {
        await handleCompactCommand();
        return;
      }
      if (options?.metadata && Object.keys(options.metadata).length > 0) {
        const unsupportedMessage =
          '当前 Conversation 命令链尚未支持视觉或其他 metadata 输入，消息未发送。';
        setError(unsupportedMessage);
        messageApi.error(unsupportedMessage);
        return;
      }
      const route = resolveChatRoute(text, agents, agentId);
      const routedText = route.messageText.trim();
      if (!routedText || route.targetAgentIds.length === 0) return;
      const isDirectSystemCommand = /^\/yolo$/i.test(routedText);
      resetMainSessionEnsureSuppression('send-message');
      setError(null);
      const perfStart = performance.now();
      const now = Date.now();
      const routeLabel = getChatRouteLabel(route, agents);
      const targetAgentId = route.primaryAgentId ?? agentId;
      const targetAgent = agents.find((item) => item.agentId === targetAgentId);
      const workingTargetAgentIds = isDirectSystemCommand
        ? []
        : Array.from(
            new Set(
              route.targetAgentIds.length > 0
                ? route.targetAgentIds
                : [targetAgentId],
            ),
          );

      let sendConversationId =
        sessionIdRef.current
        ?? selectedSessionIdRef.current
        ?? mainSessionIdRef.current;
      if (forceNewSessionRef.current && !isDirectSystemCommand) {
        const created = await createSession(
          workspaceId,
          targetAgent?.sourceTemplateId || `global:${targetAgentId}`,
          undefined,
          targetAgent ? getAgentName(targetAgent) : targetAgentId,
        );
        sendConversationId = created.sessionId;
      } else if (!sendConversationId) {
        const ensureRequest = buildAgentMainSessionRequest(workspaceId, targetAgent);
        if (!ensureRequest)
          throw new Error('无法解析 Agent 主会话');
        const ensured = await ensureMainSession(ensureRequest);
        sendConversationId = ensured.sessionId;
        setMainSessionId(ensured.sessionId);
      }

      sessionIdRef.current = sendConversationId;
      setSelectedSessionId(sendConversationId);
      startSessionEventStream(sendConversationId);

      const turnId = createId();
      if (isDirectSystemCommand) {
        const clientRequestId = createId();
        const clientMessageId = createId();
        const responseMessageId = createId();
        const systemTurn: ChatTurn = {
          turnId: clientRequestId,
          source: {
            sourceId: 'system',
            sourceType: 'system_command',
            displayName: 'System',
            avatarEmoji: '⚙',
            avatarColor: stringToColor('system'),
          },
          userMessage: {
            id: clientMessageId,
            text: route.originalText,
            timestamp: now,
            status: 'sending',
          },
          assistant: createAssistant(
            responseMessageId,
            'legacy',
            'thinking',
            true,
          ),
        };
        turnsRef.current = [...turnsRef.current, systemTurn];
        setTurns((current) => [...current, systemTurn]);
        setViewportScrollIntent({
          type: 'user-send',
          itemId: `message:user:${clientMessageId}`,
          createdAt: now,
        });

        const updateSystemTurn = (
          status: 'success' | 'error',
          answerMarkdown: string,
        ) => {
          const apply = (current: ChatTurn[]) =>
            current.map((turn) =>
              turn.turnId === clientRequestId
                ? {
                    ...turn,
                    userMessage: {
                      ...turn.userMessage,
                      status: status === 'success' ? 'success' : 'error',
                    },
                    assistant: {
                      ...turn.assistant,
                      status,
                      isStreaming: false,
                      answerMarkdown,
                    },
                  }
                : turn,
            );
          turnsRef.current = apply(turnsRef.current);
          setTurns(apply);
        };

        try {
          const result = await executeConversationSystemCommand(
            workspaceId,
            sendConversationId,
            {
              agentId: targetAgentId,
              clientRequestId,
              clientMessageId,
              responseMessageId,
              commandText: routedText,
            },
          );
          updateSystemTurn('success', result.message);
          forceNewSessionRef.current = false;
        } catch (error) {
          const errorMessage =
            error instanceof Error ? error.message : '系统指令执行失败';
          updateSystemTurn('error', errorMessage);
          setError(errorMessage);
          messageApi.error(errorMessage);
        }
        return;
      }

      markPerf(`chat.post.${turnId}.start`);
      // ADR-058: Generate stable idempotency keys at send initiation.
      // - clientRequestId: reused across retries; same send action = same ID.
      // - clientMessageId: stable user message ID = userMessage.id.
      const clientRequestId = createId();
      const clientMessageId = createId();
      const optimisticTurn: ChatTurn = {
        turnId,
        source: {
          sourceId: targetAgentId,
          sourceType: 'agent',
          displayName: route.audience === 'all'
              ? 'all'
              : targetAgent
                ? getAgentName(targetAgent)
                : targetAgentId,
          avatarEmoji: '🤖',
          avatarColor: stringToColor(targetAgentId),
          avatarUrl: targetAgent?.avatarUrl,
        },
        userMessage: {
          id: clientMessageId,
          text: route.originalText,
          timestamp: now,
          status: 'sending',
        },
        assistant: createAssistant(createId(), 'structured', 'thinking', true),
      };
      turnsRef.current = [...turnsRef.current, optimisticTurn];
      setTurns((p) => [...p, optimisticTurn]);
      setViewportScrollIntent({
        type: 'user-send',
        itemId: `message:user:${optimisticTurn.userMessage.id}`,
        createdAt: now,
      });
      const ctrl = new AbortController();
      abortRef.current = ctrl;
      setLoading(true);
      setAgentIdsWorking(workingTargetAgentIds, true);

      // 注册当前 turn 为最新，供持久 SSE 事件路由
      latestTurnIdRef.current = turnId;
      const previousSessionId = sendConversationId;
      logChatDiag('post.optimistic.appended', {
        turnId,
        previousSessionId,
        selectedSessionId: selectedSessionIdRef.current,
        sessionIdRef: sessionIdRef.current,
        mainSessionId,
        routeSessionId: routeSelection.sessionId,
        targetAgentId,
        forceNewSession: forceNewSessionRef.current,
        turnCountAfterAppend: turnsRef.current.length,
        text: route.originalText,
      });

      try {
        // Persist before the HTTP request, but only after the optimistic state is
        // synchronously attached to the conversation that initiated the send.
        // This closes the selection race where IndexedDB yielded and the turn was
        // appended to whichever conversation the user selected in the meantime.
        await enqueueCommand({
          clientRequestId,
          clientMessageId,
          workspaceId,
          conversationId: sendConversationId,
          messageText: routedText,
          agentIds: route.targetAgentIds.length > 0
            ? route.targetAgentIds
            : [targetAgentId],
        });
        await markSending(clientRequestId);
        // T-102: 非流式 POST — 202 Accepted + { success, status, commandId, messageId, turnId, sessionId, eventCursor }
        // ADR-056: turnId 由后端分配（非前端生成），确保事件系统一致。
        const acceptance =
          await submitConversationTurn(
            workspaceId,
            sendConversationId,
            {
              clientRequestId,
              clientMessageId,
              recipients: {
                type: 'agent',
                agentIds: route.targetAgentIds.length > 0
                  ? route.targetAgentIds
                  : [targetAgentId],
              },
              content: [{ type: 'text', text: routedText }],
            },
            ctrl.signal,
          );
        const messageId = acceptance.messageId;
        const returnedSessionId = acceptance.conversationId;
        const serverTurnId = acceptance.turnIds[0];
        await dequeueCommand(clientRequestId);

        const stillViewingSendSession =
          (sessionIdRef.current ?? null) === previousSessionId;
        const effectiveTurnId = serverTurnId ?? turnId;
        logChatDiag('post.returned.beforeApply', {
          turnId: effectiveTurnId,
          serverTurnId,
          messageId,
          returnedSessionId,
          previousSessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          stillViewingSendSession,
          activeMessageCount: activeMessageIdsRef.current.size,
        });

        // The POST acknowledgement confirms the durable Turn identity. Migrate
        // every turn-keyed frontend structure before restarting replay/SSE so a
        // fast terminal event cannot target a stale optimistic ID.
        if (stillViewingSendSession) {
          resetStreamCursorForSessionChange(
            previousSessionId,
            returnedSessionId,
          );
          sessionIdRef.current = returnedSessionId;
          setSelectedSessionId(returnedSessionId);

          const confirmedTurns = confirmOptimisticTurn(
            turnsRef.current,
            turnId,
            effectiveTurnId,
            messageId,
          );
          turnsRef.current = confirmedTurns;
          setTurns((current) =>
            confirmOptimisticTurn(
              current,
              turnId,
              effectiveTurnId,
              messageId,
            ),
          );
          if (latestTurnIdRef.current === turnId)
            latestTurnIdRef.current = effectiveTurnId;

          const migrateTurnKey = <T,>(map: Map<string, T>) => {
            if (turnId === effectiveTurnId || !map.has(turnId)) return;
            const value = map.get(turnId)!;
            map.delete(turnId);
            map.set(effectiveTurnId, value);
          };
          migrateTurnKey(pendingDeltaRef.current);
          migrateTurnKey(pendingThinkingRef.current);
          migrateTurnKey(duplicateDeltaReplayOffsetRef.current);
          if (completedTurnsRef.current.delete(turnId))
            completedTurnsRef.current.add(effectiveTurnId);
        }
        forceNewSessionRef.current = false;
        messageIdToTurnIdRef.current.set(messageId, effectiveTurnId);
        messageIdToAgentIdsRef.current.set(messageId, workingTargetAgentIds);
        const previousSessionAgentIds =
          sessionIdToAgentIdsRef.current.get(returnedSessionId) ?? [];
        sessionIdToAgentIdsRef.current.set(
          returnedSessionId,
          Array.from(
            new Set([...previousSessionAgentIds, ...workingTargetAgentIds]),
          ),
        );
        activeMessageIdsRef.current.add(messageId);
        if (stillViewingSendSession) {
          startSessionEventStream(returnedSessionId);
        }

        // 埋点
        console.debug('[Pudding Chat] post returned', {
          turnId: effectiveTurnId,
          serverTurnId,
          messageId,
          sessionId: returnedSessionId,
          stillViewingSendSession,
          previousSessionId,
          activeMessageCount: activeMessageIdsRef.current.size,
        });
        logChatDiag('post.returned.afterApply', {
          turnId: effectiveTurnId,
          serverTurnId,
          messageId,
          returnedSessionId,
          previousSessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          stillViewingSendSession,
          activeMessageCount: activeMessageIdsRef.current.size,
          messageIdToTurnId: Object.fromEntries(messageIdToTurnIdRef.current),
        });

        // ADR: 首条消息隐式会话的前端物化 — 将返回的 sessionId 同步到左侧 sessions 列表
        const optimisticTitle = route.audience === 'all'
          ? `all · ${routedText.slice(0, 24).trim() || '群聊'}`
          : routeLabel || routedText.slice(0, 30).trim() || '对话';
        setSessions((prev) => {
          const idx = prev.findIndex((s) => s.sessionId === returnedSessionId);
          if (idx >= 0) {
            const updated = { ...prev[idx], timestamp: now };
            if (!prev[idx].title || prev[idx].title === '对话')
              updated.title = optimisticTitle;
            return [updated, ...prev.slice(0, idx), ...prev.slice(idx + 1)];
          }
          return [
            {
              sessionId: returnedSessionId,
              title: optimisticTitle,
              timestamp: now,
            },
            ...prev,
          ];
        });

        // ADR-026: debug mode 写入 sessionId/messageId
        writeDebugSessionState(returnedSessionId, messageId);
        streamStartAtRef.current.set(messageId, perfStart);
        deltaHasFlushedRef.current = false; // 新消息开始，下次 delta 首帧立即 flush

        const ttfm = (performance.now() - perfStart).toFixed(0);
        markPerf(`chat.post.${turnId}.returned`);
        measurePerf(
          'chat.post.roundtrip',
          `chat.post.${turnId}.start`,
          `chat.post.${turnId}.returned`,
        );
        recordPerfEvent('chat.post.returned', {
          elapsedMs: Number(ttfm),
          messageId,
          sessionId: returnedSessionId,
          workspaceId,
          agentId: route.primaryAgentId,
          audience: route.audience,
          targetAgentCount: route.targetAgentIds.length,
          localBusyAtSubmit: localBusy,
        });

        // 主动拉取已生成的事件（fire-and-forget，不与 SSE 串行等待）
        if (stillViewingSendSession) {
          void replayMissedSessionEvents(returnedSessionId, ctrl.signal).catch(
            () => {
              /* SSE 会重连补偿 */
            },
          );
        }
        const replayAndReconcile = (delayMs: number) => {
          window.setTimeout(() => {
            if (sessionIdRef.current !== returnedSessionId) {
              logChatDiag('post.reconcileTimer.skipped.sessionChanged', {
                delayMs,
                returnedSessionId,
                sessionIdRef: sessionIdRef.current,
                selectedSessionId: selectedSessionIdRef.current,
              });
              return;
            }
            logChatDiag('post.reconcileTimer.fire', {
              delayMs,
              returnedSessionId,
              selectedSessionId: selectedSessionIdRef.current,
              sessionIdRef: sessionIdRef.current,
              activeMessageCount: activeMessageIdsRef.current.size,
              turnCount: turnsRef.current.length,
            });
            void (async () => {
              try {
                await replayMissedSessionEventsIfNeeded(returnedSessionId, {
                  reason: `reconcile-${delayMs}`,
                  hasActiveMessages: true,
                });
              } catch {
                /* 下次补偿 */
              }
              try {
                await reconcileCompletedSessionMessages(returnedSessionId);
              } catch {
                /* 后续轮询/刷新补偿 */
              }
            })();
          }, delayMs);
        };
        window.setTimeout(() => {
          if (sessionIdRef.current === returnedSessionId) {
            logChatDiag('post.replayTimer.fire', {
              delayMs: 800,
              returnedSessionId,
              selectedSessionId: selectedSessionIdRef.current,
              sessionIdRef: sessionIdRef.current,
            });
            void replayMissedSessionEventsIfNeeded(returnedSessionId, {
              reason: 'post-800',
              hasActiveMessages: true,
            });
          } else {
            logChatDiag('post.replayTimer.skipped.sessionChanged', {
              delayMs: 800,
              returnedSessionId,
              selectedSessionId: selectedSessionIdRef.current,
              sessionIdRef: sessionIdRef.current,
            });
          }
        }, 800);
        replayAndReconcile(2000);
        replayAndReconcile(5000);

        // ADR: 后台刷新会话列表（merge 模式保留乐观项不被覆盖）
        refreshSessions({ preserveSessionId: returnedSessionId });
      } catch (e: unknown) {
        logChatDiag('post.error', {
          turnId,
          previousSessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          error: e,
        });
        setAgentIdsWorking(workingTargetAgentIds, false);
        if (e instanceof Error && e.name === 'AbortError') {
          setTurns((p) =>
            p.map((t) =>
              t.turnId === turnId
                ? {
                    ...t,
                    assistant: {
                      ...t.assistant,
                      status: 'cancelled' as const,
                      isStreaming: false,
                    },
                  }
                : t,
            ),
          );
        } else {
          setError(e instanceof Error ? e.message : '请求失败');
          setTurns((p) =>
            p.map((t) =>
              t.turnId === turnId
                ? {
                    ...t,
                    assistant: {
                      ...t.assistant,
                      status: 'error' as const,
                      isStreaming: false,
                    },
                  }
                : t,
            ),
          );
          // ADR: POST 已成功，会话由后端持久化，不做前端回滚
        }
        setLoading(false);
      } finally {
        if (abortRef.current === ctrl) abortRef.current = null;
        // T-102: loading 由持久 SSE 的 done/error/cancelled 事件管理，不在此处关闭
      }
    },
    [
      workspaceId,
      agentId,
      agents,
      replayMissedSessionEvents,
      replayMissedSessionEventsIfNeeded,
      refreshSessions,
      handleCompactCommand,
      startSessionEventStream,
      reconcileCompletedSessionMessages,
      resetStreamCursorForSessionChange,
      setAgentIdsWorking,
      mainSessionId,
      resetMainSessionEnsureSuppression,
      routeSelection.sessionId,
      messageApi,
    ],
  );

  const enqueueInteraction = useCallback(
    (text: string, options?: ChatSendOptions) => {
      const trimmed = text.trim();
      if (!trimmed) return null;
      recordPerfEvent(
        'chat.queue.localEnqueueIgnored',
        {
          reason: 'backend-owned-queue',
          messageChars: trimmed.length,
          hasMetadata: Boolean(options?.metadata),
        },
        { throttleMs: 1_000 },
      );
      void sendMessage(trimmed, options);
      return null;
    },
    [sendMessage],
  );

  const submitInteraction = useCallback(
    async (text: string, options?: ChatSendOptions) => {
      const trimmed = text.trim();
      if (!trimmed) return;
      const localBusy =
        loading ||
        hasBlockingActiveTurn(
          turnsRef.current,
          activeMessageIdsRef.current,
          messageIdToTurnIdRef.current,
        );
      if (localBusy) {
        recordPerfEvent(
          'chat.submit.localBusyForwarded',
          {
            loading,
            activeMessageCount: activeMessageIdsRef.current.size,
            turnCount: turnsRef.current.length,
            messageChars: trimmed.length,
          },
          { throttleMs: 1_000 },
        );
      }
      await sendMessage(trimmed, options);
    },
    [loading, sendMessage],
  );

  const updateQueuedInteraction = useCallback(
    (id: string, text: string) => {
      recordPerfEvent(
        'chat.queue.localUpdateIgnored',
        {
          reason: 'backend-owned-queue',
          queueItemId: id,
          messageChars: text.trim().length,
        },
        { throttleMs: 1_000 },
      );
      messageApi.info('消息队列由后端管理，当前暂不支持本地编辑队列项');
    },
    [messageApi],
  );

  const refreshAgentMessageQueue = useCallback(
    async (reason: string) => {
      if (!workspaceId || !agentId) {
        setServerInteractionQueue([]);
        return;
      }
      const startedAt = performance.now();
      try {
        const snapshot = await getAgentMessageQueue(workspaceId, agentId, {
          limit: 20,
          includeTerminal: false,
        });
        const next = (snapshot.items ?? []).map(toChatInteractionQueueItem);
        setServerInteractionQueue(next);
        recordPerfEvent(
          'chat.queue.snapshot',
          {
            reason,
            workspaceId,
            agentId,
            itemCount: next.length,
            elapsedMs: Math.round(performance.now() - startedAt),
          },
          { throttleMs: 2_000 },
        );
      } catch (error) {
        recordPerfEvent(
          'chat.queue.snapshotFailed',
          {
            reason,
            workspaceId,
            agentId,
            error: error instanceof Error ? error.message : String(error),
            elapsedMs: Math.round(performance.now() - startedAt),
          },
          { throttleMs: 2_000 },
        );
      }
    },
    [agentId, workspaceId],
  );

  useEffect(() => {
    if (!workspaceId || !agentId) {
      setServerInteractionQueue([]);
      return;
    }
    void refreshAgentMessageQueue('selection');
    const timer = window.setInterval(
      () => {
        void refreshAgentMessageQueue('poll');
      },
      loading ? 1200 : 3500,
    );
    return () => window.clearInterval(timer);
  }, [agentId, loading, refreshAgentMessageQueue, workspaceId]);

  useEffect(() => {
    if (!workspaceId || !agentId) {
      setSteeringInteractionQueue([]);
    }
  }, [agentId, workspaceId]);

  const visibleInteractionQueue = useMemo(
    () => [...serverInteractionQueue, ...steeringInteractionQueue],
    [serverInteractionQueue, steeringInteractionQueue],
  );

  const findVisibleQueueItem = useCallback(
    (id: string) =>
      [...serverInteractionQueue, ...steeringInteractionQueue].find(
        (item) => item.id === id,
      ),
    [serverInteractionQueue, steeringInteractionQueue],
  );

  const deleteQueuedInteraction = useCallback(
    (id: string) => {
      const item = findVisibleQueueItem(id);
      if (item?.source === 'steering') {
        setSteeringInteractionQueue((prev) => prev.filter((x) => x.id !== id));
        return;
      }
      recordPerfEvent(
        'chat.queue.localDeleteIgnored',
        {
          reason: 'backend-owned-queue',
          queueItemId: id,
          status: item?.status,
        },
        { throttleMs: 1_000 },
      );
      messageApi.info('消息队列由后端管理，当前暂不支持本地删除队列项');
    },
    [findVisibleQueueItem, messageApi],
  );

  const sendQueuedInteractionNow = useCallback(
    async (id: string) => {
      const item = findVisibleQueueItem(id);
      recordPerfEvent(
        'chat.queue.sendNowIgnored',
        {
          reason: 'backend-owned-queue',
          queueItemId: id,
          status: item?.status,
        },
        { throttleMs: 1_000 },
      );
      messageApi.info('消息队列由后端调度，插队/立即发送需要后端队列命令接口');
    },
    [findVisibleQueueItem, messageApi],
  );

  const steerQueuedInteraction = useCallback(
    async (id: string) => {
      const item = findVisibleQueueItem(id);
      const sessionId = sessionIdRef.current ?? selectedSessionId;
      if (!item || item.status !== 'queued') return;
      if (!workspaceId || !sessionId) {
        messageApi.error('当前会话尚未建立，无法注入引导');
        return;
      }

      const submittedStartAt = Date.now();
      const localSteeringId = `steering-local-${id}`;
      setSteeringInteractionQueue((prev) => [
        ...prev.filter((x) => x.id !== localSteeringId),
        {
          id: localSteeringId,
          text: item.text,
          createdAt: submittedStartAt,
          status: 'steering_pending' as const,
          source: 'steering',
          submittedAt: submittedStartAt,
          error: undefined,
        },
      ]);
      recordPerfEvent('chat.steering.submit', {
        queueItemId: item.id,
        sessionId,
        agentId,
        messageChars: item.text.length,
        queueAgeMs: Math.max(0, submittedStartAt - item.createdAt),
      });

      try {
        const response = await createChatSteeringMessage(
          workspaceId,
          sessionId,
          {
            messageText: item.text,
            agentId,
            sourceQueueItemId: item.id,
            priority: 1000,
          },
        );
        setSteeringInteractionQueue((prev) =>
          prev.map((x) =>
            x.id === localSteeringId
              ? {
                  ...x,
                  status: 'steering_pending' as const,
                  steeringId: response.steeringId,
                  submittedAt: response.createdAt,
                }
              : x,
          ),
        );
        recordPerfEvent('chat.steering.submitted', {
          queueItemId: item.id,
          steeringId: response.steeringId,
          sessionId: response.sessionId,
          workspaceId: response.workspaceId,
          agentId: response.agentId,
          createdAt: response.createdAt,
          requestLatencyMs: Math.max(0, Date.now() - submittedStartAt),
        });
        messageApi.success('引导已提交，将在下一次模型请求前注入上下文');
      } catch (e: unknown) {
        const errorMessage = e instanceof Error ? e.message : '引导提交失败';
        setSteeringInteractionQueue((prev) =>
          prev.map((x) =>
            x.id === localSteeringId
              ? {
                  ...x,
                  status: 'steering_failed' as const,
                  error: errorMessage,
                }
              : x,
          ),
        );
        recordPerfEvent('chat.steering.submitFailed', {
          queueItemId: item.id,
          sessionId,
          agentId,
          requestLatencyMs: Math.max(0, Date.now() - submittedStartAt),
          error: errorMessage,
        });
        messageApi.error(errorMessage);
      }
    },
    [agentId, findVisibleQueueItem, messageApi, selectedSessionId, workspaceId],
  );

  // ── handleKeyDown ──────────────────────────────────────────
  // 使用 ref 读取 inputValue 避免每次按键都重新创建 handleKeyDown
  const inputValueRef = useRef(inputValue);
  inputValueRef.current = inputValue;

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      const v = inputValueRef.current;
      // Ctrl+Enter: 强制执行
      if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
        e.preventDefault();
        const t = v.trim();
        if (!t) return;
        setInputValue('');
        if (t.toLowerCase() === COMPACT_COMMAND) void handleCompactCommand();
        else void submitInteraction(t);
        return;
      }
      // Enter: 发送 / 中止生成
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        const t = v.trim();
        if (!t) return;
        setInputValue('');
        if (t.toLowerCase() === COMPACT_COMMAND) void handleCompactCommand();
        else void submitInteraction(t);
        return;
      }
      // ↑: 编辑上一条用户消息（输入框为空且最后一条是 user 消息）
      if (e.key === 'ArrowUp' && !v.trim()) {
        const lastTurn = turns[turns.length - 1];
        if (lastTurn?.userMessage?.text) {
          e.preventDefault();
          setInputValue(lastTurn.userMessage.text);
        }
      }
    },
    [submitInteraction, turns, handleCompactCommand],
  );

  // ── global Ctrl+Enter ──────────────────────────────────────

  useEffect(() => {
    const handler = () => {
      const text = inputValueRef.current.trim();
      if (!text) return;
      setInputValue('');
      void submitInteraction(text);
    };
    window.addEventListener('pudding:chat:send', handler);
    return () => window.removeEventListener('pudding:chat:send', handler);
  }, [submitInteraction]);

  // ── handleExport ───────────────────────────────────────────
  const handleExport = useCallback(() => {
    if (turns.length === 0) {
      messageApi.info('无对话');
      return;
    }
    const md = turns
      .map((t) => {
        const blocks: string[] = [];
        if (t.userMessage.text.trim())
          blocks.push(
            `## User · ${dayjs(t.userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${t.userMessage.text}`,
          );
        const items = t.assistant.timelineItems ?? [];
        const thinking = items.filter((i) => i.type === 'thinking');
        const steps = items.filter((i) => i.type !== 'thinking');
        const rawThinking = getThinkingRawText(thinking);
        if (rawThinking) blocks.push(`## Reasoning\n\n${rawThinking}`);
        if (steps.length > 0) {
          const stepLines = steps
            .map((i) => {
              const detail = sanitizeProcessText(
                i.message || i.output || i.arguments,
                { maxLength: 240 },
              );
              return `- [${i.status || i.type}] ${detail || i.name || i.type}`;
            })
            .join('\n');
          if (stepLines) blocks.push(`## Steps\n\n${stepLines}`);
        }
        blocks.push(
          `## Agent · ${dayjs(t.userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${t.assistant.answerMarkdown}`,
        );
        return blocks.join('\n\n');
      })
      .join('\n\n---\n\n');
    const b = new Blob([md], { type: 'text/markdown;charset=utf-8' });
    const u = URL.createObjectURL(b);
    const a = document.createElement('a');
    a.href = u;
    a.download = `pudding-chat-${dayjs().format('YYYYMMDD-HHmmss')}.md`;
    a.click();
    URL.revokeObjectURL(u);
  }, [turns, messageApi]);

  // ── onDeleteTurn / onToggleReasoning ──────────────────────
  const onDeleteTurn = useCallback((turnId: string) => {
    setTurns((p) => p.filter((t) => t.turnId !== turnId));
  }, []);

  const onToggleReasoning = useCallback((turnId: string, itemId: string) => {
    setTurns((prev) =>
      prev.map((t) =>
        t.turnId === turnId
          ? {
              ...t,
              assistant: {
                ...t.assistant,
                timelineItems: (t.assistant.timelineItems ?? []).map((item) =>
                  item.id === itemId
                    ? { ...item, collapsed: !item.collapsed }
                    : item,
                ),
              },
            }
          : t,
      ),
    );
  }, []);

  // ── derived ────────────────────────────────────────────────
  const wsOpts = useMemo(
    () =>
      workspaces.map((w) => ({
        value: w.workspaceId,
        label: w.name || w.workspaceId,
        disabled: !w.isEnabled || w.isFrozen,
      })),
    [workspaces],
  );
  const agOpts = useMemo(
    () =>
      agents.map((a) => ({
        value: a.agentId,
        label: getAgentName(a),
        disabled: !a.isEnabled || a.isFrozen,
      })),
    [agents, getAgentName],
  );
  const groups = useMemo(
    () =>
      groupSessions(sessions).map((g) => ({
        ...g,
        items: g.items.map((s) => ({
          ...s,
          unreadCount: sessionUnreadCounts[s.sessionId] || undefined,
        })),
      })),
    [sessions, sessionUnreadCounts],
  );
  const tLimit = latestUsage?.contextWindowTokens ?? 0;
  const tUsed = latestUsage?.totalTokens ?? 0;
  const tPct =
    tLimit > 0 ? Math.min(100, Math.round((tUsed / tLimit) * 100)) : 0;

  const cacheTotalTokens = sessionCacheHitTokens + sessionCacheMissTokens;
  const cacheHitRate =
    cacheTotalTokens > 0
      ? Math.round((sessionCacheHitTokens / cacheTotalTokens) * 100)
      : undefined;
  const visibleSubAgentCards = useMemo(
    () => filterSubAgentCardsForSession(subAgentCards, selectedSessionId),
    [subAgentCards, selectedSessionId],
  );
  const visibleTurns = useMemo(
    () =>
      mergeHistoryWithLifecycleTurns(
        turns,
        Array.from(compactionLifecycleTurnsRef.current.values()),
      ),
    [turns],
  );

  return {
    workspaces,
    workspaceId,
    workspaceLoading,
    setWorkspaceId,
    setWorkspaces,
    agents,
    agentId,
    agentLoading,
    setAgentId,
    selectedAgent,
    sidebarOpen,
    setSidebarOpen,
    sessions,
    selectedSessionId,
    sessionsLoading,
    groups,
    turns: visibleTurns,
    chatInteractionRuntimeEvents,
    historyLoading,
    hasMoreMessages,
    loadingMore,
    inputValue,
    setInputValue,
    loading,
    workingAgentIds,
    interactionQueue: visibleInteractionQueue,
    error,
    setError,
    latestUsage,
    tLimit,
    tUsed,
    tPct,
    mainSessionId,
    sessionCacheHitTokens,
    sessionCacheMissTokens,
    cacheHitRate,
    handleSetMainSession,
    subAgentCards: visibleSubAgentCards,
    sessionUnreadCounts,
    startWorkspaceNotificationStream,
    stopWorkspaceNotificationStream,
    clearSessionUnread,
    createSceneOpen,
    setCreateSceneOpen,
    createSceneLoading,
    createSceneForm,
    renameModalOpen,
    setRenameModalOpen,
    renameTitle,
    setRenameTitle,
    renameSessionId,
    handleSelectSession,
    handleDeleteSession,
    handleArchiveSession,
    handleRenameStart,
    handleRenameSubmit,
    ensureAgentMainSession,
    sendMessage,
    viewportScrollIntent,
    clearViewportScrollIntent,
    submitInteraction,
    enqueueInteraction,
    updateQueuedInteraction,
    deleteQueuedInteraction,
    sendQueuedInteractionNow,
    steerQueuedInteraction,
    handleKeyDown,
    loadMoreMessages,
    resetConversation,
    handleExport,
    onDeleteTurn,
    onToggleReasoning,
    messageListRef,
    listEndRef,
    abortRef,
    formatTime,
    getStepTone,
    assistantStatusLabel,
    getAgentName,
    stringToColor,
    wsOpts,
    agOpts,
    creatingSession,
  };
}

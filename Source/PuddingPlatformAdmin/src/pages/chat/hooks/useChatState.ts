// ── 聊天页状态管理 Hook ──────────────────────────────────────
import { App, Form, notification } from 'antd';
import dayjs from 'dayjs';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  archiveSession,
  compactSession,
  createSession,
  createWorkspace,
  createWorkspaceAgent,
  deleteSession,
  listSessions,
  listSessionMessages,
  listTeams,
  listWorkspaceAgents,
  listWorkspaces,
  renameSession,
  sendChatMessage,
  subscribeSessionEvents,
  subscribeWorkspaceNotifications,
  type AdminChatStreamEvent,
  type ContextCompactionResult,
  type MessageListResponse,
  type SessionRecord,
  type TokenUsageDto,
  type WorkspaceAgentDto,
  type WorkspaceNotification,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import type { AssistantStatus, ChatTurn, TimelineItem, SessionGroup, SubAgentCardMap, SubAgentCard, ChatSource } from '../types';
import { assistantStatusLabel } from '../types';
import { writeDebugSessionState, writeDebugTrace } from '@/utils/debug';

const MESSAGE_PAGE_SIZE = 20;
const SESSION_EVENT_PAGE_SIZE = 50;

interface SessionEventPageResponse {
  events?: unknown[];
  Events?: unknown[];
  hasMore?: boolean;
  HasMore?: boolean;
}

export const stringToColor = (str: string) => {
  let hash = 0;
  for (let i = 0; i < str.length; i++) hash = str.charCodeAt(i) + ((hash << 5) - hash);
  const colors = ['var(--avatar-0)','var(--avatar-1)','var(--avatar-2)','var(--avatar-3)','var(--avatar-4)','var(--avatar-5)','var(--avatar-6)','var(--avatar-7)','var(--avatar-8)','var(--avatar-9)'];
  return colors[Math.abs(hash) % colors.length];
};

export const getAgentName = (a: WorkspaceAgentDto) => a.displayName || a.name || 'Agent';

export const groupSessions = (raw: { sessionId: string; title: string; timestamp: number }[]): SessionGroup[] => {
  const now = dayjs();
  const groups: Record<string, { sessionId: string; title: string; timestamp: number }[]> = {};
  for (const s of raw) {
    const d = dayjs(s.timestamp);
    let key: string;
    if (d.isSame(now, 'day')) key = '今天';
    else if (d.isSame(now.subtract(1, 'day'), 'day')) key = '昨天';
    else if (d.isAfter(now.subtract(7, 'day'))) key = '本周';
    else key = '更早';
    (groups[key] ??= []).push(s);
  }
  return ['今天','昨天','本周','更早'].filter(k => groups[k]?.length).map(label => ({
    label, items: groups[label]!.sort((a,b) => b.timestamp - a.timestamp),
  }));
};

export function shouldAdvanceSequenceForSessionEvent(type: string, hasTargetTurn: boolean): boolean {
  return hasTargetTurn;
}

export function shouldReplayEventsAfterHistory(turns: ChatTurn[]): boolean {
  const latest = turns[turns.length - 1];
  if (!latest) return false;
  const assistant = latest.assistant;
  return assistant.isStreaming ||
    assistant.status === 'thinking' ||
    assistant.status === 'executing' ||
    assistant.status === 'streaming' ||
    assistant.answerMarkdown.trim().length === 0;
}

const HISTORICAL_REPLAY_TERMINAL_EVENTS = new Set([
  'done',
  'error',
  'cancelled',
  'session.closed',
  'context.compaction.completed',
  'context.compaction.failed',
]);

export function shouldHydrateSessionEventReplay(events: Array<{ type: string }>): boolean {
  return events.some(event => HISTORICAL_REPLAY_TERMINAL_EVENTS.has(event.type));
}

export function inferParentSessionIdFromSubSessionId(subSessionId?: string | null): string | null {
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
      const parentSessionId = card.parentSessionId ?? inferParentSessionIdFromSubSessionId(card.subSessionId);
      return parentSessionId === sessionId;
    }),
  );
}

function countCompletedAssistantTurns(turns: ChatTurn[]): number {
  return turns.filter(turn => turn.assistant.answerMarkdown.trim().length > 0).length;
}

/** 从 subagent.* 事件的 data 字段中提取 delta 文本 */
function tryExtractDelta(ev: { data?: string; delta?: string }): string | null {
  if (ev.delta) return ev.delta;
  if (ev.data) {
    try { const d = JSON.parse(ev.data); return d?.delta ?? null; }
    catch { return null; }
  }
  return null;
}

const createId = () => `msg-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
const COMPACT_COMMAND = '/compact';

const createAssistant = (
  id: string,
  renderMode: 'legacy' | 'structured',
  status: AssistantStatus,
  isStreaming: boolean,
): ChatTurn['assistant'] => ({
  id, status, timelineItems: [], answerMarkdown: '', isStreaming, renderMode,
});

const normalizeUsage = (usage?: TokenUsageDto): TokenUsageDto | undefined => (
  usage ? {
    promptTokens: usage.promptTokens,
    completionTokens: usage.completionTokens,
    totalTokens: usage.totalTokens,
    contextWindowTokens: usage.contextWindowTokens,
    promptCacheHitTokens: usage.promptCacheHitTokens,
    promptCacheMissTokens: usage.promptCacheMissTokens,
  } : undefined
);

const isReasoningStep = (status?: string) => {
  const key = (status || '').toLowerCase();
  return key.startsWith('thinking') || key.startsWith('reasoning');
};

const getStepTone = (status?: string): 'executing' | 'success' | 'error' => {
  const key = (status || '').toLowerCase();
  if (key.includes('error') || key.includes('fail') || key.includes('cancel')) return 'error';
  if (key.includes('done') || key.includes('success') || key.includes('complete')) return 'success';
  if (key.includes('tool_call')) return 'executing';
  return 'executing';
};

const getStepMessage = (payload: { message?: string; [key: string]: unknown }) => {
  if (typeof payload.message === 'string' && payload.message.trim()) return payload.message;
  const fallback = Object.entries(payload)
    .filter(([k, v]) => k !== 'status' && k !== 'type' && v !== undefined && v !== null && v !== '')
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
  sessions: { sessionId: string; title: string; timestamp: number }[];
  selectedSessionId: string | null;
  sessionsLoading: boolean;
  groups: SessionGroup[];
  // chat
  turns: ChatTurn[];
  historyLoading: boolean;
  hasMoreMessages: boolean;
  loadingMore: boolean;
  inputValue: string;
  setInputValue: (v: string) => void;
  loading: boolean;
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
  cacheHitRate: number;
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
  handleSelectSession: (sid: string) => Promise<void>;
  handleDeleteSession: (sid: string) => Promise<void>;
  handleArchiveSession: (sid: string) => Promise<void>;
  handleRenameStart: (sid: string, title: string) => void;
  handleRenameSubmit: () => Promise<void>;
  sendMessage: (text: string) => Promise<void>;
  handleKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  loadMoreMessages: () => Promise<void>;
  resetConversation: (nextWorkspaceId?: string, nextAgentId?: string) => Promise<void>;
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
}

export function useChatState(): UseChatStateReturn {
  const { message: messageApi } = App.useApp();

  const [workspaces, setWorkspaces] = useState<WorkspaceWithPermDto[]>([]);
  const [workspaceId, setWorkspaceId] = useState<string>();
  const [workspaceLoading, setWorkspaceLoading] = useState(false);
  const [agents, setAgents] = useState<WorkspaceAgentDto[]>([]);
  const [agentId, setAgentId] = useState<string>();
  const [agentLoading, setAgentLoading] = useState(false);

  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [sessions, setSessions] = useState<{ sessionId: string; title: string; timestamp: number }[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [sessionsLoading, setSessionsLoading] = useState(false);

  const [turns, setTurns] = useState<ChatTurn[]>([]);
  const [subAgentCards, setSubAgentCards] = useState<SubAgentCardMap>({});
  const [historyLoading, setHistoryLoading] = useState(false);
  const [hasMoreMessages, setHasMoreMessages] = useState(false);
  const [oldestMessageCursor, setOldestMessageCursor] = useState<number | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [creatingSession, setCreatingSession] = useState(false);

  const [inputValue, setInputValue] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [latestUsage, setLatestUsage] = useState<TokenUsageDto | undefined>();
  const [mainSessionId, setMainSessionId] = useState<string | null>(() => {
    try { return localStorage.getItem(`pudding:mainSession:${workspaceId}`); } catch { return null; }
  });
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
  const sseSessionIdRef = useRef<string | null>(null);
  const hydrateSessionReplayRef = useRef(false);

  // T-201: 工作区通知 SSE（独立于会话 SSE）
  const [sessionUnreadCounts, setSessionUnreadCounts] = useState<Record<string, number>>({});
  const workspaceNotifyAbortRef = useRef<AbortController | null>(null);
  const workspaceNotifyReconnectRef = useRef<number | null>(null);
  const workspaceNotifyWsIdRef = useRef<string | null>(null);

  // ADR-InkBloom: delta 批处理 refs
  const pendingDeltaRef = useRef<Map<string, string>>(new Map());
  const deltaFlushTimerRef = useRef<number | null>(null);

  const [createSceneOpen, setCreateSceneOpen] = useState(false);
  const [createSceneLoading, setCreateSceneLoading] = useState(false);
  const [createSceneForm] = Form.useForm<{ name: string }>();
  const [renameModalOpen, setRenameModalOpen] = useState(false);
  const [renameTitle, setRenameTitle] = useState('');
  const [renameSessionId, setRenameSessionId] = useState<string | null>(null);

  const selectedAgent = agents.find(a => a.agentId === agentId);

  const clearSessionEventTimers = useCallback(() => {
    if (sessionEventsPollTimerRef.current != null) {
      window.clearInterval(sessionEventsPollTimerRef.current);
      sessionEventsPollTimerRef.current = null;
    }
    if (sessionEventsReconnectTimerRef.current != null) {
      window.clearTimeout(sessionEventsReconnectTimerRef.current);
      sessionEventsReconnectTimerRef.current = null;
    }
  }, []);

  const stopSessionEventStream = useCallback(() => {
    clearSessionEventTimers();
    sessionEventsAbortRef.current?.abort();
    sessionEventsAbortRef.current = null;
    sseSessionIdRef.current = null;
  }, [clearSessionEventTimers]);

  // T-201: 清空会话未读标记
  const clearSessionUnread = useCallback((sessionId: string) => {
    setSessionUnreadCounts(prev => {
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

  // ADR-InkBloom: 合并 delta 到 pending 缓冲，80ms 批刷新一次
  const enqueueDelta = useCallback((turnId: string, delta: string) => {
    pendingDeltaRef.current.set(
      turnId,
      (pendingDeltaRef.current.get(turnId) ?? '') + delta,
    );
    if (deltaFlushTimerRef.current != null) return;
    deltaFlushTimerRef.current = window.setTimeout(() => {
      const pending = new Map(pendingDeltaRef.current);
      pendingDeltaRef.current.clear();
      deltaFlushTimerRef.current = null;
      setTurns(prev => prev.map(turn => {
        const d = pending.get(turn.turnId);
        if (!d) return turn;
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: 'streaming' as const,
            isStreaming: true,
            renderMode: 'structured' as const,
            answerMarkdown: turn.assistant.answerMarkdown + d,
          },
        };
      }));
    }, 80);
  }, []);

  const flushPendingDeltas = useCallback(() => {
    if (deltaFlushTimerRef.current != null) {
      window.clearTimeout(deltaFlushTimerRef.current);
      deltaFlushTimerRef.current = null;
    }
    if (pendingDeltaRef.current.size === 0) return;
    const pending = new Map(pendingDeltaRef.current);
    pendingDeltaRef.current.clear();
    setTurns(prev => prev.map(turn => {
      const d = pending.get(turn.turnId);
      if (!d) return turn;
      return {
        ...turn,
        assistant: {
          ...turn.assistant,
          status: 'streaming' as const,
          isStreaming: true,
          renderMode: 'structured' as const,
          answerMarkdown: turn.assistant.answerMarkdown + d,
        },
      };
    }));
  }, []);

  const normalizeSessionEvent = useCallback((raw: unknown): (AdminChatStreamEvent & { sequenceNum?: number }) | null => {
    if (!raw || typeof raw !== 'object') return null;
    const obj = raw as Record<string, unknown>;

    const rawSeq = obj.sequenceNum ?? obj.SequenceNum;
    const sequenceNum = rawSeq == null ? undefined : Number(rawSeq);

    let payload: Record<string, unknown> = {};
    const rawPayload = obj.payload ?? obj.Payload;
    if (rawPayload && typeof rawPayload === 'object') {
      payload = rawPayload as Record<string, unknown>;
    } else if (typeof rawPayload === 'string' && rawPayload.trim()) {
      try { payload = JSON.parse(rawPayload) as Record<string, unknown>; } catch { payload = {}; }
    }

    const rawDataJson = obj.dataJson ?? obj.DataJson;
    if (Object.keys(payload).length === 0 && typeof rawDataJson === 'string' && rawDataJson.trim()) {
      try { payload = JSON.parse(rawDataJson) as Record<string, unknown>; } catch { payload = {}; }
    }

    const type = String(
      obj.type
      ?? obj.eventType
      ?? obj.EventType
      ?? payload.type
      ?? '',
    ).trim();
    if (!type) return null;

    return {
      ...(payload as Record<string, unknown>),
      type,
      ...(Number.isFinite(sequenceNum) ? { sequenceNum } : {}),
    } as AdminChatStreamEvent & { sequenceNum?: number };
  }, []);

  const listSessionEventsPage = useCallback(async (
    sessionId: string,
    from: number,
    limit = SESSION_EVENT_PAGE_SIZE,
    signal?: AbortSignal,
  ): Promise<SessionEventPageResponse> => {
    const token = localStorage.getItem('pudding_token');
    const headers: Record<string, string> = {};
    if (token) headers.Authorization = `Bearer ${token}`;
    const url = `/api/sessions/${encodeURIComponent(sessionId)}/events?from=${encodeURIComponent(String(from))}&limit=${encodeURIComponent(String(limit))}`;
    const resp = await fetch(url, { method: 'GET', headers, signal });
    if (!resp.ok) {
      throw new Error(`listSessionEvents failed: ${resp.status}`);
    }
    return (await resp.json()) as SessionEventPageResponse;
  }, []);

  const resolveEventTurnId = useCallback((ev: AdminChatStreamEvent): string | null => {
    const anyEv = ev as Record<string, unknown>;
    const directTurnId = typeof anyEv.turnId === 'string' ? anyEv.turnId : null;
    if (directTurnId) return directTurnId;
    const messageId = typeof anyEv.messageId === 'string' ? anyEv.messageId : null;
    if (messageId) {
      const mapped = messageIdToTurnIdRef.current.get(messageId);
      if (mapped) return mapped;
    }
    return latestTurnIdRef.current;
  }, []);

  // ── mapEventToTurn ──────────────────────────────────────────
  const mapEventToTurn = useCallback((turnId: string, ev: AdminChatStreamEvent) => {
    setTurns((prev) => prev.map((turn) => {
      if (turn.turnId !== turnId) return turn;
      if (ev.type === 'metadata') {
        // T-102: 从 metadata 帧推断消息来源（持久 SSE 通道）
        // T-103: 兼容两种命名——SessionRouter 帧输出 source_id(source_name) snake_case，
        // WebSocket connector metadata 使用 sourceId camelCase。
        const anyMeta = ev as Record<string, unknown>;
        const sourceMeta = anyMeta.source_id || anyMeta.sourceId || anyMeta.source_type;
        const source: ChatSource | undefined = sourceMeta ? {
          sourceId: String(anyMeta.source_id || anyMeta.sourceId || 'agent'),
          sourceType: (anyMeta.source_type as ChatSource['sourceType']) || 'agent',
          displayName: String(anyMeta.source_name || 'AI 助手'),
          avatarEmoji: (anyMeta.source_type === 'websocket' ? '🔌' as const :
                       anyMeta.source_type === 'webhook' ? '🪝' as const :
                       anyMeta.source_type === 'email' ? '📧' as const : '🤖' as const),
          avatarColor: stringToColor(String(anyMeta.source_id || anyMeta.sourceId || 'agent')),
          avatarUrl: String(anyMeta.avatar_url || anyMeta.avatarUrl || selectedAgent?.avatarUrl || '') || undefined,
        } : undefined;
        return {
          ...turn,
          source: source || turn.source,
          userMessage: { ...turn.userMessage, status: 'success' as const },
        };
      }
      if (ev.type === 'delta') {
        if (!ev.delta) return turn;
        // ADR-InkBloom: 去重逻辑保留，通过 enqueueDelta 批处理更新
        const current = turn.assistant.answerMarkdown;
        let delta = ev.delta;
        const maxOverlap = Math.min(current.length, delta.length, 10);
        for (let n = maxOverlap; n > 0; n--) {
          if (current.endsWith(delta.substring(0, n))) {
            delta = delta.substring(n);
            break;
          }
        }
        if (!delta) return turn;
        if (hydrateSessionReplayRef.current) {
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
        const items = turn.assistant.timelineItems ?? [];
        // 合并连续 thinking delta：如果最后一个条目也是 thinking，追加而非新建
        const last = items.length > 0 ? items[items.length - 1] : null;
        if (last?.type === 'thinking') {
          return {
            ...turn,
            assistant: {
              ...turn.assistant, status: 'thinking' as const, renderMode: 'structured' as const,
              timelineItems: [
                ...items.slice(0, -1),
                { ...last, text: (last.text ?? '') + ev.delta },
              ],
            },
          };
        }
        return {
          ...turn,
          assistant: {
            ...turn.assistant, status: 'thinking' as const, renderMode: 'structured' as const,
            timelineItems: [...items, {
              id: createId(), type: 'thinking' as const, text: ev.delta,
              timestamp: Date.now(), collapsed: true,
            }],
          },
        };
      }
      if (ev.type === 'tool_call') {
        return {
          ...turn,
          assistant: {
            ...turn.assistant, renderMode: 'structured' as const,
            timelineItems: [...(turn.assistant.timelineItems ?? []), {
              id: createId(), type: 'tool_call' as const,
              status: 'tool_call', name: ev.name, arguments: ev.arguments,
              message: `🔧 调用工具: ${ev.name}\n参数: ${ev.arguments}`,
              timestamp: Date.now(), collapsed: false,
            }],
          },
        };
      }
      if (ev.type === 'tool_result') {
        const exitLabel = ev.exitCode === 0 ? '✓' : '✗';
        return {
          ...turn,
          assistant: {
            ...turn.assistant, renderMode: 'structured' as const,
            timelineItems: [...(turn.assistant.timelineItems ?? []), {
              id: createId(), type: 'tool_result' as const,
              status: ev.exitCode === 0 ? 'success' : 'error',
              name: ev.name, output: ev.output, exitCode: ev.exitCode,
              message: `🔧 ${ev.name} ${exitLabel}\n${ev.output || ev.error || '(empty)'}`,
              timestamp: Date.now(), collapsed: false,
            }],
          },
        };
      }
      if (ev.type === 'subconscious_step') {
        return {
          ...turn,
          assistant: {
            ...turn.assistant, renderMode: 'structured' as const,
            timelineItems: [...(turn.assistant.timelineItems ?? []), {
              id: createId(), type: 'subconscious_step' as const,
              status: ev.status === 'done' ? 'done' : 'thinking',
              message: `🧠 ${ev.message}`,
              timestamp: Date.now(), collapsed: false,
            }],
          },
        };
      }
      if (ev.type === 'context.compaction.started') {
        const items = turn.assistant.timelineItems ?? [];
        const last = items.length > 0 ? items[items.length - 1] : null;
        if (last?.type === 'subconscious_step' && last.status === 'compacting') {
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
            timelineItems: [...items, {
              id: createId(), type: 'subconscious_step' as const,
              status: 'compacting', message: '正在压缩上下文…',
              timestamp: Date.now(), collapsed: false,
            }],
          },
        };
      }
      if (ev.type === 'context.compaction.completed') {
        const before = typeof ev.beforeTokens === 'number' ? ev.beforeTokens : 0;
        const after = typeof ev.afterTokens === 'number' ? ev.afterTokens : 0;
        const count = typeof ev.compactedMessageCount === 'number' ? ev.compactedMessageCount : 0;
        const items = turn.assistant.timelineItems ?? [];
        const alreadyCompleted = items.some((item) =>
          item.type === 'subconscious_step' && item.status === 'success' && item.message === '上下文压缩完成',
        );
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: 'success' as const,
            isStreaming: false,
            renderMode: 'structured' as const,
            answerMarkdown: `上下文已压缩，覆盖 ${count} 条历史消息。${before > 0 ? `\n\nToken 估算：${before} → ${after}` : ''}`,
            timelineItems: alreadyCompleted ? items : [...items, {
              id: createId(), type: 'subconscious_step' as const,
              status: 'success', message: '上下文压缩完成',
              timestamp: Date.now(), collapsed: false,
            }],
          },
        };
      }
      if (ev.type === 'context.compaction.failed') {
        const items = turn.assistant.timelineItems ?? [];
        const message = String(ev.error || '上下文压缩失败');
        const alreadyFailed = items.some((item) =>
          item.type === 'subconscious_step' && item.status === 'error' && item.message === message,
        );
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: 'error' as const,
            isStreaming: false,
            renderMode: 'structured' as const,
            answerMarkdown: message,
            timelineItems: alreadyFailed ? items : [...items, {
              id: createId(), type: 'subconscious_step' as const,
              status: 'error', message,
              timestamp: Date.now(), collapsed: false,
            }],
          },
        };
      }
      // 子代理流式事件：写入独立 SubAgentCard，不再合并到父代理 timeline
      if (ev.type.startsWith('subagent.')) {
        const mappedType = ev.type.substring('subagent.'.length);
        const saData = ev as any;
        const subAgentId = saData.sub_agent_id || saData.id || 'sub';
        if (!subAgentId || subAgentId === 'sub') return turn;
        const cardId = `sa-${subAgentId}`;
        const parentSessionId = typeof saData.parent_session_id === 'string'
          ? saData.parent_session_id
          : inferParentSessionIdFromSubSessionId(subAgentId) ?? sessionIdRef.current ?? selectedSessionId ?? undefined;

        // 子代理 delta → 追加到独立卡片 output
        if (mappedType === 'delta') {
          const innerText = tryExtractDelta(saData);
          if (!innerText) return turn;
          setSubAgentCards(prev => {
            const existing = prev[cardId];
            if (existing) {
              return { ...prev, [cardId]: { ...existing, output: (existing.output ?? '') + innerText } };
            }
            return { ...prev, [cardId]: {
              turnId: cardId, subSessionId: subAgentId,
              parentSessionId,
              taskSummary: saData.template || '子代理', status: 'running',
              spawnedAt: Date.now(), output: innerText,
            }};
          });
          return turn;
        }
        // 子代理 spawned → 创建卡片
        if (mappedType === 'spawned') {
          setSubAgentCards(prev => ({
            ...prev, [cardId]: {
              turnId: cardId, subSessionId: subAgentId,
              parentSessionId,
              templateId: saData.template, modelId: saData.model,
              taskSummary: '处理中...',
              status: 'running', spawnedAt: Date.now(),
            },
          }));
          return turn;
        }
        // 子代理 completed → 更新卡片
        if (mappedType === 'completed') {
          setSubAgentCards(prev => {
            const existing = prev[cardId];
            if (!existing) return prev;
            return { ...prev, [cardId]: {
              ...existing, status: saData.success ? 'completed' : 'failed',
              parentSessionId: existing.parentSessionId ?? parentSessionId,
              completedAt: Date.now(), success: !!saData.success,
              output: existing.output || saData.result_summary || '',
            }};
          });
          return turn;
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
                ...turn.assistant, status: 'thinking' as const, renderMode: 'structured' as const,
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
              ...turn.assistant, status: 'thinking' as const, renderMode: 'structured' as const,
              timelineItems: [...items, {
                id: createId(), type: 'thinking' as const, text: message,
                timestamp: now, collapsed: true,
              }],
            },
          };
        }
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: getStepTone(status) === 'error' ? 'error' : 'executing',
            renderMode: 'structured' as const,
            timelineItems: [...(turn.assistant.timelineItems ?? []), {
              id: createId(), type: 'subconscious_step' as const,
              status, message, timestamp: now, collapsed: false,
            }],
          },
        };
      }
      if (ev.type === 'usage') {
        return { ...turn, assistant: { ...turn.assistant, usage: normalizeUsage(ev.usage) } };
      }
      if (ev.type === 'done') {
        completedTurnsRef.current.add(turnId);
        // ADR-027: debug mode 写入 traceId（来自后端 done frame）
        if (ev.traceId) writeDebugTrace(ev.traceId);
        return {
          ...turn,
          assistant: {
            ...turn.assistant, status: 'success' as const, isStreaming: false,
            answerMarkdown: turn.assistant.answerMarkdown || ev.reply || '(无回复)',
            usage: normalizeUsage(ev.usage) ?? turn.assistant.usage,
          },
        };
      }
      if (ev.type === 'cancelled') {
        return {
          ...turn,
          assistant: {
            ...turn.assistant, status: 'cancelled' as const, isStreaming: false,
            timelineItems: ev.message
              ? [...(turn.assistant.timelineItems ?? []), {
                  id: createId(), type: 'subconscious_step' as const,
                  status: 'cancelled', message: ev.message, timestamp: Date.now(), collapsed: false,
                }]
              : (turn.assistant.timelineItems ?? []),
          },
        };
      }
      if (ev.type === 'error') {
        return {
          ...turn,
          assistant: {
            ...turn.assistant, status: 'error' as const, isStreaming: false,
            timelineItems: [...(turn.assistant.timelineItems ?? []), {
              id: createId(), type: 'subconscious_step' as const,
              status: 'error', message: ev.message || '请求失败', timestamp: Date.now(), collapsed: false,
            }],
          },
        };
      }
      return turn;
    }));
  }, [enqueueDelta, selectedAgent]);

  const applySessionEvent = useCallback((ev: AdminChatStreamEvent) => {
    const anyEv = ev as Record<string, unknown>;
    const messageId = typeof anyEv.messageId === 'string' ? anyEv.messageId : null;
    if (messageId && latestTurnIdRef.current) {
      messageIdToTurnIdRef.current.set(messageId, latestTurnIdRef.current);
    }

    const targetTurnId = resolveEventTurnId(ev);
    const hasTargetTurn = Boolean(targetTurnId);
    if (shouldAdvanceSequenceForSessionEvent(ev.type, hasTargetTurn)) {
      updateLastSequence(ev);
    }

    if (ev.type === 'session.closed') {
      setLoading(false);
      return;
    }
    if (!targetTurnId) return;

    if (ev.type === 'usage' && ev.usage) setLatestUsage(ev.usage);
    if (ev.type === 'done' && ev.usage) setLatestUsage(ev.usage);

    // T-CACHE-008: Accumulate cache hit/miss for the main session
    if (ev.type === 'done' && ev.usage) {
      const hitTokens = ev.usage.promptCacheHitTokens || 0;
      const missTokens = ev.usage.promptCacheMissTokens || 0;
      if (hitTokens > 0 || missTokens > 0) {
        const currentStreamSessionId = sseSessionIdRef.current;
        if (currentStreamSessionId === mainSessionId || (!mainSessionId && currentStreamSessionId === selectedSessionId)) {
          setSessionCacheHitTokens(prev => prev + hitTokens);
          setSessionCacheMissTokens(prev => prev + missTokens);
        }
      }
    }

    // T-102: 持久 SSE 已合并为单一通道 — 不再过滤 delta/thinking/tool_call/tool_result。
    // 所有事件统一路由到 mapEventToTurn 处理。

    // ADR-InkBloom: 终端事件前 flush 所有 pending delta，不丢最后一段
    if (ev.type === 'session.closed' || ev.type === 'done' || ev.type === 'error' || ev.type === 'cancelled' || ev.type === 'context.compaction.completed' || ev.type === 'context.compaction.failed') {
      flushPendingDeltas();
    }

    // T-102: 终端事件管理 loading 状态
    if (ev.type === 'done' || ev.type === 'error' || ev.type === 'cancelled') {
      setLoading(false);
    }
    if (ev.type === 'context.compaction.completed' || ev.type === 'context.compaction.failed') {
      setLoading(false);
    }
    mapEventToTurn(targetTurnId, ev);
  }, [mapEventToTurn, resolveEventTurnId, updateLastSequence, flushPendingDeltas]);

  const formatCompactAnswer = useCallback((result: ContextCompactionResult) => {
    const tokenLine = result.beforeTokens > 0
      ? `\n\nToken 估算：${result.beforeTokens} → ${result.afterTokens}`
      : '';
    return `上下文已压缩，覆盖 ${result.compactedMessageCount} 条历史消息。${tokenLine}`;
  }, []);

  const appendCompactTurn = useCallback((text: string, status: AssistantStatus, result?: ContextCompactionResult) => {
    const now = Date.now();
    const turnId = createId();
    setTurns(prev => [...prev, {
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
        timelineItems: [{
          id: createId(),
          type: 'subconscious_step' as const,
          status: status === 'error' ? 'error' : status === 'success' ? 'success' : 'compacting',
          message: text,
          timestamp: now,
          collapsed: false,
        }],
        answerMarkdown: result
          ? formatCompactAnswer(result)
          : text,
        isStreaming: status === 'executing' || status === 'thinking',
        renderMode: 'structured',
      },
    }]);
    return turnId;
  }, [formatCompactAnswer]);

  const updateCompactTurn = useCallback((
    turnId: string,
    status: AssistantStatus,
    message: string,
    result?: ContextCompactionResult,
  ) => {
    setTurns(prev => prev.map(turn => {
      if (turn.turnId !== turnId) return turn;
      const items = turn.assistant.timelineItems ?? [];
      const itemStatus = status === 'error' ? 'error' : status === 'success' ? 'success' : 'compacting';
      return {
        ...turn,
        assistant: {
          ...turn.assistant,
          status,
          isStreaming: status === 'executing' || status === 'thinking',
          renderMode: 'structured' as const,
          answerMarkdown: result ? formatCompactAnswer(result) : message,
          timelineItems: [...items, {
            id: createId(),
            type: 'subconscious_step' as const,
            status: itemStatus,
            message,
            timestamp: Date.now(),
            collapsed: false,
          }],
        },
      };
    }));
  }, [formatCompactAnswer]);

  const replayMissedSessionEvents = useCallback(async (sessionId: string, signal?: AbortSignal) => {
    let from = Math.max(0, lastSequenceNumRef.current + 1);
    let hasMore = true;
    while (hasMore) {
      const page = await listSessionEventsPage(sessionId, from, SESSION_EVENT_PAGE_SIZE, signal);
      const list = (Array.isArray(page.events) ? page.events : Array.isArray(page.Events) ? page.Events : []) as unknown[];
      if (list.length === 0) break;

      for (const item of list) {
        const normalized = normalizeSessionEvent(item);
        if (!normalized) continue;
        const seq = normalized.sequenceNum;
        if (typeof seq === 'number' && Number.isFinite(seq) && seq <= lastSequenceNumRef.current) continue;
        applySessionEvent(normalized);
      }

      const maxSeqInPage = list
        .map((x) => {
          if (!x || typeof x !== 'object') return Number.NaN;
          const v = (x as Record<string, unknown>).sequenceNum ?? (x as Record<string, unknown>).SequenceNum;
          return Number(v);
        })
        .filter((x) => Number.isFinite(x))
        .reduce((m, x) => Math.max(m, x), Number.NaN);

      if (Number.isFinite(maxSeqInPage)) {
        from = Math.max(from, Number(maxSeqInPage) + 1);
      } else {
        hasMore = false;
      }

      hasMore = hasMore && Boolean(page.hasMore ?? page.HasMore);
    }
  }, [applySessionEvent, listSessionEventsPage, normalizeSessionEvent]);

  const replayLatestTurnSessionEvents = useCallback(async (
    sessionId: string,
    loadedTurns: ChatTurn[],
    signal?: AbortSignal,
  ) => {
    const completedAssistantTurns = countCompletedAssistantTurns(loadedTurns);
    const normalizedEvents: Array<AdminChatStreamEvent & { sequenceNum?: number }> = [];
    let from = 1;
    let hasMore = true;

    while (hasMore) {
      const page = await listSessionEventsPage(sessionId, from, SESSION_EVENT_PAGE_SIZE, signal);
      const list = (Array.isArray(page.events) ? page.events : Array.isArray(page.Events) ? page.Events : []) as unknown[];
      if (list.length === 0) break;

      let maxSeqInPage = Number.NaN;
      for (const item of list) {
        const normalized = normalizeSessionEvent(item);
        if (!normalized) continue;
        normalizedEvents.push(normalized);
        if (typeof normalized.sequenceNum === 'number' && Number.isFinite(normalized.sequenceNum)) {
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

    const previous = normalizedEvents[tailStart - 1];
    if (previous && typeof previous.sequenceNum === 'number' && Number.isFinite(previous.sequenceNum)) {
      lastSequenceNumRef.current = Math.max(lastSequenceNumRef.current, previous.sequenceNum);
    }

    const replayTail = normalizedEvents.slice(tailStart);
    const shouldHydrate = shouldHydrateSessionEventReplay(replayTail);
    const previousHydrateMode = hydrateSessionReplayRef.current;
    hydrateSessionReplayRef.current = shouldHydrate;
    try {
      for (const event of replayTail) {
        if (typeof event.sequenceNum === 'number' &&
          Number.isFinite(event.sequenceNum) &&
          event.sequenceNum <= lastSequenceNumRef.current) {
          continue;
        }
        applySessionEvent(event);
      }
    } finally {
      hydrateSessionReplayRef.current = previousHydrateMode;
    }
  }, [applySessionEvent, listSessionEventsPage, normalizeSessionEvent]);

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
    const compactTurnId = appendCompactTurn('正在压缩上下文…', 'executing');
    latestTurnIdRef.current = compactTurnId;
    try {
      const result = await compactSession(currentSessionId, {
        workspaceId,
        agentId,
        level: 'Full',
        reason: 'manual slash command',
      });
      setLoading(false);
      updateCompactTurn(compactTurnId, 'success', '上下文压缩完成', result);
      messageApi.success(`上下文已压缩，覆盖 ${result.compactedMessageCount} 条历史消息`);
    } catch (e: unknown) {
      setLoading(false);
      const msg = e instanceof Error ? e.message : '上下文压缩失败';
      setError(msg);
      updateCompactTurn(compactTurnId, 'error', msg);
      messageApi.error(msg);
    }
  }, [agentId, appendCompactTurn, loading, messageApi, selectedSessionId, updateCompactTurn, workspaceId]);

  const startSessionEventStream = useCallback((sessionId: string) => {
    if (!sessionId) return;
    stopSessionEventStream();
    sseSessionIdRef.current = sessionId;

    const ctrl = new AbortController();
    sessionEventsAbortRef.current = ctrl;

    const scheduleReconnect = () => {
      if (sessionEventsReconnectTimerRef.current != null) return;
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

    // 周期性补偿：即使连接静默断开，也可通过历史分页追上缺口。
    sessionEventsPollTimerRef.current = window.setInterval(async () => {
      if (sseSessionIdRef.current !== sessionId || ctrl.signal.aborted) return;
      try {
        await replayMissedSessionEvents(sessionId, ctrl.signal);
      } catch {
        // 下次轮询重试
      }
    }, 8000);

    try {
      subscribeSessionEvents(sessionId, (ev) => {
        if (ctrl.signal.aborted || sseSessionIdRef.current !== sessionId) return;
        applySessionEvent(ev);
      }, ctrl.signal);
    } catch {
      scheduleReconnect();
    }

    const originalAbort = ctrl.abort.bind(ctrl);
    ctrl.abort = () => {
      window.removeEventListener('online', onOnline);
      originalAbort();
    };
  }, [applySessionEvent, replayMissedSessionEvents, stopSessionEventStream]);

  // ── toTurnsFromHistory ─────────────────────────────────────
  const toTurnsFromHistory = useCallback((res: MessageListResponse): ChatTurn[] => {
    const mapped: ChatTurn[] = [];
    let pendingUserIndex: number | null = null;
    for (const item of res.items || []) {
      if (item.role === 'user') {
        mapped.push({
          turnId: `hist-turn-${item.id}`,
          userMessage: { id: `hist-user-${item.id}`, text: item.content, timestamp: item.createdAt, status: 'success' },
          assistant: createAssistant(`hist-assistant-${item.id}`, 'legacy', 'success', false),
        });
        pendingUserIndex = mapped.length - 1;
        continue;
      }
      const thinkingItems: TimelineItem[] = (item.thinking || []).map((t: { text: string }, idx: number) => ({
        id: `hist-think-${item.id}-${idx}`, type: 'thinking' as const, text: t.text,
        timestamp: item.createdAt, collapsed: true,
      }));
      let targetIndex = pendingUserIndex;
      if (targetIndex === null) {
        mapped.push({
          turnId: `hist-turn-orphan-${item.id}`,
          userMessage: { id: `hist-user-orphan-${item.id}`, text: '', timestamp: item.createdAt, status: 'success' },
          assistant: createAssistant(`hist-assistant-orphan-${item.id}`, 'legacy', 'success', false),
        });
        targetIndex = mapped.length - 1;
      }
      mapped[targetIndex] = {
        ...mapped[targetIndex],
        assistant: {
          ...mapped[targetIndex].assistant,
          status: 'success', isStreaming: false,
          usage: normalizeUsage(item.usage),
          answerMarkdown: item.content,
          timelineItems: thinkingItems,
          renderMode: thinkingItems.length > 0 ? 'structured' : 'legacy',
        },
      };
      pendingUserIndex = null;
    }
    return mapped;
  }, []);

  // ── resetConversation ──────────────────────────────────────
  const resetConversation = useCallback(async (nextWorkspaceId?: string, nextAgentId?: string) => {
    if (creatingSession) return;
    abortRef.current?.abort(); abortRef.current = null;
    stopSessionEventStream();
    setTurns([]); setError(null); setLatestUsage(undefined); setLoading(false);
    setHasMoreMessages(false); setOldestMessageCursor(null);
    lastSequenceNumRef.current = 0;
    latestTurnIdRef.current = null;
    messageIdToTurnIdRef.current.clear();
    const targetWorkspaceId = nextWorkspaceId ?? workspaceId;
    const targetAgentId = nextAgentId ?? agentId;
    if (!targetWorkspaceId || !targetAgentId) return;
    setCreatingSession(true);
    try {
      const selected = agents.find(a => a.agentId === targetAgentId);
      const agName = selected ? getAgentName(selected) : '新对话';
      const templateId = selected?.sourceTemplateId || `global:${targetAgentId}`;
      const session = await createSession(targetWorkspaceId, templateId, agName);
      sessionIdRef.current = session.sessionId;
      forceNewSessionRef.current = false;
      setSelectedSessionId(session.sessionId);
      setSessions(prev => [{ sessionId: session.sessionId, title: session.title?.trim() || agName, timestamp: Date.now() }, ...prev]);
    } catch {
      messageApi.error('创建会话失败');
    } finally {
      setCreatingSession(false);
    }
  }, [workspaceId, agentId, agents, creatingSession, messageApi, stopSessionEventStream]);

  // ── Effects: load workspaces ───────────────────────────────
  useEffect(() => {
    let a = true;
    (async () => {
      setWorkspaceLoading(true);
      try {
        const items = await listWorkspaces();
        if (!a) return;
        setWorkspaces(items);
        const wid = items.find(x => x.workspaceId === 'default' && x.isEnabled && !x.isFrozen)?.workspaceId
          ?? items.find(x => x.workspaceId === 'default')?.workspaceId
          ?? items.find(x => x.isEnabled && !x.isFrozen)?.workspaceId
          ?? items[0]?.workspaceId;
        setWorkspaceId(wid);
        if (!wid) setError('无可用工作空间');
      } catch (e: unknown) {
        if (a) setError(e instanceof Error ? e.message : '加载失败');
      } finally {
        if (a) setWorkspaceLoading(false);
      }
    })();
    return () => { a = false; };
  }, []);

  // ── Effects: load agents ───────────────────────────────────
  useEffect(() => {
    let a = true;
    (async () => {
      if (!workspaceId) { setAgents([]); setAgentId(undefined); return; }
      setAgentLoading(true);
      try {
        const items = await listWorkspaceAgents(workspaceId);
        if (!a) return;
        if (items.length === 0) {
          try {
            const c = await createWorkspaceAgent(workspaceId, { name: 'Pudding 助手', displayName: '布丁', sourceTemplateId: 'global:general-assistant' });
            setAgents([c]); setAgentId(c.agentId);
          } catch { setAgents([]); setAgentId(undefined); }
        } else {
          setAgents(items);
          setAgentId(items.find(x => x.isEnabled && !x.isFrozen)?.agentId ?? items.find(x => x.isEnabled)?.agentId ?? items[0]?.agentId);
        }
      } catch (e: unknown) {
        if (a) setError(e instanceof Error ? e.message : '加载Agent失败');
      } finally {
        if (a) setAgentLoading(false);
      }
    })();
    return () => { a = false; };
  }, [workspaceId]);

  // ── refreshSessions ────────────────────────────────────────
  const refreshSessions = useCallback(async (options?: { preserveSessionId?: string }) => {
    if (!workspaceId) return;
    try {
      const list = await listSessions(workspaceId);
      const serverMapped: { sessionId: string; title: string; timestamp: number }[]
        = (list || []).filter((s: SessionRecord) => s.status !== 'Frozen').map((s: SessionRecord) => ({
          sessionId: s.sessionId,
          title: s.title?.trim() || s.agentTemplateId?.replace('global:', '') || '对话',
          timestamp: new Date(s.createdAt).getTime(),
        })).sort((a, b) => b.timestamp - a.timestamp);

      setSessions(prev => {
        // ADR: merge 模式 — 如果服务端缺少乐观会话项，保留本地项不被覆盖
        if (options?.preserveSessionId && !serverMapped.some(s => s.sessionId === options.preserveSessionId)) {
          const localItem = prev.find(s => s.sessionId === options.preserveSessionId);
          if (localItem) return [localItem, ...serverMapped];
        }
        return serverMapped;
      });
    } catch { /* 刷新失败保留现有列表，不清空 */ }
  }, [workspaceId]);

  // ADR: 移除 turns.length 触发，会话列表由 sendMessage 同步插入 + 后台刷新补偿
  useEffect(() => { refreshSessions(); }, [refreshSessions]);

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
  const startWorkspaceNotificationStream = useCallback((workspaceId: string) => {
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

    subscribeWorkspaceNotifications(workspaceId, (ev: WorkspaceNotification) => {
      if (ctrl.signal.aborted || workspaceNotifyWsIdRef.current !== workspaceId) return;

      // 子代理完成 → 对应 sessionId 未读计数+1 + Toast
      if (ev.type === 'notification.sub_agent_completed') {
        setSessionUnreadCounts(prev => ({
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
    }, ctrl.signal);
  }, [stopWorkspaceNotificationStream, refreshSessions]);

  // ── auto scroll ────────────────────────────────────────────
  useEffect(() => {
    listEndRef.current?.scrollIntoView({ block: 'end' });
  }, [turns.length]);

  // 记录最新 turn，供持久 SSE 的异步事件路由使用。
  useEffect(() => {
    if (turns.length === 0) {
      latestTurnIdRef.current = null;
      return;
    }
    latestTurnIdRef.current = turns[turns.length - 1].turnId;
  }, [turns.length]);

  // ── handleSelectSession ────────────────────────────────────
  const handleSelectSession = useCallback(async (sid: string) => {
    if (sid === selectedSessionId && turns.length > 0) return;
    const shouldRestartSameSessionStream = sid === selectedSessionId;
    clearSessionUnread(sid);
    historyAbortRef.current?.abort();
    abortRef.current?.abort();
    stopSessionEventStream();
    setSelectedSessionId(sid);
    sessionIdRef.current = sid;
    forceNewSessionRef.current = false;
    lastSequenceNumRef.current = 0;
    messageIdToTurnIdRef.current.clear();
    latestTurnIdRef.current = null;
    setTurns([]);
    setHasMoreMessages(false);
    setOldestMessageCursor(null);
    setHistoryLoading(true);
    const ctrl = new AbortController();
    historyAbortRef.current = ctrl;
    try {
      const res: MessageListResponse = await listSessionMessages(sid, undefined, MESSAGE_PAGE_SIZE);
      if (ctrl.signal.aborted) return;
      const loadedTurns = toTurnsFromHistory(res);
      setTurns(loadedTurns);
      latestTurnIdRef.current = loadedTurns.length > 0
        ? loadedTurns[loadedTurns.length - 1].turnId
        : null;
      setHasMoreMessages(res.hasMore);
      if (res.oldestCreatedAt != null) setOldestMessageCursor(res.oldestCreatedAt);
      if (shouldReplayEventsAfterHistory(loadedTurns)) {
        await replayLatestTurnSessionEvents(sid, loadedTurns, ctrl.signal);
      }
    } catch {
      if (!ctrl.signal.aborted) messageApi.error('加载历史消息失败');
    } finally {
      if (historyAbortRef.current === ctrl) historyAbortRef.current = null;
      setHistoryLoading(false);
      if (shouldRestartSameSessionStream && !ctrl.signal.aborted && sessionIdRef.current === sid) {
        startSessionEventStream(sid);
      }
    }
  }, [selectedSessionId, turns.length, toTurnsFromHistory, messageApi, stopSessionEventStream, startSessionEventStream, clearSessionUnread, replayLatestTurnSessionEvents]);

  // ── handleSetMainSession ──────────────────────────────────
  const handleSetMainSession = useCallback((sessionId: string) => {
    setMainSessionId(sessionId);
    setSessionCacheHitTokens(0);
    setSessionCacheMissTokens(0);
    try { localStorage.setItem(`pudding:mainSession:${workspaceId}`, sessionId); } catch { /* ignore */ }
  }, [workspaceId]);

  // 持久 SSE：跟随当前会话生命周期自动切换。
  useEffect(() => {
    if (!selectedSessionId) {
      stopSessionEventStream();
      return;
    }
    startSessionEventStream(selectedSessionId);
    return () => {
      stopSessionEventStream();
    };
  }, [selectedSessionId, startSessionEventStream, stopSessionEventStream]);

  // ── loadMoreMessages ───────────────────────────────────────
  const loadMoreMessages = useCallback(async () => {
    if (!selectedSessionId || !hasMoreMessages || loadingMore || oldestMessageCursor == null) return;
    setLoadingMore(true);
    try {
      const res: MessageListResponse = await listSessionMessages(selectedSessionId, oldestMessageCursor, MESSAGE_PAGE_SIZE);
      const olderTurns = toTurnsFromHistory(res);
      setTurns(prev => [...olderTurns, ...prev]);
      setHasMoreMessages(res.hasMore);
      if (res.oldestCreatedAt != null) setOldestMessageCursor(res.oldestCreatedAt);
    } catch {
      messageApi.error('加载更早消息失败');
    } finally {
      setLoadingMore(false);
    }
  }, [selectedSessionId, hasMoreMessages, loadingMore, oldestMessageCursor, toTurnsFromHistory, messageApi]);

  // ── scroll to top handler ──────────────────────────────────
  useEffect(() => {
    const el = messageListRef.current;
    if (!el) return;
    const handleScroll = () => {
      if (el.scrollTop < 50 && hasMoreMessages && !loadingMore) {
        loadMoreMessages();
      }
    };
    el.addEventListener('scroll', handleScroll, { passive: true });
    return () => el.removeEventListener('scroll', handleScroll);
  }, [hasMoreMessages, loadingMore, loadMoreMessages]);

  // ── handleDeleteSession ────────────────────────────────────
  const handleDeleteSession = useCallback(async (sid: string) => {
    try {
      await deleteSession(sid);
      messageApi.success('会话已删除');
      setSessions(prev => prev.filter(s => s.sessionId !== sid));
      if (selectedSessionId === sid) {
        stopSessionEventStream();
        setSelectedSessionId(null);
        setTurns([]);
        sessionIdRef.current = undefined;
        lastSequenceNumRef.current = 0;
        messageIdToTurnIdRef.current.clear();
      }
    } catch { messageApi.error('删除失败'); }
  }, [selectedSessionId, messageApi, stopSessionEventStream]);

  // ── handleArchiveSession ───────────────────────────────────
  const handleArchiveSession = useCallback(async (sid: string) => {
    try {
      await archiveSession(sid);
      messageApi.success('会话已归档');
      setSessions(prev => prev.filter(s => s.sessionId !== sid));
      if (selectedSessionId === sid) {
        stopSessionEventStream();
        setSelectedSessionId(null);
        setTurns([]);
        sessionIdRef.current = undefined;
        lastSequenceNumRef.current = 0;
        messageIdToTurnIdRef.current.clear();
      }
    } catch { messageApi.error('归档失败'); }
  }, [selectedSessionId, messageApi, stopSessionEventStream]);

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
      setSessions(prev => prev.map(s => s.sessionId === renameSessionId ? { ...s, title: trimmed } : s));
      setRenameModalOpen(false);
    } catch { messageApi.error('重命名失败'); }
  }, [renameSessionId, renameTitle, messageApi]);

  // ── sendMessage ────────────────────────────────────────────
  // T-102: fire-and-forget POST + 持久 SSE 方案。
  const sendMessage = useCallback(async (text: string) => {
    if (!text || loading || !workspaceId || !agentId) return;
    if (text.trim().toLowerCase() === COMPACT_COMMAND) {
      await handleCompactCommand();
      return;
    }
    setError(null);
    const perfStart = performance.now();
    const now = Date.now();
    const turnId = createId();
    const uid = createId();
    const aid = createId();
    setTurns(p => [...p, {
      turnId,
      userMessage: { id: uid, text, timestamp: now, status: 'sending' },
      assistant: createAssistant(aid, 'structured', 'thinking', true),
    }]);
    const ctrl = new AbortController(); abortRef.current = ctrl; setLoading(true);

    // 注册当前 turn 为最新，供持久 SSE 事件路由
    latestTurnIdRef.current = turnId;

    try {
      // T-102: 非流式 POST — 立即返回 { messageId, sessionId }
      const { messageId, sessionId: returnedSessionId } = await sendChatMessage(workspaceId, {
        messageText: text,
        sessionId: sessionIdRef.current,
        agentId,
        forceNewSession: forceNewSessionRef.current,
      }, ctrl.signal);

      // 更新 sessionId 并绑定 messageId→turnId 映射
      sessionIdRef.current = returnedSessionId;
      setSelectedSessionId(returnedSessionId);
      forceNewSessionRef.current = false;
      messageIdToTurnIdRef.current.set(messageId, turnId);

      // ADR: 首条消息隐式会话的前端物化 — 将返回的 sessionId 同步到左侧 sessions 列表
      const agName = agents.find(a => a.agentId === agentId);
      const agTitle = agName ? getAgentName(agName) : null;
      const optimisticTitle = agTitle || text.slice(0, 30).trim() || '对话';
      setSessions(prev => {
        const idx = prev.findIndex(s => s.sessionId === returnedSessionId);
        if (idx >= 0) {
          const updated = { ...prev[idx], timestamp: now };
          if (!prev[idx].title || prev[idx].title === '对话') updated.title = optimisticTitle;
          return [updated, ...prev.slice(0, idx), ...prev.slice(idx + 1)];
        }
        return [{ sessionId: returnedSessionId, title: optimisticTitle, timestamp: now }, ...prev];
      });

      // ADR-026: debug mode 写入 sessionId/messageId
      writeDebugSessionState(returnedSessionId, messageId);

      // 标记用户消息已送达
      setTurns(p => p.map(t =>
        t.turnId === turnId
          ? { ...t, userMessage: { ...t.userMessage, status: 'success' as const } }
          : t,
      ));

      const ttfm = (performance.now() - perfStart).toFixed(0);
      console.log(`[Perf:SSM] POST returned in ${ttfm}ms msgId=${messageId} session=${returnedSessionId}`);

      // 主动拉取已生成的事件，减少首帧等待延迟
      try {
        await replayMissedSessionEvents(returnedSessionId, ctrl.signal);
      } catch { /* 网络波动，SSE 会重连补偿 */ }

      // ADR: 后台刷新会话列表（merge 模式保留乐观项不被覆盖）
      refreshSessions({ preserveSessionId: returnedSessionId });
    } catch (e: unknown) {
      if (e instanceof Error && e.name === 'AbortError') {
        setTurns(p => p.map(t => t.turnId === turnId ? { ...t, assistant: { ...t.assistant, status: 'cancelled' as const, isStreaming: false } } : t));
      } else {
        setError(e instanceof Error ? e.message : '请求失败');
        setTurns(p => p.map(t => t.turnId === turnId ? { ...t, assistant: { ...t.assistant, status: 'error' as const, isStreaming: false } } : t));
        // ADR: POST 已成功，会话由后端持久化，不做前端回滚
      }
      setLoading(false);
    } finally {
      if (abortRef.current === ctrl) abortRef.current = null;
      // T-102: loading 由持久 SSE 的 done/error/cancelled 事件管理，不在此处关闭
    }
  }, [loading, workspaceId, agentId, agents, replayMissedSessionEvents, refreshSessions, handleCompactCommand]);

  // ── handleKeyDown ──────────────────────────────────────────
  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // Ctrl+Enter: 强制执行
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      const t = inputValue.trim();
      if (!t) return;
      setInputValue('');
      if (t.toLowerCase() === COMPACT_COMMAND) void handleCompactCommand();
      else void sendMessage(t);
      return;
    }
    // Enter: 发送 / 中止生成
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      if (loading) {
        // P0-1 修复: POST 快速返回后 abortRef 已为 null，需改为关闭 SSE + 标记取消
        abortRef.current?.abort();
        stopSessionEventStream();
        setLoading(false);
        // 将最新 assistant 消息标记为已取消
        setTurns(prev => {
          if (prev.length === 0) return prev;
          const last = prev[prev.length - 1];
          if (last.assistant.isStreaming || last.assistant.status === 'thinking' || last.assistant.status === 'streaming' || last.assistant.status === 'executing') {
            return [
              ...prev.slice(0, -1),
              {
                ...last,
                assistant: {
                  ...last.assistant,
                  status: 'cancelled' as const,
                  isStreaming: false,
                  answerMarkdown: (last.assistant.answerMarkdown || '') + '\n\n[已取消]',
                },
              },
            ];
          }
          return prev;
        });
        // 重新建立 SSE 连接以接收后续消息事件
        if (selectedSessionId) {
          startSessionEventStream(selectedSessionId);
        }
      } else {
        const t = inputValue.trim();
        if (!t) return;
        setInputValue('');
        if (t.toLowerCase() === COMPACT_COMMAND) void handleCompactCommand();
        else void sendMessage(t);
      }
      return;
    }
    // ↑: 编辑上一条用户消息（输入框为空且最后一条是 user 消息）
    if (e.key === 'ArrowUp' && !inputValue.trim()) {
      const lastTurn = turns[turns.length - 1];
      if (lastTurn?.userMessage?.text) {
        e.preventDefault();
        setInputValue(lastTurn.userMessage.text);
      }
    }
  }, [loading, inputValue, sendMessage, turns, stopSessionEventStream, startSessionEventStream, selectedSessionId, handleCompactCommand]);

  // ── global Ctrl+Enter ──────────────────────────────────────
  const sendMessageRef = useRef(sendMessage);
  sendMessageRef.current = sendMessage;
  const inputValueRef = useRef(inputValue);
  inputValueRef.current = inputValue;

  useEffect(() => {
    const handler = () => {
      const text = inputValueRef.current.trim();
      if (!text) return;
      setInputValue('');
      sendMessageRef.current(text);
    };
    window.addEventListener('pudding:chat:send', handler);
    return () => window.removeEventListener('pudding:chat:send', handler);
  }, []);

  // ── handleExport ───────────────────────────────────────────
  const handleExport = useCallback(() => {
    if (turns.length === 0) { messageApi.info('无对话'); return; }
    const md = turns.map(t => {
      const blocks: string[] = [];
      if (t.userMessage.text.trim()) blocks.push(`## User · ${dayjs(t.userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${t.userMessage.text}`);
      const items = t.assistant.timelineItems ?? [];
      const thinking = items.filter(i => i.type === 'thinking');
      const steps = items.filter(i => i.type !== 'thinking');
      if (thinking.length > 0) blocks.push(`## Reasoning\n\n${thinking.map(i => `- ${i.text}`).join('\n')}`);
      if (steps.length > 0) blocks.push(`## Steps\n\n${steps.map(i => `- [${i.status}] ${i.message}`).join('\n')}`);
      blocks.push(`## Agent · ${dayjs(t.userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${t.assistant.answerMarkdown}`);
      return blocks.join('\n\n');
    }).join('\n\n---\n\n');
    const b = new Blob([md], { type: 'text/markdown;charset=utf-8' });
    const u = URL.createObjectURL(b);
    const a = document.createElement('a');
    a.href = u; a.download = `pudding-chat-${dayjs().format('YYYYMMDD-HHmmss')}.md`; a.click();
    URL.revokeObjectURL(u);
  }, [turns, messageApi]);

  // ── onDeleteTurn / onToggleReasoning ──────────────────────
  const onDeleteTurn = useCallback((turnId: string) => {
    setTurns(p => p.filter(t => t.turnId !== turnId));
  }, []);

  const onToggleReasoning = useCallback((turnId: string, itemId: string) => {
    setTurns(prev => prev.map(t =>
      t.turnId === turnId ? {
        ...t,
        assistant: {
          ...t.assistant,
          timelineItems: (t.assistant.timelineItems ?? []).map(item =>
            item.id === itemId ? { ...item, collapsed: !item.collapsed } : item
          ),
        },
      } : t
    ));
  }, []);

  // ── derived ────────────────────────────────────────────────
  const wsOpts = workspaces.map(w => ({ value: w.workspaceId, label: w.name || w.workspaceId, disabled: !w.isEnabled || w.isFrozen }));
  const agOpts = agents.map(a => ({
    value: a.agentId,
    label: getAgentName(a),
    disabled: !a.isEnabled || a.isFrozen,
  }));
  const groups = groupSessions(sessions).map(g => ({
    ...g,
    items: g.items.map(s => ({
      ...s,
      unreadCount: sessionUnreadCounts[s.sessionId] || undefined,
    })),
  }));
  const tLimit = latestUsage?.contextWindowTokens ?? 0;
  const tUsed = latestUsage?.totalTokens ?? 0;
  const tPct = tLimit > 0 ? Math.min(100, Math.round((tUsed / tLimit) * 100)) : 0;

  const cacheTotalTokens = sessionCacheHitTokens + sessionCacheMissTokens;
  const cacheHitRate = cacheTotalTokens > 0
    ? Math.round((sessionCacheHitTokens / cacheTotalTokens) * 100)
    : (cacheTotalTokens === 0 && sessionCacheHitTokens === 0 && sessionCacheMissTokens === 0 ? -1 : 0);
  const visibleSubAgentCards = useMemo(
    () => filterSubAgentCardsForSession(subAgentCards, selectedSessionId),
    [subAgentCards, selectedSessionId],
  );

  return {
    workspaces, workspaceId, workspaceLoading, setWorkspaceId, setWorkspaces,
    agents, agentId, agentLoading, setAgentId, selectedAgent,
    sidebarOpen, setSidebarOpen,
    sessions, selectedSessionId, sessionsLoading, groups,
    turns, historyLoading, hasMoreMessages, loadingMore,
    inputValue, setInputValue, loading, error, setError,
    latestUsage, tLimit, tUsed, tPct,
    mainSessionId, sessionCacheHitTokens, sessionCacheMissTokens, cacheHitRate, handleSetMainSession,
    subAgentCards: visibleSubAgentCards,
    sessionUnreadCounts, startWorkspaceNotificationStream, stopWorkspaceNotificationStream, clearSessionUnread,
    createSceneOpen, setCreateSceneOpen, createSceneLoading, createSceneForm,
    renameModalOpen, setRenameModalOpen, renameTitle, setRenameTitle, renameSessionId,
    handleSelectSession, handleDeleteSession, handleArchiveSession,
    handleRenameStart, handleRenameSubmit,
    sendMessage, handleKeyDown, loadMoreMessages, resetConversation, handleExport,
    onDeleteTurn, onToggleReasoning,
    messageListRef, listEndRef, abortRef,
    formatTime, getStepTone, assistantStatusLabel,
    getAgentName, stringToColor,
    wsOpts, agOpts, creatingSession,
  };
}

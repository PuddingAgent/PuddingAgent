// ── 聊天页状态管理 Hook ──────────────────────────────────────
import { App, Form } from 'antd';
import dayjs from 'dayjs';
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  archiveSession,
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
  sendAdminChatMessageStream,
  type AdminChatStreamEvent,
  type MessageListResponse,
  type SessionRecord,
  type TokenUsageDto,
  type WorkspaceAgentDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import type { AssistantStatus, ChatTurn, TimelineItem, SessionGroup } from '../types';
import { assistantStatusLabel } from '../types';

const DEFAULT_CONTEXT_WINDOW = 65536;
const MESSAGE_PAGE_SIZE = 20;

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

const createId = () => `msg-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;

const createAssistant = (
  id: string,
  renderMode: 'legacy' | 'structured',
  status: AssistantStatus,
  isStreaming: boolean,
): ChatTurn['assistant'] => ({
  id, status, timelineItems: [], answerMarkdown: '', isStreaming, renderMode,
});

const normalizeUsage = (usage?: TokenUsageDto): TokenUsageDto | undefined => (
  usage ? { promptTokens: usage.promptTokens, completionTokens: usage.completionTokens, totalTokens: usage.totalTokens, contextWindowTokens: usage.contextWindowTokens } : undefined
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
  // token
  tLimit: number;
  tUsed: number;
  tPct: number;
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
  const [historyLoading, setHistoryLoading] = useState(false);
  const [hasMoreMessages, setHasMoreMessages] = useState(false);
  const [oldestMessageCursor, setOldestMessageCursor] = useState<number | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [creatingSession, setCreatingSession] = useState(false);

  const [inputValue, setInputValue] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [latestUsage, setLatestUsage] = useState<TokenUsageDto | undefined>();
  const sessionIdRef = useRef<string | undefined>(undefined);
  const forceNewSessionRef = useRef<boolean>(false);
  const abortRef = useRef<AbortController | null>(null);
  const historyAbortRef = useRef<AbortController | null>(null);
  const messageListRef = useRef<HTMLDivElement>(null);
  const listEndRef = useRef<HTMLDivElement>(null);

  const [createSceneOpen, setCreateSceneOpen] = useState(false);
  const [createSceneLoading, setCreateSceneLoading] = useState(false);
  const [createSceneForm] = Form.useForm<{ name: string }>();
  const [renameModalOpen, setRenameModalOpen] = useState(false);
  const [renameTitle, setRenameTitle] = useState('');
  const [renameSessionId, setRenameSessionId] = useState<string | null>(null);

  const selectedAgent = agents.find(a => a.agentId === agentId);

  // ── mapEventToTurn ──────────────────────────────────────────
  const mapEventToTurn = useCallback((turnId: string, ev: AdminChatStreamEvent) => {
    setTurns((prev) => prev.map((turn) => {
      if (turn.turnId !== turnId) return turn;
      if (ev.type === 'metadata') {
        return { ...turn, userMessage: { ...turn.userMessage, status: 'success' as const } };
      }
      if (ev.type === 'delta') {
        if (!ev.delta) return turn;
        return {
          ...turn,
          assistant: {
            ...turn.assistant, status: 'streaming' as const, isStreaming: true,
            renderMode: 'structured' as const,
            answerMarkdown: turn.assistant.answerMarkdown + ev.delta,
          },
        };
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
  }, []);

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
    setTurns([]); setError(null); setLatestUsage(undefined); setLoading(false);
    setHasMoreMessages(false); setOldestMessageCursor(null);
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
  }, [workspaceId, agentId, agents, creatingSession, messageApi]);

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
  const refreshSessions = useCallback(async () => {
    if (!workspaceId) return;
    try {
      const list = await listSessions(workspaceId);
      setSessions((list || []).filter((s: SessionRecord) => s.status !== 'Frozen').map((s: SessionRecord) => ({
        sessionId: s.sessionId,
        title: s.title?.trim() || s.agentTemplateId?.replace('global:', '') || '对话',
        timestamp: new Date(s.createdAt).getTime(),
      })).sort((a, b) => b.timestamp - a.timestamp));
    } catch { setSessions([]); }
  }, [workspaceId]);

  useEffect(() => { refreshSessions(); }, [refreshSessions, turns.length]);

  // ── auto scroll ────────────────────────────────────────────
  useEffect(() => {
    listEndRef.current?.scrollIntoView({ block: 'end' });
  }, [turns.length]);

  // ── handleSelectSession ────────────────────────────────────
  const handleSelectSession = useCallback(async (sid: string) => {
    if (sid === selectedSessionId) return;
    historyAbortRef.current?.abort();
    abortRef.current?.abort();
    setSelectedSessionId(sid);
    sessionIdRef.current = sid;
    forceNewSessionRef.current = false;
    setTurns([]);
    setHasMoreMessages(false);
    setOldestMessageCursor(null);
    setHistoryLoading(true);
    const ctrl = new AbortController();
    historyAbortRef.current = ctrl;
    try {
      const res: MessageListResponse = await listSessionMessages(sid, undefined, MESSAGE_PAGE_SIZE);
      if (ctrl.signal.aborted) return;
      setTurns(toTurnsFromHistory(res));
      setHasMoreMessages(res.hasMore);
      if (res.oldestCreatedAt != null) setOldestMessageCursor(res.oldestCreatedAt);
    } catch {
      if (!ctrl.signal.aborted) messageApi.error('加载历史消息失败');
    } finally {
      if (historyAbortRef.current === ctrl) historyAbortRef.current = null;
      setHistoryLoading(false);
    }
  }, [selectedSessionId, toTurnsFromHistory, messageApi]);

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
        setSelectedSessionId(null);
        setTurns([]);
        sessionIdRef.current = undefined;
      }
    } catch { messageApi.error('删除失败'); }
  }, [selectedSessionId, messageApi]);

  // ── handleArchiveSession ───────────────────────────────────
  const handleArchiveSession = useCallback(async (sid: string) => {
    try {
      await archiveSession(sid);
      messageApi.success('会话已归档');
      setSessions(prev => prev.filter(s => s.sessionId !== sid));
      if (selectedSessionId === sid) {
        setSelectedSessionId(null);
        setTurns([]);
        sessionIdRef.current = undefined;
      }
    } catch { messageApi.error('归档失败'); }
  }, [selectedSessionId, messageApi]);

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
  const sendMessage = useCallback(async (text: string) => {
    if (!text || loading || !workspaceId || !agentId) return;
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
    try {
      await sendAdminChatMessageStream(workspaceId, {
        messageText: text,
        sessionId: sessionIdRef.current,
        agentId,
        forceNewSession: forceNewSessionRef.current,
      }, (ev) => {
        if (ev.type === 'metadata') {
          sessionIdRef.current = ev.sessionId; setSelectedSessionId(ev.sessionId); forceNewSessionRef.current = false;
          const ttfm = (performance.now() - perfStart).toFixed(0);
          console.log(`[Perf] Time to first metadata: ${ttfm}ms`);
        }
        if (ev.type === 'delta' || ev.type === 'thinking' || ev.type === 'tool_call') {
          if (!perfStart) { /* first content token */ }
        }
        if (ev.type === 'usage' && ev.usage) setLatestUsage(ev.usage);
        if (ev.type === 'done' && ev.usage) setLatestUsage(ev.usage);
        mapEventToTurn(turnId, ev);
      }, ctrl.signal);
    } catch (e: unknown) {
      if (e instanceof Error && e.name === 'AbortError') {
        setTurns(p => p.map(t => t.turnId === turnId ? { ...t, assistant: { ...t.assistant, status: 'cancelled' as const, isStreaming: false } } : t));
      } else {
        setError(e instanceof Error ? e.message : '请求失败');
        setTurns(p => p.map(t => t.turnId === turnId ? { ...t, assistant: { ...t.assistant, status: 'error' as const, isStreaming: false } } : t));
      }
    } finally {
      if (abortRef.current === ctrl) abortRef.current = null;
      setLoading(false);
    }
  }, [loading, workspaceId, agentId, mapEventToTurn]);

  // ── handleKeyDown ──────────────────────────────────────────
  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // Ctrl+Enter: 强制执行
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      const t = inputValue.trim();
      if (!t) return;
      setInputValue('');
      void sendMessage(t);
      return;
    }
    // Enter: 发送
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      if (loading) {
        abortRef.current?.abort();
      } else {
        const t = inputValue.trim();
        if (!t) return;
        setInputValue('');
        void sendMessage(t);
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
  }, [loading, inputValue, sendMessage, turns]);

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
  const groups = groupSessions(sessions);
  const tLimit = latestUsage?.contextWindowTokens ?? DEFAULT_CONTEXT_WINDOW;
  const tUsed = latestUsage?.totalTokens ?? 0;
  const tPct = Math.min(100, Math.round((tUsed / tLimit) * 100));

  return {
    workspaces, workspaceId, workspaceLoading, setWorkspaceId, setWorkspaces,
    agents, agentId, agentLoading, setAgentId, selectedAgent,
    sidebarOpen, setSidebarOpen,
    sessions, selectedSessionId, sessionsLoading, groups,
    turns, historyLoading, hasMoreMessages, loadingMore,
    inputValue, setInputValue, loading, error, setError,
    latestUsage, tLimit, tUsed, tPct,
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

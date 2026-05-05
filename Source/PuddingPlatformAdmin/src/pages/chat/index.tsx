import {
  CopyOutlined,
  EditOutlined,
  ExclamationCircleOutlined,
  DeleteOutlined,
  DownloadOutlined,
  FolderOpenOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
  MessageOutlined,
  RobotOutlined,
  PlusOutlined,
  SendOutlined,
  SettingOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { history } from '@umijs/max';
import dayjs from 'dayjs';
import { Alert, App, Avatar, Button, ConfigProvider, Divider, Dropdown, Form, Input, Modal, Progress, Select, Space, Spin, Tooltip, Typography } from 'antd';
import { createStyles } from 'antd-style';
import 'katex/dist/katex.min.css';
import Prism from 'prismjs';
import 'prismjs/components/prism-markup';
import 'prismjs/components/prism-clike';
import 'prismjs/components/prism-javascript';
import 'prismjs/components/prism-jsx';
import 'prismjs/components/prism-typescript';
import 'prismjs/components/prism-tsx';
import 'prismjs/components/prism-bash';
import 'prismjs/components/prism-csharp';
import 'prismjs/components/prism-json';
import 'prismjs/components/prism-python';
import 'prismjs/themes/prism-tomorrow.css';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeKatex from 'rehype-katex';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
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
  type TokenUsageDto,
  type WorkspaceAgentDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';

// ── 类型 ─────────────────────────────────────────────
type MessageStatus = 'sending' | 'success' | 'error';
type AssistantStatus = 'thinking' | 'executing' | 'streaming' | 'success' | 'error' | 'cancelled';

interface ReasoningBlock {
  id: string;
  text: string;
  collapsed: boolean;
}

interface StepCard {
  id: string;
  status: string;
  message: string;
  timestamp: number;
}

interface ChatTurn {
  turnId: string;
  userMessage: {
    id: string;
    text: string;
    timestamp: number;
    status: MessageStatus;
  };
  assistant: {
    id: string;
    status: AssistantStatus;
    reasoningBlocks: ReasoningBlock[];
    stepCards: StepCard[];
    answerMarkdown: string;
    isStreaming: boolean;
    usage?: TokenUsageDto;
    renderMode: 'legacy' | 'structured';
  };
}

interface SessionGroup {
  label: string;
  items: { sessionId: string; title: string; timestamp: number }[];
}

const { Text, Title } = Typography;
const SIDEBAR_WIDTH = 260;
const DEFAULT_CONTEXT_WINDOW = 4096;
const MESSAGE_PAGE_SIZE = 20;

const stringToColor = (str: string) => {
  let hash = 0;
  for (let i = 0; i < str.length; i++) hash = str.charCodeAt(i) + ((hash << 5) - hash);
  const colors = ['#f97316','#ef4444','#8b5cf6','#06b6d4','#22c55e','#eab308','#ec4899','#6366f1','#14b8a6','#f43f5e'];
  return colors[Math.abs(hash) % colors.length];
};
const getAgentName = (a: WorkspaceAgentDto) => a.displayName || a.name || 'Agent';
const renderAgentSelectLabel = (a: WorkspaceAgentDto) => {
  const name = getAgentName(a).trim() || 'Agent';
  const emoji = a.avatarEmoji?.trim();
  if (emoji) {
    return <span>{emoji} {name}</span>;
  }

  return (
    <Space size={6}>
      <RobotOutlined />
      <span>{name}</span>
    </Space>
  );
};

const groupSessions = (raw: { sessionId: string; title: string; timestamp: number }[]): SessionGroup[] => {
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

// ── 样式 ─────────────────────────────────────────────
const useStyles = createStyles(({ token }) => ({
  layout: { display: 'flex', height: '100vh', overflow: 'hidden', background: token.colorBgLayout },
  sidebar: {
    width: SIDEBAR_WIDTH, minWidth: SIDEBAR_WIDTH, height: '100%',
    display: 'flex', flexDirection: 'column',
    borderRight: `1px solid ${token.colorBorderSecondary}`,
    background: token.colorBgContainer,
    transition: 'width 0.2s ease, min-width 0.2s ease, border 0.2s ease',
    overflow: 'hidden',
  },
  sidebarCollapsed: { width: 0, minWidth: 0, borderRight: 'none' },
  sidebarHeader: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '10px 12px', borderBottom: `1px solid ${token.colorBorderSecondary}` },
  sidebarNewBtn: { flex: 1, justifyContent: 'flex-start', fontSize: 13, fontWeight: 500 },
  sessionList: { flex: 1, overflowY: 'auto' as const, padding: '4px 8px' },
  groupLabel: { padding: '12px 8px 4px', fontSize: 11, fontWeight: 600, color: token.colorTextTertiary },
  sessionItem: {
    display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', borderRadius: token.borderRadius,
    cursor: 'pointer', fontSize: 13, color: token.colorTextSecondary, transition: 'background 0.15s',
    overflow: 'hidden', '&:hover': { background: token.colorFillQuaternary },
  },
  sessionItemActive: { background: token.colorFillSecondary, color: token.colorText, fontWeight: 500 },
  sessionTitle: { flex: 1, overflow: 'hidden', whiteSpace: 'nowrap' as const, textOverflow: 'ellipsis' },
  mainArea: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, overflow: 'hidden' },
  header: {
    display: 'flex', alignItems: 'center', gap: 10, padding: '6px 14px',
    borderBottom: `1px solid ${token.colorBorderSecondary}`,
    background: token.colorBgContainer, minHeight: 44, flexShrink: 0,
  },
  headerLogo: { width: 24, height: 24, objectFit: 'contain' as const },
  headerBrand: { fontSize: 14, fontWeight: 700, color: token.colorText, whiteSpace: 'nowrap' as const },
  headerSelect: { minWidth: 110, maxWidth: 180, fontSize: 12 },
  headerSpacer: { flex: 1 },
  chatBody: { display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, overflow: 'hidden', padding: '0 20px', background: token.colorBgLayout },
  messageList: { flex: 1, overflowY: 'auto' as const, padding: '14px 0 8px', display: 'flex', flexDirection: 'column' as const, gap: 10 },
  historyLoading: { display: 'flex', justifyContent: 'center', padding: '8px 0' },
  messageRow: { display: 'flex' },
  turnContainer: { position: 'relative' as const, display: 'flex', gap: 12, width: '100%' },
  turnTimeline: { width: 2, borderRadius: 2, background: token.colorBorderSecondary, marginTop: 4, marginBottom: 4, flexShrink: 0 },
  turnBody: { display: 'flex', flexDirection: 'column' as const, gap: 8, width: '100%' },
  messageContent: {
    maxWidth: '74%', display: 'flex', flexDirection: 'column', gap: 4, position: 'relative' as const,
    '&:hover .message-actions': { opacity: 1, transform: 'translateY(0)', pointerEvents: 'auto' as const },
  },
  userContent: { alignItems: 'flex-end' },
  agentContent: { alignItems: 'flex-start' },
  userRow: { justifyContent: 'flex-end' },
  agentRow: { justifyContent: 'flex-start' },
  bubble: { maxWidth: '100%', padding: '10px 16px', borderRadius: token.borderRadiusLG, lineHeight: 1.6, wordBreak: 'break-word' as const, whiteSpace: 'pre-wrap' as const, border: '1px solid transparent' },
  userBubble: { background: token.colorPrimary, color: token.colorTextLightSolid, borderBottomRightRadius: 4 },
  agentBubble: { background: token.colorFillQuaternary, color: token.colorText, border: `1px solid ${token.colorBorderSecondary}`, borderBottomLeftRadius: 4 },
  assistantAnswer: {
    background: token.colorBgContainer,
    color: token.colorText,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: token.borderRadiusLG,
    borderBottomLeftRadius: 4,
    padding: '10px 16px',
  },
  reasoningPanel: {
    background: token.colorBgContainer,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: token.borderRadiusLG,
    padding: '8px 10px',
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 8,
  },
  reasoningHeader: {
    fontSize: 12,
    color: token.colorTextSecondary,
    display: 'flex',
    alignItems: 'center',
    gap: 8,
  },
  reasoningBlock: {
    borderRadius: token.borderRadius,
    background: token.colorFillQuaternary,
    border: `1px solid ${token.colorBorderSecondary}`,
    padding: '6px 8px',
  },
  reasoningToggle: {
    color: token.colorPrimary,
    cursor: 'pointer',
    fontSize: 12,
    userSelect: 'none' as const,
  },
  reasoningText: {
    whiteSpace: 'pre-wrap' as const,
    color: token.colorTextSecondary,
    fontSize: 13,
    lineHeight: 1.6,
    marginTop: 6,
  },
  stepCardList: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 10,
    position: 'relative' as const,
    paddingLeft: 12,
  },
  stepCardLine: {
    position: 'absolute' as const,
    left: 5,
    top: 4,
    bottom: 4,
    width: 2,
    borderRadius: 2,
    background: token.colorBorderSecondary,
  },
  stepCard: {
    position: 'relative' as const,
    background: token.colorFillQuaternary,
    borderRadius: token.borderRadius,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderLeftWidth: 4,
    padding: '8px 10px',
  },
  stepCardDot: {
    position: 'absolute' as const,
    left: -11,
    top: 14,
    width: 8,
    height: 8,
    borderRadius: '50%',
    background: token.colorBorder,
    border: `1px solid ${token.colorBgContainer}`,
  },
  stepCardExecuting: { borderLeftColor: token.colorInfo },
  stepCardSuccess: { borderLeftColor: token.colorSuccess },
  stepCardError: { borderLeftColor: token.colorError },
  stepCardTitle: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8, marginBottom: 4 },
  stepCardStatus: { color: token.colorTextSecondary, fontSize: 12, fontWeight: 600, textTransform: 'uppercase' as const },
  stepCardMessage: { color: token.colorText, fontSize: 13, lineHeight: 1.6, whiteSpace: 'pre-wrap' as const },
  stepCardTime: { color: token.colorTextQuaternary, fontSize: 12 },
  assistantStatusMeta: { display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' as const },
  assistantStatusTag: {
    fontSize: 12,
    borderRadius: 999,
    padding: '2px 8px',
    border: `1px solid ${token.colorBorderSecondary}`,
    background: token.colorFillSecondary,
    color: token.colorTextSecondary,
  },
  errorBubble: { borderColor: token.colorError },
  messageActions: { opacity: 0, transform: 'translateY(3px)', pointerEvents: 'none' as const, transition: 'opacity 0.16s ease, transform 0.16s ease' },
  messageMeta: { display: 'flex', alignItems: 'center', gap: 8, minHeight: 20 },
  timeText: { color: token.colorTextQuaternary, fontSize: 12 },
  sendingText: { color: token.colorTextTertiary, fontSize: 12 },
  timeDivider: { display: 'flex', alignItems: 'center', gap: 8, margin: '2px 0', color: token.colorTextQuaternary, fontSize: 12, '&::before, &::after': { content: '""', flex: 1, height: 1, background: token.colorBorderSecondary } },
  inputPanel: { padding: '10px 0 16px', display: 'flex', flexDirection: 'column', gap: 8 },
  tokenIndicator: { display: 'flex', alignItems: 'center', gap: 8, padding: '0 2px' },
  tokenProgress: { flex: 1 },
  inputArea: { display: 'flex', gap: 8, alignItems: 'flex-end' },
  input: { flex: 1 },
  emptyState: { flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: token.colorTextQuaternary, fontSize: 15 },
  onboardingState: { flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 12, padding: '24px 0' },
  onboardingLogo: { width: 56, height: 56, objectFit: 'contain' as const, opacity: 0.7 },
  onboardingTitle: { margin: 0 },
  onboardingSubtitle: { color: token.colorTextSecondary, fontSize: 14, textAlign: 'center' as const },
  errorAlert: { margin: '8px 0' },
  sidebarEmpty: { flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: token.colorTextQuaternary, fontSize: 13, padding: '0 16px', textAlign: 'center' as const },
  markdownBody: {
    whiteSpace: 'normal' as const,
    '& p': { margin: '0 0 8px' }, '& p:last-child': { marginBottom: 0 },
    '& ul, & ol': { paddingLeft: 22, margin: '6px 0' },
    '& blockquote': { margin: '8px 0', paddingLeft: 12, borderLeft: `3px solid ${token.colorBorder}`, color: token.colorTextSecondary },
    '& table': { borderCollapse: 'collapse' as const },
    '& th, & td': { border: `1px solid ${token.colorBorderSecondary}`, padding: '6px 10px', textAlign: 'left' as const },
    '& th': { background: token.colorFillQuaternary },
  },
  markdownTableScroll: { maxWidth: '100%', overflowX: 'auto' as const, margin: '8px 0' },
  inlineCode: { padding: '1px 5px', borderRadius: 4, background: token.colorFillSecondary, fontSize: '0.92em' },
  codeBlockWrap: { position: 'relative' as const, margin: '10px 0', borderRadius: 10, overflow: 'hidden', background: '#1e1e1e', '& pre': { margin: 0, padding: '14px 16px', overflowX: 'auto' as const } },
  codeCopyButton: { position: 'absolute' as const, top: 8, right: 8, zIndex: 1 },
  streamingCursor: { display: 'inline-block', width: 8, marginLeft: 2, color: token.colorPrimary, animation: 'cursorBlink 1s steps(1) infinite' },
  '@keyframes cursorBlink': { '0%, 100%': { opacity: 1 }, '50%': { opacity: 0 } },
}));

const CodeBlock: React.FC<{ code: string; className?: string; wrapClassName: string; buttonClassName: string }> = ({ code, className, wrapClassName, buttonClassName }) => {
  const ref = useRef<HTMLElement>(null);
  useEffect(() => { if (ref.current) Prism.highlightElement(ref.current); }, [code, className]);
  return <div className={wrapClassName}><Button size="small" className={buttonClassName} icon={<CopyOutlined />} onClick={() => navigator.clipboard.writeText(code)}>复制</Button><pre><code ref={ref} className={className}>{code}</code></pre></div>;
};

const ChatPage: React.FC = () => {
  const { styles, cx } = useStyles();
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
  const createId = () => `msg-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;

  const createAssistant = (
    id: string,
    renderMode: 'legacy' | 'structured',
    status: AssistantStatus,
    isStreaming: boolean,
  ): ChatTurn['assistant'] => ({
    id,
    status,
    reasoningBlocks: [],
    stepCards: [],
    answerMarkdown: '',
    isStreaming,
    renderMode,
  });

  const normalizeUsage = (usage?: TokenUsageDto): TokenUsageDto | undefined => (
    usage
      ? {
          promptTokens: usage.promptTokens,
          completionTokens: usage.completionTokens,
          totalTokens: usage.totalTokens,
          contextWindowTokens: usage.contextWindowTokens,
        }
      : undefined
  );

  const isReasoningStep = (status?: string) => {
    const key = (status || '').toLowerCase();
    return key.startsWith('thinking') || key.startsWith('reasoning');
  };

  const getStepTone = (status?: string): 'executing' | 'success' | 'error' => {
    const key = (status || '').toLowerCase();
    if (key.includes('error') || key.includes('fail') || key.includes('cancel')) return 'error';
    if (key.includes('done') || key.includes('success') || key.includes('complete')) return 'success';
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

  const assistantStatusLabel: Record<AssistantStatus, string> = {
    thinking: '思考中',
    executing: '执行中',
    streaming: '生成中',
    success: '完成',
    error: '错误',
    cancelled: '已取消',
  };

  const mapEventToTurn = useCallback((turnId: string, ev: AdminChatStreamEvent) => {
    setTurns((prev) => prev.map((turn) => {
      if (turn.turnId !== turnId) return turn;
      if (ev.type === 'metadata') {
        return {
          ...turn,
          userMessage: { ...turn.userMessage, status: 'success' },
        };
      }
      if (ev.type === 'delta') {
        if (!ev.delta) return turn;
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: 'streaming',
            isStreaming: true,
            renderMode: 'structured',
            answerMarkdown: turn.assistant.answerMarkdown + ev.delta,
          },
        };
      }
      if (ev.type === 'step') {
        const status = String(ev.status || 'executing');
        const message = getStepMessage(ev);
        const now = Date.now();
        if (isReasoningStep(status)) {
          return {
            ...turn,
            assistant: {
              ...turn.assistant,
              status: 'thinking',
              renderMode: 'structured',
              reasoningBlocks: [
                ...turn.assistant.reasoningBlocks,
                { id: createId(), text: message, collapsed: true },
              ],
            },
          };
        }
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: getStepTone(status) === 'error' ? 'error' : 'executing',
            renderMode: 'structured',
            stepCards: [
              ...turn.assistant.stepCards,
              {
                id: createId(),
                status,
                message,
                timestamp: now,
              },
            ],
          },
        };
      }
      if (ev.type === 'usage') {
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            usage: normalizeUsage(ev.usage),
          },
        };
      }
      if (ev.type === 'done') {
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: 'success',
            isStreaming: false,
            answerMarkdown: turn.assistant.answerMarkdown || ev.reply || '(无回复)',
            usage: normalizeUsage(ev.usage) ?? turn.assistant.usage,
          },
        };
      }
      if (ev.type === 'cancelled') {
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: 'cancelled',
            isStreaming: false,
            stepCards: ev.message
              ? [...turn.assistant.stepCards, { id: createId(), status: 'cancelled', message: ev.message, timestamp: Date.now() }]
              : turn.assistant.stepCards,
          },
        };
      }
      if (ev.type === 'error') {
        return {
          ...turn,
          assistant: {
            ...turn.assistant,
            status: 'error',
            isStreaming: false,
            stepCards: [
              ...turn.assistant.stepCards,
              { id: createId(), status: 'error', message: ev.message || '请求失败', timestamp: Date.now() },
            ],
          },
        };
      }
      return turn;
    }));
  }, []);

  const toTurnsFromHistory = (res: MessageListResponse): ChatTurn[] => {
    const mapped: ChatTurn[] = [];
    let pendingUserIndex: number | null = null;
    for (const item of res.items || []) {
      if (item.role === 'user') {
        mapped.push({
          turnId: `hist-turn-${item.id}`,
          userMessage: {
            id: `hist-user-${item.id}`,
            text: item.content,
            timestamp: item.createdAt,
            status: 'success',
          },
          assistant: createAssistant(`hist-assistant-${item.id}`, 'legacy', 'success', false),
        });
        pendingUserIndex = mapped.length - 1;
        continue;
      }

      const reasoningBlocks: ReasoningBlock[] = (item.thinking || []).map((t, idx) => ({
        id: `hist-reason-${item.id}-${idx}`,
        text: t.text,
        collapsed: true,
      }));

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
          assistant: createAssistant(`hist-assistant-orphan-${item.id}`, 'legacy', 'success', false),
        });
        targetIndex = mapped.length - 1;
      }

      mapped[targetIndex] = {
        ...mapped[targetIndex],
        assistant: {
          ...mapped[targetIndex].assistant,
          status: 'success',
          isStreaming: false,
          usage: normalizeUsage(item.usage),
          answerMarkdown: item.content,
          reasoningBlocks,
          renderMode: reasoningBlocks.length > 0 ? 'structured' : 'legacy',
        },
      };

      pendingUserIndex = null;
    }
    return mapped;
  };

  const formatTime = (ts: number) => {
    const diff = dayjs().diff(dayjs(ts), 'minute');
    if (diff < 1) return '刚刚';
    if (diff < 60) return `${diff}分钟前`;
    return dayjs(ts).format('MM-DD HH:mm');
  };

  const resetConversation = useCallback(async (nextWorkspaceId?: string, nextAgentId?: string) => {
    if (creatingSession) return;
    abortRef.current?.abort(); abortRef.current = null;
    setTurns([]); setError(null); setLatestUsage(undefined); setLoading(false);
    setHasMoreMessages(false); setOldestMessageCursor(null);

    const targetWorkspaceId = nextWorkspaceId ?? workspaceId;
    const targetAgentId = nextAgentId ?? agentId;
    if(!targetWorkspaceId || !targetAgentId) return;

    setCreatingSession(true);
    try {
      const selected = agents.find(a => a.agentId === targetAgentId);
      const agName = selected ? getAgentName(selected) : '新对话';
      const templateId = selected?.sourceTemplateId || `global:${targetAgentId}`;
      const session = await createSession(targetWorkspaceId, templateId, agName);
      sessionIdRef.current = session.sessionId;
      forceNewSessionRef.current = false;
      setSelectedSessionId(session.sessionId);
      setSessions(prev => [{
        sessionId: session.sessionId,
        title: session.title?.trim() || agName,
        timestamp: Date.now(),
      }, ...prev]);
    } catch {
      messageApi.error('创建会话失败');
    } finally {
      setCreatingSession(false);
    }
  }, [workspaceId, agentId, agents, creatingSession, messageApi]);

  useEffect(() => { let a = true; (async () => { setWorkspaceLoading(true); try { const items = await listWorkspaces(); if (!a) return; setWorkspaces(items); const wid = items.find(x => x.workspaceId==='default'&&x.isEnabled&&!x.isFrozen)?.workspaceId??items.find(x=>x.workspaceId==='default')?.workspaceId??items.find(x=>x.isEnabled&&!x.isFrozen)?.workspaceId??items[0]?.workspaceId; setWorkspaceId(wid); if(!wid) setError('无可用工作空间'); } catch(e:any){ if(a) setError(e?.message||'加载失败'); } finally { if(a) setWorkspaceLoading(false); } })(); return ()=>{a=false}; }, []);

  useEffect(() => { let a = true; (async () => { if(!workspaceId){ setAgents([]); setAgentId(undefined); return; } setAgentLoading(true); try { const items = await listWorkspaceAgents(workspaceId); if(!a) return; if(items.length===0){ try { const c = await createWorkspaceAgent(workspaceId, {name:'Pudding 助手',displayName:'布丁',sourceTemplateId:'global:general-assistant'}); setAgents([c]); setAgentId(c.agentId); } catch { setAgents([]); setAgentId(undefined); } } else { setAgents(items); setAgentId(items.find(x=>x.isEnabled&&!x.isFrozen)?.agentId??items.find(x=>x.isEnabled)?.agentId??items[0]?.agentId); } } catch(e:any){ if(a) setError(e?.message||'加载Agent失败'); } finally { if(a) setAgentLoading(false); } })(); return ()=>{a=false}; }, [workspaceId]);

  const refreshSessions = useCallback(async () => { if(!workspaceId) return; try { const list = await listSessions(workspaceId); setSessions((list||[]).filter((s:any)=>s.status!=='Frozen').map((s:any)=>({sessionId:s.sessionId,title:s.title?.trim()||s.agentTemplateId?.replace('global:','')||'对话',timestamp:new Date(s.createdAt).getTime()})).sort((a:any,b:any)=>b.timestamp-a.timestamp)); } catch { setSessions([]); } }, [workspaceId]);
  useEffect(() => { refreshSessions(); }, [refreshSessions, turns.length]);

  useEffect(() => {
    listEndRef.current?.scrollIntoView({ block: 'end' });
  }, [turns.length]);

  const handleSelectSession = useCallback(async (sid: string) => {
    if(sid === selectedSessionId) return;

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
      if (res.oldestCreatedAt != null) {
        setOldestMessageCursor(res.oldestCreatedAt);
      }
    } catch {
      if (!ctrl.signal.aborted) {
        messageApi.error('加载历史消息失败');
      }
    } finally {
      if (historyAbortRef.current === ctrl) historyAbortRef.current = null;
      setHistoryLoading(false);
    }
  }, [selectedSessionId, messageApi]);

  const loadMoreMessages = useCallback(async () => {
    if (!selectedSessionId || !hasMoreMessages || loadingMore || oldestMessageCursor == null) return;
    setLoadingMore(true);
    try {
      const res: MessageListResponse = await listSessionMessages(
        selectedSessionId, oldestMessageCursor, MESSAGE_PAGE_SIZE);
      const olderTurns = toTurnsFromHistory(res);
      setTurns(prev => [...olderTurns, ...prev]);
      setHasMoreMessages(res.hasMore);
      if (res.oldestCreatedAt != null) {
        setOldestMessageCursor(res.oldestCreatedAt);
      }
    } catch {
      messageApi.error('加载更早消息失败');
    } finally {
      setLoadingMore(false);
    }
  }, [selectedSessionId, hasMoreMessages, loadingMore, oldestMessageCursor, messageApi]);

  // 滚动到顶部时加载更早消息
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

  const handleDeleteSession = useCallback(async (sid: string) => {
    try {
      await deleteSession(sid);
      messageApi.success('会话已删除');
      setSessions((prev) => prev.filter((s) => s.sessionId !== sid));
      if (selectedSessionId === sid) {
        setSelectedSessionId(null);
        setTurns([]);
        sessionIdRef.current = undefined;
      }
    } catch {
      messageApi.error('删除失败');
    }
  }, [selectedSessionId, messageApi]);

  const handleArchiveSession = useCallback(async (sid: string) => {
    try {
      await archiveSession(sid);
      messageApi.success('会话已归档');
      setSessions((prev) => prev.filter((s) => s.sessionId !== sid));
      if (selectedSessionId === sid) {
        setSelectedSessionId(null);
        setTurns([]);
        sessionIdRef.current = undefined;
      }
    } catch {
      messageApi.error('归档失败');
    }
  }, [selectedSessionId, messageApi]);

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
      setSessions((prev) => prev.map((s) => (
        s.sessionId === renameSessionId ? { ...s, title: trimmed } : s
      )));
      setRenameModalOpen(false);
    } catch {
      messageApi.error('重命名失败');
    }
  }, [renameSessionId, renameTitle, messageApi]);

  const sendMessage = async (text: string) => {
    if(!text||loading||!workspaceId||!agentId) return;
    setError(null);
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
    try { await sendAdminChatMessageStream(workspaceId,{messageText:text,sessionId:sessionIdRef.current,agentId,forceNewSession:forceNewSessionRef.current},(ev)=>{
      if(ev.type==='metadata') {
        sessionIdRef.current=ev.sessionId; setSelectedSessionId(ev.sessionId); forceNewSessionRef.current=false;
      }
      if(ev.type==='usage' && ev.usage) setLatestUsage(ev.usage);
      if(ev.type==='done' && ev.usage) setLatestUsage(ev.usage);
      mapEventToTurn(turnId, ev);
    },ctrl.signal); } catch(e:any){ 
      if(e?.name==='AbortError') {
        setTurns(p=>p.map(t=>t.turnId===turnId ? {...t, assistant:{...t.assistant, status:'cancelled' as const, isStreaming:false}} : t));
      } else {
        setError(e?.message||'请求失败'); 
        setTurns(p=>p.map(t=>t.turnId===turnId ? {...t, assistant:{...t.assistant, status:'error' as const, isStreaming:false}} : t));
      } 
    }
    finally { if(abortRef.current===ctrl) abortRef.current=null; setLoading(false); }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => { if(e.key==='Enter'&&!e.shiftKey){ e.preventDefault(); loading ? abortRef.current?.abort() : (()=>{ const t=inputValue.trim(); if(!t) return; setInputValue(''); void sendMessage(t); })(); } };

  // 全局 Ctrl+Enter 快捷键：监听 pudding:chat:send 自定义事件触发发送
  const sendMessageRef = useRef(sendMessage);
  sendMessageRef.current = sendMessage;
  const inputValueRef2 = useRef(inputValue);
  inputValueRef2.current = inputValue;

  useEffect(() => {
    const handler = () => {
      const text = inputValueRef2.current.trim();
      if (!text) return;
      setInputValue('');
      sendMessageRef.current(text);
    };
    window.addEventListener('pudding:chat:send', handler);
    return () => window.removeEventListener('pudding:chat:send', handler);
  }, []);

  const renderMd = (markdownText: string, isStreaming?: boolean) => (
    <div className={styles.markdownBody}>
      <ReactMarkdown remarkPlugins={[remarkGfm,remarkMath]} rehypePlugins={[rehypeKatex]}
        components={{ table:({children,...p}:any)=><div className={styles.markdownTableScroll}><table {...p}>{children}</table></div>,
          code:({inline,className,children,...p}:any)=>{ const c=String(children??'').replace(/\n$/,''); if(inline) return <code className={styles.inlineCode} {...p}>{children}</code>; return <CodeBlock code={c} className={className} wrapClassName={styles.codeBlockWrap} buttonClassName={styles.codeCopyButton} />; } }}>
        {markdownText || (isStreaming ? ' ' : '')}
      </ReactMarkdown>
      {isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );

  const wsOpts = workspaces.map(w=>({value:w.workspaceId,label:w.name||w.workspaceId,disabled:!w.isEnabled||w.isFrozen}));
  const agOpts = agents.map(a=>({
    value:a.agentId,
    label:renderAgentSelectLabel(a),
    disabled:!a.isEnabled||a.isFrozen,
  }));
  const groups = groupSessions(sessions);
  const tLimit = latestUsage?.contextWindowTokens??DEFAULT_CONTEXT_WINDOW;
  const tUsed = latestUsage?.totalTokens??0;
  const tPct = Math.min(100,Math.round((tUsed/tLimit)*100));

  return (
    <ConfigProvider theme={{ token: { colorPrimary: '#7c3aed', borderRadius: 8 } }}>
      <App>
        <div className={styles.layout}>
          <div className={cx(styles.sidebar, !sidebarOpen && styles.sidebarCollapsed)}>
            <div className={styles.sidebarHeader}>
              <Button type="primary" icon={<PlusOutlined />} className={styles.sidebarNewBtn} onClick={()=>{void resetConversation();}} disabled={creatingSession}>新对话</Button>
              <Tooltip title="收起"><Button type="text" size="small" icon={<MenuFoldOutlined />} onClick={()=>setSidebarOpen(false)} /></Tooltip>
            </div>
            <div className={styles.sessionList}>
              {sessionsLoading && <div style={{textAlign:'center',padding:16}}><Spin /></div>}
              {!sessionsLoading && groups.length===0 && <div className={styles.sidebarEmpty}>在这里开始你的第一段对话</div>}
              {groups.map(g => <React.Fragment key={g.label}><div className={styles.groupLabel}>{g.label}</div>{g.items.map(s => (
                <Dropdown
                  key={s.sessionId}
                  trigger={['contextMenu']}
                  menu={{
                    items: [
                      {
                        key: 'rename',
                        icon: <EditOutlined />,
                        label: '重命名',
                        onClick: () => handleRenameStart(s.sessionId, s.title),
                      },
                      {
                        key: 'archive',
                        icon: <FolderOpenOutlined />,
                        label: '归档',
                        onClick: () => {
                          Modal.confirm({
                            title: '确认归档',
                            icon: <ExclamationCircleOutlined />,
                            content: `归档会话「${s.title}」？归档后不再显示在列表中。`,
                            okText: '归档',
                            cancelText: '取消',
                            onOk: () => handleArchiveSession(s.sessionId),
                          });
                        },
                      },
                      { type: 'divider' },
                      {
                        key: 'delete',
                        icon: <DeleteOutlined />,
                        label: '删除',
                        danger: true,
                        onClick: () => {
                          Modal.confirm({
                            title: '确认删除',
                            icon: <ExclamationCircleOutlined />,
                            content: `删除会话「${s.title}」后将无法恢复。`,
                            okText: '删除',
                            okType: 'danger',
                            cancelText: '取消',
                            onOk: () => handleDeleteSession(s.sessionId),
                          });
                        },
                      },
                    ],
                  }}
                >
                  <div className={cx(styles.sessionItem, s.sessionId===selectedSessionId&&styles.sessionItemActive)} onClick={()=>handleSelectSession(s.sessionId)}>
                    <MessageOutlined style={{fontSize:14,flexShrink:0}} />
                    <span className={styles.sessionTitle}>{s.title}</span>
                  </div>
                </Dropdown>
              ))}</React.Fragment>)}
            </div>
          </div>
          <div className={styles.mainArea}>
            <div className={styles.header}>
              {!sidebarOpen && <Button type="text" size="small" icon={<MenuUnfoldOutlined />} onClick={()=>setSidebarOpen(true)} />}
              <img src="/admin/assets/images/logo.png" alt="P" className={styles.headerLogo} />
              <span className={styles.headerBrand}>Pudding</span>
              <Select className={styles.headerSelect} size="small" variant="borderless" value={workspaceId} loading={workspaceLoading} options={wsOpts} onChange={v=>{setWorkspaceId(v);}} placeholder="工作空间" popupMatchSelectWidth={false}
                dropdownRender={menu=><><>{menu}</><Divider style={{margin:'4px 0'}} /><Button type="link" block size="small" onClick={()=>{createSceneForm.resetFields();setCreateSceneOpen(true);}}>+ 新建工作空间</Button></>} />
              <Select className={styles.headerSelect} size="small" variant="borderless" value={agentId} loading={agentLoading} options={agOpts} onChange={v=>{setAgentId(v);void resetConversation(undefined, v);}} placeholder="Agent" popupMatchSelectWidth={false} notFoundContent="无Agent" />
              <div className={styles.headerSpacer} />
              {selectedAgent && <Tooltip title={getAgentName(selectedAgent)}><Avatar size={26} src={selectedAgent.avatarUrl||undefined} style={{background:stringToColor(getAgentName(selectedAgent)),flexShrink:0}}>{getAgentName(selectedAgent).charAt(0)}</Avatar></Tooltip>}
              <Tooltip title="控制台"><Button type="text" size="small" icon={<SettingOutlined />} onClick={()=>history.push('/workspace')} /></Tooltip>
            </div>
            <div className={styles.chatBody}>
              <div className={styles.messageList} ref={messageListRef}>
                {!agentId && !error && <div className={styles.onboardingState}><img src="/admin/assets/images/logo.png" alt="Pudding" className={styles.onboardingLogo} /><Title level={2} className={styles.onboardingTitle}>你好，我是布丁</Title><Text className={styles.onboardingSubtitle}>选择一个工作空间和 Agent，然后把任务交给我。</Text></div>}
                {agentId && turns.length===0 && !error && !historyLoading && <div className={styles.emptyState}>开始和 Agent 对话吧</div>}
                {historyLoading && <div className={styles.historyLoading}><Spin /></div>}
                {loadingMore && <div style={{textAlign:'center',padding:8}}><Spin size="small" /></div>}
                {hasMoreMessages && !loadingMore && (
                  <div style={{textAlign:'center',padding:8,cursor:'pointer',color:'var(--ant-color-primary)'}} onClick={loadMoreMessages}>
                    加载更多历史消息
                  </div>
                )}
                {turns.map((turn) => {
                  const { assistant, userMessage } = turn;
                  const isLegacyAssistant = assistant.renderMode === 'legacy' && assistant.reasoningBlocks.length === 0 && assistant.stepCards.length === 0;
                  const showUserBubble = Boolean(userMessage.text.trim()) || assistant.renderMode === 'structured';
                  const showAssistant = assistant.renderMode === 'structured' || Boolean(assistant.answerMarkdown) || assistant.isStreaming || assistant.status === 'error' || assistant.status === 'cancelled';

                  return <React.Fragment key={turn.turnId}>
                    {showUserBubble && <div className={cx(styles.messageRow, styles.userRow)}>
                      <div className={cx(styles.messageContent, styles.userContent)}>
                        <div className={cx(styles.bubble, styles.userBubble, userMessage.status==='error'&&styles.errorBubble)}>{userMessage.text}</div>
                        <Space size={2} className={`${styles.messageActions} message-actions`}>
                          <Tooltip title="复制"><Button size="small" type="text" icon={<CopyOutlined />} onClick={()=>navigator.clipboard.writeText(userMessage.text)} /></Tooltip>
                          <Tooltip title="删除"><Button size="small" type="text" danger icon={<DeleteOutlined />} onClick={()=>setTurns(p=>p.filter(t=>t.turnId!==turn.turnId))} /></Tooltip>
                        </Space>
                        <div className={styles.messageMeta}>
                          <Tooltip title={dayjs(userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}><Text className={styles.timeText}>{formatTime(userMessage.timestamp)}</Text></Tooltip>
                          {userMessage.status==='sending' && <Text className={styles.sendingText}>发送中...</Text>}
                        </div>
                      </div>
                    </div>}

                    {showAssistant && <div className={cx(styles.messageRow, styles.agentRow)}>
                      <div className={cx(styles.messageContent, styles.agentContent)}>
                        {isLegacyAssistant ? (
                          <div className={cx(styles.bubble, styles.agentBubble, assistant.status==='error'&&styles.errorBubble)}>{renderMd(assistant.answerMarkdown, assistant.isStreaming)}</div>
                        ) : (
                          <div className={styles.turnContainer}>
                            <div className={styles.turnTimeline} />
                            <div className={styles.turnBody}>
                              {assistant.reasoningBlocks.length > 0 && <div className={styles.reasoningPanel}>
                                <div className={styles.reasoningHeader}>思维链</div>
                                {assistant.reasoningBlocks.map((block) => <div key={block.id} className={styles.reasoningBlock}>
                                  <div className={styles.reasoningToggle} onClick={()=>setTurns(prev=>prev.map(t=>t.turnId===turn.turnId?{...t,assistant:{...t.assistant,reasoningBlocks:t.assistant.reasoningBlocks.map(rb=>rb.id===block.id?{...rb,collapsed:!rb.collapsed}:rb)}}:t))}>{block.collapsed ? '展开' : '收起'}</div>
                                  {!block.collapsed && <div className={styles.reasoningText}>{block.text}</div>}
                                </div>)}
                              </div>}

                              {assistant.stepCards.length > 0 && <div className={styles.stepCardList}>
                                <div className={styles.stepCardLine} />
                                {assistant.stepCards.map((card) => {
                                  const tone = getStepTone(card.status);
                                  return <div key={card.id} className={cx(styles.stepCard, tone==='success'&&styles.stepCardSuccess, tone==='error'&&styles.stepCardError, tone==='executing'&&styles.stepCardExecuting)}>
                                    <span className={styles.stepCardDot} />
                                    <div className={styles.stepCardTitle}>
                                      <span className={styles.stepCardStatus}>{card.status || 'step'}</span>
                                      <span className={styles.stepCardTime}>{formatTime(card.timestamp)}</span>
                                    </div>
                                    <div className={styles.stepCardMessage}>{card.message}</div>
                                  </div>;
                                })}
                              </div>}

                              <div className={styles.assistantAnswer}>{renderMd(assistant.answerMarkdown, assistant.isStreaming)}</div>

                              <div className={cx(styles.messageMeta, styles.assistantStatusMeta)}>
                                <span className={styles.assistantStatusTag}>{assistantStatusLabel[assistant.status]}</span>
                                <Tooltip title={dayjs(userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}><Text className={styles.timeText}>{formatTime(userMessage.timestamp)}</Text></Tooltip>
                                {assistant.usage?.totalTokens ? <Text className={styles.sendingText}>{assistant.usage.totalTokens.toLocaleString()} tokens</Text> : null}
                              </div>
                            </div>
                          </div>
                        )}

                        <Space size={2} className={`${styles.messageActions} message-actions`}>
                          <Tooltip title="复制"><Button size="small" type="text" icon={<CopyOutlined />} onClick={()=>navigator.clipboard.writeText(assistant.answerMarkdown)} /></Tooltip>
                          <Tooltip title="删除"><Button size="small" type="text" danger icon={<DeleteOutlined />} onClick={()=>setTurns(p=>p.filter(t=>t.turnId!==turn.turnId))} /></Tooltip>
                        </Space>
                        {isLegacyAssistant && <div className={styles.messageMeta}>
                          <Tooltip title={dayjs(userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}><Text className={styles.timeText}>{formatTime(userMessage.timestamp)}</Text></Tooltip>
                          {assistant.isStreaming && <Text className={styles.sendingText}>生成中...</Text>}
                          {assistant.usage?.totalTokens ? <Text className={styles.sendingText}>{assistant.usage.totalTokens.toLocaleString()} tokens</Text> : null}
                        </div>}
                      </div>
                    </div>}
                  </React.Fragment>;
                })}
                {error && <Alert type="error" message={error} closable onClose={()=>setError(null)} className={styles.errorAlert} />}
                <div ref={listEndRef} />
              </div>
              <div className={styles.inputPanel}>
                <div className={styles.tokenIndicator}><Text type="secondary" style={{fontSize:12}}>Tokens</Text><Progress className={styles.tokenProgress} percent={tPct} size="small" /><Text type="secondary" style={{fontSize:12}}>{tUsed}/{tLimit}</Text></div>
                <div className={styles.inputArea}>
                  <Input.TextArea value={inputValue} onChange={e=>setInputValue(e.target.value)} onKeyDown={handleKeyDown} placeholder="交给我吧。Enter 发送，Shift+Enter 换行" disabled={!workspaceId||!agentId} autoSize={{minRows:1,maxRows:5}} className={styles.input} />
                  <Button type={loading?'default':'primary'} danger={loading} icon={loading?<StopOutlined />:<SendOutlined />} onClick={loading?(()=>abortRef.current?.abort()):(()=>{const t=inputValue.trim();if(!t)return;setInputValue('');void sendMessage(t);})} disabled={loading?false:(!inputValue.trim()||!workspaceId||!agentId)}>{loading?'停止':'发送'}</Button>
                  <Tooltip title="导出"><Button icon={<DownloadOutlined />} onClick={()=>{if(turns.length===0){messageApi.info('无对话');return;}const md=turns.map(t=>{const blocks:string[]=[];if(t.userMessage.text.trim()) blocks.push(`## User · ${dayjs(t.userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${t.userMessage.text}`);if(t.assistant.reasoningBlocks.length>0) blocks.push(`## Reasoning\n\n${t.assistant.reasoningBlocks.map(r=>`- ${r.text}`).join('\n')}`);if(t.assistant.stepCards.length>0) blocks.push(`## Steps\n\n${t.assistant.stepCards.map(s=>`- [${s.status}] ${s.message}`).join('\n')}`);blocks.push(`## Agent · ${dayjs(t.userMessage.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${t.assistant.answerMarkdown}`);return blocks.join('\n\n');}).join('\n\n---\n\n');const b=new Blob([md],{type:'text/markdown;charset=utf-8'});const u=URL.createObjectURL(b);const a=document.createElement('a');a.href=u;a.download=`pudding-chat-${dayjs().format('YYYYMMDD-HHmmss')}.md`;a.click();URL.revokeObjectURL(u);}} /></Tooltip>
                </div>
              </div>
            </div>
          </div>
          <Modal title="新建工作空间" open={createSceneOpen} onOk={async()=>{try{const v=await createSceneForm.validateFields();setCreateSceneLoading(true);const teams=await listTeams();const tid=teams[0]?.teamId;if(!tid){setError('无可用分组');return;}const wsId=(v.name.trim().toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-+|-+$/g,'').slice(0,48)||'ws')+'-'+Date.now().toString().slice(-6);await createWorkspace({workspaceId:wsId,teamId:tid,name:v.name,teamAccessPolicy:'Write',companyAccessPolicy:'None'});const items=await listWorkspaces();setWorkspaces(items);const nextWorkspaceId = items[items.length-1]?.workspaceId;setWorkspaceId(nextWorkspaceId);void resetConversation(nextWorkspaceId);setCreateSceneOpen(false);}catch(e:any){if(e&&typeof e==='object'&&'errorFields' in e) return;setError('创建工作空间失败');}finally{setCreateSceneLoading(false);}}} onCancel={()=>setCreateSceneOpen(false)} confirmLoading={createSceneLoading} okText="创建" cancelText="取消" destroyOnClose>
            <Form form={createSceneForm} layout="vertical"><Form.Item name="name" label="名称" rules={[{required:true,message:'请输入名称'},{max:128}]}><Input placeholder="例如：研发协作空间" /></Form.Item></Form>
          </Modal>
          <Modal
            title="重命名会话"
            open={renameModalOpen}
            onOk={handleRenameSubmit}
            onCancel={() => setRenameModalOpen(false)}
            okText="确定"
            cancelText="取消"
            confirmLoading={false}
          >
            <Input
              value={renameTitle}
              onChange={(e) => setRenameTitle(e.target.value)}
              onPressEnter={handleRenameSubmit}
              placeholder="输入新标题"
              maxLength={50}
              autoFocus
            />
          </Modal>
        </div>
      </App>
    </ConfigProvider>
  );
};

export default ChatPage;

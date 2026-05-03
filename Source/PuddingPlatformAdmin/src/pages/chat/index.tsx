import {
  CopyOutlined,
  DeleteOutlined,
  DownloadOutlined,
  QuestionCircleOutlined,
  ReloadOutlined,
  SendOutlined,
  SettingOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { history } from '@umijs/max';
import dayjs from 'dayjs';
import { Alert, App, Button, Card, Divider, Form, Input, Modal, Popover, Progress, Select, Skeleton, Space, Tooltip, Typography, theme } from 'antd';
import { createStyles } from 'antd-style';
import 'katex/dist/katex.min.css';
import Prism from 'prismjs';
import 'prismjs/components/prism-javascript';
import 'prismjs/components/prism-typescript';
import 'prismjs/components/prism-tsx';
import 'prismjs/components/prism-bash';
import 'prismjs/components/prism-csharp';
import 'prismjs/components/prism-json';
import 'prismjs/components/prism-python';
import 'prismjs/themes/prism-tomorrow.css';
import React, { useEffect, useRef, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeKatex from 'rehype-katex';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import {
  createWorkspace,
  listTeams,
  listWorkspaceAgents,
  listWorkspaces,
  sendAdminChatMessageStream,
  type CreateWorkspaceRequest,
  type TokenUsageDto,
  type WorkspaceAgentDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';

// ── 类型 ─────────────────────────────────────────────
interface ChatMessage {
  id: string;
  role: 'user' | 'agent';
  text: string;
  timestamp: number;
  status: 'sending' | 'success' | 'error';
  usage?: TokenUsageDto;
  isStreaming?: boolean;
}

interface CreateSceneFormValues {
  name: string;
}

const { Text, Title } = Typography;

const QUICK_PROMPTS = [
  '整理一段凌乱的想法',
  '分析一份错误日志',
  '为当前任务生成执行步骤',
  '总结一段长文本',
];

const COMMANDS = [
  { key: '/clear', title: '/clear', description: '清空当前对话' },
  { key: '/help', title: '/help', description: '显示快捷指令帮助' },
  { key: '/export', title: '/export', description: '导出当前对话为 Markdown' },
];

const DEFAULT_CONTEXT_WINDOW = 4096;

// ── 样式 ─────────────────────────────────────────────
const useStyles = createStyles(({ token }) => ({
  container: {
    display: 'flex',
    flexDirection: 'column',
    height: '100vh',
    minHeight: 0,
    padding: '20px 24px 24px',
    overflow: 'hidden',
    background: `radial-gradient(circle at 12% 10%, ${token.colorPrimaryBg} 0, transparent 28%), radial-gradient(circle at 88% 0%, ${token.colorFillAlter} 0, transparent 30%), linear-gradient(135deg, ${token.colorBgLayout} 0%, ${token.colorBgContainer} 58%, ${token.colorFillQuaternary} 100%)`,
  },
  topBar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 16,
    width: '100%',
    maxWidth: 1180,
    margin: '0 auto',
    padding: '4px 2px 12px',
  },
  brandArea: {
    display: 'flex',
    alignItems: 'center',
    gap: 12,
    minWidth: 0,
  },
  brandLogo: {
    width: 42,
    height: 42,
    objectFit: 'contain' as const,
    animation: 'puddingLogoPulse 2400ms ease-in-out infinite',
  },
  brandTitle: {
    margin: 0,
    lineHeight: 1.15,
    letterSpacing: 0.2,
  },
  brandSubtitle: {
    color: token.colorTextSecondary,
    fontSize: 13,
  },
  shellActions: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: 8,
    flexWrap: 'wrap' as const,
  },
  contextSummary: {
    maxWidth: 360,
    padding: '6px 12px',
    borderRadius: 999,
    color: token.colorTextSecondary,
    background: token.colorFillQuaternary,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    border: `1px solid ${token.colorBorderSecondary}`,
  },
  contextBar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 12,
    width: '100%',
    maxWidth: 980,
    margin: '0 auto 12px',
    padding: '10px 12px',
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: token.borderRadiusXL,
    background: token.colorBgElevated,
    boxShadow: '0 10px 32px rgba(24, 18, 12, 0.04)',
  },
  selectors: {
    display: 'flex',
    alignItems: 'center',
    gap: 12,
    flexWrap: 'wrap' as const,
  },
  selector: {
    width: 190,
  },
  selectorField: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 4,
  },
  selectorLabel: {
    color: token.colorTextTertiary,
    fontSize: 12,
    lineHeight: 1,
  },
  chatBody: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    width: '100%',
    maxWidth: 980,
    margin: '0 auto',
    minHeight: 0,
    padding: '0 20px',
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: 28,
    background: token.colorBgContainer,
    boxShadow: '0 24px 80px rgba(24, 18, 12, 0.08)',
    overflow: 'hidden',
  },
  messageList: {
    flex: 1,
    overflowY: 'auto' as const,
    padding: '22px 0 8px',
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 12,
  },
  messageRow: {
    display: 'flex',
    animation: 'slideUp 300ms ease-out',
  },
  messageContent: {
    maxWidth: '76%',
    display: 'flex',
    flexDirection: 'column',
    gap: 4,
    position: 'relative' as const,
    '&:hover .message-actions': {
      opacity: 1,
      transform: 'translateY(0)',
      pointerEvents: 'auto' as const,
    },
  },
  userContent: {
    alignItems: 'flex-end',
  },
  agentContent: {
    alignItems: 'flex-start',
  },
  userRow: {
    justifyContent: 'flex-end',
  },
  agentRow: {
    justifyContent: 'flex-start',
  },
  bubble: {
    maxWidth: '100%',
    padding: '10px 16px',
    borderRadius: token.borderRadiusLG,
    lineHeight: 1.6,
    wordBreak: 'break-word' as const,
    whiteSpace: 'pre-wrap' as const,
    border: '1px solid transparent',
  },
  userBubble: {
    background: token.colorPrimary,
    color: token.colorTextLightSolid,
    borderBottomRightRadius: 4,
  },
  agentBubble: {
    background: token.colorFillQuaternary,
    color: token.colorText,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderBottomLeftRadius: 4,
  },
  errorBubble: {
    borderColor: token.colorError,
  },
  messageActions: {
    opacity: 0,
    transform: 'translateY(3px)',
    pointerEvents: 'none' as const,
    transition: 'opacity 0.16s ease, transform 0.16s ease',
  },
  messageMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    minHeight: 20,
  },
  timeText: {
    color: token.colorTextQuaternary,
    fontSize: 12,
    cursor: 'default',
  },
  sendingText: {
    color: token.colorTextTertiary,
    fontSize: 12,
  },
  retryButton: {
    paddingInline: 4,
    fontSize: 12,
    height: 22,
    lineHeight: '22px',
  },
  timeDivider: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    margin: '2px 0',
    color: token.colorTextQuaternary,
    fontSize: 12,
    '&::before, &::after': {
      content: '""',
      flex: 1,
      height: 1,
      background: token.colorBorderSecondary,
    },
  },
  timeDividerText: {
    whiteSpace: 'nowrap' as const,
  },
  inputArea: {
    display: 'flex',
    gap: 8,
    alignItems: 'flex-end',
  },
  inputPanel: {
    padding: '12px 0 18px',
    borderTop: `1px solid ${token.colorBorderSecondary}`,
    background: token.colorBgContainer,
  },
  tokenIndicator: {
    display: 'flex',
    alignItems: 'center',
    gap: 12,
    marginBottom: 8,
    padding: '0 2px',
  },
  tokenProgress: {
    flex: 1,
  },
  loadingRow: {
    padding: '4px 0',
    display: 'flex',
    flexDirection: 'column',
    gap: 10,
  },
  skeletonRow: {
    display: 'flex',
  },
  skeletonLeft: {
    justifyContent: 'flex-start',
  },
  skeletonRight: {
    justifyContent: 'flex-end',
  },
  skeletonShort: {
    width: '30%',
  },
  skeletonLong: {
    width: '70%',
  },
  skeletonMedium: {
    width: '50%',
  },
  skeletonBubble: {
    width: '100%',
    '& .ant-skeleton-button': {
      width: '100%',
      height: 38,
      borderRadius: token.borderRadiusLG,
    },
  },
  emptyState: {
    flex: 1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: token.colorTextQuaternary,
    fontSize: 16,
  },
  onboardingState: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 14,
    padding: '24px 0',
  },
  onboardingLogo: {
    width: 84,
    height: 84,
    objectFit: 'contain' as const,
    animation: 'puddingLogoPulse 2400ms ease-in-out infinite',
  },
  onboardingIllustration: {
    width: 320,
    maxWidth: '86%',
    height: 140,
    borderRadius: token.borderRadiusXL,
    border: `1px solid ${token.colorPrimaryBorder}`,
    background: `linear-gradient(135deg, ${token.colorPrimaryBgHover} 0%, ${token.colorBgContainer} 100%)`,
    boxShadow: token.boxShadowSecondary,
    position: 'relative' as const,
    overflow: 'hidden' as const,
    animation: 'fadeIn 200ms ease-out',
    '&::before': {
      content: '""',
      position: 'absolute' as const,
      width: 180,
      height: 180,
      borderRadius: '50%',
      background: token.colorPrimaryBg,
      top: -90,
      left: -30,
      opacity: 0.9,
    },
    '&::after': {
      content: '""',
      position: 'absolute' as const,
      width: 120,
      height: 120,
      borderRadius: '50%',
      background: token.colorPrimary,
      right: -20,
      bottom: -40,
      opacity: 0.18,
    },
  },
  onboardingTitle: {
    margin: 0,
    letterSpacing: 0.2,
  },
  onboardingSubtitle: {
    color: token.colorTextSecondary,
    fontSize: 14,
    textAlign: 'center' as const,
  },
  promptList: {
    marginTop: 8,
    display: 'flex',
    justifyContent: 'center',
    flexWrap: 'wrap' as const,
    gap: 12,
    maxWidth: 820,
  },
  promptCard: {
    width: 210,
    borderRadius: token.borderRadiusXL,
    cursor: 'pointer',
    transition: 'transform 0.2s ease, box-shadow 0.2s ease, border-color 0.2s ease',
    '& .ant-card-body': {
      padding: '12px 14px',
    },
    '&:hover': {
      transform: 'translateY(-2px)',
      boxShadow: token.boxShadowSecondary,
      borderColor: token.colorPrimaryBorderHover,
    },
  },
  selectorDivider: {
    margin: '8px 0',
  },
  errorAlert: {
    margin: '8px 0',
  },
  input: {
    flex: 1,
  },
  createSceneForm: {
    marginTop: 16,
  },
  markdownBody: {
    whiteSpace: 'normal' as const,
    '& p': { margin: '0 0 8px' },
    '& p:last-child': { marginBottom: 0 },
    '& ul, & ol': { paddingLeft: 22, margin: '6px 0' },
    '& blockquote': {
      margin: '8px 0',
      paddingLeft: 12,
      borderLeft: `3px solid ${token.colorBorder}`,
      color: token.colorTextSecondary,
    },
    '& table': {
      borderCollapse: 'collapse' as const,
      minWidth: 480,
    },
    '& th, & td': {
      border: `1px solid ${token.colorBorderSecondary}`,
      padding: '6px 10px',
      textAlign: 'left' as const,
    },
    '& th': {
      background: token.colorFillQuaternary,
    },
    '& .katex-display': {
      overflowX: 'auto' as const,
      overflowY: 'hidden' as const,
      padding: '4px 0',
    },
  },
  markdownTableScroll: {
    maxWidth: '100%',
    overflowX: 'auto' as const,
    margin: '8px 0',
  },
  inlineCode: {
    padding: '1px 5px',
    borderRadius: 4,
    background: token.colorFillSecondary,
    fontSize: '0.92em',
  },
  codeBlockWrap: {
    position: 'relative' as const,
    margin: '10px 0',
    borderRadius: 10,
    overflow: 'hidden',
    background: '#1e1e1e',
    '& pre': {
      margin: 0,
      padding: '14px 16px',
      overflowX: 'auto' as const,
    },
  },
  codeCopyButton: {
    position: 'absolute' as const,
    top: 8,
    right: 8,
    zIndex: 1,
  },
  streamingCursor: {
    display: 'inline-block',
    width: 8,
    marginLeft: 2,
    color: token.colorPrimary,
    animation: 'cursorBlink 1s steps(1) infinite',
  },
  '@keyframes cursorBlink': {
    '0%, 100%': { opacity: 1 },
    '50%': { opacity: 0 },
  },
  commandList: {
    width: 280,
    display: 'flex',
    flexDirection: 'column',
    gap: 4,
  },
  commandItem: {
    justifyContent: 'flex-start',
    height: 'auto',
    padding: '8px 10px',
  },
}));

const CodeBlock: React.FC<{
  code: string;
  className?: string;
  wrapClassName: string;
  buttonClassName: string;
}> = ({ code, className, wrapClassName, buttonClassName }) => {
  const codeRef = useRef<HTMLElement>(null);

  useEffect(() => {
    if (codeRef.current) Prism.highlightElement(codeRef.current);
  }, [code, className]);

  return (
    <div className={wrapClassName}>
      <Button size="small" className={buttonClassName} icon={<CopyOutlined />} onClick={() => navigator.clipboard.writeText(code)}>
        复制
      </Button>
      <pre>
        <code ref={codeRef} className={className}>{code}</code>
      </pre>
    </div>
  );
};

// ── 组件 ─────────────────────────────────────────────
const ChatPage: React.FC = () => {
  const { styles, cx } = useStyles();
  const { message: messageApi } = App.useApp();
  const { token } = theme.useToken();

  const [workspaces, setWorkspaces] = useState<WorkspaceWithPermDto[]>([]);
  const [workspaceId, setWorkspaceId] = useState<string>();
  const [workspaceLoading, setWorkspaceLoading] = useState(false);
  const [agents, setAgents] = useState<WorkspaceAgentDto[]>([]);
  const [agentId, setAgentId] = useState<string>();
  const [agentLoading, setAgentLoading] = useState(false);
  const [createSceneOpen, setCreateSceneOpen] = useState(false);
  const [createSceneLoading, setCreateSceneLoading] = useState(false);
  const [createSceneForm] = Form.useForm<CreateSceneFormValues>();

  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [inputValue, setInputValue] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [latestUsage, setLatestUsage] = useState<TokenUsageDto | undefined>();
  const [commandOpen, setCommandOpen] = useState(false);
  const sessionIdRef = useRef<string | undefined>(undefined);
  const abortControllerRef = useRef<AbortController | null>(null);
  const listEndRef = useRef<HTMLDivElement>(null);

  const createMessageId = () => `msg-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;

  const formatRelativeTime = (timestamp: number) => {
    const now = dayjs();
    const target = dayjs(timestamp);
    const diffMinutes = now.diff(target, 'minute');

    if (diffMinutes < 1) return '刚刚';
    if (diffMinutes < 60) return `${diffMinutes}分钟前`;

    const diffHours = now.diff(target, 'hour');
    if (diffHours < 24) return `${diffHours}小时前`;

    return target.format('MM-DD HH:mm');
  };

  const resetConversation = () => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    setMessages([]);
    setError(null);
    setLatestUsage(undefined);
    setLoading(false);
    sessionIdRef.current = undefined;
  };

  const pickDefaultWorkspaceId = (items: WorkspaceWithPermDto[]) => (
    items.find((x) => x.workspaceId === 'default' && x.isEnabled && !x.isFrozen)?.workspaceId
    ?? items.find((x) => x.workspaceId === 'default')?.workspaceId
    ?? items.find((x) => x.isEnabled && !x.isFrozen)?.workspaceId
    ?? items[0]?.workspaceId
  );

  const pickDefaultAgentId = (items: WorkspaceAgentDto[]) => (
    items.find((x) => x.isEnabled && !x.isFrozen)?.agentId
    ?? items.find((x) => x.isEnabled)?.agentId
    ?? items[0]?.agentId
  );

  const goWorkspaceSettings = () => {
    history.push('/workspace');
  };

  const goAgentSettings = () => {
    if (workspaceId) {
      history.push(`/workspace/${workspaceId}?tab=workspace-agents`);
      return;
    }
    history.push('/workspace');
  };

  const buildWorkspaceId = (name: string) => {
    const slugBase = name
      .trim()
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 48) || 'scene';
    return `${slugBase}-${Date.now().toString().slice(-6)}`;
  };

  const openCreateSceneModal = () => {
    createSceneForm.resetFields();
    setCreateSceneOpen(true);
  };

  const handleCreateScene = async () => {
    try {
      const values = await createSceneForm.validateFields();
      setCreateSceneLoading(true);

      const teams = await listTeams();
      const defaultTeamId = teams[0]?.teamId;
      if (!defaultTeamId) {
        setError('创建失败：系统尚未初始化可用分组。');
        return;
      }

      const request: CreateWorkspaceRequest = {
        workspaceId: buildWorkspaceId(values.name),
        teamId: defaultTeamId,
        name: values.name,
        teamAccessPolicy: 'Write',
        companyAccessPolicy: 'None',
      };

      const created = await createWorkspace(request);
      const items = await listWorkspaces();
      setWorkspaces(items);
      setWorkspaceId(created.workspaceId);
      setAgents([]);
      setAgentId(undefined);
      resetConversation();
      setError(null);
      setCreateSceneOpen(false);
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      setError('创建场景失败，请稍后重试。');
    } finally {
      setCreateSceneLoading(false);
    }
  };

  // 初始化：加载场景
  useEffect(() => {
    let active = true;

    const loadWorkspaces = async () => {
      setWorkspaceLoading(true);
      try {
        const items = await listWorkspaces();
        if (!active) return;

        setWorkspaces(items);
        const nextWorkspaceId = pickDefaultWorkspaceId(items);
        setWorkspaceId(nextWorkspaceId);

        if (!nextWorkspaceId) {
          setError('没有可用场景，请先在设置中创建场景。');
        }
      } catch (err: any) {
        if (!active) return;
        setError(err?.message || '加载场景失败，请稍后重试。');
      } finally {
        if (active) {
          setWorkspaceLoading(false);
        }
      }
    };

    loadWorkspaces();
    return () => {
      active = false;
    };
  }, []);

  // 场景变更：加载 Agent 列表
  useEffect(() => {
    let active = true;

    const loadAgents = async () => {
      if (!workspaceId) {
        setAgents([]);
        setAgentId(undefined);
        return;
      }

      setAgentLoading(true);
      try {
        const items = await listWorkspaceAgents(workspaceId);
        if (!active) return;

        setAgents(items);
        const nextAgentId = pickDefaultAgentId(items);
        setAgentId(nextAgentId);

        if (!nextAgentId) {
          setError('当前场景没有可用 Agent，请先在设置中启用或创建 Agent。');
        }
      } catch (err: any) {
        if (!active) return;
        setError(err?.message || '加载 Agent 列表失败，请稍后重试。');
      } finally {
        if (active) {
          setAgentLoading(false);
        }
      }
    };

    loadAgents();
    return () => {
      active = false;
    };
  }, [workspaceId]);

  // 自动滚动到底部
  useEffect(() => {
    listEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages, loading]);

  const appendAgentDelta = (agentMessageId: string, delta: string) => {
    if (!delta) return;
    setMessages((prev) => prev.map((msg) => (
      msg.id === agentMessageId
        ? { ...msg, text: `${msg.text}${delta}`, status: 'sending', isStreaming: true }
        : msg
    )));
  };

  const updateAgentUsage = (agentMessageId: string, usage: TokenUsageDto) => {
    setLatestUsage(usage);
    setMessages((prev) => prev.map((msg) => (
      msg.id === agentMessageId ? { ...msg, usage } : msg
    )));
  };

  const sendMessage = async (text: string, retryMessageId?: string) => {
    if (!text || loading) return;
    if (!workspaceId) {
      setError('请先选择场景。');
      return;
    }

    if (!agentId) {
      setError('请先选择 Agent。');
      return;
    }

    setError(null);
    const now = Date.now();
    let userMessageId = retryMessageId;

    if (retryMessageId) {
      setMessages((prev) => prev.map((msg) => (
        msg.id === retryMessageId
          ? { ...msg, status: 'sending', timestamp: now }
          : msg
      )));
    } else {
      userMessageId = createMessageId();
      const userMessage: ChatMessage = {
        id: userMessageId,
        role: 'user',
        text,
        timestamp: now,
        status: 'sending',
      };
      setMessages((prev) => [...prev, userMessage]);
    }

    const agentMessageId = createMessageId();
    const agentMessage: ChatMessage = {
      id: agentMessageId,
      role: 'agent',
      text: '',
      timestamp: Date.now(),
      status: 'sending',
      isStreaming: true,
    };
    setMessages((prev) => [...prev, agentMessage]);

    const controller = new AbortController();
    abortControllerRef.current = controller;
    setLoading(true);

    let streamError: string | undefined;
    let cancelled = false;

    try {
      await sendAdminChatMessageStream(
        workspaceId,
        {
          messageText: text,
          sessionId: sessionIdRef.current,
          agentId,
        },
        (event) => {
          if (event.type === 'metadata') {
            sessionIdRef.current = event.sessionId;
            if (userMessageId) {
              setMessages((prev) => prev.map((msg) => (
                msg.id === userMessageId ? { ...msg, status: 'success' } : msg
              )));
            }
          } else if (event.type === 'delta') {
            appendAgentDelta(agentMessageId, event.delta);
          } else if (event.type === 'usage') {
            updateAgentUsage(agentMessageId, event.usage);
          } else if (event.type === 'done') {
            if (event.usage) updateAgentUsage(agentMessageId, event.usage);
            setMessages((prev) => prev.map((msg) => (
              msg.id === agentMessageId
                ? {
                    ...msg,
                    text: msg.text || event.reply || '（Agent 未返回可展示文本）',
                    status: 'success',
                    isStreaming: false,
                    usage: event.usage ?? msg.usage,
                  }
                : msg
            )));
          } else if (event.type === 'cancelled') {
            cancelled = true;
          } else if (event.type === 'error') {
            streamError = event.message;
          }
        },
        controller.signal,
      );

      if (streamError) throw new Error(streamError);

      if (cancelled) {
        setMessages((prev) => prev.map((msg) => (
          msg.id === agentMessageId ? { ...msg, status: 'success', isStreaming: false } : msg
        )));
      }
    } catch (err: any) {
      if (err?.name === 'AbortError') {
        setMessages((prev) => prev.map((msg) => (
          msg.id === agentMessageId
            ? { ...msg, status: 'success', isStreaming: false, text: msg.text || '（已停止生成）' }
            : msg
        )));
        messageApi.info('已停止生成');
      } else {
        if (userMessageId) {
          setMessages((prev) => prev.map((msg) => (
            msg.id === userMessageId ? { ...msg, status: 'error' } : msg
          )));
        }
        setMessages((prev) => prev.map((msg) => (
          msg.id === agentMessageId ? { ...msg, status: 'error', isStreaming: false } : msg
        )));
        setError(err?.message || '网络错误，请检查后端服务是否启动');
      }
    } finally {
      if (abortControllerRef.current === controller) {
        abortControllerRef.current = null;
      }
      setLoading(false);
    }
  };

  // 发送消息
  const handleSend = () => {
    const text = inputValue.trim();
    if (!text) return;

    if (text.startsWith('/')) {
      handleCommand(text.split(/\s+/)[0]);
      return;
    }

    setInputValue('');
    setCommandOpen(false);
    void sendMessage(text);
  };

  const handleStop = () => {
    abortControllerRef.current?.abort();
  };

  const handleRetry = (message: ChatMessage) => {
    if (message.status !== 'error') return;
    const messageIndex = messages.findIndex((msg) => msg.id === message.id);
    const nextMessage = messageIndex >= 0 ? messages[messageIndex + 1] : undefined;

    if (nextMessage?.role === 'agent' && nextMessage.status === 'error') {
      setMessages((prev) => prev.filter((msg) => msg.id !== nextMessage.id));
    }

    void sendMessage(message.text, message.id);
  };

  const handleRegenerate = (agentMessage: ChatMessage) => {
    const index = messages.findIndex((msg) => msg.id === agentMessage.id);
    const previousUser = [...messages.slice(0, index)].reverse().find((msg) => msg.role === 'user');
    if (!previousUser) return;

    setMessages((prev) => prev.filter((msg) => msg.id !== agentMessage.id));
    void sendMessage(previousUser.text, previousUser.id);
  };

  const handleCopy = async (text: string) => {
    await navigator.clipboard.writeText(text);
    messageApi.success('已复制');
  };

  const handleDelete = (messageId: string) => {
    setMessages((prev) => prev.filter((msg) => msg.id !== messageId));
  };

  const exportConversation = () => {
    if (messages.length === 0) {
      messageApi.info('当前没有可导出的对话');
      return;
    }

    const content = messages.map((msg) => (
      `## ${msg.role === 'user' ? 'User' : 'Agent'} · ${dayjs(msg.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${msg.text}`
    )).join('\n\n---\n\n');
    const blob = new Blob([content], { type: 'text/markdown;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `pudding-chat-${dayjs().format('YYYYMMDD-HHmmss')}.md`;
    link.click();
    URL.revokeObjectURL(url);
  };

  const appendHelpMessage = () => {
    const helpText = [
      '### 快捷指令',
      '',
      '| 指令 | 作用 |',
      '|---|---|',
      '| `/clear` | 清空当前对话 |',
      '| `/help` | 显示快捷指令帮助 |',
      '| `/export` | 将当前对话导出为 Markdown |',
    ].join('\n');

    setMessages((prev) => [...prev, {
      id: createMessageId(),
      role: 'agent',
      text: helpText,
      timestamp: Date.now(),
      status: 'success',
    }]);
  };

  const handleCommand = (command: string) => {
    const normalized = command.trim().toLowerCase();
    setCommandOpen(false);

    if (normalized === '/clear') {
      resetConversation();
      setInputValue('');
      messageApi.success('已清空对话');
    } else if (normalized === '/help') {
      appendHelpMessage();
      setInputValue('');
    } else if (normalized === '/export') {
      exportConversation();
      setInputValue('');
    }
  };

  const handleInputChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const next = e.target.value;
    setInputValue(next);
    setCommandOpen(next.startsWith('/'));
  };

  // Enter 发送，Shift+Enter 换行
  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      if (loading) handleStop();
      else handleSend();
    }
  };

  const handleWorkspaceChange = (nextWorkspaceId: string) => {
    if (nextWorkspaceId === workspaceId) return;
    setWorkspaceId(nextWorkspaceId);
    setAgentId(undefined);
    setAgents([]);
    resetConversation();
  };

  const handleAgentChange = (nextAgentId: string) => {
    if (nextAgentId === agentId) return;
    setAgentId(nextAgentId);
    resetConversation();
  };

  const workspaceOptions = workspaces.map((item) => ({
    value: item.workspaceId,
    label: item.name || item.workspaceId,
    disabled: !item.isEnabled || item.isFrozen,
  }));

  const agentOptions = agents.map((item) => ({
    value: item.agentId,
    label: item.name || item.agentId,
    disabled: !item.isEnabled || item.isFrozen,
  }));

  const currentWorkspaceName = workspaces.find((item) => item.workspaceId === workspaceId)?.name ?? workspaceId ?? '未选择场景';
  const currentAgentName = agents.find((item) => item.agentId === agentId)?.name ?? agentId ?? '未选择 Agent';

  const tokenLimit = latestUsage?.contextWindowTokens ?? DEFAULT_CONTEXT_WINDOW;
  const tokenUsed = latestUsage?.totalTokens ?? 0;
  const tokenPercent = Math.min(100, Math.round((tokenUsed / tokenLimit) * 100));
  const tokenStatus = tokenPercent >= 95 ? 'exception' : tokenPercent >= 80 ? 'normal' : 'active';
  const tokenColor = tokenPercent >= 95 ? token.colorError : tokenPercent >= 80 ? token.colorWarning : undefined;

  const commandContent = (
    <div className={styles.commandList}>
      {COMMANDS.map((cmd) => (
        <Button
          key={cmd.key}
          type="text"
          className={styles.commandItem}
          onClick={() => handleCommand(cmd.key)}
        >
          <Space direction="vertical" size={0} align="start">
            <Text code>{cmd.title}</Text>
            <Text type="secondary">{cmd.description}</Text>
          </Space>
        </Button>
      ))}
    </div>
  );

  const renderMarkdown = (msg: ChatMessage) => (
    <div className={styles.markdownBody}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath]}
        rehypePlugins={[rehypeKatex]}
        components={{
          table: ({ children, ...props }: any) => (
            <div className={styles.markdownTableScroll}>
              <table {...props}>{children}</table>
            </div>
          ),
          code: ({ inline, className, children, ...props }: any) => {
            const code = String(children ?? '').replace(/\n$/, '');
            if (inline) {
              return <code className={styles.inlineCode} {...props}>{children}</code>;
            }
            return (
              <CodeBlock
                code={code}
                className={className}
                wrapClassName={styles.codeBlockWrap}
                buttonClassName={styles.codeCopyButton}
              />
            );
          },
        }}
      >
        {msg.text || (msg.isStreaming ? ' ' : '')}
      </ReactMarkdown>
      {msg.isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );

  return (
    <div className={styles.container}>
      <div className={styles.topBar}>
        <div className={styles.brandArea}>
          <img src="/admin/assets/images/logo.png" alt="Pudding" className={styles.brandLogo} />
          <div>
            <Title level={4} className={styles.brandTitle}>Pudding</Title>
            <Text className={styles.brandSubtitle}>安静地理解，可靠地执行</Text>
          </div>
        </div>

        <div className={styles.shellActions}>
          <Text className={styles.contextSummary}>{currentWorkspaceName} · {currentAgentName}</Text>
          <Button type="text" onClick={resetConversation} disabled={loading && messages.length === 0}>
            新对话
          </Button>
          <Button icon={<SettingOutlined />} onClick={goWorkspaceSettings}>
            控制台
          </Button>
        </div>
      </div>

      <div className={styles.contextBar}>
        <div className={styles.selectors}>
          <div className={styles.selectorField}>
            <Text className={styles.selectorLabel}>场景</Text>
            <Select
              className={styles.selector}
              value={workspaceId}
              loading={workspaceLoading}
              options={workspaceOptions}
              onChange={handleWorkspaceChange}
              placeholder="请选择场景"
              popupMatchSelectWidth={false}
              dropdownRender={(menu) => (
                <>
                  {menu}
                  <Divider className={styles.selectorDivider} />
                  <Button type="link" block onClick={openCreateSceneModal}>
                    + 新建场景
                  </Button>
                </>
              )}
            />
          </div>

          <div className={styles.selectorField}>
            <Text className={styles.selectorLabel}>Agent</Text>
            <Select
              className={styles.selector}
              value={agentId}
              loading={agentLoading}
              options={agentOptions}
              onChange={handleAgentChange}
              placeholder="选择助手 Agent"
              notFoundContent="暂无可用 Agent"
              popupMatchSelectWidth={false}
              dropdownRender={(menu) => (
                <>
                  {menu}
                  <Divider className={styles.selectorDivider} />
                  <Button type="link" block onClick={goAgentSettings}>
                    管理 Agent...
                  </Button>
                </>
              )}
            />
          </div>
        </div>

        <Text type="secondary">当前对话上下文</Text>
      </div>

      <div className={styles.chatBody}>
        <div className={styles.messageList}>
          {!agentId && !error && (
            <div className={styles.onboardingState}>
              <img src="/admin/assets/images/logo.png" alt="Pudding Logo" className={styles.onboardingLogo} />
              <div className={styles.onboardingIllustration} aria-hidden="true" />
              <Title level={2} className={styles.onboardingTitle}>你好，我是布丁</Title>
              <Text className={styles.onboardingSubtitle}>我在这里。选择一个场景和 Agent，然后把任务交给我。</Text>

              <div className={styles.promptList}>
                {QUICK_PROMPTS.map((prompt) => (
                  <Card
                    key={prompt}
                    hoverable
                    className={styles.promptCard}
                    onClick={() => setInputValue(prompt)}
                  >
                    {prompt}
                  </Card>
                ))}
              </div>
            </div>
          )}

          {agentId && messages.length === 0 && !error && (
            <div className={styles.emptyState}>
              <Text type="secondary">开始和 Agent 对话吧</Text>
            </div>
          )}

          {messages.map((msg, idx) => (
            <React.Fragment key={msg.id}>
              {idx > 0 && msg.timestamp - messages[idx - 1].timestamp > 5 * 60 * 1000 && (
                <div className={styles.timeDivider}>
                  <span className={styles.timeDividerText}>—— {dayjs(msg.timestamp).format('HH:mm')} ——</span>
                </div>
              )}

              <div
                className={cx(
                  styles.messageRow,
                  msg.role === 'user' ? styles.userRow : styles.agentRow,
                )}
              >
                <div
                  className={cx(
                    styles.messageContent,
                    msg.role === 'user' ? styles.userContent : styles.agentContent,
                  )}
                >
                  <div
                    className={cx(
                      styles.bubble,
                      msg.role === 'user' ? styles.userBubble : styles.agentBubble,
                      msg.status === 'error' && styles.errorBubble,
                    )}
                  >
                    {msg.role === 'agent' ? renderMarkdown(msg) : msg.text}
                  </div>

                  <Space size={2} className={`${styles.messageActions} message-actions`}>
                    <Tooltip title="复制">
                      <Button size="small" type="text" icon={<CopyOutlined />} onClick={() => handleCopy(msg.text)} />
                    </Tooltip>
                    {msg.role === 'agent' && (
                      <Tooltip title="重新生成">
                        <Button size="small" type="text" icon={<ReloadOutlined />} onClick={() => handleRegenerate(msg)} disabled={loading} />
                      </Tooltip>
                    )}
                    <Tooltip title="删除">
                      <Button size="small" type="text" danger icon={<DeleteOutlined />} onClick={() => handleDelete(msg.id)} />
                    </Tooltip>
                  </Space>

                  <div className={styles.messageMeta}>
                    <Tooltip title={dayjs(msg.timestamp).format('YYYY-MM-DD HH:mm:ss')}>
                      <Text className={styles.timeText}>{formatRelativeTime(msg.timestamp)}</Text>
                    </Tooltip>
                    {msg.status === 'sending' && msg.role === 'user' && (
                      <Text className={styles.sendingText}>发送中...</Text>
                    )}
                    {msg.isStreaming && (
                      <Text className={styles.sendingText}>生成中...</Text>
                    )}
                    {msg.usage?.totalTokens && (
                      <Text className={styles.sendingText}>{msg.usage.totalTokens.toLocaleString()} tokens</Text>
                    )}
                  </div>

                  {msg.status === 'error' && msg.role === 'user' && (
                    <Button
                      type="link"
                      size="small"
                      className={styles.retryButton}
                      onClick={() => handleRetry(msg)}
                    >
                      重新发送
                    </Button>
                  )}
                </div>
              </div>
            </React.Fragment>
          ))}

          {/* Loading 骨架：仅在还没有流式气泡时展示 */}
          {loading && !messages.some((msg) => msg.isStreaming) && (
            <div className={styles.loadingRow}>
              <div className={cx(styles.skeletonRow, styles.skeletonRight)}>
                <div className={styles.skeletonShort}>
                  <Skeleton.Button active block className={styles.skeletonBubble} />
                </div>
              </div>
              <div className={cx(styles.skeletonRow, styles.skeletonLeft)}>
                <div className={styles.skeletonLong}>
                  <Skeleton.Button active block className={styles.skeletonBubble} />
                </div>
              </div>
              <div className={cx(styles.skeletonRow, styles.skeletonLeft)}>
                <div className={styles.skeletonMedium}>
                  <Skeleton.Button active block className={styles.skeletonBubble} />
                </div>
              </div>
            </div>
          )}

          {/* 错误提示 */}
          {error && (
            <Alert
              type="error"
              message={error}
              closable
              onClose={() => setError(null)}
              className={styles.errorAlert}
            />
          )}

          <div ref={listEndRef} />
        </div>

        <div className={styles.inputPanel}>
          <div className={styles.tokenIndicator}>
            <Text type="secondary">Token</Text>
            <Progress
              className={styles.tokenProgress}
              percent={tokenPercent}
              size="small"
              status={tokenStatus as any}
              strokeColor={tokenColor}
            />
            <Text type={tokenPercent >= 80 ? 'warning' : 'secondary'}>
              {tokenUsed.toLocaleString()} / {tokenLimit.toLocaleString()}
            </Text>
          </div>

          <div className={styles.inputArea}>
            <Popover
              open={commandOpen && inputValue.startsWith('/')}
              content={commandContent}
              placement="topLeft"
              trigger="click"
              onOpenChange={setCommandOpen}
            >
              <Input.TextArea
                value={inputValue}
                onChange={handleInputChange}
                onKeyDown={handleKeyDown}
                placeholder="交给我吧。Enter 发送，Shift+Enter 换行"
                disabled={!workspaceId || !agentId}
                autoSize={{ minRows: 1, maxRows: 5 }}
                className={styles.input}
              />
            </Popover>
            <Button
              type={loading ? 'default' : 'primary'}
              danger={loading}
              icon={loading ? <StopOutlined /> : <SendOutlined />}
              onClick={loading ? handleStop : handleSend}
              disabled={loading ? false : (!inputValue.trim() || !workspaceId || !agentId)}
            >
              {loading ? '停止' : '发送'}
            </Button>
            <Tooltip title="帮助">
              <Button icon={<QuestionCircleOutlined />} onClick={() => handleCommand('/help')} />
            </Tooltip>
            <Tooltip title="导出对话">
              <Button icon={<DownloadOutlined />} onClick={exportConversation} />
            </Tooltip>
          </div>
        </div>
      </div>

      <Modal
        title="新建场景"
        open={createSceneOpen}
        onOk={handleCreateScene}
        onCancel={() => setCreateSceneOpen(false)}
        confirmLoading={createSceneLoading}
        okText="创建"
        cancelText="取消"
        destroyOnClose
      >
        <Form form={createSceneForm} layout="vertical" className={styles.createSceneForm}>
          <Form.Item
            name="name"
            label="名称"
            rules={[
              { required: true, message: '请输入场景名称' },
              { max: 128, message: '最多 128 个字符' },
            ]}
          >
            <Input placeholder="例如：研发协作场景" />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  );
};

export default ChatPage;

// ── ChatMain：右侧主聊天区（Header + MessageList + InputArea）─
import {
  AppstoreOutlined,
  BugOutlined,
  FieldTimeOutlined,
  HistoryOutlined,
  MenuUnfoldOutlined,
  SoundOutlined,
} from '@ant-design/icons';
import { Alert, Button, Divider, Select, Tooltip } from 'antd';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { WorkspaceNavigationHeader } from '@/components';
import type {
  WorkspaceAgentDto,
  WorkspaceWithPermDto,
} from '@/services/platform/api';
import {
  rememberWorkspaceVisit,
} from '@/utils/workspaceNavigation';
import type { RecentlyDeniedItem } from '../classifier/autoReviewClassifier';
import type { AgentConversationView } from '../client/types';
import { useAutoReviewClassifier } from '../hooks/useAutoReviewClassifier';
import { useAutoTts } from '../hooks/useAutoTts';
import type {
  ChatInteractionQueueItem,
  ChatInteractionRuntimeEvent,
} from '../hooks/useChatState';
import { useGoal } from '../hooks/useGoal';
import { useInitialIdleReady } from '../hooks/useInitialIdleReady';
import { useNotificationSound } from '../hooks/useNotificationSound';
import { useProviderBalance } from '../hooks/useProviderBalance';
import type { ExecutionFlowProjection } from '../projections/executionFlowProjector';
import type {
  SandboxBoundaryInfo,
  SandboxNetworkMode,
} from '../sandbox/sandboxBoundary';
import { createDefaultSandboxBoundary } from '../sandbox/sandboxBoundary';
import { useChatStyles } from '../styles';
import type {
  ChatTurn,
  ParentDelegationActivity,
  SubAgentCardMap,
} from '../types';
import type { PermissionMode } from '../types/chatStateTypes';
import { currencySymbolFor, resolveBillingAdapter } from '../utils/providerBilling';
import GoalBanner from './GoalBanner';
import IntentConsole, { type ChatStatus } from './IntentConsole';
import MessageList from './MessageList';
import ProviderBalanceIndicator from './ProviderBalanceIndicator';
import type { TranscriptMode } from './TranscriptModeSwitch';

const loadCheckpointTimelinePanel = () =>
  import('./CheckpointTimelinePanel');
const loadDevPanel = () => import('./DevPanel');
const loadHistorySearchModal = () => import('./HistorySearchModal');
const loadSubAgentActivityDock = () => import('./SubAgentActivityDock');
const loadTaskBoardModal = async () => {
  const module = await import('@/pages/workspace-tasks');
  return { default: module.TaskBoardModal };
};

const CheckpointTimelinePanel =
  process.env.NODE_ENV === 'test'
    ? (require('./CheckpointTimelinePanel')
        .default as typeof import('./CheckpointTimelinePanel').default)
    : React.lazy(loadCheckpointTimelinePanel);

const DevPanel =
  process.env.NODE_ENV === 'test'
    ? (require('./DevPanel').default as typeof import('./DevPanel').default)
    : React.lazy(loadDevPanel);
const HistorySearchModal =
  process.env.NODE_ENV === 'test'
    ? (require('./HistorySearchModal')
        .default as typeof import('./HistorySearchModal').default)
    : React.lazy(loadHistorySearchModal);
const SubAgentActivityDock =
  process.env.NODE_ENV === 'test'
    ? (require('./SubAgentActivityDock')
        .default as typeof import('./SubAgentActivityDock').default)
    : React.lazy(loadSubAgentActivityDock);
const TaskBoardModal =
  process.env.NODE_ENV === 'test'
    ? (require('@/pages/workspace-tasks')
        .TaskBoardModal as typeof import('@/pages/workspace-tasks').TaskBoardModal)
    : React.lazy(loadTaskBoardModal);

import { useDevRuntimeEvents } from './useDevRuntimeEvents';

interface ChatMainProps {
  sidebarOpen: boolean;
  reconnectCountRef?: React.MutableRefObject<number>;
  onToggleSidebar: () => void;
  // workspace
  workspaces: WorkspaceWithPermDto[];
  workspaceId: string | undefined;
  workspaceLoading: boolean;
  wsOpts: { value: string; label: string; disabled: boolean }[];
  onWorkspaceChange: (v: string | undefined) => void;
  // agent
  agents: WorkspaceAgentDto[];
  agentId: string | undefined;
  agentLoading: boolean;
  agOpts: { value: string; label: React.ReactNode; disabled: boolean }[];
  selectedAgent: WorkspaceAgentDto | undefined;
  onAgentChange: (v: string | undefined) => void;
  // new workspace
  onCreateWorkspace: () => void;
  // session
  selectedSessionId: string | null;
  // chat
  turns: ChatTurn[];
  conversationView?: AgentConversationView | null;
  chatInteractionRuntimeEvents?: ChatInteractionRuntimeEvent[];
  subAgentCards: SubAgentCardMap;
  historyLoading: boolean;
  loadingMore: boolean;
  hasMoreMessages: boolean;
  error: string | null;
  onClearError: () => void;
  onLoadMore: () => void;
  // input
  inputValue: string;
  onInputChange: (v: string) => void;
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  loading: boolean;
  interactionQueue?: ChatInteractionQueueItem[];
  onUpdateQueuedInteraction?: (id: string, text: string) => void;
  onDeleteQueuedInteraction?: (id: string) => void;
  onSendQueuedInteractionNow?: (id: string) => Promise<void>;
  onSteerQueuedInteraction?: (id: string) => Promise<void>;
  /** P1#6：本地待发队列拖拽重排 */
  onReorderQueuedInteraction?: (fromId: string, toId: string) => void;
  /** P1#6：取消全部（中止当前请求 + 清空待发队列） */
  onStopAll?: () => void;
  onSend: () => void;
  onSendWithMetadata?: (
    content: string,
    metadata: Record<string, string>,
  ) => Promise<void> | void;
  onStop: () => void;
  onExport: () => void;
  disabled: boolean;
  // token
  tLimit: number;
  tUsed: number;
  tPct: number;
  // cache
  cacheHitTokens?: number;
  cacheMissTokens?: number;
  cacheHitRate?: number;
  /** 来自 useCompaction hook 的压缩状态文案 */
  compactionStatus?: string | null;
  /** CU-11 Phase 2: per-turn 投影选择器（灰度开启时按 turnId 取 canonical 投影）。 */
  getTurnProjection?: (turnId: string) => ExecutionFlowProjection | undefined;
  onTurnVisible?: (turnId: string) => void;
  // message rendering
  formatTime: (ts: number) => string;
  onDeleteTurn: (turnId: string) => void;
  onContextMenu: (
    e: React.MouseEvent,
    turnId: string,
    role: 'user' | 'assistant',
    content: string,
  ) => void;
  onRerunTurn: (turnId: string) => void;
  onPinTurn: (turnId: string) => void;
  // refs
  messageListRef: React.RefObject<HTMLDivElement | null>;
  listEndRef: React.RefObject<HTMLDivElement | null>;
  /** 当前登录用户信息 */
  currentUser?: { name?: string; avatar?: string };
  viewportScrollIntent?: import('../viewport/types').ScrollIntent;
  onViewportScrollIntentHandled?: () => void;
  /** P1#4：权限模式（全局状态，经 ChatLayout 下传） */
  permissionMode?: PermissionMode;
  /** P1#4：权限模式变更回调 */
  onPermissionModeChange?: (mode: PermissionMode) => void;
  /** P2#7：Checkpoint 时间线 — 当前会话快照列表 */
  checkpoints?: import('../client/checkpointStore').ChatCheckpoint[];
  /** P2#7：Checkpoint 时间线面板开关 */
  checkpointTimelineOpen?: boolean;
  onToggleCheckpointTimeline?: () => void;
  /** P2#7：Restore / Fork / Delete / ClearAll 回调 */
  onRestoreCheckpoint?: (checkpointId: string) => void;
  onForkCheckpoint?: (checkpointId: string) => void;
  onDeleteCheckpoint?: (checkpointId: string) => void;
  onClearAllCheckpoints?: () => void;
  /** P2#7：当前还原中的快照 id（顶部提示条用） */
  restoredCheckpointId?: string | null;
  clearRestoredMarker?: () => void;
}

const DEV_MODE_KEY = 'pudding-dev-mode';

const ChatMain: React.FC<ChatMainProps> = ({
  reconnectCountRef,
  sidebarOpen,
  onToggleSidebar,
  workspaceId,
  workspaceLoading,
  wsOpts,
  onWorkspaceChange,
  agentId,
  selectedAgent,
  onCreateWorkspace,
  selectedSessionId,
  turns,
  conversationView,
  historyLoading,
  loadingMore,
  hasMoreMessages,
  error,
  onClearError,
  onLoadMore,
  inputValue,
  onInputChange,
  onKeyDown,
  loading,
  onSend,
  onSendWithMetadata,
  onStop,
  onExport,
  disabled,
  interactionQueue = [],
  onUpdateQueuedInteraction,
  onDeleteQueuedInteraction,
  onSendQueuedInteractionNow,
  onSteerQueuedInteraction,
  onReorderQueuedInteraction,
  onStopAll,
  tLimit,
  tUsed,
  tPct,
  cacheHitTokens,
  cacheMissTokens,
  cacheHitRate,
  compactionStatus,
  formatTime,
  onDeleteTurn,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
  messageListRef,
  getTurnProjection,
  onTurnVisible,
  listEndRef,
  subAgentCards,
  currentUser,
  viewportScrollIntent,
  onViewportScrollIntentHandled,
  permissionMode = 'auto',
  onPermissionModeChange = () => undefined,
  checkpoints = [],
  checkpointTimelineOpen = false,
  onToggleCheckpointTimeline,
  onRestoreCheckpoint,
  onForkCheckpoint,
  onDeleteCheckpoint,
  onClearAllCheckpoints,
  restoredCheckpointId = null,
  clearRestoredMarker,
}) => {
  const { styles } = useChatStyles();
  const auxiliaryDataReady = useInitialIdleReady();
  // ── 主代理服务商余额徽标（多服务商计费展示适配器；非 DeepSeek 等未适配服务商不渲染）──
  const billingAdapter = resolveBillingAdapter(selectedAgent?.preferredProviderId);
    const {
    balance: providerBalance,
    currency: providerBalanceCurrency,
    grantedBalance: providerBalanceGranted,
    toppedUpBalance: providerBalanceToppedUp,
    queriedAt: providerBalanceQueriedAt,
    errorText: providerBalanceError,
    loading: providerBalanceLoading,
    refresh: refreshProviderBalance,
  } = useProviderBalance(
    selectedAgent?.preferredProviderId,
    !!billingAdapter && auxiliaryDataReady,
  );
  // ── P2#9：Auto-review classifier 状态机（回退自动切手动）──
  const autoReview = useAutoReviewClassifier({
    enabled: permissionMode === 'auto',
    onFallbackToManual: () => {
      // 连续 block 3 次或累计 20 次 → 自动切回手动审批
      onPermissionModeChange('manual');
    },
  });
  // 权限模式变化时同步 classifier 启用状态（setEnabled 为稳定引用）
  React.useEffect(() => {
    autoReview.setEnabled(permissionMode === 'auto');
  }, [permissionMode, autoReview.setEnabled]);
  // ── P2#10：Sandbox 边界可视化（当前为前端推导，后端就绪后可替换）──
  const [sandboxNetworkMode, setSandboxNetworkMode] =
    React.useState<SandboxNetworkMode>('allowlist');
  const sandboxBoundary: SandboxBoundaryInfo = React.useMemo(
    () => createDefaultSandboxBoundary(workspaceId, sandboxNetworkMode),
    [workspaceId, sandboxNetworkMode],
  );
  const handleRestoreAuto = React.useCallback(() => {
    autoReview.resetToAuto();
    onPermissionModeChange('auto');
  }, [autoReview, onPermissionModeChange]);
  const [devMode, setDevMode] = useState<boolean>(
    () => localStorage.getItem(DEV_MODE_KEY) === '1',
  );
  // 任务看板模态窗口（原独立路由改为弹窗）
  const [taskBoardOpen, setTaskBoardOpen] = useState(false);
  /** P0#2：转录视图分级（normal | verbose | summary） */
  const [transcriptMode, setTranscriptMode] =
    useState<TranscriptMode>('normal');
  /** P2#8：Focus view 单行折叠模式 */
  const [focusView, setFocusView] = useState(false);
  const rawEvents = useDevRuntimeEvents(devMode, turns);
  const [inferredSessionId, setInferredSessionId] = useState<string | null>(
    null,
  );
  const [autoTtsEnabled, setAutoTtsEnabled] = useState<boolean>(true);
  const [historyModalOpen, setHistoryModalOpen] = useState(false);
  const [subAgentInspectorOpen, setSubAgentInspectorOpen] = useState(false);
  const [selectedSubAgentRunId, setSelectedSubAgentRunId] = useState<
    string | null
  >(null);

  const handleOpenSubAgentInspector = useCallback((runId?: string) => {
    setSelectedSubAgentRunId(runId ?? null);
    setSubAgentInspectorOpen(true);
  }, []);

  const handleHistoryQuote = useCallback(
    (quoteText: string) => {
      // 将引用文本追加到当前输入框内容末尾
      const current =
        (document.querySelector('textarea') as HTMLTextAreaElement)?.value ??
        '';
      onInputChange(current ? `${current}\n${quoteText}` : quoteText);
    },
    [onInputChange],
  );
  // inputValue 存 ref：回调身份稳定（否则每次按键 lift 都重建 → MessageList 的
  // React.memo 被击穿，整条消息流随打字重渲染——输入框卡顿的放大器）。
  const inputValueRef = useRef(inputValue);
  inputValueRef.current = inputValue;
  const handlePinnedQuote = useCallback(
    (quoteText: string) => {
      const current = inputValueRef.current;
      onInputChange(current ? `${current}\n${quoteText}` : quoteText);
    },
    [onInputChange],
  );
  const activeSubAgentCards = React.useMemo(
    () =>
      Object.values(subAgentCards ?? {}).filter(
        (card) => card.status === 'running' || card.status === 'spawning',
      ),
    [subAgentCards],
  );
    const subAgentCount = activeSubAgentCards.length;
  // 571fb2fa：等待卡片仅聚合「同步委派」（invocationMode !== 'async'）；
  // 异步/后台子代理只在右侧托盘坞呈现，不进入主消息等待卡片。
  // 归属校验：缺 parentTurnId 的 sync 卡不聚合（宁缺勿滥）——与 MessageList
  // 的「缺 parentTurnId 不绑」一致，防止无法精确归属的委派状态串台。
  const syncSubAgentCards = React.useMemo(
    () =>
      activeSubAgentCards.filter(
        (card) =>
          card.invocationMode !== 'async' && Boolean(card.parentTurnId),
      ),
    [activeSubAgentCards],
  );
  const parentDelegationActivity = React.useMemo<
    ParentDelegationActivity | undefined
  >(() => {
    if (syncSubAgentCards.length === 0) return undefined;
    const latest = [...syncSubAgentCards].sort(
      (left, right) =>
        (right.lastActivityAt ?? right.spawnedAt) -
        (left.lastActivityAt ?? left.spawnedAt),
    )[0];
    const label =
      latest.role ||
      (latest.originToolId && latest.originToolId !== 'spawn_sub_agent'
        ? latest.originToolId
        : undefined);
    return {
      activeCount: syncSubAgentCards.length,
      label,
      turnId: latest.parentTurnId,
      startedAt: Math.min(...syncSubAgentCards.map((card) => card.spawnedAt)),
      updatedAt: Math.max(
        ...syncSubAgentCards.map(
          (card) => card.lastActivityAt ?? card.spawnedAt,
        ),
      ),
    };
  }, [syncSubAgentCards]);
  const hasSubAgentActivity = React.useMemo(
    () => Object.keys(subAgentCards ?? {}).length > 0,
    [subAgentCards],
  );
  const latestAssistantText = React.useMemo(() => {
    for (let index = turns.length - 1; index >= 0; index -= 1) {
      const answer = turns[index]?.assistant.answerMarkdown?.trim();
      if (answer) return answer;
    }
    return '';
  }, [turns]);

  // ── 自动 TTS ──
  const autoTtsMessages = useMemo(
    () =>
      turns.map((t) => ({
        id: t.turnId,
        role: 'assistant' as const,
        content: t.assistant.answerMarkdown,
        voice: t.assistant.voice,
      })),
    [turns],
  );
  useAutoTts(autoTtsMessages, autoTtsEnabled);
  useNotificationSound(turns, true);

  /** 根据当前 turns 和 loading 推导 Agent Console 状态文案 */
  const chatStatus: ChatStatus = React.useMemo(() => {
    if (!loading && turns.length === 0) return 'idle';
    // 用户正在输入内容但未发送
    if (!loading && inputValue.trim().length > 0) return 'composing';
    const lastTurn = turns[turns.length - 1];
    if (loading) {
      // loading 但无 turn：初始化 / 历史加载中
      if (!lastTurn) return 'initializing';
      const st = lastTurn.assistant.status;
      if (st === 'thinking') return 'thinking';
      if (st === 'executing') return 'tool_executing';
      return 'streaming';
    }
    // 非 loading 状态，检查最后一轮结果
    const st = lastTurn?.assistant.status;
    if (st === 'error' || st === 'cancelled') return 'error';
    return 'completed';
  }, [loading, turns, inputValue]);

  const dropdownRender = useCallback(
    (menu: React.ReactNode) => (
      <div className="pudding-chat-select-popup-container">
        {menu}
        <Divider style={{ margin: '4px 0' }} />
        <Button type="link" block size="small" onClick={onCreateWorkspace}>
          + 新建工作空间
        </Button>
      </div>
    ),
    [onCreateWorkspace],
  );

  useEffect(() => {
    localStorage.setItem(DEV_MODE_KEY, devMode ? '1' : '0');
  }, [devMode]);

  useEffect(() => {
    if (!workspaceId) return;
    rememberWorkspaceVisit({ workspaceId, agentId });
  }, [agentId, workspaceId]);

  useEffect(() => {
    if (!auxiliaryDataReady) {
      setInferredSessionId(null);
      return;
    }
    if (!workspaceId) {
      setInferredSessionId(null);
      return;
    }
    if (selectedSessionId) {
      setInferredSessionId(null);
      return;
    }

    let alive = true;
    const token = localStorage.getItem('pudding_token');
    if (!token) {
      setInferredSessionId(null);
      return;
    }

    const loadSession = async () => {
      try {
        const resp = await fetch(
          `/api/sessions?workspaceId=${encodeURIComponent(workspaceId)}`,
          {
            method: 'GET',
            headers: { Authorization: `Bearer ${token}` },
          },
        );
        if (!resp.ok || !alive) return;
        const sessions = (await resp.json()) as Array<{ sessionId: string }>;
        if (alive && Array.isArray(sessions) && sessions.length > 0) {
          setInferredSessionId(sessions[0]?.sessionId ?? null);
        } else if (alive) {
          setInferredSessionId(null);
        }
      } catch {
        // no-op：开发者面板容错，不影响主聊天流程
      }
    };

    void loadSession();
    return () => {
      alive = false;
    };
  }, [auxiliaryDataReady, selectedSessionId, workspaceId]);

  // SSE 断流状态轮询
  const [reconnectCount, setReconnectCount] = React.useState(0);
  React.useEffect(() => {
    if (!reconnectCountRef) return;
    const timer = setInterval(
      () => setReconnectCount(reconnectCountRef.current),
      500,
    );
    return () => clearInterval(timer);
  }, [reconnectCountRef]);

  // ADR-074 G1：Goal 持久控制面状态条（服务端投影；无 Goal 时渲染 null）
  const goalState = useGoal({
    workspaceId,
    conversationId: selectedSessionId ?? undefined,
    agentId,
    enabled: auxiliaryDataReady,
  });

  return (
    <main
      className={`${styles.mainArea} ${styles.workbenchShell}`}
      aria-label="Agent 工作台"
    >
      <div className={styles.workbenchCenter}>
        {reconnectCount > 0 && (
          <Alert
            type="warning"
            banner
            showIcon={false}
            message={`连接中断，正在重连（第 ${reconnectCount} 次）...`}
            style={{ marginBottom: 0, borderRadius: 0 }}
          />
        )}
        <GoalBanner
          goal={goalState.goal}
          commandRunning={goalState.commandRunning}
          onCommand={goalState.runCommand}
        />
        <WorkspaceNavigationHeader
          leading={
            !sidebarOpen ? (
              <Button
                type="text"
                size="small"
                icon={<MenuUnfoldOutlined />}
                onClick={onToggleSidebar}
                aria-label="展开会话列表"
              />
            ) : undefined
          }
          crumbs={[]}
          controls={
            <>
              <Select
                className={`${styles.headerSelect} ${styles.headerSwitchSelect}`}
                size="small"
                variant="borderless"
                value={workspaceId}
                loading={workspaceLoading}
                options={wsOpts}
                onChange={onWorkspaceChange}
                placeholder="工作空间"
                popupMatchSelectWidth={false}
                popupRender={dropdownRender}
                classNames={{ popup: { root: styles.headerSelectPopup } }}
              />
              <Button
                type="text"
                size="small"
                icon={<AppstoreOutlined />}
                className={styles.taskBoardButton}
                disabled={!workspaceId}
                aria-label="任务看板"
                onMouseEnter={() => void loadTaskBoardModal()}
                onFocus={() => void loadTaskBoardModal()}
                onClick={() => {
                  if (workspaceId) {
                    setTaskBoardOpen(true);
                  }
                }}
              >
                任务看板
              </Button>
            </>
          }
          extraActions={
            <>
              {billingAdapter && (
                <span
                  aria-label="刷新服务商余额"
                  onClick={refreshProviderBalance}
                  style={{
                    display: 'inline-flex',
                    alignItems: 'center',
                    cursor: 'pointer',
                  }}
                >
                                    <ProviderBalanceIndicator
                    provider={billingAdapter.displayName}
                    balance={providerBalance}
                    currency={currencySymbolFor(
                      providerBalanceCurrency,
                      billingAdapter,
                    )}
                    grantedBalance={providerBalanceGranted}
                    toppedUpBalance={providerBalanceToppedUp}
                    queriedAt={providerBalanceQueriedAt}
                    loading={providerBalanceLoading}
                    error={!!providerBalanceError}
                    detail={providerBalanceError ?? '点击刷新'}
                  />
                </span>
              )}
              <Tooltip title="搜索历史消息">
                <Button
                  type="text"
                  size="small"
                  icon={<HistoryOutlined />}
                  aria-label="搜索历史消息"
                  onMouseEnter={() => void loadHistorySearchModal()}
                  onFocus={() => void loadHistorySearchModal()}
                  onClick={() => setHistoryModalOpen(true)}
                />
              </Tooltip>
              <Tooltip title="Checkpoint 时间线">
                <Button
                  type="text"
                  size="small"
                  icon={<FieldTimeOutlined />}
                  aria-label="Checkpoint 时间线"
                  onMouseEnter={() => void loadCheckpointTimelinePanel()}
                  onFocus={() => void loadCheckpointTimelinePanel()}
                  onClick={onToggleCheckpointTimeline}
                  className={checkpointTimelineOpen ? styles.devModeActive : ''}
                />
              </Tooltip>
              <Tooltip title={autoTtsEnabled ? '关闭自动朗读' : '开启自动朗读'}>
                <Button
                  type="text"
                  size="small"
                  icon={<SoundOutlined />}
                  aria-label={autoTtsEnabled ? '关闭自动朗读' : '开启自动朗读'}
                  onClick={() => setAutoTtsEnabled(!autoTtsEnabled)}
                  className={autoTtsEnabled ? styles.devModeActive : ''}
                />
              </Tooltip>
              <Tooltip title="开发者模式">
                <Button
                  type="text"
                  size="small"
                  icon={<BugOutlined />}
                  aria-label="开发者模式"
                  onMouseEnter={() => void loadDevPanel()}
                  onFocus={() => void loadDevPanel()}
                  onClick={() => setDevMode(!devMode)}
                  className={devMode ? styles.devModeActive : ''}
                />
              </Tooltip>
            </>
          }
        />

        <div className={styles.chatBody}>
          <div
            className={devMode ? styles.chatBodyWithDev : styles.chatBodyMain}
          >
            <div className={styles.chatBodyMain}>
              <div className={styles.chatInteractionShell}>
                <div className={styles.chatConversationColumn}>
                  <section
                    className={styles.timelineRegion}
                    aria-label="会话时间线"
                  >
                    <MessageList
                      turns={turns}
                      conversationView={conversationView}
                      sessionId={selectedSessionId}
                      workspaceId={workspaceId}
                      agentId={agentId}
                      selectedAgent={selectedAgent}
                      error={error}
                      historyLoading={historyLoading}
                      loadingMore={loadingMore}
                      hasMoreMessages={hasMoreMessages}
                      onClearError={onClearError}
                      onLoadMore={onLoadMore}
                      formatTime={formatTime}
                      onDeleteTurn={onDeleteTurn}
                      onContextMenu={onContextMenu}
                      onRerunTurn={onRerunTurn}
                      onPinTurn={onPinTurn}
                      getTurnProjection={getTurnProjection}
                      onTurnVisible={onTurnVisible}
                      onPinnedQuote={handlePinnedQuote}
                      messageListRef={messageListRef}
                      listEndRef={listEndRef}
                      currentUser={currentUser}
                      viewportScrollIntent={viewportScrollIntent}
                      onViewportScrollIntentHandled={
                        onViewportScrollIntentHandled
                      }
                      parentDelegationActivity={parentDelegationActivity}
                      transcriptMode={transcriptMode}
                      onTranscriptModeChange={setTranscriptMode}
                      focusView={focusView}
                      onFocusViewChange={setFocusView}
                      onApprovalDenied={(card) =>
                        autoReview.denyFromApproval(card)
                      }
                    />
                  </section>
                  <IntentConsole
                    inputValue={inputValue}
                    onInputChange={onInputChange}
                    onKeyDown={onKeyDown}
                    loading={loading}
                    interactionQueue={interactionQueue}
                    onUpdateQueuedInteraction={onUpdateQueuedInteraction}
                    onDeleteQueuedInteraction={onDeleteQueuedInteraction}
                    onSendQueuedInteractionNow={onSendQueuedInteractionNow}
                    onSteerQueuedInteraction={onSteerQueuedInteraction}
                    onReorderQueuedInteraction={onReorderQueuedInteraction}
                    onStopAll={onStopAll}
                    onSend={onSend}
                    onSendWithMetadata={onSendWithMetadata}
                    onStop={onStop}
                    onExport={onExport}
                    onOpenDevDetails={() => setDevMode(true)}
                    disabled={disabled}
                    tLimit={tLimit}
                    tUsed={tUsed}
                    tPct={tPct}
                    status={chatStatus}
                    sessionId={inferredSessionId ?? selectedSessionId}
                    workspaceId={workspaceId}
                    cacheHitTokens={cacheHitTokens}
                    cacheMissTokens={cacheMissTokens}
                    cacheHitRate={cacheHitRate}
                    compactionStatus={compactionStatus}
                    subAgentsRunning={subAgentCount}
                    onOpenSubAgentInspector={() =>
                      handleOpenSubAgentInspector()
                    }
                    latestAssistantText={latestAssistantText}
                    permissionMode={permissionMode}
                    onPermissionModeChange={onPermissionModeChange}
                    autoReviewState={autoReview.state}
                    recentlyDenied={autoReview.recentlyDenied}
                    onAutoReviewRestore={handleRestoreAuto}
                    onRetryDenied={(item: RecentlyDeniedItem) =>
                      autoReview.retryDenied(item.id)
                    }
                    onRemoveDenied={autoReview.removeDenied}
                    onClearDenied={autoReview.clearDenied}
                    sandboxBoundary={sandboxBoundary}
                    onSandboxNetworkModeChange={setSandboxNetworkMode}
                  />
                </div>
                {(hasSubAgentActivity || subAgentInspectorOpen) && (
                  <React.Suspense fallback={null}>
                    <SubAgentActivityDock
                      sessionId={inferredSessionId ?? selectedSessionId}
                      subAgentCards={subAgentCards}
                      inspectorOpen={subAgentInspectorOpen}
                      onInspectorOpenChange={setSubAgentInspectorOpen}
                      selectedRunId={selectedSubAgentRunId}
                      onSelectedRunIdChange={setSelectedSubAgentRunId}
                    />
                  </React.Suspense>
                )}
              </div>
            </div>

            {devMode && (
              <React.Suspense fallback={null}>
                <DevPanel
                  workspaceId={workspaceId}
                  sessionId={inferredSessionId}
                  rawEvents={rawEvents}
                  onRunBenchmarkPrompt={async (prompt, metadata) => {
                    onInputChange(prompt);
                    if (onSendWithMetadata) {
                      await onSendWithMetadata(prompt, metadata);
                    }
                  }}
                />
              </React.Suspense>
            )}
          </div>
        </div>
      </div>
      {historyModalOpen && (
        <React.Suspense fallback={null}>
          <HistorySearchModal
            open
            workspaceId={workspaceId ?? ''}
            onClose={() => setHistoryModalOpen(false)}
            onQuote={handleHistoryQuote}
          />
        </React.Suspense>
      )}

      {taskBoardOpen && workspaceId && (
        <React.Suspense fallback={null}>
          <TaskBoardModal
            open
            workspaceId={workspaceId}
            onClose={() => setTaskBoardOpen(false)}
          />
        </React.Suspense>
      )}

      {checkpointTimelineOpen && (
        <div className={styles.checkpointPanelHost}>
          <React.Suspense fallback={null}>
            <CheckpointTimelinePanel
              open
              sessionId={selectedSessionId}
              checkpoints={checkpoints}
              restoredCheckpointId={restoredCheckpointId}
              formatTime={formatTime}
              onRestore={(checkpointId) => onRestoreCheckpoint?.(checkpointId)}
              onFork={(checkpointId) => onForkCheckpoint?.(checkpointId)}
              onDelete={(checkpointId) => onDeleteCheckpoint?.(checkpointId)}
              onClearAll={() => onClearAllCheckpoints?.()}
              onClose={() => onToggleCheckpointTimeline?.()}
            />
          </React.Suspense>
        </div>
      )}
    </main>
  );
};

export default ChatMain;

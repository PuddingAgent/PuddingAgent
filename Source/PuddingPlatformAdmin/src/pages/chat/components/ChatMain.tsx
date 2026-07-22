// ── ChatMain：右侧主聊天区（Header + MessageList + InputArea）─
import {
  BugOutlined,
  HistoryOutlined,
  MenuUnfoldOutlined,
  
  SoundOutlined,
} from '@ant-design/icons';
import { history } from '@umijs/max';
import { Button, Divider, Select, Switch, Tooltip } from 'antd';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { WorkspaceNavigationHeader } from '@/components';
import type {
  WorkspaceAgentDto,
  WorkspaceWithPermDto,
} from '@/services/platform/api';
import {
  
  rememberWorkspaceVisit,
} from '@/utils/workspaceNavigation';
import type { AgentConversationView } from '../client/types';
import { useAutoTts } from '../hooks/useAutoTts';
import type {
  ChatInteractionQueueItem,
  ChatInteractionRuntimeEvent,
} from '../hooks/useChatState';
import { useNotificationSound } from '../hooks/useNotificationSound';
import { useChatStyles } from '../styles';
import type { ChatTurn, SubAgentCardMap } from '../types';
import IntentConsole, { type ChatStatus } from './IntentConsole';
import MessageList from './MessageList';

import SubAgentActivityDock from './SubAgentActivityDock';

const DevPanel = React.lazy(() => import('./DevPanel'));
const HistorySearchModal = React.lazy(() => import('./HistorySearchModal'));
import { useDevRuntimeEvents } from './useDevRuntimeEvents';

interface ChatMainProps {
  // layout
  sidebarOpen: boolean;
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
}

const DEV_MODE_KEY = 'pudding-dev-mode';

const ChatMain: React.FC<ChatMainProps> = ({
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
  tLimit,
  tUsed,
  tPct,
  cacheHitTokens,
  cacheMissTokens,
  cacheHitRate,
  formatTime,
  onDeleteTurn,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
  messageListRef,
  listEndRef,
  subAgentCards,
  currentUser,
  viewportScrollIntent,
  onViewportScrollIntentHandled,
}) => {
  const { styles } = useChatStyles();
  const [devMode, setDevMode] = useState<boolean>(
    () => localStorage.getItem(DEV_MODE_KEY) === '1',
  );
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
      const current = (document.querySelector('textarea') as HTMLTextAreaElement)?.value ?? '';
      onInputChange(current ? current + '\n' + quoteText : quoteText);
    },
    [onInputChange],
  );
  const handlePinnedQuote = useCallback(
    (quoteText: string) => {
      onInputChange(inputValue ? `${inputValue}\n${quoteText}` : quoteText);
    },
    [inputValue, onInputChange],
  );
  const subAgentCount = React.useMemo(
    () =>
      Object.values(subAgentCards ?? {}).filter(
        (card) => card.status === 'running' || card.status === 'spawning',
      ).length,
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
  const { playing: ttsPlaying, loading: ttsLoading } = useAutoTts(
    autoTtsMessages,
    autoTtsEnabled,
  );
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
  }, [selectedSessionId, workspaceId]);

  return (
    <main
      className={`${styles.mainArea} ${styles.workbenchShell}`}
      aria-label="Agent 工作台"
    >
      <div className={styles.workbenchCenter}>
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
              
            </>
          }
          extraActions={
            <>
              <Tooltip title="搜索历史消息">
                <Button
                  type="text"
                  size="small"
                  icon={<HistoryOutlined />}
                  aria-label="搜索历史消息"
                  onClick={() => setHistoryModalOpen(true)}
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
                  onClick={() => setDevMode(!devMode)}
                  className={devMode ? styles.devModeActive : ''}
                />
              </Tooltip>
            </>
          }
        />

        {/* Chat Body */}
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
                      onPinnedQuote={handlePinnedQuote}
                      messageListRef={messageListRef}
                      listEndRef={listEndRef}
                      currentUser={currentUser}
                      viewportScrollIntent={viewportScrollIntent}
                      onViewportScrollIntentHandled={onViewportScrollIntentHandled}
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
                    subAgentsRunning={subAgentCount}
                    onOpenSubAgentInspector={() =>
                      handleOpenSubAgentInspector()
                    }
                    latestAssistantText={latestAssistantText}
                  />
                </div>
                <SubAgentActivityDock
                  sessionId={inferredSessionId ?? selectedSessionId}
                  subAgentCards={subAgentCards}
                  inspectorOpen={subAgentInspectorOpen}
                  onInspectorOpenChange={setSubAgentInspectorOpen}
                  selectedRunId={selectedSubAgentRunId}
                  onSelectedRunIdChange={setSelectedSubAgentRunId}
                />
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
      <React.Suspense fallback={null}>
      <HistorySearchModal
        open={historyModalOpen}
        workspaceId={workspaceId ?? ''}
        onClose={() => setHistoryModalOpen(false)}
        onQuote={handleHistoryQuote}
      />
      </React.Suspense>
    </main>
  );
};

export default ChatMain;

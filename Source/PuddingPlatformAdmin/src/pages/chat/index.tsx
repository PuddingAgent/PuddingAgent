// ── ChatPage 壳：路由入口，组装布局 + 模态框 ────────────────

import { history, useLocation, useModel } from '@umijs/max';
import { App, ConfigProvider, Form, Input, Modal } from 'antd';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  createWorkspace,
  listTeams,
  listWorkspaces,
} from '@/services/platform/api';
import { recordPerfStep } from '@/utils/debug';
import {
  buildChatPath,
  buildChatPathWithQuery,
} from '@/utils/workspaceNavigation';
import { shouldIgnoreAgentContactClick } from './agentMainSessionSelection';
import { getAgentConversation, listAgentStatuses } from './client/agentChatApi';
import { conversationNeedsProjectionCatchUp } from './client/chatClientStore';
import { isAgentClientArchitectureEnabled } from './client/featureFlag';
import { createIndexedDbAgentChatCache } from './client/localCache';
import ChatLayout from './components/ChatLayout';
import ContextMenu, { type ContextMenuState } from './components/ContextMenu';
import { useAgentChatClient } from './hooks/useAgentChatClient';
import { useChatState } from './hooks/useChatState';
import { useTtsPlayer } from './hooks/useTtsPlayer';
import {
  savePinnedMessage,
  summarizePinnedMessage,
} from './utils/pinnedMessage';

/** 浅比较两个 Record 是否内容相同（用于避免轮询状态创建新引用导致下游重渲染） */
function shallowEqualRecord(
  a: Record<string, unknown>,
  b: Record<string, unknown>,
): boolean {
  const keysA = Object.keys(a);
  const keysB = Object.keys(b);
  if (keysA.length !== keysB.length) return false;
  for (const key of keysA) {
    if (!(key in b)) return false;
    const va = a[key];
    const vb = b[key];
    if (va === vb) continue;
    if (
      typeof va === 'object' &&
      typeof vb === 'object' &&
      va !== null &&
      vb !== null
    ) {
      if (JSON.stringify(va) !== JSON.stringify(vb)) return false;
    } else {
      return false;
    }
  }
  return true;
}

const ChatPageContent: React.FC = () => {
  const location = useLocation();
  const chat = useChatState(location.search);
  const { message: messageApi } = App.useApp();
  const { initialState } = useModel('@@initialState');
  const currentUser = initialState?.currentUser;
  const [createLoading, setCreateLoading] = useState(false);
  const agentSwitchSeqRef = useRef(0);
  const projectedStatusesRef = useRef<
    Record<string, { status: string; summary?: string }>
  >({});
  const useAgentClientArchitecture = useMemo(
    isAgentClientArchitectureEnabled,
    [],
  );
  const agentChatCache = useMemo(() => createIndexedDbAgentChatCache(), []);
  const agentChatApi = useMemo(
    () => ({
      listStatuses: listAgentStatuses,
      getConversation: getAgentConversation,
    }),
    [],
  );
  const agentClient = useAgentChatClient({
    cache: agentChatCache,
    api: agentChatApi,
    ownerUserId: currentUser?.userid || currentUser?.name,
  });
  const projectedAgentStatuses = useMemo(() => {
    if (!useAgentClientArchitecture)
      return {} as Record<string, { status: string; summary?: string }>;
    const next = Object.fromEntries(
      agentClient.snapshot.statuses.map((status) => [
        status.agentId,
        { status: status.status, summary: status.summary },
      ]),
    ) as Record<string, { status: string; summary?: string }>;
    // 与上次结果做深度比较，内容未变则返回旧引用避免下游重渲染
    const prev = projectedStatusesRef.current;
    if (prev && shallowEqualRecord(prev, next)) return prev;
    projectedStatusesRef.current = next;
    return next;
  }, [agentClient.snapshot.statuses, useAgentClientArchitecture]);
  const projectedConversation =
    useAgentClientArchitecture &&
    agentClient.snapshot.workspaceId === chat.workspaceId &&
    agentClient.snapshot.agentId === chat.agentId
      ? agentClient.snapshot.conversation
      : null;
  const selectedAgentStatus = useMemo(
    () =>
      agentClient.snapshot.statuses.find(
        (status) => status.agentId === chat.agentId,
      ),
    [agentClient.snapshot.statuses, chat.agentId],
  );

  useEffect(() => {
    if (!useAgentClientArchitecture || !chat.workspaceId) return;
    void agentClient.refreshStatuses(chat.workspaceId);
  }, [
    agentClient.refreshStatuses,
    chat.workspaceId,
    useAgentClientArchitecture,
  ]);

  useEffect(() => {
    if (!useAgentClientArchitecture || !chat.workspaceId) return;
    const workspaceId = chat.workspaceId;
    const timer = window.setInterval(() => {
      void agentClient.syncStatuses(workspaceId);
    }, 3000);
    return () => window.clearInterval(timer);
  }, [agentClient.syncStatuses, chat.workspaceId, useAgentClientArchitecture]);

  useEffect(() => {
    if (!useAgentClientArchitecture || !chat.workspaceId || !chat.agentId)
      return;
    void agentClient.selectAgent(chat.workspaceId, chat.agentId);
  }, [
    agentClient.selectAgent,
    chat.agentId,
    chat.workspaceId,
    useAgentClientArchitecture,
  ]);

  useEffect(() => {
    if (!useAgentClientArchitecture || !chat.workspaceId || !chat.agentId)
      return;
    const isActive =
      projectedConversation?.activeRun ||
      conversationNeedsProjectionCatchUp(projectedConversation) ||
      selectedAgentStatus?.status === 'running' ||
      selectedAgentStatus?.status === 'waiting';
    const intervalMs = isActive ? 1200 : 5000;
    const timer = window.setInterval(() => {
      void agentClient.syncSelectedAgent();
    }, intervalMs);
    return () => window.clearInterval(timer);
  }, [
    agentClient.syncSelectedAgent,
    chat.agentId,
    chat.workspaceId,
    projectedConversation?.activeRun,
    selectedAgentStatus?.status,
    useAgentClientArchitecture,
  ]);

  // T-201: 工作区通知 SSE — 页面级，跟随 workspaceId 自动启停
  useEffect(() => {
    if (chat.workspaceId) {
      chat.startWorkspaceNotificationStream(chat.workspaceId);
    }
    return () => {
      chat.stopWorkspaceNotificationStream();
    };
  }, [
    chat.workspaceId,
    chat.startWorkspaceNotificationStream,
    chat.stopWorkspaceNotificationStream,
  ]);

  // ── 右键菜单状态 ──────────────────────────────────────────
  const [contextMenu, setContextMenu] = useState<ContextMenuState>({
    visible: false,
    x: 0,
    y: 0,
    turnId: '',
    role: 'user',
    content: '',
  });
  const tts = useTtsPlayer();

  const handleContextMenu = useCallback(
    (
      e: React.MouseEvent,
      turnId: string,
      role: 'user' | 'assistant',
      content: string,
    ) => {
      e.preventDefault();
      setContextMenu({
        visible: true,
        x: e.clientX,
        y: e.clientY,
        turnId,
        role,
        content,
      });
    },
    [],
  );

  const closeContextMenu = useCallback(() => {
    setContextMenu((prev) => ({ ...prev, visible: false }));
  }, []);

  // ── 右键菜单回调 ──────────────────────────────────────────
  const inputValueRef = useRef(chat.inputValue);
  inputValueRef.current = chat.inputValue;

  const ctxCallbacks = useMemo(
    () => ({
      onCopy: async (turnId: string) => {
        const turn = chat.turns.find((t) => t.turnId === turnId);
        const text =
          contextMenu.content ||
          (turn
            ? contextMenu.role === 'user'
              ? turn.userMessage.text
              : turn.assistant.answerMarkdown
            : '');
        if (!text) return;
        try {
          await navigator.clipboard.writeText(text);
          messageApi.success('已复制');
          closeContextMenu();
        } catch {
          messageApi.error('复制失败，请手动复制');
        }
      },
      onQuote: (turnId: string) => {
        const turn = chat.turns.find((t) => t.turnId === turnId);
        const text =
          contextMenu.content ||
          (turn
            ? contextMenu.role === 'user'
              ? turn.userMessage.text
              : turn.assistant.answerMarkdown
            : '');
        if (!text) return;

        // 结构化引用格式：消息ID + 摘要（截取前三行）
        const dbMessageId = turn?.userMessage.dbMessageId;
        const previewLines = text.split('\n').slice(0, 3).join('\n');
        const lines = [
          dbMessageId
            ? `> 消息ID：${dbMessageId}`
            : `> 消息ID：请通过Query Session Log查询 turnId=${turnId}`,
          '> 请通过Query Session Log工具获取原始信息',
          `> 摘要：${previewLines.length > 120 ? previewLines.substring(0, 120) + '…' : previewLines}`,
        ];
        const quote = lines.join('\n') + '\n';
        const currentInput = inputValueRef.current;
        chat.setInputValue(currentInput ? currentInput + '\n' + quote : quote);
      },
      onDelete: (turnId: string) => {
        chat.onDeleteTurn(turnId);
      },
      onSpeak: (turnId: string) => {
        const turn = chat.turns.find((t) => t.turnId === turnId);
        const text = turn?.assistant?.answerMarkdown || contextMenu.content;
        if (text) {
          tts.speak(text);
          closeContextMenu();
        }
      },
      onRerun: (turnId: string) => {
        const turn = chat.turns.find((t) => t.turnId === turnId);
        if (turn) {
          chat.setInputValue(turn.userMessage.text);
        }
      },
      onEditAndRerun: (turnId: string) => {
        const turn = chat.turns.find((t) => t.turnId === turnId);
        if (turn) {
          chat.setInputValue(turn.userMessage.text);
        }
      },
      onAddToMemory: (_turnId: string) => {
        /* TODO: 接入记忆引擎 */
      },
      onPinContext: (_turnId: string) => {
        /* TODO: 固定为上下文 */
      },
      onPin: (turnId: string) => {
        const turn = chat.turns.find((t) => t.turnId === turnId);
        const text =
          contextMenu.content ||
          (turn
            ? contextMenu.role === 'user'
              ? turn.userMessage.text
              : turn.assistant.answerMarkdown
            : '');
        if (!text) return;
        savePinnedMessage({
          messageId:
            contextMenu.role === 'user'
              ? turn?.userMessage.dbMessageId
              : undefined,
          turnId,
          preview: summarizePinnedMessage(text),
          fullText: text,
          pinnedAt: Date.now(),
        });
        messageApi.success('已钉住');
        closeContextMenu();
      },
      onBranch: (_turnId: string) => {
        /* TODO: 创建分支 */
      },
    }),
    [
      chat.turns,
      contextMenu.content,
      contextMenu.role,
      chat.setInputValue,
      chat.onDeleteTurn,
      closeContextMenu,
      messageApi,
      tts.speak,
    ],
  );

  // ── 内联操作回调 ──────────────────────────────────────────
  const handleRerunTurn = useCallback(
    (turnId: string) => {
      const turn = chat.turns.find((t) => t.turnId === turnId);
      if (turn) {
        chat.setInputValue(turn.userMessage.text);
      }
    },
    [chat.turns, chat.setInputValue],
  );

  const handlePinTurn = useCallback(
    (turnId: string) => {
      const turn = chat.turns.find((t) => t.turnId === turnId);
      const text =
        turn?.assistant.answerMarkdown || turn?.userMessage.text || '';
      if (!turn || !text.trim()) return;
      savePinnedMessage({
        turnId,
        preview: summarizePinnedMessage(text),
        fullText: text,
        pinnedAt: Date.now(),
      });
      messageApi.success('已钉住');
    },
    [chat.turns, messageApi],
  );

  const handleCreateWorkspace = useCallback(async () => {
    try {
      const v = await chat.createSceneForm.validateFields();
      setCreateLoading(true);
      const teams = await listTeams();
      const tid = teams[0]?.teamId;
      if (!tid) {
        chat.setError('无可用分组');
        return;
      }
      const wsId =
        (v.name
          .trim()
          .toLowerCase()
          .replace(/[^a-z0-9]+/g, '-')
          .replace(/^-+|-+$/g, '')
          .slice(0, 48) || 'ws') +
        '-' +
        Date.now().toString().slice(-6);
      await createWorkspace({
        workspaceId: wsId,
        teamId: tid,
        name: v.name,
        teamAccessPolicy: 'Write',
        companyAccessPolicy: 'None',
      });
      const items = await listWorkspaces();
      chat.setWorkspaces(items);
      const nextWid = items[items.length - 1]?.workspaceId;
      chat.setWorkspaceId(nextWid);
      const sessionId = await chat.resetConversation(nextWid);
      history.replace(buildChatPath({ workspaceId: nextWid, sessionId }));
      chat.setCreateSceneOpen(false);
    } catch (e: unknown) {
      if (e && typeof e === 'object' && 'errorFields' in e) return;
      chat.setError('创建工作空间失败');
    } finally {
      setCreateLoading(false);
    }
  }, [chat]);

  // ── 布局回调（提取为 useCallback 以避免每次 render 破坏子组件 memo）──
  const handleToggleSidebar = useCallback(() => {
    chat.setSidebarOpen(!chat.sidebarOpen);
  }, [chat.setSidebarOpen, chat.sidebarOpen]);

  const handleNewSession = useCallback(() => {
    void (async () => {
      history.replace(
        buildChatPathWithQuery(
          {
            workspaceId: chat.workspaceId,
            agentId: chat.agentId,
            sessionId: null,
          },
          location.search,
        ),
      );
      await chat.resetConversation();
      // 主会话不暴露 sessionId 到 URL — 后端通过 "main" sentinel 解析
    })();
  }, [chat.workspaceId, chat.agentId, chat.resetConversation, location.search]);

  const handleSelectSession = useCallback(
    (sessionId: string) => {
      history.replace(
        buildChatPathWithQuery(
          { workspaceId: chat.workspaceId, agentId: chat.agentId, sessionId },
          location.search,
        ),
      );
      void chat.handleSelectSession(sessionId);
    },
    [chat.workspaceId, chat.agentId, chat.handleSelectSession, location.search],
  );

  const handleWorkspaceChange = useCallback(
    (v: string) => {
      chat.setWorkspaceId(v);
      history.replace(
        buildChatPathWithQuery(
          { workspaceId: v, agentId: null, sessionId: null },
          location.search,
        ),
      );
    },
    [chat.setWorkspaceId, location.search],
  );

  const handleAgentChange = useCallback(
    (v: string) => {
      if (
        shouldIgnoreAgentContactClick({
          clickedAgentId: v,
          currentAgentId: chat.agentId,
          selectedSessionId: chat.selectedSessionId,
          mainSessionId:
            chat.mainSessionId ?? chat.selectedAgent?.mainSessionId,
          turnCount: chat.turns.length,
        })
      )
        return;
      const switchStartedAt = performance.now();
      const traceId = `agent-switch-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
      const switchSeq = agentSwitchSeqRef.current + 1;
      agentSwitchSeqRef.current = switchSeq;
      recordPerfStep('agent.switch', 'click', switchStartedAt, {
        traceId,
        workspaceId: chat.workspaceId,
        fromAgentId: chat.agentId,
        agentId: v,
        selectedSessionId: chat.selectedSessionId,
        turnCount: chat.turns.length,
      });
      const commitStartedAt = performance.now();
      chat.setAgentId(v);
      // 主会话不暴露 sessionId 到 URL — 后端通过 "main" sentinel 解析实际 session
      history.replace(
        buildChatPathWithQuery(
          { workspaceId: chat.workspaceId, agentId: v, sessionId: null },
          location.search,
        ),
      );
      recordPerfStep(
        'agent.switch',
        'route.optimisticReplace',
        commitStartedAt,
        {
          traceId,
          workspaceId: chat.workspaceId,
          agentId: v,
        },
      );
      void (async () => {
        const ensureStartedAt = performance.now();
        const sessionId = await chat.ensureAgentMainSession(
          chat.workspaceId,
          v,
          {
            isCurrent: () => agentSwitchSeqRef.current === switchSeq,
            selectSession: !useAgentClientArchitecture,
          },
        );
        recordPerfStep('agent.switch', 'mainSession.ensure', ensureStartedAt, {
          traceId,
          workspaceId: chat.workspaceId,
          agentId: v,
          sessionId: sessionId ?? null,
          selectSession: !useAgentClientArchitecture,
          status: agentSwitchSeqRef.current === switchSeq ? 'ok' : 'stale',
        });
        if (agentSwitchSeqRef.current !== switchSeq) {
          recordPerfStep('agent.switch', 'switch.stale', switchStartedAt, {
            traceId,
            workspaceId: chat.workspaceId,
            agentId: v,
            status: 'stale',
          });
          return;
        }
        const finalizeStartedAt = performance.now();
        // 主会话不暴露 sessionId 到 URL
        history.replace(
          buildChatPathWithQuery(
            { workspaceId: chat.workspaceId, agentId: v, sessionId: null },
            location.search,
          ),
        );
        recordPerfStep(
          'agent.switch',
          'route.finalReplace',
          finalizeStartedAt,
          {
            traceId,
            workspaceId: chat.workspaceId,
            agentId: v,
            sessionId: sessionId ?? null,
          },
        );
        recordPerfStep('agent.switch', 'switch.finish', switchStartedAt, {
          traceId,
          workspaceId: chat.workspaceId,
          agentId: v,
          sessionId: sessionId ?? null,
        });
      })();
    },
    [
      chat.agentId,
      chat.selectedSessionId,
      chat.mainSessionId,
      chat.selectedAgent,
      chat.turns.length,
      chat.workspaceId,
      chat.setAgentId,
      chat.ensureAgentMainSession,
      useAgentClientArchitecture,
      location.search,
    ],
  );

  const handleCreateWorkspaceClick = useCallback(() => {
    chat.createSceneForm.resetFields();
    chat.setCreateSceneOpen(true);
  }, [chat.createSceneForm, chat.setCreateSceneOpen]);

  const handleSend = useCallback(() => {
    const t = chat.inputValue.trim();
    if (!t) return;
    chat.setInputValue('');
    void chat.submitInteraction(t);
  }, [chat.inputValue, chat.setInputValue, chat.submitInteraction]);

  const handleSendWithMetadata = useCallback(
    async (content: string, metadata?: Record<string, unknown>) => {
      const text = content.trim();
      if (!text) return;
      chat.setInputValue('');
      await chat.submitInteraction(text, { metadata });
    },
    [chat.setInputValue, chat.submitInteraction],
  );

  const handleStop = useCallback(() => {
    chat.abortRef.current?.abort();
  }, [chat.abortRef]);

  const handleClearError = useCallback(() => {
    chat.setError(null);
  }, [chat.setError]);

  return (
    <>
      <ChatLayout
        sidebarOpen={chat.sidebarOpen}
        onToggleSidebar={handleToggleSidebar}
        sessionsLoading={chat.sessionsLoading}
        groups={chat.groups}
        selectedSessionId={chat.selectedSessionId}
        creatingSession={chat.creatingSession}
        onNewSession={handleNewSession}
        onSelectSession={handleSelectSession}
        onRenameStart={chat.handleRenameStart}
        onArchiveSession={chat.handleArchiveSession}
        onDeleteSession={chat.handleDeleteSession}
        unreadCounts={chat.sessionUnreadCounts}
        workspaces={chat.workspaces}
        workspaceId={chat.workspaceId}
        workspaceLoading={chat.workspaceLoading}
        wsOpts={chat.wsOpts}
        onWorkspaceChange={handleWorkspaceChange}
        agents={chat.agents}
        agentId={chat.agentId}
        agentLoading={chat.agentLoading}
        agOpts={chat.agOpts}
        selectedAgent={chat.selectedAgent}
        onAgentChange={handleAgentChange}
        onCreateWorkspace={handleCreateWorkspaceClick}
        turns={chat.turns}
        chatInteractionRuntimeEvents={chat.chatInteractionRuntimeEvents}
        historyLoading={chat.historyLoading}
        loadingMore={chat.loadingMore}
        hasMoreMessages={chat.hasMoreMessages}
        error={chat.error}
        onClearError={handleClearError}
        onLoadMore={chat.loadMoreMessages}
        inputValue={chat.inputValue}
        onInputChange={chat.setInputValue}
        onKeyDown={chat.handleKeyDown}
        loading={chat.loading}
        agentStatuses={projectedAgentStatuses}
        conversationView={projectedConversation}
        workingAgentIds={chat.workingAgentIds}
        interactionQueue={chat.interactionQueue}
        onUpdateQueuedInteraction={chat.updateQueuedInteraction}
        onDeleteQueuedInteraction={chat.deleteQueuedInteraction}
        onSendQueuedInteractionNow={chat.sendQueuedInteractionNow}
        onSteerQueuedInteraction={chat.steerQueuedInteraction}
        onSend={handleSend}
        onSendWithMetadata={handleSendWithMetadata}
        onStop={handleStop}
        onExport={chat.handleExport}
        disabled={!chat.workspaceId || !chat.agentId}
        tLimit={chat.tLimit}
        tUsed={chat.tUsed}
        tPct={chat.tPct}
        cacheHitTokens={chat.sessionCacheHitTokens}
        cacheMissTokens={chat.sessionCacheMissTokens}
        cacheHitRate={chat.cacheHitRate}
        formatTime={chat.formatTime}
        onDeleteTurn={chat.onDeleteTurn}
        onContextMenu={handleContextMenu}
        onRerunTurn={handleRerunTurn}
        onPinTurn={handlePinTurn}
        messageListRef={chat.messageListRef}
        listEndRef={chat.listEndRef}
        subAgentCards={chat.subAgentCards}
        reconnectCountRef={chat.reconnectCountRef}
        currentUser={currentUser}
        viewportScrollIntent={chat.viewportScrollIntent}
        onViewportScrollIntentHandled={chat.clearViewportScrollIntent}
      />

      <Modal
        title="新建工作空间"
        open={chat.createSceneOpen}
        onOk={handleCreateWorkspace}
        onCancel={() => chat.setCreateSceneOpen(false)}
        confirmLoading={createLoading}
        okText="创建"
        cancelText="取消"
        destroyOnHidden
      >
        <Form form={chat.createSceneForm} layout="vertical">
          <Form.Item
            name="name"
            label="名称"
            rules={[{ required: true, message: '请输入名称' }, { max: 128 }]}
          >
            <Input placeholder="例如：研发协作空间" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title="重命名会话"
        open={chat.renameModalOpen}
        onOk={chat.handleRenameSubmit}
        onCancel={() => chat.setRenameModalOpen(false)}
        okText="确定"
        cancelText="取消"
      >
        <Input
          value={chat.renameTitle}
          onChange={(e) => chat.setRenameTitle(e.target.value)}
          onPressEnter={chat.handleRenameSubmit}
          placeholder="输入新标题"
          maxLength={50}
          autoFocus
        />
      </Modal>

      <ContextMenu
        state={contextMenu}
        onClose={closeContextMenu}
        onCopy={ctxCallbacks.onCopy}
        onQuote={ctxCallbacks.onQuote}
        onDelete={ctxCallbacks.onDelete}
        onSpeak={ctxCallbacks.onSpeak}
        onRerun={ctxCallbacks.onRerun}
        onEditAndRerun={ctxCallbacks.onEditAndRerun}
        onAddToMemory={ctxCallbacks.onAddToMemory}
        onPinContext={ctxCallbacks.onPinContext}
        onPin={ctxCallbacks.onPin}
        onBranch={ctxCallbacks.onBranch}
      />
    </>
  );
};

const ChatPage: React.FC = () => (
  <ConfigProvider
    theme={{ token: { colorPrimary: 'var(--accent-purple)', borderRadius: 8 } }}
  >
    <App>
      <ChatPageContent />
    </App>
  </ConfigProvider>
);

export default ChatPage;

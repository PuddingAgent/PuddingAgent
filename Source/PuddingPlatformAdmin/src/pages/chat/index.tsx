// ── ChatPage 壳：路由入口，组装布局 + 模态框 ────────────────
import { App, ConfigProvider, Form, Input, Modal } from 'antd';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useModel } from 'umi';
import {
  createWorkspace,
  listTeams,
  listWorkspaces,
} from '@/services/platform/api';
import ChatLayout from './components/ChatLayout';
import ContextMenu, { type ContextMenuState } from './components/ContextMenu';
import { useChatState } from './hooks/useChatState';

const ChatPage: React.FC = () => {
  const chat = useChatState();
  const { initialState } = useModel('@@initialState');
  const currentUser = initialState?.currentUser;
  const [createLoading, setCreateLoading] = useState(false);

  // T-201: 工作区通知 SSE — 页面级，跟随 workspaceId 自动启停
  useEffect(() => {
    if (chat.workspaceId) {
      chat.startWorkspaceNotificationStream(chat.workspaceId);
    }
    return () => {
      chat.stopWorkspaceNotificationStream();
    };
  }, [chat.workspaceId, chat.startWorkspaceNotificationStream, chat.stopWorkspaceNotificationStream]);

  // ── 右键菜单状态 ──────────────────────────────────────────
  const [contextMenu, setContextMenu] = useState<ContextMenuState>({ visible: false, x: 0, y: 0, turnId: '', role: 'user' });

  const handleContextMenu = useCallback((e: React.MouseEvent, turnId: string, role: 'user' | 'assistant') => {
    e.preventDefault();
    setContextMenu({ visible: true, x: e.clientX, y: e.clientY, turnId, role });
  }, []);

  const closeContextMenu = useCallback(() => {
    setContextMenu(prev => ({ ...prev, visible: false }));
  }, []);

  // ── 右键菜单回调 ──────────────────────────────────────────
  const ctxCallbacks = useMemo(() => ({
    onCopy: (turnId: string) => {
      const turn = chat.turns.find(t => t.turnId === turnId);
      if (!turn) return;
      const text = contextMenu.role === 'user' ? turn.userMessage.text : turn.assistant.answerMarkdown;
      navigator.clipboard.writeText(text).catch(() => {});
    },
    onQuote: (turnId: string) => {
      const turn = chat.turns.find(t => t.turnId === turnId);
      if (!turn) return;
      const text = contextMenu.role === 'user' ? turn.userMessage.text : turn.assistant.answerMarkdown;
      const quote = '> ' + text.replace(/\n/g, '\n> ') + '\n';
      chat.setInputValue(chat.inputValue ? chat.inputValue + '\n' + quote : quote);
    },
    onDelete: (turnId: string) => { chat.onDeleteTurn(turnId); },
    onRerun: (turnId: string) => {
      const turn = chat.turns.find(t => t.turnId === turnId);
      if (turn) { chat.setInputValue(turn.userMessage.text); }
    },
    onEditAndRerun: (turnId: string) => {
      const turn = chat.turns.find(t => t.turnId === turnId);
      if (turn) { chat.setInputValue(turn.userMessage.text); }
    },
    onAddToMemory: (_turnId: string) => { /* TODO: 接入记忆引擎 */ },
    onPinContext: (_turnId: string) => { /* TODO: 固定为上下文 */ },
    onBranch: (_turnId: string) => { /* TODO: 创建分支 */ },
  }), [chat.turns, contextMenu.role, chat.inputValue, chat.setInputValue, chat.onDeleteTurn]);

  // ── 内联操作回调 ──────────────────────────────────────────
  const handleRerunTurn = useCallback((turnId: string) => {
    const turn = chat.turns.find(t => t.turnId === turnId);
    if (turn) { chat.setInputValue(turn.userMessage.text); }
  }, [chat.turns, chat.setInputValue]);

  const handlePinTurn = useCallback((_turnId: string) => {
    // TODO: 固定消息为上下文
  }, []);

  const handleCreateWorkspace = useCallback(async () => {
    try {
      const v = await chat.createSceneForm.validateFields();
      setCreateLoading(true);
      const teams = await listTeams();
      const tid = teams[0]?.teamId;
      if (!tid) { chat.setError('无可用分组'); return; }
      const wsId = (v.name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 48) || 'ws') + '-' + Date.now().toString().slice(-6);
      await createWorkspace({ workspaceId: wsId, teamId: tid, name: v.name, teamAccessPolicy: 'Write', companyAccessPolicy: 'None' });
      const items = await listWorkspaces();
      chat.setWorkspaces(items);
      const nextWid = items[items.length - 1]?.workspaceId;
      chat.setWorkspaceId(nextWid);
      void chat.resetConversation(nextWid);
      chat.setCreateSceneOpen(false);
    } catch (e: unknown) {
      if (e && typeof e === 'object' && 'errorFields' in e) return;
      chat.setError('创建工作空间失败');
    } finally {
      setCreateLoading(false);
    }
  }, [chat]);

  return (
    <ConfigProvider theme={{ token: { colorPrimary: 'var(--accent-purple)', borderRadius: 8 } }}>
      <App>
        <ChatLayout
          sidebarOpen={chat.sidebarOpen}
          onToggleSidebar={() => chat.setSidebarOpen(!chat.sidebarOpen)}
          sessionsLoading={chat.sessionsLoading}
          groups={chat.groups}
          selectedSessionId={chat.selectedSessionId}
          creatingSession={chat.creatingSession}
          onNewSession={() => { void chat.resetConversation(); }}
          onSelectSession={chat.handleSelectSession}
          onRenameStart={chat.handleRenameStart}
          onArchiveSession={chat.handleArchiveSession}
          onDeleteSession={chat.handleDeleteSession}
          unreadCounts={chat.sessionUnreadCounts}
          workspaces={chat.workspaces}
          workspaceId={chat.workspaceId}
          workspaceLoading={chat.workspaceLoading}
          wsOpts={chat.wsOpts}
          onWorkspaceChange={(v) => { chat.setWorkspaceId(v); }}
          agents={chat.agents}
          agentId={chat.agentId}
          agentLoading={chat.agentLoading}
          agOpts={chat.agOpts}
          selectedAgent={chat.selectedAgent}
          onAgentChange={(v) => { chat.setAgentId(v); void chat.resetConversation(undefined, v); }}
          onCreateWorkspace={() => { chat.createSceneForm.resetFields(); chat.setCreateSceneOpen(true); }}
          turns={chat.turns}
          historyLoading={chat.historyLoading}
          loadingMore={chat.loadingMore}
          hasMoreMessages={chat.hasMoreMessages}
          error={chat.error}
          onClearError={() => chat.setError(null)}
          onLoadMore={chat.loadMoreMessages}
          inputValue={chat.inputValue}
          onInputChange={chat.setInputValue}
          onKeyDown={chat.handleKeyDown}
          loading={chat.loading}
          onSend={() => { const t = chat.inputValue.trim(); if (!t) return; chat.setInputValue(''); void chat.sendMessage(t); }}
          onStop={() => { chat.abortRef.current?.abort(); }}
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
          currentUser={currentUser}
        />

        <Modal
          title="新建工作空间"
          open={chat.createSceneOpen}
          onOk={handleCreateWorkspace}
          onCancel={() => chat.setCreateSceneOpen(false)}
          confirmLoading={createLoading}
          okText="创建"
          cancelText="取消"
          destroyOnClose
        >
          <Form form={chat.createSceneForm} layout="vertical">
            <Form.Item name="name" label="名称" rules={[{ required: true, message: '请输入名称' }, { max: 128 }]}>
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
          onRerun={ctxCallbacks.onRerun}
          onEditAndRerun={ctxCallbacks.onEditAndRerun}
          onAddToMemory={ctxCallbacks.onAddToMemory}
          onPinContext={ctxCallbacks.onPinContext}
          onBranch={ctxCallbacks.onBranch}
        />
      </App>
    </ConfigProvider>
  );
};

export default ChatPage;

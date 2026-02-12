// ── ChatLayout：整体布局（Sidebar + Main）───────────────────
import React from 'react';
import type {
  WorkspaceAgentDto,
  WorkspaceWithPermDto,
} from '@/services/platform/api';
import type { AgentConversationView } from '../client/types';
import type {
  ChatInteractionQueueItem,
  ChatInteractionRuntimeEvent,
} from '../hooks/useChatState';
import { useChatStyles } from '../styles';
import type { ChatTurn, SessionGroup, SubAgentCardMap } from '../types';
import ChatMain from './ChatMain';
import SessionSidebar, {
  type AgentStatusChipProjection,
} from './SessionSidebar';

interface ChatLayoutProps {
  // sidebar
  sidebarOpen: boolean;
  onToggleSidebar: () => void;
  sessionsLoading: boolean;
  groups: SessionGroup[];
  selectedSessionId: string | null;
  creatingSession: boolean;
  onNewSession: () => void;
  onSelectSession: (sid: string) => void;
  onRenameStart: (sid: string, title: string) => void;
  onArchiveSession: (sid: string) => void;
  onDeleteSession: (sid: string) => void;
  /** T-201: 会话未读计数 */
  unreadCounts?: Record<string, number>;
  // main (delegated to ChatMain)
  workspaces: WorkspaceWithPermDto[];
  workspaceId: string | undefined;
  workspaceLoading: boolean;
  wsOpts: { value: string; label: string; disabled: boolean }[];
  onWorkspaceChange: (v: string | undefined) => void;
  agents: WorkspaceAgentDto[];
  agentId: string | undefined;
  agentLoading: boolean;
  agOpts: { value: string; label: React.ReactNode; disabled: boolean }[];
  selectedAgent: WorkspaceAgentDto | undefined;
  onAgentChange: (v: string | undefined) => void;
  onCreateWorkspace: () => void;
  turns: ChatTurn[];
  conversationView?: AgentConversationView | null;
  chatInteractionRuntimeEvents?: ChatInteractionRuntimeEvent[];
  historyLoading: boolean;
  loadingMore: boolean;
  hasMoreMessages: boolean;
  error: string | null;
  onClearError: () => void;
  onLoadMore: () => void;
  inputValue: string;
  onInputChange: (v: string) => void;
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  loading: boolean;
  workingAgentIds: string[];
  agentStatuses?: Record<string, AgentStatusChipProjection>;
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
  tLimit: number;
  tUsed: number;
  tPct: number;
  cacheHitTokens?: number;
  cacheMissTokens?: number;
  cacheHitRate?: number;
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
  messageListRef: React.RefObject<HTMLDivElement | null>;
  listEndRef: React.RefObject<HTMLDivElement | null>;
  subAgentCards: SubAgentCardMap;
  currentUser?: { name?: string; avatar?: string };
  viewportScrollIntent?: import('../viewport/types').ScrollIntent;
  onViewportScrollIntentHandled?: () => void;
}

const ChatLayout: React.FC<ChatLayoutProps> = (props) => {
  const { styles } = useChatStyles();
  const workingAgentIds = Array.from(new Set(props.workingAgentIds));

  return (
    <div className={styles.layout}>
      <SessionSidebar
        sidebarOpen={props.sidebarOpen}
        onToggleSidebar={props.onToggleSidebar}
        sessionsLoading={props.sessionsLoading}
        groups={props.groups}
        selectedSessionId={props.selectedSessionId}
        creatingSession={props.creatingSession}
        onNewSession={props.onNewSession}
        onSelectSession={props.onSelectSession}
        onRenameStart={props.onRenameStart}
        onArchiveSession={props.onArchiveSession}
        onDeleteSession={props.onDeleteSession}
        unreadCounts={props.unreadCounts}
        agents={props.agents}
        agentId={props.agentId}
        agentLoading={props.agentLoading}
        onAgentChange={props.onAgentChange}
        agentStatuses={props.agentStatuses}
        workingAgentIds={workingAgentIds}
      />
      <ChatMain
        sidebarOpen={props.sidebarOpen}
        onToggleSidebar={props.onToggleSidebar}
        workspaces={props.workspaces}
        workspaceId={props.workspaceId}
        workspaceLoading={props.workspaceLoading}
        wsOpts={props.wsOpts}
        onWorkspaceChange={props.onWorkspaceChange}
        agents={props.agents}
        agentId={props.agentId}
        agentLoading={props.agentLoading}
        agOpts={props.agOpts}
        selectedAgent={props.selectedAgent}
        onAgentChange={props.onAgentChange}
        onCreateWorkspace={props.onCreateWorkspace}
        selectedSessionId={props.selectedSessionId}
        turns={props.turns}
        conversationView={props.conversationView}
        chatInteractionRuntimeEvents={props.chatInteractionRuntimeEvents}
        historyLoading={props.historyLoading}
        loadingMore={props.loadingMore}
        hasMoreMessages={props.hasMoreMessages}
        error={props.error}
        onClearError={props.onClearError}
        onLoadMore={props.onLoadMore}
        inputValue={props.inputValue}
        onInputChange={props.onInputChange}
        onKeyDown={props.onKeyDown}
        loading={props.loading}
        interactionQueue={props.interactionQueue}
        onUpdateQueuedInteraction={props.onUpdateQueuedInteraction}
        onDeleteQueuedInteraction={props.onDeleteQueuedInteraction}
        onSendQueuedInteractionNow={props.onSendQueuedInteractionNow}
        onSteerQueuedInteraction={props.onSteerQueuedInteraction}
        onSend={props.onSend}
        onSendWithMetadata={props.onSendWithMetadata}
        onStop={props.onStop}
        onExport={props.onExport}
        disabled={props.disabled}
        tLimit={props.tLimit}
        tUsed={props.tUsed}
        tPct={props.tPct}
        cacheHitTokens={props.cacheHitTokens}
        cacheMissTokens={props.cacheMissTokens}
        cacheHitRate={props.cacheHitRate}
        formatTime={props.formatTime}
        onDeleteTurn={props.onDeleteTurn}
        onContextMenu={props.onContextMenu}
        onRerunTurn={props.onRerunTurn}
        onPinTurn={props.onPinTurn}
        messageListRef={props.messageListRef}
        listEndRef={props.listEndRef}
        subAgentCards={props.subAgentCards}
        currentUser={props.currentUser}
        viewportScrollIntent={props.viewportScrollIntent}
        onViewportScrollIntentHandled={props.onViewportScrollIntentHandled}
      />
    </div>
  );
};

export default ChatLayout;

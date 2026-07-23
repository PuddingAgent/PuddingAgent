// ── MessageList：消息列表容器（虚拟滚动）───────────────────────────────
import {
  ArrowDownOutlined,
  VerticalAlignBottomOutlined,
} from '@ant-design/icons';
import { Alert, Badge, Button, Skeleton, Spin, Tooltip } from 'antd';
import React, {
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import type {
  AgentConversationView,
  ConversationMessageView,
  ProcessSummaryItem,
} from '../client/types';
import { useChatStyles } from '../styles';
import type {
  AssistantStatus,
  ChatQuotedMessage,
  ChatTurn,
  MessageStatus,
  TimelineItem,
} from '../types';
import { buildVirtualMessageItems } from '../viewport/messageProjection';
import { useMessageViewportRuntime } from '../viewport/useMessageViewportRuntime';
import type { ScrollIntent, VirtualMessageItem } from '../viewport/types';
import { inboundDebug } from '../utils/inboundDebug';
import { getPerfEvents } from '@/utils/debug';
import type { ChatEmptyStateMode } from './ChatEmptyState';
import ChatEmptyState from './ChatEmptyState';
import MessageStream from './MessageStream';
import PinnedMessageButton from './PinnedMessageButton';

interface MessageListProps {
  turns: ChatTurn[];
  sessionId?: string | null;
  /** 当前工作空间 ID，用于用户视觉消息的图片加载 */
  workspaceId?: string;
  agentId: string | undefined;
  selectedAgent?: WorkspaceAgentDto;
  error: string | null;
  historyLoading: boolean;
  loadingMore: boolean;
  hasMoreMessages: boolean;
  onClearError: () => void;
  onLoadMore: () => void;
  formatTime: (ts: number) => string;
  onDeleteTurn: (turnId: string) => void;
  onContextMenu: (
    e: React.MouseEvent,
    turnId: string,
    role: 'user' | 'assistant',
    content: string,
  ) => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
  onPinnedQuote?: (quoteText: string) => void;
  messageListRef: React.RefObject<HTMLDivElement | null>;
  listEndRef: React.RefObject<HTMLDivElement | null>;
  conversationView?: AgentConversationView | null;
  /** 当前登录用户信息 */
  currentUser?: { name?: string; avatar?: string };
  viewportScrollIntent?: ScrollIntent;
  onViewportScrollIntentHandled?: () => void;
}

const toTimestamp = (value: string) => {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
};

const toUserMessageStatus = (
  status: ConversationMessageView['status'],
): MessageStatus => {
  if (status === 'sending') return 'sending';
  if (status === 'failed') return 'error';
  return 'success';
};

const toAssistantStatus = (
  status: ConversationMessageView['status'],
): AssistantStatus => {
  switch (status) {
    case 'streaming':
      return 'streaming';
    case 'failed':
      return 'error';
    case 'cancelled':
      return 'cancelled';
    case 'sending':
      return 'thinking';
    default:
      return 'success';
  }
};

type ActiveRunView = NonNullable<AgentConversationView['activeRun']>;

const toActiveRunAssistantStatus = (
  status: ActiveRunView['status'],
): AssistantStatus => {
  switch (status) {
    case 'queued':
    case 'waiting':
      return 'thinking';
    case 'running':
      return 'streaming';
    case 'failed':
      return 'error';
    case 'cancelled':
      return 'cancelled';
    default:
      return 'success';
  }
};

const isActiveRunStreaming = (status: ActiveRunView['status']): boolean =>
  status === 'queued' || status === 'running' || status === 'waiting';

const toTimelineItems = (items: ProcessSummaryItem[]): TimelineItem[] =>
  items.map((item) => ({
    id: item.id,
    type:
      item.kind === 'tool_call' ||
      item.kind === 'tool_result' ||
      item.kind === 'thinking'
        ? item.kind
        : 'subconscious_step',
    text: item.text,
    status: item.status,
    name: item.name ?? undefined,
    arguments: item.arguments ?? undefined,
    output: item.output ?? undefined,
    exitCode: item.exitCode ?? undefined,
    message: item.message ?? undefined,
    timestamp: toTimestamp(item.timestamp),
    collapsed: true,
  }));

const readPuddingMessage = (
  source: string,
): Record<string, unknown> | undefined => {
  try {
    const parsed = JSON.parse(source) as Record<string, unknown>;
    return parsed?.schema === 'pudding-message' ? parsed : undefined;
  } catch {
    // Message Fabric existed before the JSON envelope and some persisted
    // cross-agent messages are still stored as XML. Normalize them here so the
    // chat renderer treats both protocol shapes as the same domain message.
    if (
      !source.trimStart().startsWith('<pudding-message') ||
      typeof DOMParser === 'undefined'
    ) {
      return undefined;
    }
    try {
      const doc = new DOMParser().parseFromString(source, 'application/xml');
      if (doc.querySelector('parsererror')) return undefined;
      const root = doc.documentElement;
      if (root?.nodeName !== 'pudding-message') return undefined;
      const from = root.querySelector('from');
      const context = root.querySelector('context');
      return {
        schema: 'pudding-message',
        message_type:
          root.querySelector('message-type')?.textContent ?? undefined,
        from: {
          kind: from?.getAttribute('kind') ?? undefined,
          id: from?.getAttribute('id') ?? undefined,
          display_name: from?.getAttribute('display-name') ?? undefined,
        },
        context: {
          text: context?.textContent ?? '',
        },
      };
    } catch {
      return undefined;
    }
  }
};

const parseAgentQuotedMessage = (
  message: ConversationMessageView,
  timestamp: number,
): ChatQuotedMessage | undefined => {
  if (message.sourceKind !== 'agent') {
    inboundDebug.log(
      'parse',
      'SKIP sourceKind=',
      message.sourceKind,
      'messageType=',
      message.messageType,
    );
    return undefined;
  }

  inboundDebug.log(
    'parse',
    'CHECK sourceKind=',
    message.sourceKind,
    'messageType=',
    message.messageType,
    'content[0:120]=',
    message.content?.substring(0, 120),
  );

  const parsed = readPuddingMessage(message.content);
  if (!parsed) {
    inboundDebug.warn(
      'parse',
      'NOT pudding-message JSON, content[0:80]=',
      message.content?.substring(0, 80),
    );
    return undefined;
  }

  const from = parsed.from as
    | { kind?: string; id?: string; display_name?: string }
    | undefined;
  inboundDebug.log('parse', 'parsed from=', from);

  if (from?.kind !== 'agent') {
    inboundDebug.log('parse', 'SKIP from.kind=', from?.kind);
    return undefined;
  }

  const context = parsed.context as { text?: string } | undefined;
  const result: ChatQuotedMessage = {
    sourceId: from.id || message.sourceId || 'agent',
    sourceName: from.display_name || message.sourceName || 'Agent',
    sourceKind: 'agent',
    messageType:
      typeof parsed.message_type === 'string' ? parsed.message_type : undefined,
    content: context?.text ?? message.content,
    createdAt: timestamp,
  };
  inboundDebug.log('parse', 'MATCHED result=', result);
  return result;
};

/** 检测是否为系统心跳消息（sourceKind='system' + sourceId='heartbeat'） */
const isHeartbeatMessage = (message: ConversationMessageView): boolean =>
  message.role === 'system' &&
  message.sourceKind === 'system' &&
  message.sourceId === 'heartbeat';

const createProjectedTurn = (
  message: ConversationMessageView,
  agentName: string,
): ChatTurn => {
  const timestamp = toTimestamp(message.createdAt);
  const turnId = message.turnId || message.runId || message.messageId;
  const isUser = message.role === 'user';
  const isHeartbeat = isHeartbeatMessage(message);
  const quotedMessage = parseAgentQuotedMessage(message, timestamp);
  const isInboundAgentMessage = Boolean(quotedMessage);
  inboundDebug.log(
    'project',
    'role=',
    message.role,
    'sourceKind=',
    message.sourceKind,
    'sourceName=',
    message.sourceName,
    'isInbound=',
    isInboundAgentMessage,
    'isHeartbeat=',
    isHeartbeat,
  );
  const sourceName = message.sourceName || agentName;

  return {
    turnId,
    source: {
      sourceId: message.sourceId || 'agent',
      sourceType: isHeartbeat
        ? 'system_command'
        : isInboundAgentMessage
          ? 'agent'
          : 'agent',
      displayName: isHeartbeat ? '系统心跳' : sourceName,
      avatarEmoji: isHeartbeat ? '💓' : '🤖',
      avatarColor: isHeartbeat ? '#1677ff' : '#7c3aed',
    },
    userMessage: {
      id: isUser ? message.messageId : `${turnId}:placeholder-user`,
      text: isUser && !isInboundAgentMessage ? message.content : '',
      timestamp,
      status: isUser ? toUserMessageStatus(message.status) : 'success',
      metadata: isUser ? message.metadata : undefined,
    },
    assistant: {
      id: isUser ? `${turnId}:placeholder-assistant` : message.messageId,
      status:
        isUser || isInboundAgentMessage || isHeartbeat
          ? 'success'
          : toAssistantStatus(message.status),
      timelineItems:
        isUser || isInboundAgentMessage || isHeartbeat
          ? []
          : toTimelineItems(message.processItems),
      answerMarkdown:
        (isUser || isInboundAgentMessage) && !isHeartbeat
          ? ''
          : message.content,
      isStreaming:
        !isUser &&
        !isInboundAgentMessage &&
        !isHeartbeat &&
        message.status === 'streaming',
      renderMode: isHeartbeat
        ? ('heartbeat' as const)
        : isInboundAgentMessage
          ? ('inbound' as const)
          : 'structured',
      quotedMessage,
    },
  };
};

const createActiveRunTurn = (
  activeRun: ActiveRunView,
  agentName: string,
  answerMarkdown?: string,
): ChatTurn => {
  const timestamp = toTimestamp(activeRun.startedAt || activeRun.updatedAt);
  return {
    turnId: activeRun.runId,
    source: {
      sourceId: activeRun.agentId || 'agent',
      sourceType: 'agent',
      displayName: agentName,
      avatarEmoji: '🤖',
      avatarColor: '#7c3aed',
    },
    userMessage: {
      id: `${activeRun.runId}:active-user-placeholder`,
      text: '',
      timestamp,
      status: 'success',
    },
    assistant: {
      id: `${activeRun.runId}:active-assistant`,
      status: toActiveRunAssistantStatus(activeRun.status),
      timelineItems: toTimelineItems(
        activeRun.outputSnapshot.processItems ?? [],
      ),
      answerMarkdown: answerMarkdown ?? activeRun.outputSnapshot.markdown,
      isStreaming: isActiveRunStreaming(activeRun.status),
      renderMode: 'structured',
    },
  };
};

const mergeProjectedMessageIntoTurns = (
  turns: ChatTurn[],
  message: ConversationMessageView,
  agentName: string,
) => {
  const projected = createProjectedTurn(message, agentName);
  if (message.role !== 'agent' || !message.runId) {
    turns.push(projected);
    return;
  }

  const previous = turns[turns.length - 1];
  if (
    !previous ||
    previous.turnId !== projected.turnId ||
    previous.assistant.answerMarkdown
  ) {
    turns.push(projected);
    return;
  }

  previous.assistant = {
    ...projected.assistant,
    quotedMessage:
      previous.assistant.quotedMessage ?? projected.assistant.quotedMessage,
  };
  previous.source = projected.source;
};

const buildProjectedTurns = (
  conversationView: AgentConversationView | null | undefined,
  agentName: string,
): ChatTurn[] => {
  if (!conversationView?.messages.length) return [];
  const projectedTurns: ChatTurn[] = [];
  for (const message of conversationView.messages) {
    // 跳过非心跳的系统消息（心跳消息允许通过以渲染为 heartbeat role）
    if (message.role === 'system' && !isHeartbeatMessage(message)) continue;
    mergeProjectedMessageIntoTurns(projectedTurns, message, agentName);
  }
  return projectedTurns;
};

const isPendingLocalTurn = (turn: ChatTurn): boolean =>
  turn.userMessage.status === 'sending' ||
  turn.assistant.isStreaming ||
  turn.assistant.status === 'thinking' ||
  turn.assistant.status === 'executing' ||
  turn.assistant.status === 'streaming';

const SERVER_PROJECTION_CLOCK_SKEW_MS = 1_000;
const bottomScrollControlsStyle: React.CSSProperties = {
  position: 'fixed',
};

const hasProjectedUserTurn = (
  projectedTurns: ChatTurn[],
  localTurn: ChatTurn,
): boolean => {
  const localText = localTurn.userMessage.text.trim();
  if (!localText) return false;
  const localTimestamp = localTurn.userMessage.timestamp;
  return projectedTurns.some(
    (turn) =>
      turn.userMessage.id === localTurn.userMessage.id ||
      (turn.userMessage.text.trim() === localText &&
        Math.abs(turn.userMessage.timestamp - localTimestamp) <=
          SERVER_PROJECTION_CLOCK_SKEW_MS),
  );
};

const mergeLocalTurnsAwaitingProjection = (
  projectedTurns: ChatTurn[],
  localTurns: ChatTurn[],
): ChatTurn[] => {
  let merged = projectedTurns;

  for (const localTurn of localTurns) {
    const projectedUserIndex = merged.findIndex((projectedTurn) =>
      hasProjectedUserTurn([projectedTurn], localTurn),
    );

    if (isPendingLocalTurn(localTurn)) {
      if (projectedUserIndex < 0) {
        merged = [...merged, localTurn];
        continue;
      }

      const projectedTurn = merged[projectedUserIndex];
      const projectedAssistantIsTerminal =
        Boolean(projectedTurn.assistant.answerMarkdown.trim()) &&
        !isPendingLocalTurn(projectedTurn);
      if (!projectedAssistantIsTerminal) {
        // Materializing the user message must not hide the live local assistant
        // state. This matters when a later system command temporarily becomes
        // the newest canonical projection and activeRun is unavailable even
        // though the original Agent Turn (and its subagents) is still running.
        merged = merged.map((candidate, index) =>
          index === projectedUserIndex
            ? {
                ...candidate,
                turnId: localTurn.turnId,
                source: localTurn.source ?? candidate.source,
                assistant: localTurn.assistant,
              }
            : candidate,
        );
      }
      continue;
    }

    const localAnswer = localTurn.assistant.answerMarkdown.trim();
    if (!localAnswer || projectedUserIndex < 0) continue;

    const terminalAlreadyProjected = merged.some(
      (projectedTurn) =>
        projectedTurn.assistant.answerMarkdown.trim() === localAnswer,
    );
    if (terminalAlreadyProjected) continue;

    // The SSE projection is authoritative for the just-completed local Turn
    // while the canonical conversation read model is still user-only. Overlay
    // the terminal assistant state on the matching projected user Turn instead
    // of hiding it until a refresh materializes the assistant message row.
    merged = merged.map((projectedTurn, index) =>
      index === projectedUserIndex
        ? {
            ...projectedTurn,
            turnId: localTurn.turnId,
            source: localTurn.source ?? projectedTurn.source,
            assistant: localTurn.assistant,
          }
        : projectedTurn,
    );
  }

  return merged;
};

const isActiveRunCoveredByLocalTerminal = (
  activeRun: ActiveRunView | null,
  localTurns: ChatTurn[],
): boolean => {
  if (!activeRun?.commandClientId) return false;
  return localTurns.some(
    (turn) =>
      turn.userMessage.id === activeRun.commandClientId &&
      !isPendingLocalTurn(turn) &&
      Boolean(turn.assistant.answerMarkdown.trim()),
  );
};

const ACTIVE_RUN_PENDING_ATTACH_SKEW_MS = 1_000;

const canAttachActiveRunToPendingTurn = (
  activeRun: ActiveRunView,
  pendingTurn: ChatTurn,
): boolean => {
  if (
    activeRun.commandClientId &&
    activeRun.commandClientId === pendingTurn.userMessage.id
  ) {
    return true;
  }
  const activeStartedAt = toTimestamp(
    activeRun.startedAt || activeRun.updatedAt,
  );
  if (activeStartedAt <= 0) return false;
  return (
    activeStartedAt >=
    pendingTurn.userMessage.timestamp - ACTIVE_RUN_PENDING_ATTACH_SKEW_MS
  );
};

const findActiveRunPendingTurnIndex = (
  turns: ChatTurn[],
  activeRun: ActiveRunView,
): number => {
  if (!activeRun.commandClientId) return -1;
  return turns.findIndex(
    (turn) =>
      isPendingLocalTurn(turn) &&
      !turn.assistant.answerMarkdown.trim() &&
      turn.userMessage.id === activeRun.commandClientId,
  );
};

const mergeActiveRunIntoTurns = (
  turns: ChatTurn[],
  activeRun: AgentConversationView['activeRun'],
  agentName: string,
  activeRunMarkdown?: string,
): ChatTurn[] => {
  if (!activeRun) return turns;
  const activeTurn = createActiveRunTurn(
    activeRun,
    agentName,
    activeRunMarkdown,
  );
  const existingIndex = turns.findIndex(
    (turn) => turn.turnId === activeRun.runId,
  );
  if (existingIndex >= 0) {
    return turns.map((turn, index) =>
      index === existingIndex
        ? {
            ...turn,
            assistant: activeTurn.assistant,
            source: activeTurn.source,
          }
        : turn,
    );
  }

  const matchingPendingIndex = findActiveRunPendingTurnIndex(turns, activeRun);
  if (matchingPendingIndex >= 0) {
    return turns.map((turn, index) =>
      index === matchingPendingIndex
        ? {
            ...turn,
            assistant: activeTurn.assistant,
            source: activeTurn.source,
          }
        : turn,
    );
  }

  const lastTurn = turns[turns.length - 1];
  if (
    lastTurn &&
    isPendingLocalTurn(lastTurn) &&
    !lastTurn.assistant.answerMarkdown.trim() &&
    canAttachActiveRunToPendingTurn(activeRun, lastTurn)
  ) {
    return [
      ...turns.slice(0, -1),
      {
        ...lastTurn,
        assistant: activeTurn.assistant,
        source: activeTurn.source,
      },
    ];
  }

  return [...turns, activeTurn];
};

const MessageList: React.FC<MessageListProps> = ({
    turns,
  sessionId,
  workspaceId,
  agentId,
  selectedAgent,
  error,
  historyLoading,
  loadingMore,
  hasMoreMessages,
  onClearError,
  onLoadMore,
  formatTime,
  onDeleteTurn,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
  onPinnedQuote,
  messageListRef,
  listEndRef,
  conversationView,
  currentUser,
  viewportScrollIntent,
  onViewportScrollIntentHandled,
}) => {
  const { styles } = useChatStyles();
  const [diagCopied, setDiagCopied] = useState(false);
  const activeRun = conversationView?.activeRun ?? null;
  const activeRunMarkdownCacheRef = useRef<{ runId: string; markdown: string } | null>(null);
  const activeRunMarkdown = useMemo(() => {
    if (!activeRun) return undefined;
    const incoming = activeRun.outputSnapshot.markdown ?? '';
    const cached = activeRunMarkdownCacheRef.current;
    // 同一次 run：保留较长版本（流式累积场景 incoming 单调增长）
    if (cached && cached.runId === activeRun.runId) {
      const stable = incoming.length >= cached.markdown.length ? incoming : cached.markdown;
      activeRunMarkdownCacheRef.current = { runId: activeRun.runId, markdown: stable };
      return stable;
    }
    // 新 run：重置缓存
    activeRunMarkdownCacheRef.current = { runId: activeRun.runId, markdown: incoming };
    return incoming;
  }, [activeRun]);
  const projectedTurns = useMemo(
    () =>
      buildProjectedTurns(conversationView, selectedAgent?.name || 'Pudding'),
    [conversationView, selectedAgent?.name],
  );
  const hasProjectedConversation = Boolean(
    conversationView && (projectedTurns.length > 0 || activeRun),
  );
  const visibleTurns = useMemo(() => {
    if (!hasProjectedConversation) return turns;
    const activeRunForProjection = isActiveRunCoveredByLocalTerminal(
      activeRun,
      turns,
    )
      ? null
      : activeRun;
    const merged = mergeActiveRunIntoTurns(
      mergeLocalTurnsAwaitingProjection(projectedTurns, turns),
      activeRunForProjection,
      selectedAgent?.name || 'Pudding',
      activeRunMarkdown,
    );
    if (merged.length === 0 && turns.length > 0) return turns;
    return merged;
  }, [
    hasProjectedConversation,
    projectedTurns,
    turns,
    activeRun,
    selectedAgent?.name,
    activeRunMarkdown,
  ]);

  const projection = useMemo(
    () =>
      buildVirtualMessageItems({
        turns: visibleTurns,
        agentName: selectedAgent?.name || 'Pudding',
        sessionId,
        hasMoreBefore: hasMoreMessages,
        currentUser,
      }),
    [visibleTurns, selectedAgent?.name, sessionId, hasMoreMessages, currentUser],
  );

  const viewport = useMessageViewportRuntime({
    items: projection.items,
    hasMoreBefore: hasMoreMessages,
    loadingBefore: loadingMore,
    onRequestLoadBefore: () => onLoadMore(),
  });

  useEffect(() => {
    if (viewportScrollIntent && viewportScrollIntent.type !== 'none') {
      viewport.applyIntent(viewportScrollIntent);
      onViewportScrollIntentHandled?.();
    }
  }, [viewport, viewportScrollIntent, onViewportScrollIntentHandled]);

  const renderProjectionItem = (item: VirtualMessageItem) => {
    if (item.kind === 'loader') {
      return (
        <div
          style={{
            textAlign: 'center',
            padding: 8,
            cursor: loadingMore ? 'default' : 'pointer',
            color: 'var(--ant-color-primary)',
          }}
          onClick={
            loadingMore ? undefined : viewport.requestLoadBefore
          }
        >
          {loadingMore ? <Spin size="small" /> : '加载更多历史消息'}
        </div>
      );
    }

    return (
      <MessageStream
        turns={[{
          turnId: item.block.turnId,
          source: item.block.role === 'agent' ? {
            sourceId: item.block.agentId || 'agent',
            sourceType: item.block.sourceType || 'agent',
            displayName: item.block.agentName || selectedAgent?.name || 'Pudding',
            avatarEmoji: item.block.agentAvatarEmoji || '🤖',
            avatarColor: item.block.agentAvatarColor || '#7c3aed',
            avatarUrl: item.block.agentAvatarUrl,
          } : undefined,
          userMessage: item.block.role === 'user' ? {
            id: item.block.id,
            text: item.block.content,
            timestamp: item.block.createdAt,
            status: item.block.status === 'sending' ? 'sending' : 'success',
            metadata: item.block.metadata,
          } : { id: '', text: '', timestamp: item.block.createdAt, status: 'success' },
          assistant: item.block.role === 'agent' ? {
            id: item.block.id,
            status: item.block.status === 'streaming' ? 'streaming' : item.block.status === 'error' ? 'error' : item.block.status === 'cancelled' ? 'cancelled' : item.block.status === 'thinking' ? 'thinking' : 'success',
            timelineItems: item.block.processItems ?? [],
            answerMarkdown: item.block.content,
            isStreaming: item.block.isStreaming ?? false,
            renderMode: 'structured',
            usage: item.block.usage,
            quotedMessage: item.block.quotedMessage,
          } : { id: '', status: 'success', timelineItems: [], answerMarkdown: '', isStreaming: false, renderMode: 'structured' },
        }]}
        sessionId={sessionId}
        workspaceId={workspaceId}
        agentName={selectedAgent?.name || 'Pudding'}
        defaultAvatarUrl={selectedAgent?.avatarUrl}
        currentUser={currentUser}
        formatTime={formatTime}
        onContextMenu={onContextMenu}
        onRerunTurn={onRerunTurn}
        onPinTurn={onPinTurn}
        onDeleteTurn={onDeleteTurn}
      />
    );
  };

  return (
    <div
      className={styles.messageList}
      ref={(node) => {
        viewport.parentRef.current = node;
        if (typeof messageListRef === 'object') {
          (messageListRef as React.MutableRefObject<HTMLDivElement | null>).current = node;
        }
      }}
      onScroll={viewport.onScroll}
      data-testid="chat-message-list"
    >
      {(() => {
        const emptyStateMode: ChatEmptyStateMode | null = (() => {
          if (historyLoading || projection.items.length > 0 || activeRun)
            return null;
          if (error) return 'error';
          if (!agentId) return 'no-agent';
          return 'ready';
        })();
        return emptyStateMode ? (
          <ChatEmptyState
            mode={emptyStateMode}
            errorText={error ?? undefined}
            onRetry={onClearError}
            onSuggestionClick={(text) => {
              window.dispatchEvent(
                new CustomEvent('pudding:chat:suggestion', { detail: text }),
              );
            }}
          />
        ) : null;
      })()}
      {historyLoading && turns.length === 0 && (
        <div className={styles.historyLoading}>
          <Skeleton active avatar paragraph={{ rows: 4 }} style={{ padding: 16 }} />
        </div>
      )}
      {historyLoading && turns.length > 0 && (
        <div className={styles.historyLoading}>
          <Spin />
        </div>
      )}
      {/* 虚拟滚动容器 */}
      {projection.items.length > 0 && (
        <div
          ref={viewport.contentRef}
          data-testid="chat-message-viewport-content"
          data-virtualized={viewport.virtualizationEnabled ? 'true' : 'false'}
          style={
            viewport.virtualizationEnabled
              ? {
                  height: `${viewport.totalSize}px`,
                  width: '100%',
                  position: 'relative',
                }
              : { width: '100%', position: 'relative' }
          }
        >
          {viewport.virtualizationEnabled
            ? viewport.virtualRows.map((virtualRow) => {
                const item = projection.items[virtualRow.index];
                if (!item) return null;
                return (
                  <div
                    key={virtualRow.key}
                    data-index={virtualRow.index}
                    data-viewport-item-id={item.id}
                    ref={viewport.virtualizer.measureElement}
                    style={{
                      position: 'absolute',
                      top: 0,
                      left: 0,
                      width: '100%',
                      transform: `translateY(${virtualRow.start}px)`,
                    }}
                  >
                    {renderProjectionItem(item)}
                  </div>
                );
              })
            : projection.items.map((item, index) => (
                <div
                  key={item.id}
                  data-index={index}
                  data-viewport-item-id={item.id}
                  style={{ width: '100%', position: 'relative', display: 'flow-root' }}
                >
                  {renderProjectionItem(item)}
                </div>
              ))}
        </div>
      )}
            {error && (
        <Alert
          type="error"
          message={error}
          closable
          onClose={onClearError}
          className={styles.errorAlert}
          action={
            <Button
              size="small"
              type="link"
              onClick={() => {
                const payload = {
                  timestamp: new Date().toISOString(),
                  userAgent: navigator.userAgent,
                  url: window.location.href,
                  sessionId: sessionId ?? null,
                  agentId: agentId ?? null,
                  turnsCount: turns.length,
                  lastTurnStatus:
                    turns.length > 0
                      ? (turns[turns.length - 1] as { status?: string }).status ?? null
                      : null,
                  error,
                  recentPerfEvents: getPerfEvents().slice(-5),
                };
                navigator.clipboard
                  .writeText(JSON.stringify(payload, null, 2))
                  .then(() => {
                    setDiagCopied(true);
                    setTimeout(() => setDiagCopied(false), 2000);
                  });
              }}
            >
              {diagCopied ? '✓ 已复制' : '复制诊断信息'}
            </Button>
          }
        />
      )}
      {/* 底部滚动控制 */}
      {projection.items.length > 0 && (
        <div
          data-testid="chat-bottom-scroll-controls"
          className={styles.messageViewportControls}
          style={bottomScrollControlsStyle}
        >
          {onPinnedQuote && (
            <PinnedMessageButton
              onQuote={onPinnedQuote}
              className={styles.messageViewportControlButton}
            />
          )}
          <Tooltip title={viewport.state.followMode === 'pinned' ? '取消贴底跟随' : '开启贴底跟随'}>
            <Button
              type={viewport.state.followMode === 'pinned' ? 'primary' : 'default'}
              icon={<VerticalAlignBottomOutlined />}
              onClick={() => viewport.setPinnedBottom(viewport.state.followMode !== 'pinned')}
              aria-label={viewport.state.followMode === 'pinned' ? '取消贴底跟随' : '开启贴底跟随'}
              className={styles.messageViewportControlButton}
            />
          </Tooltip>
          {viewport.state.showBottomButton && (
            <Tooltip title="回到底部">
              <Badge dot offset={[-3, 3]}>
                <Button
                  type="default"
                  icon={<ArrowDownOutlined />}
                  onClick={() => viewport.scrollToBottom({ behavior: 'smooth', reason: 'manual-bottom' })}
                  aria-label="回到底部"
                  className={styles.messageViewportControlButton}
                />
              </Badge>
            </Tooltip>
          )}
        </div>
      )}
      <div ref={listEndRef} />
    </div>
  );
};

export default React.memo(MessageList);

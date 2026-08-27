// ── MessageRow：单条消息行（路由到 User/Agent/Heartbeat 气泡）──
import { HeartOutlined } from '@ant-design/icons';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import type { ExecutionFlowProjection } from '../projections/executionFlowProjector';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import type { ChatMessageBlock, ParentDelegationActivity } from '../types';
import AgentMessageBubble from './AgentMessageBubble';
import FocusViewRow, { type FocusViewRowTone } from './FocusViewRow';
import MessageItem from './MessageItem';
import { getCurrentRunActivity, sanitizeProcessText } from './processPreview';
import type { TranscriptMode } from './TranscriptModeSwitch';
import UserMessageBubble from './UserMessageBubble';

interface MessageRowProps {
  block: ChatMessageBlock;
  sessionId?: string | null;
  /** 当前工作空间 ID，用于用户视觉消息的图片加载 */
  workspaceId?: string;
  defaultAvatarUrl?: string;
  formatTime: (ts: number) => string;
  onContextMenu?: (
    e: React.MouseEvent,
    turnId: string,
    role: 'user' | 'assistant',
    content: string,
  ) => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
  onDeleteTurn?: (turnId: string) => void;
  parentDelegationActivity?: ParentDelegationActivity;
  /** P0#2：转录视图分级 */
  transcriptMode?: TranscriptMode;
  onTranscriptModeChange?: (mode: TranscriptMode) => void;
  /** P2#8：Focus view 单行折叠模式 */
  focusView?: boolean;
  /** 当前 Turn 的 canonical 投影；未变化的 Turn 保持对象引用稳定。 */
  executionFlowProjection?: ExecutionFlowProjection;
  /** 进入消息视口附近时上报 turnId，驱动有界懒水合。 */
  onTurnVisible?: (turnId: string) => void;
}

const optionalRecordEquals = (
  previous: object | undefined,
  next: object | undefined,
): boolean => {
  if (previous === next) return true;
  if (!previous || !next) return false;
  const previousEntries = Object.entries(previous);
  const nextEntries = Object.entries(next);
  return (
    previousEntries.length === nextEntries.length &&
    previousEntries.every(([key, value]) =>
      Object.is(value, (next as Record<string, unknown>)[key]),
    )
  );
};

const optionalStringArrayEquals = (
  previous: string[] | undefined,
  next: string[] | undefined,
): boolean =>
  previous === next ||
  Boolean(
    previous &&
      next &&
      previous.length === next.length &&
      previous.every((value, index) => value === next[index]),
  );

const processItemsEqual = (
  previous: ChatMessageBlock['processItems'],
  next: ChatMessageBlock['processItems'],
): boolean =>
  previous === next ||
  Boolean(
    previous &&
      next &&
      previous.length === next.length &&
      previous.every((item, index) => {
        const candidate = next[index];
        return (
          item.id === candidate.id &&
          item.type === candidate.type &&
          item.status === candidate.status &&
          item.timestamp === candidate.timestamp &&
          item.collapsed === candidate.collapsed &&
          item.text === candidate.text &&
          item.name === candidate.name &&
          item.arguments === candidate.arguments &&
          item.output === candidate.output &&
          item.exitCode === candidate.exitCode &&
          item.message === candidate.message
        );
      }),
  );

const messageBlockEquals = (
  previous: ChatMessageBlock,
  next: ChatMessageBlock,
): boolean =>
  previous === next ||
  (previous.id === next.id &&
    previous.turnId === next.turnId &&
    previous.role === next.role &&
    previous.content === next.content &&
    previous.status === next.status &&
    previous.createdAt === next.createdAt &&
    previous.modality === next.modality &&
    previous.visionArtifactId === next.visionArtifactId &&
    optionalStringArrayEquals(
      previous.visionArtifactIds,
      next.visionArtifactIds,
    ) &&
    previous.userName === next.userName &&
    previous.userAvatarUrl === next.userAvatarUrl &&
    previous.agentId === next.agentId &&
    previous.sourceType === next.sourceType &&
    previous.agentName === next.agentName &&
    previous.agentAvatarUrl === next.agentAvatarUrl &&
    previous.agentAvatarColor === next.agentAvatarColor &&
    previous.agentAvatarEmoji === next.agentAvatarEmoji &&
    previous.processMessageId === next.processMessageId &&
    previous.groupedWithPrevious === next.groupedWithPrevious &&
    previous.isStreaming === next.isStreaming &&
    optionalRecordEquals(previous.metadata, next.metadata) &&
    processItemsEqual(previous.processItems, next.processItems) &&
    optionalRecordEquals(previous.processSummary, next.processSummary) &&
    optionalRecordEquals(previous.usage, next.usage) &&
    optionalRecordEquals(previous.quotedMessage, next.quotedMessage) &&
    optionalRecordEquals(previous.approvalCard, next.approvalCard) &&
    optionalRecordEquals(previous.planCard, next.planCard));

export const areMessageRowPropsEqual = (
  previous: MessageRowProps,
  next: MessageRowProps,
): boolean =>
  messageBlockEquals(previous.block, next.block) &&
  previous.sessionId === next.sessionId &&
  previous.workspaceId === next.workspaceId &&
  previous.defaultAvatarUrl === next.defaultAvatarUrl &&
  previous.formatTime === next.formatTime &&
  previous.onContextMenu === next.onContextMenu &&
  previous.onRerunTurn === next.onRerunTurn &&
  previous.onPinTurn === next.onPinTurn &&
  previous.onDeleteTurn === next.onDeleteTurn &&
  previous.transcriptMode === next.transcriptMode &&
  previous.onTranscriptModeChange === next.onTranscriptModeChange &&
  (previous.focusView ?? false) === (next.focusView ?? false) &&
  previous.executionFlowProjection === next.executionFlowProjection &&
  previous.onTurnVisible === next.onTurnVisible &&
  optionalRecordEquals(
    previous.parentDelegationActivity,
    next.parentDelegationActivity,
  );

// ── P2#8 Focus view 单行摘要 ─────────────────────────────────
const FOCUS_SUMMARY_MAX_LENGTH = 120;

const getFocusViewTone = (block: ChatMessageBlock): FocusViewRowTone => {
  if (block.status === 'error' || block.status === 'cancelled') return 'error';
  if (
    block.isStreaming ||
    block.status === 'thinking' ||
    block.status === 'streaming'
  ) {
    return 'running';
  }
  return 'done';
};

const getFocusViewSummary = (block: ChatMessageBlock): string => {
  if (block.role === 'user') {
    if (block.modality === 'voice') return '语音消息';
    if (block.modality === 'camera' || block.modality === 'image') {
      return '图片消息';
    }
    return (
      sanitizeProcessText(block.content, {
        maxLength: FOCUS_SUMMARY_MAX_LENGTH,
      }) || '（空消息）'
    );
  }

  const isRunning =
    block.isStreaming ||
    block.status === 'thinking' ||
    block.status === 'streaming';
  if (isRunning) {
    // 运行中优先显示当前真实活动（工具调用 / 子代理 / 工具结果处理）
    const activity = getCurrentRunActivity(block.processItems, block.status);
    if (activity && activity.kind !== 'thinking' && activity.title) {
      return activity.title;
    }
    if (block.isStreaming && block.content.trim()) {
      return `${sanitizeProcessText(block.content, {
        maxLength: FOCUS_SUMMARY_MAX_LENGTH,
      })}…`;
    }
    return '正在处理…';
  }

  if (block.status === 'error' || block.status === 'cancelled') {
    return (
      sanitizeProcessText(block.content, {
        maxLength: FOCUS_SUMMARY_MAX_LENGTH,
      }) || '任务失败'
    );
  }

  if (block.quotedMessage) {
    return `引用 ${block.quotedMessage.sourceName}：${sanitizeProcessText(
      block.quotedMessage.content,
      { maxLength: 60 },
    )}`;
  }

  return (
    sanitizeProcessText(block.content, {
      maxLength: FOCUS_SUMMARY_MAX_LENGTH,
    }) || '（无文本回复）'
  );
};

const MessageRow: React.FC<MessageRowProps> = ({
  block,
  sessionId,
  workspaceId,
  defaultAvatarUrl,
  formatTime,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
  onDeleteTurn,
  parentDelegationActivity,
  transcriptMode,
  onTranscriptModeChange,
  focusView = false,
  executionFlowProjection,
  onTurnVisible,
}) => {
  const { styles, cx } = useChatMessageStyles();
  const agentRowRef = useRef<HTMLDivElement | null>(null);
  const registeredVisibleTurnRef = useRef<string | null>(null);
  // P2#8：Focus view 单行展开状态（折叠/展开同一行内切换，保持完整内容在同一
  // 虚拟行内渲染，避免折叠展开引发整列重渲染）。
  const [focusExpanded, setFocusExpanded] = useState(false);
  useEffect(() => {
    const turnId = block.turnId;
    if (
      block.role !== 'agent' ||
      !turnId ||
      !onTurnVisible ||
      registeredVisibleTurnRef.current === turnId
    ) {
      return;
    }

    const registerVisibleTurn = () => {
      if (registeredVisibleTurnRef.current === turnId) return;
      registeredVisibleTurnRef.current = turnId;
      onTurnVisible(turnId);
    };
    const row = agentRowRef.current;
    if (!row || typeof IntersectionObserver === 'undefined') {
      // JSDOM / 旧 WebView 降级：保持功能可用；产品 WebView2 走真实近视口观察。
      registerVisibleTurn();
      return;
    }

    const root = row.closest<HTMLElement>('[data-testid="chat-message-list"]');
    const observer = new IntersectionObserver(
      (entries) => {
        if (!entries.some((entry) => entry.isIntersecting)) return;
        registerVisibleTurn();
        observer.disconnect();
      },
      {
        root,
        // 提前一屏水合，既避免滚入时空壳，又不把正常流中的全部历史误判为可见。
        rootMargin: '600px 0px',
      },
    );
    observer.observe(row);
    return () => observer.disconnect();
  }, [block.role, block.turnId, onTurnVisible]);
  const focusSummary = useMemo(() => getFocusViewSummary(block), [block]);
  const focusTone = useMemo(() => getFocusViewTone(block), [block]);

  const renderUserBubble = () => (
    <UserMessageBubble
      content={block.content}
      createdAt={block.createdAt}
      status={block.status}
      modality={block.modality}
      visionArtifactId={block.visionArtifactId}
      visionArtifactIds={block.visionArtifactIds}
      workspaceId={workspaceId}
      userName={block.userName}
      userAvatarUrl={block.userAvatarUrl}
      metadata={block.metadata}
      formatTime={formatTime}
      onContextMenu={(e) =>
        onContextMenu?.(e, block.turnId, 'user', block.content)
      }
    />
  );

  const renderAgentBubble = () => (
    <AgentMessageBubble
      id={block.id}
      content={block.content}
      status={block.status}
      createdAt={block.createdAt}
      agentName={block.agentName || 'Pudding'}
      agentAvatarEmoji={block.agentAvatarEmoji}
      agentAvatarColor={block.agentAvatarColor}
      agentAvatarUrl={block.agentAvatarUrl || defaultAvatarUrl}
      processItems={block.processItems}
      processSummary={block.processSummary}
      processMessageId={block.processMessageId}
      workspaceId={workspaceId}
      agentId={block.agentId}
      usage={block.usage}
      quotedMessage={block.quotedMessage}
      groupedWithPrevious={block.groupedWithPrevious}
      isStreaming={block.isStreaming}
      formatTime={formatTime}
      turnId={block.turnId}
      sessionId={sessionId}
      onContextMenu={onContextMenu}
      onRerun={onRerunTurn ? () => onRerunTurn(block.turnId) : undefined}
      onPin={onPinTurn ? () => onPinTurn(block.turnId) : undefined}
      onDelete={onDeleteTurn ? () => onDeleteTurn(block.turnId) : undefined}
      parentDelegationActivity={parentDelegationActivity}
      transcriptMode={transcriptMode}
      onTranscriptModeChange={onTranscriptModeChange}
      executionFlowProjection={executionFlowProjection}
    />
  );

  if (block.role === 'user') {
    if (focusView) {
      return (
        <div className={cx(styles.messageRow, styles.messageRowUser)}>
          <FocusViewRow
            role="user"
            name={block.userName || '我'}
            timeText={formatTime(block.createdAt)}
            summary={focusSummary}
            tone={focusTone}
            expanded={focusExpanded}
            onToggle={() => setFocusExpanded((value) => !value)}
          >
            {renderUserBubble()}
          </FocusViewRow>
        </div>
      );
    }
    return (
      <div className={cx(styles.messageRow, styles.messageRowUser)}>
        {renderUserBubble()}
      </div>
    );
  }

  if (block.role === 'agent') {
    const rowClassName = cx(
      styles.messageRow,
      styles.messageRowAgent,
      block.groupedWithPrevious && styles.messageRowGrouped,
    );
    if (focusView) {
      return (
        <div
          ref={agentRowRef}
          className={rowClassName}
          data-agent={block.agentName}
          data-streaming={block.isStreaming ? 'true' : undefined}
        >
          <FocusViewRow
            role="agent"
            name={block.agentName || 'Pudding'}
            avatarEmoji={block.agentAvatarEmoji}
            avatarColor={block.agentAvatarColor}
            avatarUrl={block.agentAvatarUrl || defaultAvatarUrl}
            timeText={formatTime(block.createdAt)}
            summary={focusSummary}
            tone={focusTone}
            expanded={focusExpanded}
            onToggle={() => setFocusExpanded((value) => !value)}
          >
            {renderAgentBubble()}
          </FocusViewRow>
        </div>
      );
    }
    return (
      <div
        ref={agentRowRef}
        className={rowClassName}
        data-agent={block.agentName}
        data-streaming={block.isStreaming ? 'true' : undefined}
      >
        {renderAgentBubble()}
      </div>
    );
  }

  if (block.role === 'heartbeat') {
    return (
      <div className={cx(styles.messageRow, styles.messageRowHeartbeat)}>
        <div className={styles.heartbeatContainer}>
          <div className={styles.heartbeatHeader}>
            <HeartOutlined className={styles.heartbeatIcon} />
            <span className={styles.heartbeatLabel}>系统心跳</span>
          </div>
          <div className={styles.heartbeatBody}>
            <MessageItem markdownText={block.content} />
          </div>
        </div>
      </div>
    );
  }

  // system 消息暂不渲染
  return null;
};

export default React.memo(MessageRow, areMessageRowPropsEqual);

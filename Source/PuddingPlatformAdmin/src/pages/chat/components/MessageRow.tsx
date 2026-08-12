// ── MessageRow：单条消息行（路由到 User/Agent/Heartbeat 气泡）──
import { HeartOutlined } from '@ant-design/icons';
import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import type { ChatMessageBlock, ParentDelegationActivity } from '../types';
import AgentMessageBubble from './AgentMessageBubble';
import MessageItem from './MessageItem';
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
    optionalRecordEquals(previous.approvalCard, next.approvalCard));

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
  optionalRecordEquals(
    previous.parentDelegationActivity,
    next.parentDelegationActivity,
  );

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
}) => {
  const { styles, cx } = useChatMessageStyles();

  if (block.role === 'user') {
    return (
      <div className={cx(styles.messageRow, styles.messageRowUser)}>
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
          formatTime={formatTime}
          onContextMenu={(e) =>
            onContextMenu?.(e, block.turnId, 'user', block.content)
          }
        />
      </div>
    );
  }

  if (block.role === 'agent') {
    return (
      <div
        className={cx(
          styles.messageRow,
          styles.messageRowAgent,
          block.groupedWithPrevious && styles.messageRowGrouped,
        )}
        data-agent={block.agentName}
        data-streaming={block.isStreaming ? 'true' : undefined}
      >
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
        />
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

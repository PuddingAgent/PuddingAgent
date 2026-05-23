// ── AgentMessageBubble：Agent 消息气泡（左对齐）─────────────
import React from 'react';
import { useChatStyles } from '../styles';
import type { TimelineItem } from '../types';
import type { TokenUsageDto } from '@/services/platform/api';
import AgentAvatar from './AgentAvatar';
import MessageItem from './MessageItem';
import MessageProcessSummary from './MessageProcessSummary';
import MessageActions from './MessageActions';
import { useTypewriterStreaming } from '../hooks/useTypewriterStreaming';

interface AgentMessageBubbleProps {
  id: string;
  content: string;
  status: string;
  createdAt: number;
  agentName: string;
  agentAvatarEmoji?: string;
  agentAvatarColor?: string;
  agentAvatarUrl?: string;
  processItems?: TimelineItem[];
  usage?: TokenUsageDto;
  groupedWithPrevious?: boolean;
  isStreaming?: boolean;
  formatTime: (ts: number) => string;
  onContextMenu?: (e: React.MouseEvent, turnId: string, role: 'assistant') => void;
  onRerun?: () => void;
  onPin?: () => void;
  onDelete?: () => void;
  turnId?: string;
}

const AgentMessageBubble: React.FC<AgentMessageBubbleProps> = ({
  id,
  content,
  status,
  createdAt,
  agentName,
  agentAvatarEmoji,
  agentAvatarColor,
  agentAvatarUrl,
  processItems,
  usage,
  groupedWithPrevious,
  isStreaming,
  formatTime,
  onContextMenu,
  onRerun,
  onPin,
  onDelete,
  turnId,
}) => {
  const { styles, cx } = useChatStyles();
  const [showActions, setShowActions] = React.useState(false);

  const isError = status === 'error' || status === 'cancelled';
  const typewriter = useTypewriterStreaming({
    text: content,
    isStreaming: Boolean(isStreaming),
    tickMs: 28,
    maxLagChars: 240,
  });
  const hasAnswerContent = content.trim().length > 0 ||
    typewriter.stableMarkdown.length > 0 ||
    typewriter.liveText.length > 0;
  const shouldRenderAnswerBubble = hasAnswerContent || (!isStreaming && content.trim().length > 0);
  const shouldShowPreAnswerThinking = Boolean(isStreaming) && !hasAnswerContent;
  const thinkingPreview = React.useMemo(() => {
    const thinkingText = [...(processItems ?? [])]
      .reverse()
      .find(item => item.type === 'thinking')
      ?.text
      ?.trim();
    if (!thinkingText) return null;
    return thinkingText.length > 180 ? `${thinkingText.slice(0, 180)}...` : thinkingText;
  }, [processItems]);

  const handleContextMenu = (e: React.MouseEvent) => {
    if (turnId) {
      onContextMenu?.(e, turnId, 'assistant');
    }
  };

  return (
    <div
      style={{ display: 'flex', alignItems: 'flex-start', width: '100%' }}
      onMouseEnter={() => setShowActions(true)}
      onMouseLeave={() => setShowActions(false)}
    >
      <AgentAvatar
        name={agentName}
        emoji={agentAvatarEmoji}
        color={agentAvatarColor}
        imageUrl={agentAvatarUrl}
        grouped={groupedWithPrevious}
      />
      <div className={styles.agentMessageContainer}>
        {/* 名称 + 时间 */}
        {!groupedWithPrevious && (
          <div className={styles.agentNameRow}>
            <span className={styles.agentNameText}>{agentName}</span>
            <span className={styles.agentTimeText}>{formatTime(createdAt)}</span>
          </div>
        )}

        {/* 首 token 前：用轻量思考态占位，不渲染空回答气泡 */}
        {shouldShowPreAnswerThinking && (
          <div className={styles.preAnswerThinkingPanel}>
            <div className={styles.firstTokenLoading}>
              <span className={cx(styles.pulseDot, styles.mainStatusDotActive)} />
              <span className={styles.pulseLabel}>正在思考...</span>
            </div>
            {thinkingPreview && (
              <div className={styles.preAnswerThinkingText}>{thinkingPreview}</div>
            )}
          </div>
        )}

        {/* 消息气泡 */}
        {shouldRenderAnswerBubble && (
          <div
            className={cx(
              styles.agentBubbleNew,
              groupedWithPrevious && styles.agentBubbleGrouped,
              isStreaming && styles.agentBubbleStreaming,
              isStreaming && styles.paperStreaming,
              !isStreaming && styles.paperSettled,
              isError && styles.agentBubbleError,
            )}
            onContextMenu={handleContextMenu}
          >
            <MessageItem
              markdownText={content}
              isStreaming={isStreaming}
              stableMarkdown={isStreaming ? typewriter.stableMarkdown : undefined}
              liveText={isStreaming ? typewriter.liveText : undefined}
              visibleLiveText={isStreaming ? typewriter.visibleLiveText : undefined}
              visibleStartOffset={isStreaming ? typewriter.visibleStartOffset : undefined}
            />
          </div>
        )}

        {/* 过程摘要（完成后默认折叠；流式打印时隐藏实时思考，避免挤压气泡） */}
        {!isStreaming && processItems && processItems.length > 0 && (
          <MessageProcessSummary
            items={processItems}
            status={status}
            onRerun={onRerun}
          />
        )}

        {/* 流式状态提示 */}
        {isStreaming && hasAnswerContent && !processItems?.length && (
          <div className={styles.processSummaryRow}>
            <span className={styles.processThinkingLabel}>正在生成回复...</span>
          </div>
        )}

        {/* 错误时显示重试 */}
        {isError && !processItems?.length && onRerun && (
          <div className={styles.processSummaryRow}>
            <button className={styles.processRetryBtn} onClick={onRerun}>重试</button>
          </div>
        )}

        {/* 操作按钮 */}
        <MessageActions
          content={content}
          visible={showActions}
          onCopy={() => navigator.clipboard.writeText(content).catch(() => {})}
          onRerun={onRerun}
          onPin={onPin}
          onDelete={onDelete}
        />

        {/* Token 用量 */}
        {usage?.totalTokens && (
          <div className={styles.tokenUsageLine}>
            {usage.totalTokens.toLocaleString()} tokens
          </div>
        )}
      </div>
    </div>
  );
};

export default React.memo(AgentMessageBubble);

// ── AgentMessageBubble：Agent 消息气泡（左对齐）─────────────

import { Tooltip } from 'antd';
import React from 'react';
import type { TokenUsageDto } from '@/services/platform/api';
import { defaultBrowserVoiceOutputAdapter } from '../hooks/browserVoiceOutput';
import { useTtsPlayer } from '../hooks/useTtsPlayer';
import { useTypewriterStreaming } from '../hooks/useTypewriterStreaming';
import { useChatStyles } from '../styles';
import type { ChatQuotedMessage, TimelineItem } from '../types';
import AgentAvatar from './AgentAvatar';
import MessageActions from './MessageActions';
import MessageItem from './MessageItem';
import MessageProcessSummary from './MessageProcessSummary';
import {
  type CurrentRunActivity,
  getCurrentRunActivity,
} from './processPreview';
import SessionBenchmarkDrawer from './SessionBenchmarkDrawer';

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
  quotedMessage?: ChatQuotedMessage;
  groupedWithPrevious?: boolean;
  isStreaming?: boolean;
  formatTime: (ts: number) => string;
  onContextMenu?: (
    e: React.MouseEvent,
    turnId: string,
    role: 'assistant',
    content: string,
  ) => void;
  onRerun?: () => void;
  onPin?: () => void;
  onDelete?: () => void;
  turnId?: string;
  sessionId?: string | null;
}

const StreamingAnswer = React.memo(function StreamingAnswer({
  content,
  isStreaming,
  className,
  quotedMessage,
  onContextMenu,
}: {
  content: string;
  isStreaming?: boolean;
  className: string;
  quotedMessage?: ChatQuotedMessage;
  onContextMenu: (e: React.MouseEvent) => void;
}) {
  const typewriter = useTypewriterStreaming({
    text: content,
    isStreaming: Boolean(isStreaming),
    tickMs: 40,
    maxLagChars: 48,
  });

  return (
    <div className={className} onContextMenu={onContextMenu}>
      {quotedMessage && <QuotedMessageBlock quotedMessage={quotedMessage} />}
      <MessageItem
        markdownText={content}
        isStreaming={isStreaming}
        stableMarkdown={typewriter.stableMarkdown}
        liveText={typewriter.liveText}
        visibleLiveText={typewriter.visibleLiveText}
        visibleStartOffset={typewriter.visibleStartOffset}
      />
    </div>
  );
});

const agentAvatarColors = [
  '#7c3aed',
  '#6366f1',
  '#8b5cf6',
  '#a78bfa',
  '#c084fc',
];

const formatElapsed = (startedAt?: number, now = Date.now()): string | null => {
  if (!Number.isFinite(startedAt)) return null;
  const elapsedMs = Math.max(0, now - (startedAt as number));
  const seconds = Math.floor(elapsedMs / 1000);
  if (seconds < 1) return '刚刚';
  if (seconds < 60) return `${seconds} 秒`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return rest > 0 ? `${minutes} 分 ${rest} 秒` : `${minutes} 分`;
};

const activityStatusText: Record<CurrentRunActivity['status'], string> = {
  running: '运行中',
  waiting_output: '等待输出',
  processing_result: '正在处理结果',
  completed: '已完成',
  failed: '失败',
};

const isRawStructuredParameterText = (text?: string): boolean => {
  const trimmed = text?.trim();
  return Boolean(trimmed && /^[{[]/.test(trimmed));
};

const CurrentActivityPanel: React.FC<{
  activity: CurrentRunActivity;
  now: number;
}> = ({ activity, now }) => {
  const { styles: rawStyles, cx } = useChatStyles();
  const styles = rawStyles as Record<string, string>;
  const elapsed = formatElapsed(activity.startedAt, now);
  const shouldShowElapsed =
    elapsed &&
    (activity.status === 'running' ||
      activity.status === 'waiting_output' ||
      activity.status === 'processing_result');
  const toneClass =
    activity.status === 'failed'
      ? styles.currentActivityToneError
      : activity.status === 'completed'
        ? styles.currentActivityToneSuccess
        : styles.currentActivityToneRunning;
  const isWorkingActivity =
    activity.status === 'running' ||
    activity.status === 'waiting_output' ||
    activity.status === 'processing_result';
  const preview =
    activity.outputPreview ||
    (!activity.subject ? activity.inputPreview : undefined);
  const previewFull = activity.outputFull || activity.inputFull || preview;
  const subjectFull = activity.subjectFull || activity.subject;
  const tooltipOverlayStyle = { maxWidth: 'min(720px, calc(100vw - 64px))' };
  const tooltipOverlayInnerStyle = {
    maxHeight: 'min(52vh, 420px)',
    overflowY: 'auto' as const,
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
    fontSize: 12,
    lineHeight: 1.55,
  };
  const shouldShowSubjectTooltip = Boolean(
    subjectFull &&
      subjectFull !== activity.subject &&
      !isRawStructuredParameterText(subjectFull) &&
      !activity.subject?.includes('查看过程'),
  );

  return (
    <div
      className={cx(
        styles.currentActivityPanel,
        isWorkingActivity && styles.agentActiveOutputSurface,
        toneClass,
      )}
      aria-live="polite"
    >
      <div className={styles.currentActivityHeader}>
        <span className={cx(styles.pulseDot, styles.currentActivityDot)} />
        <span className={styles.currentActivityTitle}>{activity.title}</span>
        <span className={styles.currentActivityStatus}>
          {activityStatusText[activity.status]}
        </span>
        {shouldShowElapsed && (
          <span className={styles.currentActivityElapsed}>
            已运行 {elapsed}
          </span>
        )}
      </div>
      {activity.subject &&
        (shouldShowSubjectTooltip ? (
          <Tooltip
            title={subjectFull}
            overlayStyle={tooltipOverlayStyle}
            overlayInnerStyle={tooltipOverlayInnerStyle}
            mouseEnterDelay={0.35}
          >
            <div className={styles.currentActivitySubject}>
              {activity.subject}
            </div>
          </Tooltip>
        ) : (
          <div className={styles.currentActivitySubject}>
            {activity.subject}
          </div>
        ))}
      {preview && (
        <Tooltip
          title={previewFull}
          overlayStyle={tooltipOverlayStyle}
          overlayInnerStyle={tooltipOverlayInnerStyle}
          mouseEnterDelay={0.35}
        >
          <pre className={styles.currentActivityPreview}>{preview}</pre>
        </Tooltip>
      )}
    </div>
  );
};

const QuotedMessageBlock: React.FC<{ quotedMessage: ChatQuotedMessage }> = ({
  quotedMessage,
}) => {
  const { styles } = useChatStyles();
  const isAgentSource = quotedMessage.sourceKind === 'agent';
  const avatarColor =
    agentAvatarColors[
      hashString(quotedMessage.sourceName) % agentAvatarColors.length
    ];

  if (isAgentSource) {
    return (
      <div
        style={{
          display: 'flex',
          alignItems: 'flex-start',
          gap: 10,
          width: '100%',
        }}
      >
        <AgentAvatar
          name={quotedMessage.sourceName}
          emoji="🤖"
          color={avatarColor}
          grouped={false}
        />
        <div className={styles.agentMessageContainer}>
          <div className={styles.inboundAgentCard}>
            <div className={styles.inboundAgentCardHeader}>
              {quotedMessage.sourceName}
              <span className={styles.inboundAgentCardLabel}>发来的消息</span>
            </div>
            <div className={styles.inboundAgentCardBody}>
              <MessageItem
                markdownText={quotedMessage.content}
                isStreaming={false}
                stableMarkdown={quotedMessage.content}
              />
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.agentQuotedMessage}>
      <div className={styles.agentQuotedMessageHeader}>
        引用自 {quotedMessage.sourceName}
      </div>
      <div className={styles.agentQuotedMessageBody}>
        <MessageItem
          markdownText={quotedMessage.content}
          isStreaming={false}
          stableMarkdown={quotedMessage.content}
        />
      </div>
    </div>
  );
};

const AgentMessageBubble: React.FC<AgentMessageBubbleProps> = ({
  id: _id,
  content,
  status,
  createdAt,
  agentName,
  agentAvatarEmoji,
  agentAvatarColor,
  agentAvatarUrl,
  processItems,
  usage,
  quotedMessage,
  groupedWithPrevious,
  isStreaming,
  formatTime,
  onContextMenu,
  onRerun,
  onPin,
  onDelete,
  turnId,
  sessionId,
}) => {
  const { styles: rawStyles, cx } = useChatStyles();
  const styles = rawStyles as Record<string, string>;
  const [showActions, setShowActions] = React.useState(false);
  const [diagnosticsOpen, setDiagnosticsOpen] = React.useState(false);
  // 一旦过程摘要首次挂载，保持挂载避免 streaming 中 processItems 短暂清空导致 expanded 状态丢失
  const processSummaryEverMounted = React.useRef(false);

  const tts = useTtsPlayer();

  const isError = status === 'error' || status === 'cancelled';
  const shouldUseTypewriter = Boolean(isStreaming);
  const hasAnswerContent = content.trim().length > 0;
  const hasQuotedOnly =
    Boolean(quotedMessage) &&
    !hasAnswerContent &&
    !isStreaming &&
    status !== 'error' &&
    status !== 'cancelled';
  const isRunActive =
    !isError &&
    (Boolean(isStreaming) ||
      status === 'thinking' ||
      status === 'executing' ||
      status === 'streaming') &&
    status !== 'success';
  const isBeforeFirstToken = isRunActive && !hasAnswerContent;
  const shouldRenderAnswerBubble = hasAnswerContent || hasQuotedOnly;
  const currentActivity = React.useMemo(
    () => getCurrentRunActivity(processItems, status),
    [processItems, status],
  );
  const shouldShowCurrentActivity = Boolean(isRunActive && currentActivity);
  const shouldShowPreAnswerWaiting = isBeforeFirstToken && !currentActivity;
  const [activityNow, setActivityNow] = React.useState(() => Date.now());

  React.useEffect(() => {
    if (!shouldShowCurrentActivity) return undefined;
    const timer = window.setInterval(() => setActivityNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [shouldShowCurrentActivity]);

  const handleContextMenu = (e: React.MouseEvent) => {
    if (turnId) {
      onContextMenu?.(e, turnId, 'assistant', content);
    }
  };

  return (
    <div
      style={{ display: 'flex', alignItems: 'flex-start', width: '100%' }}
      onMouseEnter={() => setShowActions(true)}
      onMouseLeave={() => setShowActions(false)}
    >
      {/* 仅入站引用消息：直接渲染卡片，不套气泡外壳 */}
      {hasQuotedOnly ? (
        <QuotedMessageBlock quotedMessage={quotedMessage!} />
      ) : (
        <>
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
                <span className={styles.agentTimeText}>
                  {formatTime(createdAt)}
                </span>
              </div>
            )}

            {/* 当前活动区只展示真实运行事件；完整累计过程仍由下方“查看过程”入口承载。 */}
            {shouldShowCurrentActivity && currentActivity && (
              <CurrentActivityPanel
                activity={currentActivity}
                now={activityNow}
              />
            )}

            {/* 首 token 前且暂无运行事件：保留轻量等待态，不编造具体思维阶段。 */}
            {shouldShowPreAnswerWaiting && (
              <div
                className={cx(
                  styles.agentBubbleNew,
                  styles.agentBubbleStreaming,
                  styles.agentActiveOutputSurface,
                  styles.agentWaitingBubble,
                )}
              >
                <span
                  className={cx(styles.pulseDot, styles.mainStatusDotActive)}
                />
                <span className={styles.pulseLabel}>等待运行事件...</span>
              </div>
            )}

            {/* 消息气泡 */}
            {shouldRenderAnswerBubble &&
              (() => {
                const bubbleClassName = cx(
                  styles.agentBubbleNew,
                  groupedWithPrevious && styles.agentBubbleGrouped,
                  isStreaming && styles.agentBubbleStreaming,
                  isStreaming && styles.agentActiveOutputSurface,
                  isStreaming && styles.paperStreaming,
                  !isStreaming && styles.paperSettled,
                  isError && styles.agentBubbleError,
                );
                return shouldUseTypewriter ? (
                  <StreamingAnswer
                    content={content}
                    isStreaming={isStreaming}
                    className={bubbleClassName}
                    quotedMessage={quotedMessage}
                    onContextMenu={handleContextMenu}
                  />
                ) : (
                  <div
                    className={bubbleClassName}
                    onContextMenu={handleContextMenu}
                  >
                    {quotedMessage && (
                      <QuotedMessageBlock quotedMessage={quotedMessage} />
                    )}
                    <MessageItem markdownText={content} isStreaming={false} />
                  </div>
                );
              })()}

            {/* 过程摘要：首 token 前显示预览气泡；正文输出后折叠为可展开时间线 */}
            {(() => {
              const hasItems = processItems && processItems.length > 0;
              if (hasItems) processSummaryEverMounted.current = true;
              const shouldRender =
                !isBeforeFirstToken &&
                (hasItems || processSummaryEverMounted.current);
              if (!shouldRender) return null;
              return (
                <MessageProcessSummary
                  items={processItems || []}
                  status={status}
                  onRerun={onRerun}
                  onOpenDiagnostics={
                    sessionId ? () => setDiagnosticsOpen(true) : undefined
                  }
                />
              );
            })()}

            {/* 流式状态提示（仅在过程摘要未挂载时显示，避免重复） */}
            {isStreaming &&
              hasAnswerContent &&
              !processItems?.length &&
              !processSummaryEverMounted.current && (
                <div className={styles.processSummaryRow}>
                  <span className={styles.processThinkingLabel}>
                    正在生成回复...
                  </span>
                </div>
              )}

            {/* 错误时显示重试 */}
            {isError && !processItems?.length && onRerun && (
              <div className={styles.processSummaryRow}>
                <button
                  type="button"
                  className={styles.processRetryBtn}
                  onClick={onRerun}
                >
                  重试
                </button>
              </div>
            )}

            {/* 操作按钮 */}
            <MessageActions
              content={content}
              visible={showActions}
              onCopy={() =>
                navigator.clipboard.writeText(content).catch(() => {})
              }
              onRerun={onRerun}
              onPin={onPin}
              onDelete={onDelete}
              voiceOutputAdapter={defaultBrowserVoiceOutputAdapter}
              onTtsSpeak={() => tts.speak(content)}
              ttsPlaying={tts.playing}
              ttsLoading={tts.loading}
            />

            {/* Token 用量 */}
            {usage?.totalTokens && (
              <div className={styles.tokenUsageLine}>
                {usage.totalTokens.toLocaleString()} tokens
              </div>
            )}
            <SessionBenchmarkDrawer
              sessionId={sessionId}
              open={diagnosticsOpen}
              onClose={() => setDiagnosticsOpen(false)}
            />
          </div>
        </>
      )}
    </div>
  );
};

const hashString = (s: string): number => {
  let h = 0;
  for (let i = 0; i < s.length; i++) {
    h = ((h << 5) - h + s.charCodeAt(i)) | 0;
  }
  return Math.abs(h);
};

export default React.memo(AgentMessageBubble);

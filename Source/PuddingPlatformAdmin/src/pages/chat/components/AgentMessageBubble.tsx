// ── AgentMessageBubble：Agent 消息气泡（左对齐）─────────────

import { Tooltip } from 'antd';
import dayjs from 'dayjs';
import React from 'react';
import type { TokenUsageDto } from '@/services/platform/api';
import { getAgentMessageProcessItems } from '../client/agentChatApi';
import type {
  ConversationProcessSummary,
  ProcessSummaryItem,
} from '../client/types';
import { defaultBrowserVoiceOutputAdapter } from '../hooks/browserVoiceOutput';
import { useTtsPlayer } from '../hooks/useTtsPlayer';
import { useTypewriterStreaming } from '../hooks/useTypewriterStreaming';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import type {
  ChatQuotedMessage,
  ParentDelegationActivity,
  TimelineItem,
} from '../types';
import { summarizeError } from '../utils/summarizeError';
import AgentAvatar from './AgentAvatar';
import {
  deriveTurnStatusFromFacts,
  type TurnPhase,
  TurnStatus,
} from './execution-flow/TurnStatus';
import MessageActions from './MessageActions';
import MessageItem from './MessageItem';
import MessageProcessSummary from './MessageProcessSummary';
import ModelRetryRow from './ModelRetryRow';
import {
  type CurrentRunActivity,
  getCurrentRunActivity,
  sanitizeProcessText,
} from './processPreview';
import { ReasoningPreview } from './ReasoningPreview';
import StateDot from './StateDot';
import ToolCallRowList from './ToolCallRow';
import type { TranscriptMode } from './TranscriptModeSwitch';

const SessionBenchmarkDrawer =
  process.env.NODE_ENV === 'test'
    ? (require('./SessionBenchmarkDrawer')
        .default as typeof import('./SessionBenchmarkDrawer').default)
    : React.lazy(() => import('./SessionBenchmarkDrawer'));

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
  processSummary?: ConversationProcessSummary;
  processMessageId?: string;
  workspaceId?: string;
  agentId?: string;
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
  parentDelegationActivity?: ParentDelegationActivity;
  /** P0#2：转录视图分级 */
  transcriptMode?: TranscriptMode;
  onTranscriptModeChange?: (mode: TranscriptMode) => void;
}

const MESSAGE_ENTRANCE_WINDOW_MS = 5_000;
const COMPLETION_PARTICLE_OFFSETS = [
  { x: '15px', y: '-17px', delay: 0 },
  { x: '-15px', y: '-12px', delay: 35 },
  { x: '19px', y: '-4px', delay: 70 },
  { x: '-9px', y: '-21px', delay: 105 },
  { x: '6px', y: '-22px', delay: 140 },
  { x: '-18px', y: '-3px', delay: 175 },
] as const;

const StreamingAnswer = React.memo(function StreamingAnswer({
  content,
  isStreaming,
  className,
  workspaceId,
  quotedMessage,
  onContextMenu,
}: {
  content: string;
  isStreaming?: boolean;
  className: string;
  workspaceId?: string;
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
        workspaceId={workspaceId}
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

const toTimelineItems = (items: ProcessSummaryItem[]): TimelineItem[] =>
  items
    .filter(
      (item) =>
        !item.kind.startsWith('subagent.') &&
        !item.kind.startsWith('subagent_'),
    )
    .map((item) => ({
      id: item.id,
      toolCallId: item.toolCallId ?? undefined,
      // TR-01 冻结字段透传（服务端穿透后生效；缺失时回落 generic）。
      parentToolCallId: item.parentToolCallId ?? undefined,
      durationMs: item.durationMs ?? undefined,
      presentation: item.presentation ?? undefined,
      type:
        item.kind === 'thinking' ||
        item.kind === 'tool_call' ||
        item.kind === 'tool_result'
          ? item.kind
          : 'subconscious_step',
      text: item.text,
      status: item.status,
      name: item.name ?? undefined,
      arguments: item.arguments ?? undefined,
      output: item.output ?? undefined,
      exitCode: item.exitCode ?? undefined,
      message: item.message ?? undefined,
      timestamp: Date.parse(item.timestamp),
      collapsed: true,
    }));

const isRawStructuredParameterText = (text?: string): boolean => {
  const trimmed = text?.trim();
  return Boolean(trimmed && /^[{[]/.test(trimmed));
};

const CurrentActivityPanel: React.FC<{
  activity: CurrentRunActivity;
  hidePreview?: boolean;
}> = ({ activity, hidePreview = false }) => {
  const { styles: rawStyles, cx } = useChatMessageStyles();
  const styles = rawStyles as Record<string, string>;
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
        styles.agentBubbleEntrance,
        isWorkingActivity && styles.agentActiveOutputSurface,
        toneClass,
      )}
    >
      <div className={styles.currentActivityHeader}>
        <span className={styles.currentActivityTitle}>{activity.title}</span>
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
      {preview && !hidePreview && (
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
  const { styles } = useChatMessageStyles();
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
  processSummary,
  processMessageId,
  workspaceId,
  agentId,
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
  parentDelegationActivity,
  transcriptMode,
  onTranscriptModeChange,
}) => {
  const { styles: rawStyles, cx } = useChatMessageStyles();
  const styles = rawStyles as Record<string, string>;
  const [showActions, setShowActions] = React.useState(false);
  const [actionsMounted, setActionsMounted] = React.useState(false);
  const [diagnosticsOpen, setDiagnosticsOpen] = React.useState(false);
  // 一旦过程摘要首次挂载，保持挂载避免 streaming 中 processItems 短暂清空导致 expanded 状态丢失
  const processSummaryEverMounted = React.useRef(false);
  const loadHistoricalProcessItems = React.useCallback(async () => {
    if (!workspaceId || !agentId || !processMessageId) return [];
    const details = await getAgentMessageProcessItems(
      workspaceId,
      agentId,
      processMessageId,
    );
    return toTimelineItems(details.processItems);
  }, [workspaceId, agentId, processMessageId]);

  const tts = useTtsPlayer();
  const hasAnswerContent = content.trim().length > 0;
  const isError = status === 'error' || status === 'cancelled';

  // P0-1: 错误摘要行 — 优先取失败时间线条目（tool_result / subconscious_step）的
  // message/output（error 事件会把错误写入该条目），无失败条目时回退到 content
  // （error 事件同时会把诊断文本写入 answerMarkdown）。
  const errorText = React.useMemo(() => {
    const candidates: string[] = [];
    if (processItems) {
      for (const item of processItems) {
        const failed =
          item.status === 'error' ||
          item.status === 'failed' ||
          (typeof item.exitCode === 'number' && item.exitCode !== 0);
        const text = failed ? item.message || item.output : '';
        if (text && text.trim()) candidates.push(text);
      }
    }
    for (const candidate of candidates) {
      if (candidate.trim()) return candidate;
    }
    return content;
  }, [processItems, content]);
  const errorSummary = React.useMemo(
    () => summarizeError(errorText),
    [errorText],
  );

  // P3: 完成粒子 — 回答落定时在气泡右下角播放一次粒子飞散动画
  const [showCompletionParticles, setShowCompletionParticles] =
    React.useState(false);
  const hasShownParticles = React.useRef(false);
  const previousIsStreaming = React.useRef(Boolean(isStreaming));
  const previousHasAnswerContent = React.useRef(hasAnswerContent);
  React.useEffect(() => {
    const justFinishedStreaming =
      previousIsStreaming.current && !isStreaming && hasAnswerContent;
    const justReceivedCompletedAnswer =
      !previousHasAnswerContent.current && hasAnswerContent && !isStreaming;

    previousIsStreaming.current = Boolean(isStreaming);
    previousHasAnswerContent.current = hasAnswerContent;

    if (
      isError ||
      hasShownParticles.current ||
      (!justFinishedStreaming && !justReceivedCompletedAnswer)
    )
      return undefined;

    hasShownParticles.current = true;
    setShowCompletionParticles(true);
    const timer = setTimeout(() => setShowCompletionParticles(false), 820);
    return () => clearTimeout(timer);
  }, [isStreaming, hasAnswerContent, isError]);

  const shouldUseTypewriter = Boolean(isStreaming);
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
  const messageAgeMs = Math.max(0, Date.now() - createdAt);
  const shouldAnimateEntrance =
    isRunActive || messageAgeMs <= MESSAGE_ENTRANCE_WINDOW_MS;
  const isBeforeFirstToken = isRunActive && !hasAnswerContent;
  const shouldRenderAnswerBubble = hasAnswerContent || hasQuotedOnly;
  const processActivity = React.useMemo(
    () => getCurrentRunActivity(processItems, status),
    [processItems, status],
  );
  const delegationActivity = React.useMemo<CurrentRunActivity | null>(() => {
    if (!parentDelegationActivity?.activeCount) return null;
    const { activeCount, label, startedAt, updatedAt } =
      parentDelegationActivity;
    return {
      kind: 'subagent',
      title:
        activeCount === 1
          ? label
            ? `正在调用子代理：${label}`
            : '正在调用子代理'
          : `正在调用 ${activeCount} 个子代理`,
      subject: '主代理正在等待子代理返回；内部进度请查看右侧托盘坞',
      status: 'running',
      startedAt,
      updatedAt,
    };
  }, [parentDelegationActivity]);
  const currentActivity = React.useMemo(() => {
    if (!delegationActivity) return processActivity;
    if (!processActivity) return delegationActivity;
    return (delegationActivity.updatedAt ?? 0) >=
      (processActivity.updatedAt ?? 0)
      ? delegationActivity
      : processActivity;
  }, [delegationActivity, processActivity]);
  // thinking/tool have canonical compact rows below. Keep the activity card only
  // for states that do not yet own a dedicated trajectory row.
  const shouldShowProcessActivity = Boolean(
    isRunActive &&
      processActivity &&
      (processActivity.kind === 'system' ||
        processActivity.kind === 'subagent'),
  );
  const shouldShowDelegationActivity = Boolean(
    isRunActive && delegationActivity && processActivity?.kind !== 'subagent',
  );
  // 思维链预览：从 timeline 提取已清洗的 thinking 文本；有内容时等待气泡升级为思维链预览
  const reasoningLines = React.useMemo(() => {
    if (!processItems || processItems.length === 0) return [];
    return processItems
      .filter((item) => item.type === 'thinking' && item.text)
      .map((item) => ({ id: item.id, text: sanitizeProcessText(item.text) }))
      .filter((line) => line.text.length > 0);
  }, [processItems]);
  const hasReasoningContent = reasoningLines.length > 0;
  // 推理摘要与当前工具/委派活动并列展示，避免阶段切换时丢失主代理上下文。
  const showReasoningPreview = hasReasoningContent && isBeforeFirstToken;
  const reasoningIsCurrent =
    !currentActivity || currentActivity.kind === 'thinking';
  // CU-05: TurnStatus —— 收敛 WaitingBubble/CurrentActivityPanel 的重复状态区。
  // 阶段文案只来自已知事实（delegation/reasoning/tool/system/answer 活动），
  // 无可见事件时为 pending（「{agentName} 正在运行」）；终态由消息状态驱动不在此派生。
  const turnStatus = React.useMemo<TurnStatus | null>(() => {
    if (!isRunActive) return null;
    const hasVisibleEvents = Boolean(
      currentActivity || reasoningLines.length > 0 || hasAnswerContent,
    );
    let phase: TurnPhase | undefined;
    if (currentActivity?.kind === 'subagent' || delegationActivity) {
      phase = 'delegating';
    } else if (currentActivity?.kind === 'thinking') {
      phase = 'reasoning';
    } else if (
      currentActivity?.kind === 'tool' &&
      (currentActivity.status === 'running' ||
        currentActivity.status === 'waiting_output' ||
        currentActivity.status === 'processing_result')
    ) {
      phase = 'executing';
    } else if (currentActivity?.kind === 'system') {
      phase = 'connecting';
    } else if (hasAnswerContent) {
      phase = 'answering';
    }
    return deriveTurnStatusFromFacts({
      active: true,
      hasVisibleEvents,
      phase,
    });
  }, [
    isRunActive,
    currentActivity,
    delegationActivity,
    reasoningLines,
    hasAnswerContent,
  ]);
  const shouldShowRunMonitor = isRunActive;

  // E2: 流式停滞检测 — 15s 无内容增量触发琥珀色警告
  const lastDeltaRef = React.useRef(Date.now());
  const [stallSeconds, setStallSeconds] = React.useState(0);
  const stallCheckRef = React.useRef<
    ReturnType<typeof setInterval> | undefined
  >(undefined);
  React.useEffect(() => {
    lastDeltaRef.current = Date.now();
  }, [content]);
  React.useEffect(() => {
    if (isStreaming) {
      stallCheckRef.current = setInterval(() => {
        setStallSeconds(Math.floor((Date.now() - lastDeltaRef.current) / 1000));
      }, 1000);
      return () => clearInterval(stallCheckRef.current);
    }
    setStallSeconds(0);
    return undefined;
  }, [isStreaming]);

  const handleContextMenu = (e: React.MouseEvent) => {
    if (turnId) {
      onContextMenu?.(e, turnId, 'assistant', content);
    }
  };

  const revealActions = React.useCallback(() => {
    setActionsMounted(true);
    setShowActions(true);
  }, []);

  const hideActions = React.useCallback(() => {
    setShowActions(false);
  }, []);

  return (
    <div
      style={{ display: 'flex', alignItems: 'flex-start', width: '100%' }}
      onMouseEnter={revealActions}
      onMouseLeave={hideActions}
    >
      {/* 仅入站引用消息：直接渲染卡片，不套气泡外壳 */}
      {hasQuotedOnly && quotedMessage ? (
        <QuotedMessageBlock quotedMessage={quotedMessage} />
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
                <span
                  className={styles.agentTimeText}
                  title={dayjs(createdAt).format('YYYY-MM-DD HH:mm:ss')}
                >
                  {formatTime(createdAt)}
                </span>
              </div>
            )}

            {shouldShowRunMonitor && (
              <div
                className={styles.agentRunMonitor}
                data-testid="agent-run-monitor"
              >
                {/* CU-05：唯一 L0 状态行（单 aria-live）；WaitingBubble 已退出生产路径。 */}
                {turnStatus && (
                  <TurnStatus
                    status={turnStatus}
                    turnStartedAt={createdAt}
                    agentName={agentName}
                  />
                )}

                {/* 当前活动区仅展示主代理真实阶段或有界委派摘要（状态/计时已收敛到 TurnStatus）。 */}
                {shouldShowProcessActivity && processActivity && (
                  <CurrentActivityPanel activity={processActivity} />
                )}

                {shouldShowDelegationActivity && delegationActivity && (
                  <CurrentActivityPanel activity={delegationActivity} />
                )}

                {/* 推理摘要与工具/子代理当前活动并列，保留主代理过程连续性。 */}
                {showReasoningPreview && (
                  <ReasoningPreview
                    lines={reasoningLines}
                    isCurrent={reasoningIsCurrent}
                  />
                )}
              </div>
            )}

            {/* 消息气泡 */}
            {shouldRenderAnswerBubble &&
              (() => {
                const isStalled = isStreaming && stallSeconds >= 15;
                const bubbleClassName = cx(
                  styles.agentBubbleNew,
                  shouldAnimateEntrance && styles.agentBubbleEntrance,
                  groupedWithPrevious && styles.agentBubbleGrouped,
                  isStreaming && styles.agentBubbleStreaming,
                  isStreaming && styles.agentActiveOutputSurface,
                  isStreaming && styles.paperStreaming,
                  !isStreaming && styles.paperSettled,
                  isStalled && styles.agentBubbleWarning,
                  isError && styles.agentBubbleError,
                );
                return shouldUseTypewriter ? (
                  <StreamingAnswer
                    content={content}
                    isStreaming={isStreaming}
                    className={bubbleClassName}
                    workspaceId={workspaceId}
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
                    <MessageItem
                      markdownText={content}
                      isStreaming={false}
                      workspaceId={workspaceId}
                    />
                    {showCompletionParticles && (
                      <div
                        className={styles.answerParticlesContainer}
                        data-testid="answer-completion-particles"
                        aria-hidden="true"
                      >
                        {COMPLETION_PARTICLE_OFFSETS.map(
                          ({ x, y, delay }, index) => (
                            <span
                              key={`${x}:${y}`}
                              className={styles.answerParticle}
                              style={
                                {
                                  '--bx': x,
                                  '--by': y,
                                  animationDelay: `${delay}ms`,
                                  width: index % 3 === 0 ? 4 : 3,
                                  height: index % 3 === 0 ? 4 : 3,
                                } as React.CSSProperties
                              }
                            />
                          ),
                        )}
                      </div>
                    )}
                  </div>
                );
              })()}

            {/* P1-1: 工具调用行（对齐 D5 ToolCallRow）：单行摘要 + 展开 IN/OUT，与过程时间线共存 */}
            {processItems?.some((item) => item.type === 'tool_call') && (
              <ToolCallRowList items={processItems} />
            )}

            {/* 过程摘要：首 token 前显示预览气泡；正文输出后折叠为可展开时间线 */}
            {(() => {
              const hasItems = processItems && processItems.length > 0;
              const hasHistoricalSummary = Boolean(processSummary?.hasDetails);
              if (hasItems || hasHistoricalSummary)
                processSummaryEverMounted.current = true;
              const shouldRender =
                hasItems ||
                hasHistoricalSummary ||
                processSummaryEverMounted.current;
              if (!shouldRender) return null;
              return (
                <MessageProcessSummary
                  items={processItems || []}
                  summary={processSummary}
                  status={status}
                  onLoadDetails={
                    hasHistoricalSummary
                      ? loadHistoricalProcessItems
                      : undefined
                  }
                  onRerun={onRerun}
                  onOpenDiagnostics={
                    sessionId ? () => setDiagnosticsOpen(true) : undefined
                  }
                  transcriptMode={transcriptMode}
                  onTranscriptModeChange={onTranscriptModeChange}
                />
              );
            })()}

            {/* P1-2: 模型重试行 — 嗅探 processItems 中的 LLM retry 条目；无条目时组件内部返回 null，不占用布局 */}
            <ModelRetryRow items={processItems} />

            {/* P0-1: 错误摘要行（StateDot + 标题 + 摘要，title 挂全量原文）；
                重试按钮与摘要行同行（沿用 !processItems 条件，不破坏既有 onRerun 逻辑） */}
            {isError && (
              <div
                className={styles.agentErrorSummaryRow}
                data-testid="agent-error-summary-row"
              >
                <StateDot
                  state={status === 'cancelled' ? 'warning' : 'error'}
                  size={10}
                />
                <span
                  className={cx(
                    styles.agentErrorSummaryTitle,
                    status === 'cancelled' &&
                      styles.agentErrorSummaryTitleWarning,
                  )}
                >
                  {status === 'cancelled' ? '已取消' : '本轮运行失败'}
                </span>
                {errorSummary.summary && (
                  <span
                    className={styles.agentErrorSummaryText}
                    title={errorSummary.full}
                    data-testid="agent-error-summary-text"
                  >
                    {errorSummary.summary}
                  </span>
                )}
                {!processItems?.length && onRerun && (
                  <button
                    type="button"
                    className={styles.processRetryBtn}
                    onClick={onRerun}
                  >
                    重试
                  </button>
                )}
              </div>
            )}

            {/* 操作按钮 */}
            {actionsMounted && (
              <MessageActions
                content={content}
                visible={showActions}
                onRerun={onRerun}
                onPin={onPin}
                onDelete={onDelete}
                voiceOutputAdapter={defaultBrowserVoiceOutputAdapter}
                onTtsSpeak={() => tts.speak(content)}
                ttsPlaying={tts.playing}
                ttsLoading={tts.loading}
              />
            )}

            {/* Token 用量 */}
            {usage?.totalTokens && (
              <div className={styles.tokenUsageLine}>
                {usage.totalTokens.toLocaleString()} tokens
              </div>
            )}
            {diagnosticsOpen && (
              <React.Suspense fallback={null}>
                <SessionBenchmarkDrawer
                  sessionId={sessionId}
                  open
                  onClose={() => setDiagnosticsOpen(false)}
                />
              </React.Suspense>
            )}
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

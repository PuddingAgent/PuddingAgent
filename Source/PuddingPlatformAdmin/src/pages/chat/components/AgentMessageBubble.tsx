// ── AgentMessageBubble：Agent 消息气泡（左对齐）─────────────

import { Tooltip } from 'antd';
import dayjs from 'dayjs';
import React from 'react';
import type { TokenUsageDto } from '@/services/platform/api';
import type {
  ConversationProcessSummary,
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
  deriveTurnStatusFromProjection,
  type TurnPhase,
  TurnStatus,
} from './execution-flow/TurnStatus';
import MessageActions from './MessageActions';
import MessageItem from './MessageItem';
import ModelRetryRow from './ModelRetryRow';
import {
  type CurrentRunActivity,
  getCurrentRunActivity,
  sanitizeProcessText,
} from './processPreview';
import {
  deriveStatsFromProjection,
} from '../projections/turnContentBlocks';
import TurnContentStream from './execution-flow/TurnContentStream';
import {
  deriveStatsFromProcessItems,
} from './execution-flow/TurnContentStream';
import { TurnStatsLine } from './execution-flow/TurnStatsLine';
import type { ExecutionFlowProjection } from '../projections/executionFlowProjector';
import StateDot from './StateDot';
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
  /** CU-09：展开态「打开检查器」入口（runId → SubAgentActivityDock 检查器）。 */
  onOpenInspector?: (runId: string) => void;
  /** P0#2：转录视图分级 */
  transcriptMode?: TranscriptMode;
  onTranscriptModeChange?: (mode: TranscriptMode) => void;
  /** CU-11 Phase 2: per-turn canonical 投影（灰度开启时走新路径 B；undefined 回退旧路径 A）。 */
  executionFlowProjection?: ExecutionFlowProjection;
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
    // 输出自然度：不再覆盖 tick/maxLag——hook 的 B2 自适应（24ms tick、
    // 速率追踪 maxLag、拥堵降速、分档 charsPerTick）已针对平滑流式调优；
    // 旧覆盖（40/48）滞后余量过小，追平激进导致文字 bursts 式蹦出。
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
  workspaceId,
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
  onOpenInspector,
  executionFlowProjection,
}) => {
  const { styles: rawStyles, cx } = useChatMessageStyles();
  const styles = rawStyles as Record<string, string>;
  const [showActions, setShowActions] = React.useState(false);
  const [actionsMounted, setActionsMounted] = React.useState(false);
  const [diagnosticsOpen, setDiagnosticsOpen] = React.useState(false);

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
        if (text?.trim()) candidates.push(text);
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

  // AgentTurnCard 重构（2026-08-25）：只要 canonical 投影已经产生正文节点，
  // 正文就必须由 TurnContentStream 的 TextBlock 承载。answerMarkdown 是完成态
  // 物化文本/复制与 TTS 来源，不再参与 UI 分段判定；用 startsWith 等字符串
  // 关系切换渲染路径，会在「完整流文本 + 最终回答后缀」场景把全部 TextBlock
  // 隐藏，再把整块正文沉到底部，重新制造“轨迹块 + 正文块”两段式 UI。
  const hasProjectedTextBlocks = React.useMemo(() => {
    if (!executionFlowProjection) return false;
    return executionFlowProjection.nodes.some(
      (node) => node.kind === 'message' && node.text.trim().length > 0,
    );
  }, [executionFlowProjection]);

  const shouldUseTypewriter = Boolean(isStreaming);
  const hasQuotedOnly =
    Boolean(quotedMessage) &&
    !hasAnswerContent &&
    !isStreaming &&
    status !== 'error' &&
    status !== 'cancelled';
  // 仅在尚无 canonical 正文节点时保留整块正文兜底（例如非流式旧记录）。
  // 投影一旦有 TextBlock，就绝不能因文本值差异切回第二正文源。
  const shouldRenderAnswerBubble =
    !hasProjectedTextBlocks && (hasAnswerContent || hasQuotedOnly);
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
  const processActivity = React.useMemo(
    () => getCurrentRunActivity(processItems, status),
    [processItems, status],
  );
  const reasoningLines = React.useMemo(() => {
    if (!processItems || processItems.length === 0) return [];
    return processItems
      .filter((item) => item.type === 'thinking' && item.text)
      .map((item) => ({ id: item.id, text: sanitizeProcessText(item.text) }))
      .filter((line) => line.text.length > 0);
  }, [processItems]);
  const hasReasoningContent = reasoningLines.length > 0;
  const delegationActivity = React.useMemo<CurrentRunActivity | null>(() => {
    if (!parentDelegationActivity?.activeCount) return null;
    // 571fb2fa：等待卡片 4 条件（防御纵深，防直接使用组件时串台）——
    // ① 仅同步委派：ChatMain 聚合已过滤 async，activity 只含 sync；
    // ② 属于当前父 Turn：activity.turnId 与气泡 turnId 必须匹配；
    // ③ 父 Turn 活动：仅运行态显示；
    // ④ parentToolCallId 未完成：聚合只含 running/spawning 卡，隐含未完成。
    if (!isRunActive) return null;
    if (
      parentDelegationActivity.turnId &&
      turnId &&
      parentDelegationActivity.turnId !== turnId
    ) {
      return null;
    }
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
  }, [parentDelegationActivity, isRunActive, turnId]);
  const currentActivity = React.useMemo(() => {
    if (!delegationActivity) return processActivity;
    if (!processActivity) return delegationActivity;
    return (delegationActivity.updatedAt ?? 0) >=
      (processActivity.updatedAt ?? 0)
      ? delegationActivity
      : processActivity;
  }, [delegationActivity, processActivity]);
  // thinking/tool/delegation have canonical compact rows below (timeline). The
  // activity card remains only for system-stage facts without a dedicated row.
  const shouldShowProcessActivity = Boolean(
    isRunActive && processActivity && processActivity.kind === 'system',
  );
  // 思维链数据已在时间线 memo 中提取（reasoningLines）；这里只保留 TurnStatus 派生。
  // CU-05 + 行为链 P2: TurnStatus —— 有 canonical 投影时直接消费投影派生（路径 B），
  // 阶段来自最后节点 kind（reasoning/tool/delegation/message）；无投影回退 facts 派生。
  const turnStatus = React.useMemo<TurnStatus | null>(() => {
    if (!isRunActive) return null;
    if (executionFlowProjection) {
      return deriveTurnStatusFromProjection(executionFlowProjection);
    }
    const hasVisibleEvents = Boolean(
      currentActivity || reasoningLines.length > 0 || hasAnswerContent,
    );
    let phase: TurnPhase | undefined;
    if (currentActivity?.kind === 'subagent' || delegationActivity) {
      phase = 'delegating';
    } else if (
      currentActivity?.kind === 'tool' &&
      (currentActivity.status === 'running' ||
        currentActivity.status === 'waiting_output')
    ) {
      // 仅真实运行中的工具算 executing；processing_result 是工具已完成、
      // 模型正在产出下一轮（推理/回答），不得压过 answering/reasoning。
      phase = 'executing';
    } else if (hasAnswerContent && isStreaming) {
      phase = 'answering';
    } else if (currentActivity?.kind === 'thinking' || hasReasoningContent) {
      phase = 'reasoning';
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
    executionFlowProjection,
    currentActivity,
    delegationActivity,
    reasoningLines,
    hasAnswerContent,
    hasReasoningContent,
    isStreaming,
  ]);
  const shouldShowRunMonitor = isRunActive;

  // 行为链 §3.3：turn 终态计量（StatsLine）—— 段数/工具数来自投影（路径 B）或
  // processItems（路径 A），总时长 = turn 起点 → 终态 occurredAt（路径 A 回退末条
  // 时间线 timestamp），token 来自 usage；全部缺失时 TurnStatsLine 自行不渲染。
  const turnStats = React.useMemo(() => {
    if (isRunActive) return null;
    const base = executionFlowProjection
      ? deriveStatsFromProjection(executionFlowProjection.nodes)
      : deriveStatsFromProcessItems(processItems ?? []);
    let endMs = Number.NaN;
    const terminalAt = executionFlowProjection?.terminal?.occurredAt;
    if (terminalAt) endMs = Date.parse(terminalAt);
    if (!Number.isFinite(endMs) && processItems && processItems.length > 0) {
      endMs = processItems[processItems.length - 1].timestamp;
    }
    const totalDurationMs =
      Number.isFinite(endMs) && endMs > createdAt ? endMs - createdAt : null;
    return {
      reasoningSegments: base.reasoningSegments,
      toolCount: base.toolCount,
      totalDurationMs,
      totalTokens: usage?.totalTokens ?? null,
    };
  }, [isRunActive, executionFlowProjection, processItems, createdAt, usage]);

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
            <div className={styles.agentTurnCard}>
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
                {/* 无障碍（验收 6）：终态卡保留可达状态标记（成功/失败/取消），
                    不只依赖颜色与计量行。运行态由 TurnStatus 行承载。 */}
                {/* 失败/取消语义由既有错误摘要行承载（StateDot+标题），此处
                    只补成功终态标记，避免同卡重复状态行。 */}
                {!isRunActive && status === 'success' && (
                  <span
                    className={styles.agentTurnStateChip}
                    aria-label="回合已完成"
                  >
                    <StateDot state="done" size={8} />
                    已完成
                  </span>
                )}
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

                {/* 当前活动区仅保留尚无专属轨迹行的 system 阶段事实
                    （委派等待态由 TurnStatus delegating + DelegationRow 承载，不再渲染大卡）。 */}
                {shouldShowProcessActivity && processActivity && (
                  <CurrentActivityPanel activity={processActivity} />
                )}
              </div>
            )}

            {/* AgentTurnCard 重构：正文段 ⇄ 行为组内容块流 —— 按 canonical
                sequence 交错；正文段（TextBlock）永久可见且只渲染一次，行为
                轨迹收进可折叠 ActivityGroup（历史组默认折叠并卸载成员 DOM，
                最新组默认展开）。只要投影存在正文节点，正文就始终在这里按
                sequence 渲染；answerMarkdown 不再控制分段路径。 */}
            {hasProjectedTextBlocks && quotedMessage && (
              <QuotedMessageBlock quotedMessage={quotedMessage} />
            )}
            <TurnContentStream
              projection={executionFlowProjection}
              processItems={processItems}
              isRunActive={isRunActive}
              workspaceId={workspaceId}
              onAnswerContextMenu={handleContextMenu}
              onOpenInspector={onOpenInspector}
            />
            {hasProjectedTextBlocks && showCompletionParticles && (
              <div
                className={styles.answerParticlesContainer}
                data-testid="answer-completion-particles"
                aria-hidden="true"
              >
                {COMPLETION_PARTICLE_OFFSETS.map(({ x, y, delay }, index) => (
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
                ))}
              </div>
            )}

            {/* P1-2: 模型重试行 — 仅识别 canonical LLM retry 运行事实；普通思考文本不触发 */}
            <ModelRetryRow items={processItems} active={isRunActive} />

            {/* 回退正文气泡（守卫失败/无投影）：整块 content 单一承载 */}
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

            {/* 行为链 §3.3：turn 终态计量行（段数/工具数/总时长/tokens；升级自
                原单行 token 计数，数据刷新不归零） */}
            {turnStats && <TurnStatsLine {...turnStats} />}
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

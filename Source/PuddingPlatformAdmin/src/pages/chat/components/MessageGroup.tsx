// ── MessageGroup：Runtime Timeline 容器（连续信息流，不是卡片堆叠）──
import {
  CopyOutlined,
  DeleteOutlined,
  PushpinOutlined,
  ReloadOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { Button, Space, Tooltip, Typography } from 'antd';
import dayjs from 'dayjs';
import React, { useMemo } from 'react';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import { useTypewriterStreaming } from '../hooks/useTypewriterStreaming';
import { useChatStyles } from '../styles';
import type { ChatTurn, TimelineItem } from '../types';
import { inboundDebug } from '../utils/inboundDebug';
import MessageItem from './MessageItem';
import { summarizeThinkingText } from './processPreview';

const { Text } = Typography;

interface MessageGroupProps {
  turn: ChatTurn;
  turns: ChatTurn[];
  isLatest?: boolean;
  selectedAgent?: WorkspaceAgentDto;
  formatTime: (ts: number) => string;
  getStepTone: (status?: string) => 'executing' | 'success' | 'error';
  onDeleteTurn: (turnId: string) => void;
  onToggleReasoning: (turnId: string, itemId: string) => void;
  onContextMenu: (
    e: React.MouseEvent,
    turnId: string,
    role: 'user' | 'assistant',
    content: string,
  ) => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
}

/** 从 TimelineItem status 推断色调 */
const getItemTone = (item: TimelineItem): 'executing' | 'success' | 'error' => {
  const s = (item.status || '').toLowerCase();
  if (s.includes('error') || s.includes('fail') || s.includes('cancel'))
    return 'error';
  if (
    s.includes('success') ||
    s.includes('done') ||
    s.includes('complete') ||
    (item.type === 'tool_result' && item.exitCode === 0)
  )
    return 'success';
  return 'executing';
};

/** P0-可信度：清洗内部变量泄漏，返回安全文本或空字符串 */
const sanitizeDisplayText = (text?: string): string => {
  if (!text) return '';
  let cleaned = text
    .replace(/\bundefined\b/gi, '')
    .replace(/\bnull\b/gi, '')
    .replace(/\bNaN\b/gi, '')
    .trim();
  // 如果清洗后只剩下标点/空白，返回空
  if (!cleaned || /^[\s，,。.！!？?：:；;、·]+$/.test(cleaned)) return '';
  // 去除连续多余空格
  cleaned = cleaned.replace(/\s{2,}/g, ' ');
  return cleaned;
};

/** 生成思考摘要：把模型内部推理转换成用户可读的阶段预览 */
const makeThinkingSummary = (text?: string): string => {
  return sanitizeDisplayText(text)
    ? summarizeThinkingText(text)
    : '正在整理上下文…';
};

/** 生成工具状态文本 */
const getToolStatusText = (tone: 'executing' | 'success' | 'error'): string => {
  if (tone === 'executing') return '执行中';
  if (tone === 'error') return '失败';
  return '完成';
};

/** P0：将运行时状态映射为单一人话主状态 */
const getMainStatusText = (state: string): string => {
  const map: Record<string, string> = {
    thinking: '正在整理上下文…',
    tool_executing: '正在调用工具…',
    streaming: '正在生成回复…',
    success: '已完成',
    error: '出错了，可重试',
    cancelled: '已取消',
    idle: '就绪',
  };
  return map[state] || '处理中…';
};

/** P0：完成后生成过程摘要行 */
const getCompletionSummary = (items: TimelineItem[]): string | null => {
  if (!items || items.length === 0) return null;
  const parts: string[] = [];
  const thinkingCount = items.filter((i) => i.type === 'thinking').length;
  const toolSuccessCount = items.filter(
    (i) =>
      (i.type === 'tool_call' || i.type === 'tool_result') &&
      (i.status?.toLowerCase().includes('success') ||
        i.status?.toLowerCase().includes('done')),
  ).length;
  if (thinkingCount > 0) parts.push(`已思考 ${thinkingCount} 步`);
  if (toolSuccessCount > 0) parts.push(`已调用 ${toolSuccessCount} 个工具`);
  if (parts.length === 0) parts.push('已完成');
  return parts.join(' · ');
};

const MessageGroup: React.FC<MessageGroupProps> = ({
  turn,
  isLatest,
  selectedAgent,
  formatTime,
  onDeleteTurn,
  onToggleReasoning,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
}) => {
  const { styles: rawStyles, cx } = useChatStyles();
  const styles = rawStyles as Record<string, string>;
  const { assistant, userMessage } = turn;

  // ── Ink Bloom Typewriter 流式渲染 ──
  const typewriter = useTypewriterStreaming({
    text: assistant.answerMarkdown,
    isStreaming: assistant.isStreaming,
    tickMs: 40,
    maxLagChars: 48,
  });

  const timelineItems = assistant.timelineItems ?? [];
  const [processExpanded, setProcessExpanded] = React.useState(false);
  const isLegacyAssistant =
    assistant.renderMode === 'legacy' && timelineItems.length === 0;
  const showUserBubble =
    Boolean(userMessage.text.trim()) || assistant.renderMode === 'structured';
  const isInbound = assistant.renderMode === 'inbound';
  const showAssistant =
    assistant.renderMode === 'structured' ||
    isInbound ||
    Boolean(assistant.answerMarkdown) ||
    assistant.isStreaming ||
    assistant.status === 'error' ||
    assistant.status === 'cancelled';
  inboundDebug.log(
    'group',
    'renderMode=',
    assistant.renderMode,
    'isInbound=',
    isInbound,
    'showAssistant=',
    showAssistant,
    'hasQuoted=',
    !!assistant.quotedMessage,
  );

  // ── 运行时状态判断（用于视觉映射）──
  const runtimeState = useMemo(() => {
    if (assistant.status === 'error' || assistant.status === 'cancelled')
      return 'error';
    if (assistant.status === 'success') return 'success';
    if (assistant.isStreaming && assistant.answerMarkdown) return 'streaming';
    const lastItem = timelineItems[timelineItems.length - 1];
    if (!lastItem) {
      if (assistant.status === 'thinking') return 'thinking';
      return 'idle';
    }
    if (lastItem.type === 'thinking') return 'thinking';
    if (lastItem.type === 'tool_call' || lastItem.type === 'tool_result') {
      const tone = getItemTone(lastItem);
      if (tone === 'executing') return 'tool_executing';
      if (tone === 'error') return 'error';
      return 'tool_executing';
    }
    return 'idle';
  }, [
    assistant.status,
    assistant.isStreaming,
    assistant.answerMarkdown,
    timelineItems,
  ]);

  // ── 渲染一个 Thinking 节点（默认摘要化 / 紫色记忆光）──
  const renderThinkingNode = (item: TimelineItem) => {
    const summary = makeThinkingSummary(item.text);
    const isMemoryRecall =
      (item.text || '').includes('记忆') ||
      (item.text || '').includes('检索') ||
      (item.text || '').includes('recall');
    const stateClass = isMemoryRecall
      ? styles.runtimeStateMemory
      : styles.runtimeStateThinking;
    const labelClass = isMemoryRecall
      ? styles.statusTextMemory
      : styles.statusTextThinking;
    const labelText = isMemoryRecall ? '正在检索记忆' : '正在思考';
    return (
      <div
        key={item.id}
        className={cx(
          styles.timelineNode,
          stateClass,
          styles.timelineNodeAppear,
        )}
        aria-label="思考节点"
      >
        <span
          className={cx(styles.toolSummaryStatus, labelClass)}
          style={{ display: 'block', marginBottom: 2, fontSize: 10 }}
        >
          {labelText}
        </span>
        <div className={styles.thinkingSummaryText}>{summary}</div>
      </div>
    );
  };

  // ── 渲染一个 Tool 节点（默认摘要化：工具名 + 状态色 + 耗时）──
  const renderToolNode = (item: TimelineItem) => {
    const tone = getItemTone(item);
    const isRunning = tone === 'executing';
    const toolName = item.name || item.status || '工具调用';
    const statusText = getToolStatusText(tone);
    const messagePreview = sanitizeDisplayText(
      item.message?.length && item.message.length > 200
        ? item.message.slice(0, 200) + '…'
        : item.message,
    );

    const statusLabelClass = isRunning
      ? styles.statusTextTool
      : tone === 'error'
        ? styles.statusTextError
        : styles.statusTextSuccess;

    const nodeClass = cx(
      styles.timelineNode,
      styles.timelineNodeAppear,
      isRunning ? styles.runtimeStateToolRunning : styles.runtimeStateTool,
      tone === 'success' && !isRunning && styles.runtimeStateSuccess,
      tone === 'error' && styles.runtimeStateError,
    );

    return (
      <div key={item.id} className={nodeClass} aria-label="工具节点">
        <div className={styles.toolSummaryRow}>
          <span className={styles.toolSummaryName}>{toolName}</span>
          <span className={cx(styles.toolSummaryStatus, statusLabelClass)}>
            {statusText}
          </span>
          <span className={styles.toolSummaryDuration}>
            {formatTime(item.timestamp)}
          </span>
          {tone === 'success' && item.message && (
            <span className={styles.toolSummaryResult}>
              {item.message.split('\n').length} 行结果
            </span>
          )}
        </div>

        {/* 失败时显示可重试 */}
        {tone === 'error' && (
          <div style={{ marginTop: 4 }}>
            <Text type="danger" style={{ fontSize: 11 }}>
              {messagePreview || '工具调用失败'}
            </Text>
            <button
              className={styles.toolRetryBtn}
              onClick={() => onRerunTurn?.(turn.turnId)}
              style={{ marginLeft: 8 }}
            >
              重试
            </button>
          </div>
        )}

        {/* 成功完成且有短输出时显示摘要 */}
        {tone === 'success' &&
          messagePreview &&
          messagePreview !== sanitizeDisplayText(item.message) && (
            <div
              className={styles.collapsedSummary}
              onClick={() => onToggleReasoning(turn.turnId, item.id)}
            >
              {messagePreview}
            </div>
          )}

        {/* 折叠区域：点击展开/收起详情 */}
        {item.message && item.message.length > 200 && item.collapsed && (
          <div
            className={styles.collapsedSummary}
            onClick={() => onToggleReasoning(turn.turnId, item.id)}
          >
            点击查看详情
          </div>
        )}
        {item.message && item.message.length > 200 && !item.collapsed && (
          <div>
            <div
              className={styles.collapsedSummary}
              onClick={() => onToggleReasoning(turn.turnId, item.id)}
            >
              收起详情
            </div>
            <div
              className={styles.thinkingExpandedText}
              style={{ fontFamily: 'monospace', fontSize: 11 }}
            >
              {item.message}
            </div>
          </div>
        )}
      </div>
    );
  };

  // ── 渲染单个时间线条目 ────────────────────────────────────
  const renderTimelineItem = (item: TimelineItem, _idx: number) => {
    if (item.type === 'thinking') return renderThinkingNode(item);
    return renderToolNode(item);
  };

  return (
    <div data-testid={`chat-message-${turn.turnId}`}>
      {/* ═══════════ 旧版兼容：legacy 模式保持原样 ═══════════ */}
      {isLegacyAssistant && showAssistant && (
        <div className={styles.avatarGroup}>
          <div className={styles.groupContent}>
            <div
              className={cx(
                styles.bubble,
                styles.agentBubble,
                assistant.status === 'error' && styles.errorBubble,
              )}
              onContextMenu={(e) =>
                onContextMenu(
                  e,
                  turn.turnId,
                  'assistant',
                  turn.assistant.answerMarkdown,
                )
              }
            >
              <MessageItem
                markdownText={assistant.answerMarkdown}
                isStreaming={assistant.isStreaming}
              />
            </div>
            <Space
              size={2}
              className={`${styles.messageActions} message-actions`}
            >
              <Tooltip title="复制">
                <Button
                  size="small"
                  type="text"
                  icon={<CopyOutlined />}
                  onClick={() =>
                    navigator.clipboard.writeText(assistant.answerMarkdown)
                  }
                />
              </Tooltip>
              <Tooltip title="重新生成">
                <Button
                  size="small"
                  type="text"
                  icon={<ReloadOutlined />}
                  onClick={() => onRerunTurn?.(turn.turnId)}
                />
              </Tooltip>
              <Tooltip title="固定">
                <Button
                  size="small"
                  type="text"
                  icon={<PushpinOutlined />}
                  onClick={() => onPinTurn?.(turn.turnId)}
                />
              </Tooltip>
              <Tooltip title="删除">
                <Button
                  size="small"
                  type="text"
                  danger
                  icon={<DeleteOutlined />}
                  onClick={() => onDeleteTurn(turn.turnId)}
                />
              </Tooltip>
            </Space>
          </div>
        </div>
      )}

      {/* ═══════════ Runtime Timeline（新结构）═══════════ */}
      {!isLegacyAssistant && showAssistant && (
        <>
          {/* ── 来源标识（多渠道头像+名称）── */}
          {turn.source && turn.source.sourceType !== 'agent' && (
            <div
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 6,
                marginBottom: 6,
                padding: '4px 8px',
                borderRadius: 6,
                fontSize: 12,
                background: 'var(--ant-color-bg-elevated)',
              }}
            >
              <span
                style={{
                  width: 20,
                  height: 20,
                  borderRadius: 10,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  background: turn.source.avatarColor,
                  fontSize: 12,
                }}
              >
                {turn.source.avatarEmoji}
              </span>
              <Text type="secondary" style={{ fontSize: 12 }}>
                {turn.source.displayName}
              </Text>
            </div>
          )}

          {/* ── 入站 Agent 消息卡片 ── */}
          {assistant.renderMode === 'inbound' && assistant.quotedMessage && (
            <div className={styles.inboundAgentCard}>
              <div className={styles.inboundAgentCardHeader}>
                <span className={styles.inboundAgentCardIcon}>🤖</span>
                <span>{assistant.quotedMessage.sourceName}</span>
                <span className={styles.inboundAgentCardLabel}>发来的消息</span>
                <Tooltip
                  title={dayjs(assistant.quotedMessage.createdAt).format(
                    'YYYY-MM-DD HH:mm:ss',
                  )}
                >
                  <Text className={styles.timeText}>
                    {formatTime(assistant.quotedMessage.createdAt)}
                  </Text>
                </Tooltip>
              </div>
              <div className={styles.inboundAgentCardBody}>
                <MessageItem
                  markdownText={assistant.quotedMessage.content}
                  isStreaming={false}
                  stableMarkdown={assistant.quotedMessage.content}
                />
              </div>
            </div>
          )}

          {/* ── 用户消息：保持原有头像+右对齐气泡风格 ── */}
          {showUserBubble && (
            <div className={cx(styles.avatarGroup, styles.userAvatarGroup)}>
              <div className={styles.avatarCol}>
                <div className={styles.avatarUserIcon}>
                  <UserOutlined />
                </div>
              </div>
              <div className={styles.userGroupContent}>
                <div className={styles.avatarNameRow}>
                  <Text className={styles.avatarName}>我</Text>
                  <Tooltip
                    title={dayjs(userMessage.timestamp).format(
                      'YYYY-MM-DD HH:mm:ss',
                    )}
                  >
                    <Text className={styles.timeText}>
                      {formatTime(userMessage.timestamp)}
                    </Text>
                  </Tooltip>
                  {userMessage.status === 'sending' && (
                    <Text className={styles.sendingText}>发送中...</Text>
                  )}
                </div>
                <div
                  className={cx(styles.bubble, styles.userBubble)}
                  onContextMenu={(e) =>
                    onContextMenu(e, turn.turnId, 'user', turn.userMessage.text)
                  }
                >
                  {userMessage.text}
                </div>
              </div>
            </div>
          )}

          {/* ── Agent Runtime Timeline ── */}
          {assistant.renderMode !== 'inbound' && (
            <div
              className={styles.timeline}
              aria-label="Runtime Timeline"
              aria-live="polite"
            >
              {/* ── 首 Token 等待 Loading（始终可见）── */}
              {assistant.isStreaming &&
                timelineItems.length === 0 &&
                !assistant.answerMarkdown && (
                  <div
                    className={styles.timelineNode}
                    style={{
                      borderLeft: '2px solid var(--memory-glow, #A78BFA)',
                      paddingLeft: 12,
                    }}
                  >
                    <div className={styles.firstTokenLoading}>
                      <div className={styles.pulseDot} />
                      <span className={styles.pulseLabel}>
                        Agent 正在思考...
                      </span>
                    </div>
                  </div>
                )}

              {/* ── P0 默认折叠：单行主状态（替代 Thinking/Tool 节点序列）── */}
              {!processExpanded && runtimeState !== 'idle' && (
                <div className={styles.mainStatusLine}>
                  {(runtimeState === 'thinking' ||
                    runtimeState === 'tool_executing' ||
                    runtimeState === 'streaming') && (
                    <span
                      className={cx(
                        styles.mainStatusDot,
                        styles.mainStatusDotActive,
                      )}
                    />
                  )}
                  <span>{getMainStatusText(runtimeState)}</span>
                  {runtimeState === 'success' &&
                    getCompletionSummary(timelineItems) && (
                      <>
                        <span className={styles.completionSummary}>
                          {getCompletionSummary(timelineItems)}
                        </span>
                        <span
                          className={styles.viewProcessLink}
                          onClick={() => setProcessExpanded(true)}
                        >
                          查看过程
                        </span>
                      </>
                    )}
                </div>
              )}

              {/* ── P0 展开态：完整 Timeline 节点序列 ── */}
              {processExpanded && (
                <>
                  <div
                    className={styles.collapseProcessLink}
                    onClick={() => setProcessExpanded(false)}
                  >
                    收起过程 ▲
                  </div>
                  {timelineItems.map((item, idx) =>
                    renderTimelineItem(item, idx),
                  )}
                </>
              )}

              {/* ── Final Answer：Ink Bloom 纸感卡片（稳定 Markdown + 墨迹尾段）── */}
              {(typewriter.stableMarkdown ||
                typewriter.liveText ||
                assistant.answerMarkdown) && (
                <div
                  className={cx(styles.timelineNode, styles.timelineNodeAnswer)}
                >
                  <div
                    className={cx(
                      styles.timelineAnswerBlock,
                      assistant.isStreaming && styles.paperStreaming,
                      !assistant.isStreaming && styles.paperSettled,
                      runtimeState === 'error' && styles.runtimeStateError,
                    )}
                    onContextMenu={(e) =>
                      onContextMenu(
                        e,
                        turn.turnId,
                        'assistant',
                        turn.assistant.answerMarkdown,
                      )
                    }
                  >
                    <MessageItem
                      markdownText={assistant.answerMarkdown}
                      isStreaming={assistant.isStreaming}
                      stableMarkdown={typewriter.stableMarkdown}
                      liveText={typewriter.liveText}
                      visibleLiveText={typewriter.visibleLiveText}
                      visibleStartOffset={typewriter.visibleStartOffset}
                    />
                  </div>
                </div>
              )}

              {/* ── 流式等待态（无内容但有 typewriter 标记）── */}
              {!typewriter.stableMarkdown &&
                !typewriter.liveText &&
                !assistant.answerMarkdown &&
                assistant.isStreaming &&
                timelineItems.length > 0 && (
                  <div
                    className={cx(
                      styles.timelineNode,
                      styles.runtimeStateStreaming,
                    )}
                  >
                    <span
                      className={cx(
                        styles.toolSummaryStatus,
                        styles.statusTextStreaming,
                      )}
                    >
                      正在生成...
                    </span>
                  </div>
                )}

              {/* ── 操作按钮（settle 淡入）── */}
              <Space
                size={2}
                className={cx(
                  styles.messageActions,
                  'message-actions',
                  (!assistant.isStreaming || typewriter.isSettling) &&
                    styles.actionButtonsSettled,
                )}
                style={{ paddingLeft: 28 }}
              >
                <Tooltip title="复制回答">
                  <Button
                    size="small"
                    type="text"
                    icon={<CopyOutlined />}
                    onClick={() =>
                      navigator.clipboard.writeText(assistant.answerMarkdown)
                    }
                  />
                </Tooltip>
                <Tooltip title="重新生成">
                  <Button
                    size="small"
                    type="text"
                    icon={<ReloadOutlined />}
                    onClick={() => onRerunTurn?.(turn.turnId)}
                  />
                </Tooltip>
                <Tooltip title="固定">
                  <Button
                    size="small"
                    type="text"
                    icon={<PushpinOutlined />}
                    onClick={() => onPinTurn?.(turn.turnId)}
                  />
                </Tooltip>
                <Tooltip title="删除">
                  <Button
                    size="small"
                    type="text"
                    danger
                    icon={<DeleteOutlined />}
                    onClick={() => onDeleteTurn(turn.turnId)}
                  />
                </Tooltip>
              </Space>

              {/* ── Token 用量（仅展开时显示）── */}
              {processExpanded && assistant.usage?.totalTokens ? (
                <div
                  style={{
                    paddingLeft: 28,
                    fontSize: 11,
                    color: 'var(--text-muted)',
                    opacity: 0.5,
                  }}
                >
                  {assistant.usage.totalTokens.toLocaleString()} tokens
                </div>
              ) : null}
            </div>
          )}
        </>
      )}
    </div>
  );
};

export default MessageGroup;

// ── MessageProcessSummary：过程摘要（默认折叠）─────────────
import { DownOutlined, UpOutlined } from '@ant-design/icons';
import React, { useState } from 'react';
import { useChatStyles } from '../styles';
import type { TimelineItem } from '../types';
import {
  buildProcessRounds,
  formatProcessDuration,
  getProcessMetrics,
  getProcessSummaryText,
  getToolDisplayName,
  getToolStatusTone,
  sanitizeProcessText,
} from './processPreview';

interface MessageProcessSummaryProps {
  items: TimelineItem[];
  status: string;
  onRerun?: () => void;
  onOpenDiagnostics?: () => void;
}

type ProcessDisplayItem =
  | {
      id: string;
      type: 'thinking';
      text: string;
      sourceItems: TimelineItem[];
    }
  | {
      id: string;
      type: 'item';
      item: TimelineItem;
    };

const buildDisplayItems = (items: TimelineItem[]): ProcessDisplayItem[] => {
  const displayItems: ProcessDisplayItem[] = [];
  let thinkingBuffer = '';
  let thinkingItems: TimelineItem[] = [];
  const flushThinking = () => {
    const text = sanitizeProcessText(thinkingBuffer, { compact: false });
    if (text && thinkingItems.length > 0) {
      displayItems.push({
        id: `thinking-group-${thinkingItems[0].id}`,
        type: 'thinking',
        text,
        sourceItems: thinkingItems,
      });
    }
    thinkingBuffer = '';
    thinkingItems = [];
  };

  for (const item of items) {
    if (item.type !== 'thinking') {
      flushThinking();
      displayItems.push({ id: item.id, type: 'item', item });
      continue;
    }

    const text =
      typeof item.text === 'string'
        ? item.text
            .replace(/(?:undefined|null|NaN)+/gi, '')
            .split('\u0000')
            .join('')
        : '';
    if (!text.trim()) continue;
    if (
      thinkingBuffer.length > 0 &&
      thinkingBuffer.length + text.length > 900
    ) {
      flushThinking();
    }
    thinkingBuffer += text;
    thinkingItems.push(item);
  }
  flushThinking();
  return displayItems;
};

const formatTimestamp = (value?: number): string | null => {
  if (!Number.isFinite(value)) return null;
  return new Date(value as number).toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
};

const buildTraceChips = (
  items: TimelineItem[],
): Array<{ label: string; value: string }> => {
  const metrics = getProcessMetrics(items);
  const timestamps = items
    .map((item) => item.timestamp)
    .filter(Number.isFinite);
  const chips: Array<{ label: string; value: string }> = [
    { label: '事件', value: `${items.length}` },
  ];

  if (metrics.thinkingSteps > 0)
    chips.push({ label: '思维', value: `${metrics.thinkingSteps}` });
  if (metrics.toolCalls > 0 || metrics.toolResults > 0) {
    chips.push({
      label: '工具',
      value: `${metrics.toolCalls}/${metrics.toolResults}`,
    });
  }
  if (metrics.subAgentCalls > 0)
    chips.push({ label: '子代理', value: `${metrics.subAgentCalls}` });
  if (metrics.failedTools > 0)
    chips.push({ label: '失败', value: `${metrics.failedTools}` });
  if (metrics.failedSubAgents > 0)
    chips.push({ label: '子代理失败', value: `${metrics.failedSubAgents}` });
  const duration = formatProcessDuration(metrics.durationMs);
  if (duration) chips.push({ label: '耗时', value: duration });
  if (timestamps.length > 0) {
    chips.push({
      label: '开始',
      value: formatTimestamp(Math.min(...timestamps)) ?? '-',
    });
  }
  return chips;
};

const buildToolDetailBlocks = (
  item: TimelineItem,
): Array<{ key: string; label: string; value: string }> => {
  const blocks = [
    {
      key: 'arguments',
      label: '参数',
      value: sanitizeProcessText(item.arguments, { compact: false }),
    },
    {
      key: 'output',
      label: '输出',
      value: sanitizeProcessText(item.output, { compact: false }),
    },
    {
      key: 'message',
      label: '消息',
      value: sanitizeProcessText(item.message || item.text, { compact: false }),
    },
  ];
  return blocks.filter((block) => block.value);
};

const getToolCompactDetail = (item: TimelineItem): string => {
  const blocks = buildToolDetailBlocks(item);
  const output = blocks.find((block) => block.key === 'output')?.value;
  const message = blocks.find((block) => block.key === 'message')?.value;
  const args = blocks.find((block) => block.key === 'arguments')?.value;
  return sanitizeProcessText(output || message || args, { maxLength: 120 });
};

const MessageProcessSummary: React.FC<MessageProcessSummaryProps> = ({
  items,
  status,
  onRerun,
  onOpenDiagnostics,
}) => {
  const { styles: rawStyles, cx } = useChatStyles();
  const styles = rawStyles as Record<string, string>;
  const [expanded, setExpanded] = useState(false);
  const [expandedItems, setExpandedItems] = useState<Set<string>>(new Set());

  if (!items || items.length === 0) {
    if (status === 'streaming') {
      return (
        <div className={styles.processSummaryRow}>
          <span className={styles.processThinkingLabel}>正在生成回复...</span>
        </div>
      );
    }
    if (status === 'thinking') {
      return (
        <div className={styles.processSummaryRow}>
          <span className={styles.processThinkingLabel}>正在思考...</span>
        </div>
      );
    }
    if (status === 'error') {
      return (
        <div className={styles.processSummaryRow}>
          <span className={styles.processRetryBtn} onClick={onRerun}>
            重试
          </span>
        </div>
      );
    }
    return null;
  }

  const summary = getProcessSummaryText(items);
  const rounds = buildProcessRounds(items);
  const displayItems = buildDisplayItems(items);
  const traceChips = buildTraceChips(items);
  const roundByItemId = new Map<string, number>();
  rounds.forEach((round) => {
    round.items.forEach((item) => roundByItemId.set(item.id, round.index));
  });
  const toggleExpandedItem = (id: string, shouldExpand: boolean) => {
    const next = new Set(expandedItems);
    if (shouldExpand) {
      next.add(id);
    } else {
      next.delete(id);
    }
    setExpandedItems(next);
  };

  if (!expanded) {
    return (
      <div className={styles.processSummaryRow}>
        <button
          type="button"
          onClick={() => setExpanded(true)}
          style={{
            all: 'unset',
            display: 'inline-flex',
            alignItems: 'center',
            gap: 6,
            cursor: 'pointer',
          }}
        >
          <span className={styles.processSummaryDot} />
          {summary && (
            <span className={styles.processSummaryText}>{summary}</span>
          )}
          <span className={styles.processSummaryLink}>查看过程</span>
        </button>
        {onOpenDiagnostics && (
          <button
            type="button"
            className={styles.processSummaryLink}
            onClick={(e) => {
              e.stopPropagation();
              onOpenDiagnostics();
            }}
          >
            诊断报告
          </button>
        )}
      </div>
    );
  }

  return (
    <div style={{ marginTop: 4 }}>
      <button
        type="button"
        className={styles.processCollapseLink}
        onClick={() => setExpanded(false)}
        style={{ all: 'unset', cursor: 'pointer' }}
      >
        <UpOutlined /> 收起过程
      </button>
      <div className={styles.processExpandedArea}>
        <div className={styles.processTraceSummary}>
          {traceChips.map((chip) => (
            <span key={chip.label} className={styles.processTraceChip}>
              <span className={styles.processTraceChipLabel}>{chip.label}</span>
              <span className={styles.processTraceChipValue}>{chip.value}</span>
            </span>
          ))}
        </div>
        {displayItems.map((displayItem, index) => {
          if (displayItem.type === 'thinking') {
            const first = displayItem.sourceItems[0];
            return (
              <section key={displayItem.id} className={styles.processRound}>
                <div className={styles.processRoundHeader}>
                  <div className={styles.processRoundTitle}>
                    思维消息 {index + 1}
                  </div>
                  <div className={styles.processRoundMeta}>
                    第 {roundByItemId.get(first.id) ?? 1} 轮 ·{' '}
                    {displayItem.sourceItems.length} 段
                  </div>
                </div>
                <div className={styles.processThinkingRaw}>
                  {displayItem.text}
                </div>
              </section>
            );
          }

          const item = displayItem.item;
          if (item.type === 'tool_call' || item.type === 'tool_result') {
            const tone = getToolStatusTone(item);
            const isExpanded = expandedItems.has(item.id);
            const toolName = getToolDisplayName(item);
            const statusClass =
              tone === 'running'
                ? styles.processItemStatusRunning
                : tone === 'error'
                  ? styles.processItemStatusError
                  : styles.processItemStatusSuccess;
            const statusLabel =
              tone === 'running'
                ? '执行中'
                : tone === 'error'
                  ? '失败'
                  : '完成';
            const detailBlocks = buildToolDetailBlocks(item);
            const detail = detailBlocks
              .map((block) => block.value)
              .join('\n\n');
            const compactDetail = getToolCompactDetail(item);

            return (
              <section key={item.id} className={styles.processRound}>
                <div className={styles.processRoundHeader}>
                  <div className={styles.processRoundTitle}>
                    {item.type === 'tool_call' ? '工具调用' : '工具结果'}{' '}
                    {index + 1}
                  </div>
                  <div className={styles.processRoundMeta}>
                    第 {roundByItemId.get(item.id) ?? 1} 轮
                  </div>
                </div>
                <div className={cx(styles.processItem, styles.processToolItem)}>
                  <div className={styles.processItemHeader}>
                    <span className={styles.processItemName}>{toolName}</span>
                    <span
                      className={`${styles.processItemStatus} ${statusClass}`}
                    >
                      {statusLabel}
                    </span>
                  </div>
                  {detailBlocks.length > 0 &&
                    (isExpanded ? (
                      <>
                        <button
                          type="button"
                          className={styles.processItemToggleButton}
                          onClick={(e) => {
                            e.stopPropagation();
                            toggleExpandedItem(item.id, false);
                          }}
                        >
                          <UpOutlined /> 收起工具详情
                        </button>
                        <div className={styles.processToolDetailBlocks}>
                          {detailBlocks.map((block) => (
                            <div
                              key={block.key}
                              className={styles.processToolDetailBlock}
                            >
                              <div className={styles.processToolDetailLabel}>
                                {block.label}
                              </div>
                              <pre className={styles.processToolDetailPre}>
                                {block.value}
                              </pre>
                            </div>
                          ))}
                        </div>
                      </>
                    ) : (
                      <>
                        {compactDetail && (
                          <div className={styles.processItemDetail}>
                            {compactDetail}
                          </div>
                        )}
                        {(detail.length > compactDetail.length ||
                          detailBlocks.length > 1) && (
                          <button
                            type="button"
                            className={styles.processItemToggleButton}
                            onClick={(e) => {
                              e.stopPropagation();
                              toggleExpandedItem(item.id, true);
                            }}
                          >
                            <DownOutlined /> 查看工具详情
                          </button>
                        )}
                      </>
                    ))}
                </div>
              </section>
            );
          }

          if (
            item.type === 'subagent_spawned' ||
            item.type === 'subagent_progress' ||
            item.type === 'subagent_completed'
          ) {
            const tone = getToolStatusTone(item);
            const statusClass =
              tone === 'running'
                ? styles.processItemStatusRunning
                : tone === 'error'
                  ? styles.processItemStatusError
                  : styles.processItemStatusSuccess;
            const statusLabel =
              tone === 'running'
                ? '运行中'
                : tone === 'error'
                  ? '失败'
                  : '完成';
            const title =
              item.type === 'subagent_spawned'
                ? '子代理调用'
                : item.type === 'subagent_progress'
                  ? '子代理进展'
                  : '子代理结果';
            const detailBlocks = buildToolDetailBlocks(item);
            return (
              <section key={item.id} className={styles.processRound}>
                <div className={styles.processRoundHeader}>
                  <div className={styles.processRoundTitle}>
                    {title} {index + 1}
                  </div>
                  <div className={styles.processRoundMeta}>
                    第 {roundByItemId.get(item.id) ?? 1} 轮
                  </div>
                </div>
                <div className={cx(styles.processItem, styles.processToolItem)}>
                  <div className={styles.processItemHeader}>
                    <span className={styles.processItemName}>
                      {getToolDisplayName(item)}
                    </span>
                    <span
                      className={`${styles.processItemStatus} ${statusClass}`}
                    >
                      {statusLabel}
                    </span>
                  </div>
                  {detailBlocks.map((block) => (
                    <div key={block.key} className={styles.processItemDetail}>
                      {block.label}：{block.value}
                    </div>
                  ))}
                </div>
              </section>
            );
          }

          const detail = sanitizeProcessText(item.message || item.text, {
            compact: false,
          });
          if (!detail) return null;
          return (
            <section key={item.id} className={styles.processRound}>
              <div className={styles.processItem}>
                <span className={styles.processItemName}>过程</span>
                <div className={styles.processItemDetail}>{detail}</div>
              </div>
            </section>
          );
        })}
        {status === 'error' && onRerun && (
          <button
            type="button"
            className={styles.processRetryBtn}
            onClick={onRerun}
          >
            重试
          </button>
        )}
      </div>
    </div>
  );
};

export default React.memo(MessageProcessSummary);

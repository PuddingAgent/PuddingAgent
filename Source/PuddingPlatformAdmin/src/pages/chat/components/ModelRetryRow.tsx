// ── ModelRetryRow：模型重试状态行（P1-2，对齐 deepseek-harness D3 ModelRetryItem）──────────
// 数据驱动：嗅探 processItems 中后端投影的 LLM retry 条目（DirectLlmClient 重试 summary：
// 「LLM call retry {n}/{max}.」/「LLM stream retry before first delta {n}/{max}.」，
// 经 ProcessSummaryItem.kind/text 投影进 TimelineItem：type='subconscious_step'、text=summary、
// message=底层错误）。
// 渲染单行：StateDot(warning) + 「模型重试中」 + (n/max) + 原因摘要（summarizeError，title 挂全量）。
// 多条 retry 条目 = 多次重试：折叠行取最新一条，展开体列历次重试时间线（timestamp + 原因摘要）。
// 数据无 deadline/delayMs → 不做倒计时，仅状态 + 次数 + 原因。
// 样式：自包含 createStyles（参考 toolcall.styles 的 card/行式模式，深底浅字卡省略，轻量即可）；
// 不触碰 toolcall.styles.ts / message.styles.ts / process.styles.ts。
import { createStyles } from 'antd-style';
import React, { useMemo, useState } from 'react';
import type { TimelineItem } from '../types';
import { summarizeError } from '../utils/summarizeError';
import { sanitizeProcessText } from './processPreview';
import StateDot from './StateDot';

export interface ModelRetryEntry {
  id: string;
  /** 重试序号（从文本 (n/max) 提取；无匹配时为 0） */
  attempt: number;
  /** 重试上限（从文本 (n/max) 提取；无匹配时为 0） */
  maxRetries: number;
  /** 原因摘要（summarizeError；message 优先，回退 text） */
  reasonSummary: string;
  /** 原因全量原文（title 悬浮展示） */
  reasonFull: string;
  timestamp: number;
  item: TimelineItem;
}

/**
 * 嗅探规则：kind 非 tool_call/tool_result 且文本命中 LLM retry 形态，或文本含「retry」。
 * 对齐 ALREADY_KNOWN：DirectLlmClient.cs L255「LLM call retry {attempt+1}/{maxRetries}.」
 * 与 L600「LLM stream retry before first delta {retryAttempt}/{maxRetries}.」。
 */
export const isModelRetryItem = (item: TimelineItem): boolean => {
  if (item.type === 'tool_call' || item.type === 'tool_result') return false;
  const text = sanitizeProcessText(item.text || item.message);
  if (!text) return false;
  return /LLM (call |stream )?retry/i.test(text) || /retry/i.test(text);
};

/** 从「LLM call retry 2/3.」类文本提取 (n/max)；无匹配返回 null。 */
export const parseRetryRatio = (
  text?: string,
): { attempt: number; maxRetries: number } | null => {
  const safe = sanitizeProcessText(text);
  const match = /(\d+)\s*\/\s*(\d+)/.exec(safe);
  if (!match) return null;
  const attempt = Number(match[1]);
  const maxRetries = Number(match[2]);
  if (
    !Number.isInteger(attempt) ||
    !Number.isInteger(maxRetries) ||
    attempt < 1 ||
    maxRetries < 1
  ) {
    return null;
  }
  return { attempt, maxRetries };
};

/**
 * 纯函数：筛出 retry 条目并按时间升序排列（展开时间线顺序；折叠行取末位 = 最新）。
 */
export const buildModelRetryEntries = (
  items?: TimelineItem[],
): ModelRetryEntry[] =>
  (items ?? [])
    .filter(isModelRetryItem)
    .map((item) => {
      const raw = sanitizeProcessText(item.message || item.text, {
        compact: false,
      });
      const err = summarizeError(raw);
      const ratio = parseRetryRatio(item.text);
      return {
        id: item.id,
        attempt: ratio?.attempt ?? 0,
        maxRetries: ratio?.maxRetries ?? 0,
        reasonSummary: err.summary,
        reasonFull: err.full,
        timestamp: item.timestamp,
        item,
      };
    })
    .sort((a, b) => a.timestamp - b.timestamp);

const useModelRetryStyles = createStyles(() => ({
  /** 列表容器：仅在有 retry 条目时渲染 */
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: 4,
    width: '100%',
    maxWidth: 'min(720px, 100%)',
    marginTop: 6,
    boxSizing: 'border-box' as const,
  },
  /** 单行：24px 高；button role 整行可点；hover/focus-visible 反馈 */
  row: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    minHeight: 24,
    padding: '0 8px',
    boxSizing: 'border-box' as const,
    borderRadius: 6,
    cursor: 'pointer',
    transition: 'background 150ms ease',
    overflow: 'hidden',
    '&:hover': {
      background:
        'color-mix(in srgb, var(--pudding-chat-text-subtle) 10%, transparent)',
    },
    '&:focus-visible': {
      outline: '2px solid var(--pudding-status-warning)',
      outlineOffset: -2,
    },
  },
  /** leading：16px 状态点（StateDot） */
  leading: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 16,
    height: 24,
  },
  /** 标题 14px */
  title: {
    flexShrink: 0,
    fontSize: 14,
    fontWeight: 600,
    lineHeight: '24px',
    whiteSpace: 'nowrap' as const,
    color: 'var(--pudding-chat-text)',
  },
  /** (n/max)：warning token + 等宽数字 */
  ratio: {
    flexShrink: 0,
    fontSize: 12,
    lineHeight: '24px',
    whiteSpace: 'nowrap' as const,
    fontVariantNumeric: 'tabular-nums' as const,
    color: 'var(--pudding-status-warning)',
  },
  /** 2×2 分隔点 */
  dotGrid: {
    flexShrink: 0,
    display: 'grid',
    gridTemplateColumns: 'repeat(2, 2px)',
    gridTemplateRows: 'repeat(2, 2px)',
    gap: 2,
  },
  dot: {
    width: 2,
    height: 2,
    borderRadius: '50%',
    background: 'var(--pudding-chat-text-subtle)',
    opacity: 0.55,
  },
  /** summary FILL 单行截断 */
  summary: {
    flex: 1,
    minWidth: 0,
    fontSize: 12,
    lineHeight: '24px',
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    color: 'var(--pudding-chat-text-muted)',
  },
  /** chevron：展开旋转 180° */
  chevron: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 16,
    height: 24,
    fontSize: 10,
    lineHeight: 1,
    color: 'var(--pudding-chat-text-subtle)',
    transition: 'transform 150ms ease',
  },
  chevronOpen: {
    transform: 'rotate(180deg)',
  },
  /** 展开体：历次重试时间线（轻量，深底卡省略） */
  expanded: {
    display: 'flex',
    flexDirection: 'column',
    gap: 6,
    padding: '0 8px 6px 32px',
    boxSizing: 'border-box' as const,
  },
  timelineRow: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: 8,
    fontSize: 12,
    lineHeight: 1.55,
  },
  timelineTime: {
    flexShrink: 0,
    minWidth: 76,
    fontSize: 11,
    lineHeight: '18px',
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    color: 'var(--pudding-chat-text-subtle)',
  },
  timelineBody: {
    flex: 1,
    minWidth: 0,
    color: 'var(--pudding-chat-text-muted)',
    wordBreak: 'break-word' as const,
  },
  timelineLabel: {
    color: 'var(--pudding-status-warning)',
    fontWeight: 600,
  },
}));

const formatTime = (timestamp: number): string => {
  if (!Number.isFinite(timestamp) || timestamp <= 0) return '';
  return new Date(timestamp).toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
};

export interface ModelRetryRowProps {
  /** 消息过程时间线（TimelineItem[]）；无 retry 条目时组件返回 null */
  items?: TimelineItem[];
}

/**
 * 模型重试状态行：单行摘要（StateDot(warning) + 模型重试中 + (n/max) + 原因摘要），
 * 整行可点展开历次重试时间线。仅当嗅探到 retry 条目时渲染。
 */
const ModelRetryRow: React.FC<ModelRetryRowProps> = ({ items }) => {
  const { styles, cx } = useModelRetryStyles();
  const [expanded, setExpanded] = useState(false);
  const entries = useMemo(() => buildModelRetryEntries(items), [items]);
  if (entries.length === 0) return null;

  const latest = entries[entries.length - 1];
  const ratioText =
    latest.attempt > 0 && latest.maxRetries > 0
      ? `(${latest.attempt}/${latest.maxRetries})`
      : '';
  const ariaLabel = ratioText ? `模型重试中 ${ratioText}` : '模型重试中';

  return (
    <div className={styles.list} data-testid="model-retry-list">
      {/* biome-ignore lint/a11y/useSemanticElements: 与 ToolCallRow 同范式，整行可点需 div role=button + Enter/Space 键盘 + aria-expanded */}
      <div
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        aria-label={ariaLabel}
        data-testid="model-retry-row"
        className={styles.row}
        onClick={() => setExpanded((value) => !value)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            setExpanded((value) => !value);
          }
        }}
      >
        <span className={styles.leading}>
          <StateDot state="warning" size={10} />
        </span>
        <span className={styles.title} data-testid="model-retry-title">
          模型重试中
        </span>
        {ratioText && (
          <span className={styles.ratio} data-testid="model-retry-ratio">
            {ratioText}
          </span>
        )}
        <span className={styles.dotGrid} aria-hidden="true">
          <span className={styles.dot} />
          <span className={styles.dot} />
          <span className={styles.dot} />
          <span className={styles.dot} />
        </span>
        {latest.reasonSummary && (
          <span
            className={styles.summary}
            title={latest.reasonFull || undefined}
            data-testid="model-retry-summary"
          >
            {latest.reasonSummary}
          </span>
        )}
        <span
          className={cx(styles.chevron, expanded && styles.chevronOpen)}
          aria-hidden="true"
        >
          ▾
        </span>
      </div>
      {expanded && (
        <div className={styles.expanded} data-testid="model-retry-expanded">
          {entries.map((entry) => (
            <div
              key={entry.id}
              className={styles.timelineRow}
              data-testid="model-retry-timeline-row"
            >
              <span className={styles.timelineTime}>
                {formatTime(entry.timestamp)}
              </span>
              <span className={styles.timelineBody}>
                {entry.attempt > 0 && entry.maxRetries > 0 && (
                  <span
                    className={styles.timelineLabel}
                    data-testid="model-retry-timeline-ratio"
                  >
                    ({entry.attempt}/{entry.maxRetries}){' '}
                  </span>
                )}
                {entry.reasonSummary ? (
                  <span title={entry.reasonFull || undefined}>
                    {entry.reasonSummary}
                  </span>
                ) : (
                  sanitizeProcessText(entry.item.text || entry.item.message, {
                    maxLength: 160,
                  })
                )}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};

export default React.memo(ModelRetryRow);

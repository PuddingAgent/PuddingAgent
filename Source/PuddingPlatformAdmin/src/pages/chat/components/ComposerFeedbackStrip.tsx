// ── ComposerFeedbackStrip：轻反馈带（上下文 · 记忆 · 子任务 N）──
import React from 'react';
import { useChatStyles } from '../styles';

export interface FeedbackState {
  /** 上下文服务是否参与 */
  context: boolean;
  /** 上下文窗口已使用百分比；用于上下文胶囊的边框风险色和从左到右填充。 */
  contextUsagePercentage?: number;
  /** 上下文窗口总量；可选，仅用于胶囊 tooltip/aria 的容量说明。 */
  contextLimitTokens?: number;
  /** 后端或 SSE 计算出的剩余上下文；可选，仅用于胶囊 tooltip/aria 的容量说明。 */
  contextRemainingTokens?: number;
  /** 记忆参考条数（0 时隐藏） */
  memoryCount: number;
  /** 索引是否可用 */
  indexAvailable: boolean;
  /** 当前会话可见的子任务数 */
  subAgentsRunning: number;
  /** 后台记忆整理是否运行中 */
  backgroundMemoryRunning: boolean;
}

interface ComposerFeedbackStripProps {
  state: FeedbackState;
  onClick?: () => void;
  onSubAgentsClick?: () => void;
}

const DOT_CLASSES = {
  context: { active: '#6f8f72', idle: '#ddd8d1', error: '#c4944c' },
  memory: { active: '#8b5cf6', idle: '#ddd8d1' },
  index: { active: '#5b9bd5', idle: '#ddd8d1', error: '#c4944c' },
};

const formatTokenCapacity = (tokens?: number): string | null => {
  if (typeof tokens !== 'number' || !Number.isFinite(tokens) || tokens < 0)
    return null;
  if (tokens >= 1000) return `${(tokens / 1000).toFixed(1)}k`;
  return String(Math.round(tokens));
};

interface ContextAuraStyle {
  itemStyle: React.CSSProperties;
  progressStyle: React.CSSProperties;
}

const createProgressStyle = (
  pct: number,
  gradient: string,
): React.CSSProperties =>
  ({
    width: `${pct}%`,
    '--composer-feedback-fill': gradient,
  }) as React.CSSProperties;

function getContextAura(percentage?: number): ContextAuraStyle | undefined {
  if (typeof percentage !== 'number' || !Number.isFinite(percentage))
    return undefined;
  const pct = Math.max(0, Math.min(100, percentage));
  if (percentage >= 70) {
    return {
      itemStyle: {
        borderColor: '#d84a3a',
        boxShadow:
          '0 0 0 1px rgba(216, 74, 58, 0.28), 0 0 10px rgba(216, 74, 58, 0.35)',
      },
      progressStyle: createProgressStyle(
        pct,
        'linear-gradient(90deg, rgba(216, 74, 58, 0.2), rgba(216, 74, 58, 0.32))',
      ),
    };
  }
  if (percentage >= 50) {
    return {
      itemStyle: {
        borderColor: '#d98b28',
        boxShadow:
          '0 0 0 1px rgba(217, 139, 40, 0.22), 0 0 8px rgba(217, 139, 40, 0.28)',
      },
      progressStyle: createProgressStyle(
        pct,
        'linear-gradient(90deg, rgba(217, 139, 40, 0.18), rgba(217, 139, 40, 0.28))',
      ),
    };
  }
  return {
    itemStyle: {
      borderColor: '#6f8f72',
      boxShadow:
        '0 0 0 1px rgba(111, 143, 114, 0.16), 0 0 7px rgba(111, 143, 114, 0.2)',
    },
    progressStyle: createProgressStyle(
      pct,
      'linear-gradient(90deg, rgba(111, 143, 114, 0.16), rgba(111, 143, 114, 0.24))',
    ),
  };
}

const ComposerFeedbackStrip: React.FC<ComposerFeedbackStripProps> = ({
  state,
  onClick,
  onSubAgentsClick,
}) => {
  const { styles } = useChatStyles();
  const contextAura = getContextAura(state.contextUsagePercentage);
  const contextRemainingLabel = formatTokenCapacity(
    state.contextRemainingTokens,
  );
  const contextLimitLabel = formatTokenCapacity(state.contextLimitTokens);
  const contextCapacityLabel =
    contextRemainingLabel && contextLimitLabel
      ? ` · 剩余 ${contextRemainingLabel} / ${contextLimitLabel}`
      : '';
  const items: {
    key: string;
    label: string;
    color: string;
    active: boolean;
    show: boolean;
    ariaLabel: string;
    onClick?: () => void;
    style?: React.CSSProperties;
    progressStyle?: React.CSSProperties;
  }[] = [];

  // 上下文
  items.push({
    key: 'context',
    label: '上下文',
    color: state.context
      ? DOT_CLASSES.context.active
      : DOT_CLASSES.context.idle,
    active: state.context,
    show: true,
    ariaLabel: `${state.context ? '上下文已启用' : '上下文待机'}${contextCapacityLabel}`,
    style: contextAura?.itemStyle,
    progressStyle: contextAura?.progressStyle,
  });

  // 记忆
  if (state.memoryCount > 0) {
    items.push({
      key: 'memory',
      label: `记忆 ${state.memoryCount}`,
      color: DOT_CLASSES.memory.active,
      active: true,
      show: true,
      ariaLabel: `${state.memoryCount} 条记忆参考`,
    });
  }

  // 索引
  if (state.indexAvailable) {
    items.push({
      key: 'index',
      label: '索引',
      color: DOT_CLASSES.index.active,
      active: true,
      show: true,
      ariaLabel: '索引可用',
    });
  }

  // 子代理
  items.push({
    key: 'subagents',
    label:
      state.subAgentsRunning > 0
        ? `子代理 ${state.subAgentsRunning}`
        : '子代理',
    color:
      state.subAgentsRunning > 0
        ? DOT_CLASSES.context.active
        : DOT_CLASSES.context.idle,
    active: state.subAgentsRunning > 0,
    show: true,
    ariaLabel:
      state.subAgentsRunning > 0
        ? `${state.subAgentsRunning} 个子代理运行中，打开子代理管理器`
        : '没有子代理运行，打开子代理管理器',
    onClick: onSubAgentsClick,
  });

  // 后台整理
  if (state.backgroundMemoryRunning) {
    items.push({
      key: 'background-memory',
      label: '后台',
      color: DOT_CLASSES.memory.active,
      active: true,
      show: true,
      ariaLabel: '后台记忆整理运行中',
    });
  }

  const visibleItems = items.filter((i) => i.show);

  return (
    <div
      className={styles.composerFeedbackStrip}
      role="group"
      aria-label="运行状态反馈"
    >
      {visibleItems.map((item, i) => (
        <React.Fragment key={item.key}>
          {i > 0 && <span className={styles.composerFeedbackSep}>·</span>}
          <span
            className={styles.composerFeedbackItem}
            data-active={item.active ? 'true' : undefined}
            aria-label={item.ariaLabel}
            title={item.ariaLabel}
            role="button"
            tabIndex={0}
            style={item.style}
            onClick={(event) => {
              if (item.onClick) {
                event.stopPropagation();
                item.onClick();
                return;
              }
              onClick?.();
            }}
            onKeyDown={(event) => {
              if (event.key !== 'Enter' && event.key !== ' ') return;
              event.preventDefault();
              if (item.onClick) {
                event.stopPropagation();
                item.onClick();
                return;
              }
              onClick?.();
            }}
          >
            {item.progressStyle && (
              <span
                aria-hidden="true"
                className={styles.composerFeedbackProgress}
                style={item.progressStyle}
              />
            )}
            <span
              className={styles.composerFeedbackDot}
              style={{ background: item.color }}
            />
            <span
              className={styles.composerFeedbackLabel}
              data-active={item.active ? 'true' : undefined}
            >
              {item.label}
            </span>
          </span>
        </React.Fragment>
      ))}
    </div>
  );
};

export default ComposerFeedbackStrip;

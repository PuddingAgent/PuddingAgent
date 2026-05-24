// ── ComposerFeedbackStrip：轻反馈带（上下文 · 记忆 · 子任务 N）──
import React from 'react';
import { useChatStyles } from '../styles';

export interface FeedbackState {
  /** 上下文服务是否参与 */
  context: boolean;
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
}

const DOT_CLASSES = {
  context: { active: '#6f8f72', idle: '#d1c9c0', error: '#c4944c' },
  memory: { active: '#8b5cf6', idle: '#d1c9c0' },
  index: { active: '#5b9bd5', idle: '#d1c9c0', error: '#c4944c' },
};

const ComposerFeedbackStrip: React.FC<ComposerFeedbackStripProps> = ({ state, onClick }) => {
  const { styles } = useChatStyles();
  const items: { label: string; color: string; show: boolean }[] = [];

  // 上下文
  items.push({
    label: '上下文',
    color: state.context ? DOT_CLASSES.context.active : DOT_CLASSES.context.idle,
    show: true,
  });

  // 记忆
  if (state.memoryCount > 0) {
    items.push({
      label: `记忆 ${state.memoryCount}`,
      color: DOT_CLASSES.memory.active,
      show: true,
    });
  }

  // 索引
  if (state.indexAvailable) {
    items.push({
      label: '索引',
      color: DOT_CLASSES.index.active,
      show: true,
    });
  }

  // 子代理
  items.push({
    label: state.subAgentsRunning > 0 ? `子任务 ${state.subAgentsRunning}` : '子任务 0',
    color: state.subAgentsRunning > 0 ? DOT_CLASSES.context.active : DOT_CLASSES.context.idle,
    show: true,
  });

  // 后台整理
  if (state.backgroundMemoryRunning) {
    items.push({
      label: '后台',
      color: DOT_CLASSES.memory.active,
      show: true,
    });
  }

  const visibleItems = items.filter(i => i.show);

  return (
    <div
      className={styles.composerFeedbackStrip}
      onClick={onClick}
      role="button"
      tabIndex={0}
      aria-label="查看运行状态详情"
    >
      {visibleItems.map((item, i) => (
        <React.Fragment key={item.label}>
          {i > 0 && <span className={styles.composerFeedbackSep}>·</span>}
          <span className={styles.composerFeedbackItem}>
            <span
              className={styles.composerFeedbackDot}
              style={{ background: item.color }}
            />
            <span className={styles.composerFeedbackLabel}>{item.label}</span>
          </span>
        </React.Fragment>
      ))}
    </div>
  );
};

export default ComposerFeedbackStrip;

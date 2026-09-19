// ── CompactionCard：上下文压缩专用卡（运行 / 完成 / 未完成三态）──
// 用户反馈（2026-09-19）：压缩卡片显示效果乱，未触发压缩也会亮起运行态。本卡只呈现
// 有事实支撑的内容——运行中 = 不定量扫描条 + 「已运行 Xs/Xm」+ 收敛字形（三根逐根
// 塌缩的竖条），不伪造百分比进度、不编造前端无从证实的阶段名；未完成必须原样露出
// 原因。动画在 prefers-reduced-motion 下全部降级。
import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import type { CurrentRunActivity } from './processPreview';

/** 仍算「在跑」的活动状态；其余一律视为终态。 */
const RUNNING_STATUSES = new Set([
  'running',
  'waiting_output',
  'processing_result',
]);

const RUNNING_HINT = '整理早期对话腾出上下文窗口；压缩期间新消息进入队列';
const SUCCESS_HINT = '摘要已写回上下文，后续按压缩后的记录继续';
const INTERRUPTED_HINT = '没有收到终态记录，本轮压缩已结束；需要时可手动重新压缩';

const formatElapsed = (ms: number): string => {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  return seconds < 60
    ? `${seconds}s`
    : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
};

const CompactionCard: React.FC<{ activity: CurrentRunActivity }> = ({
  activity,
}) => {
  const { styles } = useChatMessageStyles();
  const running = RUNNING_STATUSES.has(activity.status);
  const interrupted = activity.status === 'failed';
  const [now, setNow] = React.useState(() => Date.now());

  // 只有运行中才 tick：终态耗时取自事件时间，刷新不归零也不继续走。
  React.useEffect(() => {
    if (!running) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [running]);

  const startedAt = activity.startedAt;
  const elapsedMs = startedAt
    ? Math.max(
        0,
        (running ? now : (activity.updatedAt ?? startedAt)) - startedAt,
      )
    : null;

  return (
    <div
      className={`${styles.compactionCard} ${
        running
          ? ''
          : interrupted
            ? styles.compactionCardInterrupted
            : styles.compactionCardSuccess
      }`}
      data-testid="compaction-card"
      data-state={
        running ? 'running' : interrupted ? 'interrupted' : 'completed'
      }
      role="status"
      aria-live="polite"
    >
      <div className={styles.compactionHeader}>
        <span
          className={styles.compactionGlyph}
          data-running={running}
          aria-hidden="true"
        >
          <span />
          <span />
          <span />
        </span>
        <span className={styles.compactionTitle}>
          {interrupted
            ? '上下文压缩未完成'
            : running
              ? activity.title
              : '上下文压缩完成'}
        </span>
        {elapsedMs !== null && (
          <span
            className={styles.compactionElapsed}
            data-testid="compaction-elapsed"
          >
            {running ? '已运行' : '耗时'} {formatElapsed(elapsedMs)}
          </span>
        )}
      </div>
      <div className={styles.compactionRail} data-running={running} aria-hidden="true">
        {running && (
          <span
            className={styles.compactionRailSweep}
            data-testid="compaction-sweep"
          />
        )}
      </div>
      <div className={styles.compactionHint}>
        {running ? RUNNING_HINT : interrupted ? INTERRUPTED_HINT : SUCCESS_HINT}
      </div>
      {interrupted && activity.outputPreview && (
        <div className={styles.compactionReason}>{activity.outputPreview}</div>
      )}
    </div>
  );
};

export default CompactionCard;

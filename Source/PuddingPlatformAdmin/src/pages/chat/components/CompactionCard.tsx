// ── CompactionCard：上下文压缩专用卡（运行 / 完成 / 未完成）──
// 设计（用户 2026-09-19 第二次反馈：旧形态「并不好看」）：收敛到一行表达 ——
//   · 运行中：一行流光文本「正在压缩上下文…」+ 右侧「已运行 Xs」；
//   · 完成：一行居中标记「—— 已完成压缩 ✓ ——」+ 右侧「耗时 Xs」；
//   · 未完成：居中标记「—— 压缩未完成 ——」+ 原样露出原因 + 一句可操作提示。
// 不伪造百分比进度、不编造前端无从证实的阶段名；终态耗时取自事件时间，刷新不归零
// 也不继续走；流光动画在 prefers-reduced-motion 下回落为静态弱色文本。
import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import type { CurrentRunActivity } from './processPreview';

/** 仍算「在跑」的活动状态；其余一律视为终态。 */
const RUNNING_STATUSES = new Set([
  'running',
  'waiting_output',
  'processing_result',
]);

const RUNNING_TEXT = '正在压缩上下文…';
const SUCCESS_MARKER = '—— 已完成压缩 ✓ ——';
const INTERRUPTED_MARKER = '—— 压缩未完成 ——';
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
        {running ? (
          <span
            className={styles.compactionShimmer}
            data-testid="compaction-shimmer"
          >
            {RUNNING_TEXT}
          </span>
        ) : (
          <span
            className={styles.compactionMarker}
            data-testid="compaction-marker"
          >
            {interrupted ? INTERRUPTED_MARKER : SUCCESS_MARKER}
          </span>
        )}
        {elapsedMs !== null && (
          <span
            className={styles.compactionElapsed}
            data-testid="compaction-elapsed"
          >
            {running ? '已运行' : '耗时'} {formatElapsed(elapsedMs)}
          </span>
        )}
      </div>
      {interrupted && (
        <>
          {activity.outputPreview && (
            <div className={styles.compactionReason}>{activity.outputPreview}</div>
          )}
          <div className={styles.compactionHint}>{INTERRUPTED_HINT}</div>
        </>
      )}
    </div>
  );
};

export default CompactionCard;

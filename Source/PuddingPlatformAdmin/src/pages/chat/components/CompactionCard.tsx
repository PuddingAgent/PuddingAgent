// ── CompactionCard：上下文压缩专用卡（运行 / 完成 / 未完成三态）──
// 用户反馈（2026-09-19）：压缩卡片显示效果乱，而且未触发压缩时也会亮起运行态。
// 本卡只呈现有事实支撑的内容：
//   · 运行中 = 不定量扫描条 + 「已运行 Xs/Xm」+ 收敛字形（三根塌缩竖条），
//     不伪造百分比进度，也不编造「提取事实 / 生成摘要」一类前端无从证实的阶段名；
//   · 完成 = 明确收口文案 + 一次性 settle 动画，不再留任何运行态痕迹；
//   · 未完成 = 明确说明「没有收到终态记录」，把失败原因原样放出来供排查。
import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import type { CurrentRunActivity } from './processPreview';

/** 仍算「在跑」的活动状态（其余一律视为终态）。 */
const RUNNING_STATUSES = new Set<string>([
  'running',
  'waiting_output',
  'processing_result',
]);

const RUNNING_HINT =
  '整理早期对话，为后续推理腾出上下文窗口；压缩期间新消息进入队列';
const SUCCESS_HINT = '摘要已写回上下文，后续轮次按压缩后的记录继续';
const INTERRUPTED_HINT =
  '没有收到终态记录，本轮压缩已结束；需要时请手动重新压缩';

const COMPLETED_TITLE = '上下文压缩完成';
const INTERRUPTED_TITLE = '上下文压缩未完成';

const formatElapsed = (ms: number): string => {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  if (minutes < 60) return `${minutes}m ${totalSeconds % 60}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
};

interface CompactionCardProps {
  activity: CurrentRunActivity;
}

const CompactionCard: React.FC<CompactionCardProps> = ({ activity }) => {
  const { styles } = useChatMessageStyles();
  const running = RUNNING_STATUSES.has(activity.status);
  const interrupted = activity.status === 'failed';
  const [now, setNow] = React.useState(() => Date.now());

  // 只有运行中才需要 tick：终态耗时取自事件时间，刷新不归零也不继续走。
  React.useEffect(() => {
    if (!running) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [running]);

  const elapsedMs = (() => {
    const startedAt = activity.startedAt;
    if (!startedAt) return null;
    if (running) return Math.max(0, now - startedAt);
    const endedAt = activity.updatedAt ?? startedAt;
    return Math.max(0, endedAt - startedAt);
  })();

  const reason = interrupted ? activity.outputPreview || undefined : undefined;
  const title = interrupted
    ? INTERRUPTED_TITLE
    : running
      ? activity.title
      : COMPLETED_TITLE;

  return (
    <div
      className={[
        styles.compactionCard,
        running ? '' : styles.compactionCardSettle,
        interrupted
          ? styles.compactionCardInterrupted
          : running
            ? ''
            : styles.compactionCardSuccess,
      ]
        .filter(Boolean)
        .join(' ')}
      data-testid="compaction-card"
      data-state={running ? 'running' : interrupted ? 'interrupted' : 'completed'}
      role="status"
      aria-live="polite"
    >
      <div className={styles.compactionHeader}>
        <span className={styles.compactionGlyph} aria-hidden="true">
          {[0, 1, 2].map((index) => (
            <span
              key={index}
              className={[
                styles.compactionGlyphBar,
                running ? '' : styles.compactionGlyphBarStatic,
              ]
                .filter(Boolean)
                .join(' ')}
              style={{ height: 14 - index * 4, animationDelay: `${index * 180}ms` }}
            />
          ))}
        </span>
        <span className={styles.compactionTitle}>{title}</span>
        {elapsedMs !== null && (
          <span
            className={styles.compactionElapsed}
            data-testid="compaction-elapsed"
          >
            {running ? '已运行' : '耗时'} {formatElapsed(elapsedMs)}
          </span>
        )}
      </div>
      <div className={styles.compactionRail} aria-hidden="true">
        {running ? (
          <span className={styles.compactionRailSweep} data-testid="compaction-sweep" />
        ) : (
          <span className={styles.compactionRailFill} />
        )}
      </div>
      <div className={styles.compactionHint}>
        {running ? RUNNING_HINT : interrupted ? INTERRUPTED_HINT : SUCCESS_HINT}
      </div>
      {reason && <div className={styles.compactionReason}>{reason}</div>}
    </div>
  );
};

export default React.memo(CompactionCard);

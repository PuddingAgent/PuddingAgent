import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import type { CurrentRunActivity } from './processPreview';

export const COMPACTION_VERIFICATION_LEASE_MS = 30_000;
const elapsed = (ms: number) => {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  return seconds < 60 ? `${seconds} 秒` : `${Math.floor(seconds / 60)} 分 ${seconds % 60} 秒`;
};

/** Animation requires a recent server confirmation, never a historical start. */
const CompactionCard: React.FC<{ activity: CurrentRunActivity }> = ({ activity }) => {
  const { styles } = useChatMessageStyles();
  const compact = activity.compaction;
  const [now, setNow] = React.useState(Date.now);
  const running = compact?.state === 'running' && compact.verifiedAt !== undefined &&
    now - compact.verifiedAt < COMPACTION_VERIFICATION_LEASE_MS;
  const state = compact?.state === 'running' && !running ? 'unknown' : compact?.state ?? 'unknown';
  React.useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [running]);
  const labels = { running: '正在整理上下文', checking: '压缩请求已提交',
    completed: '上下文已整理', skipped: '本次未执行压缩', failed: '上下文整理失败', unknown: '压缩状态待确认' };
  const hint = running ? '正在保留关键信息并整理历史内容，请稍候。'
    : state === 'checking' ? '正在检查是否需要整理上下文。'
    : state === 'unknown' ? '目前无法确认执行状态，连接恢复后会自动更新，无需重复提交。'
    : state === 'failed' ? activity.outputPreview
    : state === 'skipped' ? '候选内容、冷却或收益检查未通过，上下文保持原状。'
    : state === 'completed' ? activity.outputPreview : undefined;
  const duration = compact?.startedAt !== undefined && (running || compact.endedAt !== undefined)
    ? (running ? now : compact.endedAt!) - compact.startedAt : undefined;
  return (
    <section className={styles.compactionCard} data-testid="compaction-card" data-state={state} role="status" aria-live="polite" aria-busy={running}>
      <div className={styles.compactionHeader}>
        <span className={running ? styles.compactionActivityIcon : styles.compactionStaticIcon} aria-hidden="true">{state === 'completed' ? '✓' : '▤'}</span>
        <span className={styles.compactionTitle}>{labels[state]}</span>
        {duration !== undefined && <span className={styles.compactionElapsed} data-testid="compaction-elapsed">{running ? '已用时' : '耗时'} {elapsed(duration)}</span>}
        <span className={styles.compactionBadge}>{running ? '执行中' : state === 'checking' ? '等待确认' : '记录'}</span>
      </div>
      {hint && <div className={styles.compactionHint}>{hint}</div>}
      {running && <div className={styles.compactionTrack} data-testid="compaction-animation"><span /></div>}
      {!running && compact?.endedAt !== undefined && <time className={styles.compactionElapsed}>{new Date(compact.endedAt).toLocaleString('zh-CN', { hour12: false })}</time>}
    </section>
  );
};
export default CompactionCard;

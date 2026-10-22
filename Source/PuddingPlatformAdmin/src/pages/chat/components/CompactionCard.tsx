import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import type { CurrentRunActivity } from './processPreview';

export const COMPACTION_VERIFICATION_LEASE_MS = 30_000;
const elapsed = (ms: number) => {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  return seconds < 60 ? `${seconds} 秒` : `${Math.floor(seconds / 60)} 分 ${seconds % 60} 秒`;
};

const validTime = (value?: number): value is number =>
  value !== undefined && Number.isFinite(value) && !Number.isNaN(new Date(value).getTime());

/** A folding stack suggests consolidation without pretending to measure progress. */
const CompactionGlyph: React.FC<{ state: string; animated: boolean; className: string }> = ({ state, animated, className }) => (
  <span className={className} data-animated={animated} data-testid={animated ? 'compaction-animation' : undefined} aria-hidden="true">
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
      {state === 'completed' ? <path d="m6 12 4 4 8-8" />
        : state === 'failed' ? <><path d="m12 4 9 16H3L12 4Z" /><path d="M12 9v4m0 3h.01" /></>
        : state === 'unknown' ? <><circle cx="12" cy="12" r="8" /><path d="M10 9a2 2 0 0 1 4 0c0 2-2 2-2 4m0 3h.01" /></>
        : <><path data-layer="top" d="m4 7 8-4 8 4-8 4-8-4Z" /><path data-layer="middle" d="m4 12 8 4 8-4" /><path data-layer="bottom" d="m4 17 8 4 8-4" /></>}
    </svg>
  </span>
);

/** Only recently verified work may animate; details and timers never enter the live region. */
const CompactionCard: React.FC<{ activity: CurrentRunActivity }> = ({ activity }) => {
  const { styles } = useChatMessageStyles();
  const compact = activity.compaction;
  const titleId = React.useId();
  const [now, setNow] = React.useState(Date.now);
  const currentTime = Math.max(now, Date.now());
  const running = compact?.state === 'running' && validTime(compact.verifiedAt) &&
    currentTime - compact.verifiedAt < COMPACTION_VERIFICATION_LEASE_MS;
  const state = compact?.state === 'running' && !running ? 'unknown' : compact?.state ?? 'unknown';
  const stage = running ? compact?.stage : undefined;
  const prepared = stage === 'prepared';
  const blocking = stage === 'waiting' || stage === 'applying';
  const animated = running && !prepared;
  const terminal = state === 'completed' || state === 'skipped';
  React.useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [running, compact?.id]);

  const labels = {
    running: '正在整理上下文', checking: '正在确认整理请求', completed: '上下文已整理',
    skipped: '上下文保持不变', failed: '上下文整理未完成', unknown: '整理状态待确认',
  };
  const phaseLabels = {
    preparing: '正在准备上下文摘要', prepared: '摘要已准备好',
    waiting: '正在整理上下文', applying: '正在保存整理结果',
  };
  const title = stage ? phaseLabels[stage] : labels[state];
  const badge = blocking ? '请求暂缓' : prepared ? '等待切换'
    : running ? compact?.background ? '后台整理' : '进行中'
    : state === 'completed' ? '已保存' : state === 'failed' ? '需关注'
    : state === 'checking' || state === 'unknown' ? '待确认' : '未改动';
  const hint = blocking ? '下一次模型请求将在整理完成后继续。你仍可发送消息。'
    : prepared ? '将在合适的请求间隙切换，当前任务继续。'
    : running ? compact?.background ? '不影响当前任务，你可以继续发送消息。' : '正在保留关键信息，原始会话记录仍保留。'
    : state === 'checking' ? '正在确认是否需要整理，暂不代表已经开始。'
    : state === 'unknown' ? '暂时无法确认状态，连接恢复后会自动更新。无需重复提交。'
    : state === 'failed' ? '原始会话仍保留。展开详情查看原因。'
    : state === 'skipped' ? '本次没有替换上下文，原始会话仍保留。'
    : '关键信息已整理为摘要，原始会话仍保留。';
  const duration = validTime(compact?.startedAt) && (running || validTime(compact?.endedAt))
    ? (running ? currentTime : compact!.endedAt!) - compact!.startedAt! : undefined;
  const detail = (activity.outputFull || activity.outputPreview)?.trim();
  const step = stage === 'applying' ? 2 : prepared ? 1 : 0;
  const header = <>
    <CompactionGlyph state={state} animated={animated} className={styles.compactionGlyph} />
    <span className={styles.compactionHeading}>
      <span id={titleId} className={styles.compactionTitle}>{title}</span>
      <span className={styles.compactionBadge}>{badge}</span>
    </span>
    {duration !== undefined && <span className={styles.compactionElapsed} data-testid="compaction-elapsed">{running ? '已用时' : '耗时'} {elapsed(duration)}</span>}
  </>;
  const details = <div className={styles.compactionDetailBody}>
    {terminal && <p className={styles.compactionHint}>{hint}</p>}
    {detail && <pre className={styles.compactionReason}>{detail}</pre>}
    <dl className={styles.compactionMetadata}>
      {validTime(compact?.endedAt) && <><dt>结束时间</dt><dd><time dateTime={new Date(compact.endedAt).toISOString()}>{new Date(compact.endedAt).toLocaleString('zh-CN', { hour12: false })}</time></dd></>}
      {compact?.id && <><dt>记录 ID</dt><dd>{compact.id}</dd></>}
    </dl>
  </div>;
  return (
    <section className={styles.compactionCard} data-testid="compaction-card" data-state={state} data-stage={stage} data-blocking={blocking} aria-labelledby={titleId}>
      <span className={styles.compactionAnnouncement} role="status" aria-live="polite" aria-atomic="true">{title} · {badge}</span>
      {terminal ? <details className={styles.compactionDisclosure}>
        <summary className={styles.compactionHeader}>
          {header}<span className={styles.compactionDisclosureLabel}>详情</span><span className={styles.compactionChevron} aria-hidden="true">⌄</span>
        </summary>
        {details}
      </details> : <>
        <div className={styles.compactionHeader}>{header}</div>
        <div className={styles.compactionBody}>
          <p className={styles.compactionHint}>{hint}</p>
          {running && stage && <ol className={styles.compactionSteps} aria-label="上下文整理阶段">
            {['准备摘要', '等待切换', '保存结果'].map((label, index) => (
              <li key={label} data-step={index < step ? 'done' : index === step ? 'current' : 'pending'} aria-current={index === step ? 'step' : undefined}>
                <span aria-hidden="true">{index < step ? '✓' : index + 1}</span>{label}
              </li>
            ))}
          </ol>}
          {(detail || compact?.id) && <details className={styles.compactionDisclosure}>
            <summary className={styles.compactionDetailToggle}>查看详情<span className={styles.compactionChevron} aria-hidden="true">⌄</span></summary>
            {details}
          </details>}
        </div>
      </>}
    </section>
  );
};
export default CompactionCard;

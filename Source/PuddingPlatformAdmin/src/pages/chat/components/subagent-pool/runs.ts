// ── 运行历史的纯展示逻辑 ────────────────────────────────────────
// 纯函数，不依赖 React / antd，便于单测（与 avatar.ts / errors.ts 同一约定）。
import dayjs from 'dayjs';
import type { SubAgentRun } from './api';

const DASH = '—';

/**
 * 运行状态 → 中文标签。
 *
 * 词表来自 `SubAgentRunEntity`：非终态只有 `running`；终态有
 * completed / failed / cancelled / paused / budget_exhausted / timed_out / interrupted。
 * 未知值**原样返回**而不是归到「已完成」—— 把未知状态显示成成功，
 * 会让操作者以为一次没跑完的执行已经跑完了，而这个错误的代价不可逆。
 */
const RUN_STATUS_LABEL: Record<string, string> = {
  running: '运行中',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消',
  paused: '已暂停',
  budget_exhausted: '预算耗尽',
  timed_out: '超时',
  interrupted: '已中断',
};

export const describeRunStatus = (status: string): string =>
  RUN_STATUS_LABEL[status] ?? status;

/**
 * 耗时展示。
 *
 * 后端在 `running` 期间把 `total_duration_ms` 写成 0（尚未结算），
 * 所以 0 必须显示成「—」。显示成「0.0s」是在断言一次执行瞬间完成 —— 那是假话。
 */
export const describeRunDuration = (ms: number | null | undefined): string => {
  if (typeof ms !== 'number' || !Number.isFinite(ms) || ms <= 0) return DASH;
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const totalSeconds = Math.round(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}m${String(seconds).padStart(2, '0')}s`;
};

/** 开始时刻（本地时区，精确到分钟）；无法解析时退回原串而不是显示空。 */
export const formatRunStartedAt = (value: string | null | undefined): string => {
  if (!value) return DASH;
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format('MM-DD HH:mm') : value;
};

/**
 * 一行摘要：`11-24 06:33 · 已完成 · 8 轮 · 23 次工具 · 41.2s`。
 *
 * 用开始时刻而不是 runId 打头：运维要看的是「什么时候跑的」，
 * `run_20261124_063322_a16babc8` 这种标识对扫视没有帮助。
 */
export const describeRunSummary = (
  run: Pick<
    SubAgentRun,
    'startedAt' | 'status' | 'totalRounds' | 'totalToolCalls' | 'totalDurationMs'
  >,
): string =>
  [
    formatRunStartedAt(run.startedAt),
    describeRunStatus(run.status),
    `${run.totalRounds ?? 0} 轮`,
    `${run.totalToolCalls ?? 0} 次工具`,
    describeRunDuration(run.totalDurationMs),
  ].join(' · ');

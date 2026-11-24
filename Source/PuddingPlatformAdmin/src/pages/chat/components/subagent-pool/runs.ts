// ── 运行历史的纯展示逻辑 ────────────────────────────────────────
// 纯函数，不依赖 React / antd，便于单测（与 avatar.ts / errors.ts 同一约定）。
import dayjs from 'dayjs';
import type { SubAgentRun, SubAgentSpend } from './api';

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

/**
 * 美元金额。
 *
 * **精度随量级变化是故意的：** 子代理一次调用实测低至 $0.0033，
 * 统一 `toFixed(2)` 会把它显示成 `$0.00` —— 那是把「花了一点」说成「没花」，
 * 而用户看这列正是为了判断花了多少。所以量级越小给的有效位越多，
 * 低于万分之一时改用不等式，而不是默不作声地归零。
 */
export const formatUsd = (value: number): string => {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return DASH;
  if (value === 0) return '$0';
  if (value >= 1) return `$${value.toFixed(2)}`;
  if (value >= 0.01) return `$${value.toFixed(3)}`;
  if (value >= 0.0001) return `$${value.toFixed(4)}`;
  return '<$0.0001';
};

/** token 数的粗粒度展示：`17.13M`、`291.7K`。保留两位是为了能看出量级差异而不是精确值。 */
export const formatTokenCount = (value: number): string => {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return DASH;
  if (value >= 1_000_000_000) return `${(value / 1_000_000_000).toFixed(2)}B`;
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(2)}M`;
  if (value >= 1_000) return `${(value / 1_000).toFixed(1)}K`;
  return String(value);
};

/** 花费汇总的展示形状；`null` 表示「不该渲染这一块」。 */
export interface SpendSummary {
  /** 金额（或「全部未计价」/「无可计量用量」这类非金额结论）。 */
  amount: string;
  /** 辅助行：token 总量与调用次数。 */
  detail: string;
  /** 需要人知道的限定；没有则为 null。 */
  warning: string | null;
}

/**
 * 花费汇总展示。
 *
 * 四种状态必须分开，不能归并成一条金额：
 * - `spend` 为空 → 从未产生账本记录（不渲染）
 * - 有已计价、有未计价 → 金额 + 「另有 N 次没有价格」
 * - 全部未计价 → 「全部未计价」（而不是 `$0`）
 * - 有记录但既无已计价也无未计价 → 「无可计量用量」（失败请求，不是缺价）
 *
 * 把后两种显示成 `$0` 会在两个不同原因上给出同一个错误结论：
 * 前者是「价格没登记」，后者是「根本没量」，都不是「免费」。
 */
export const describeSpend = (
  spend: SubAgentSpend | null | undefined,
): SpendSummary | null => {
  if (!spend) return null;

  const priced = typeof spend.pricedCalls === 'number' ? spend.pricedCalls : 0;
  const unpriced = typeof spend.unpricedCalls === 'number' ? spend.unpricedCalls : 0;
  const totalTokens = typeof spend.totalTokens === 'number' ? spend.totalTokens : 0;
  const calls = priced + unpriced;

  let amount: string;
  if (priced > 0) amount = formatUsd(spend.pricedCostUsd);
  else if (unpriced > 0) amount = '全部未计价';
  else amount = '无可计量用量';

  const detail = `${formatTokenCount(totalTokens)} tokens · ${calls} 次调用`;
  const warning =
    unpriced > 0 && priced > 0 ? `另有 ${unpriced} 次没有价格，未计入金额` : null;

  return { amount, detail, warning };
};

/**
 * 列表用的**极简**花费：只给一个能一眼扫完的短串；无账本时返回 `null`（不渲染）。
 *
 * 与 `describeSpend` 的区别只在长度，不在结论：金额、未计价、无用量三者分开的口径完全复用后者，
 * 不另写一套判定（两处各写一遍 ⇒ 同一条目左右两栏会给出不同金额，而两者都「看起来对」）。
 *
 * 有未计价调用时把金额写成 `$1.56+`：那笔钱确实**至少**这么多，多出来的部分没有价格。
 * 已经是下界的不等式（`<$0.0001`）不再叠加 `+`，否则会拼出 `<$0.0001+` 这种自相矛盾的串。
 */
export const describeSpendBrief = (
  spend: SubAgentSpend | null | undefined,
): string | null => {
  const summary = describeSpend(spend);
  if (summary === null) return null;
  if (summary.warning !== null && summary.amount.startsWith('$')) {
    return `${summary.amount}+`;
  }
  return summary.amount;
};

// ── TurnTimingPanel：本轮耗时 / 首包明细（只渲染真正采到的事实）──────────
// 契约真源：`TurnTimings`（`services/platform/api.ts`，手写冻结契约）
//   ← 后端 `AgentTurnTimingCollector.ToPayload()`，随终端 `done` 帧下发（= 前端 `turn.completed`）。
//
// 纪律（本文件存在的唯一理由）：
//   **未采集 ≠ 0**。`null` / `undefined` / 非有限数一律渲染「未采集」；
//   反过来，真实采到的 `0` 必须渲染成 `0 ms` / `0 次`（不得被折叠成「未采集」）。
//   这层判据放在纯函数里（`formatTimingMs` / `formatCount` / `formatTokens`），页面 JSX 不再分叉。
//
// 独立成文件（而不是塞进 IntentConsole）的原因：chat 首屏有硬性包预算门禁
// （`scripts/check-chat-bundle-budget.cjs`），IntentConsole 已经很重。
import React from 'react';
import type { TurnTimings } from '@/services/platform/api';
import { useChatStyles } from '../styles';

/** 未采集的唯一统一文案（避免各处写「—」「N/A」各说各话）。 */
export const NOT_COLLECTED_TEXT = '未采集';

/** 最近一次 usage 里本面板消费的两个口径（输入/输出 token）。 */
export interface TurnUsageTokens {
  promptTokens?: number;
  completionTokens?: number;
}

export interface TurnTimingPanelProps {
  /** 本 Turn 的后端 timing 明细；未采集（尚未完成一轮 / 旧后端）为 `undefined`。 */
  timings?: TurnTimings;
  /** 最近一次 usage 记录；未采集为 `undefined`。 */
  usage?: TurnUsageTokens;
}

/** 采集判据：只有真实有限数才算「采到了」。 */
const isCollected = (value: number | null | undefined): value is number =>
  typeof value === 'number' && Number.isFinite(value);

/** 毫秒三态：未采集 ⇒ 「未采集」· `0` ⇒ `0 ms` · 其余按 ms/s 给人类可读值。 */
export function formatTimingMs(value: number | null | undefined): string {
  if (!isCollected(value)) return NOT_COLLECTED_TEXT;
  if (Math.abs(value) < 1000) return `${Math.round(value)} ms`;
  return `${(value / 1000).toFixed(1)} s`;
}

/** 次数三态：未采集 ⇒ 「未采集」· `0` ⇒ `0 次`。 */
export function formatTimingCount(value: number | null | undefined): string {
  if (!isCollected(value)) return NOT_COLLECTED_TEXT;
  return `${value} 次`;
}

/** token 三态：未采集 ⇒ 「未采集」· `0` ⇒ `0`。 */
export function formatTimingTokens(value: number | null | undefined): string {
  if (!isCollected(value)) return NOT_COLLECTED_TEXT;
  return value.toLocaleString('zh-CN');
}

/** TTFT 计时基准的人话（后端 `providerTtftSource`）：本地回退会包含请求构建与传输。 */
export function formatTtftSource(
  source: TurnTimings['providerTtftSource'] | undefined,
): string | undefined {
  if (source === 'provider_dispatch') return '后端计时';
  if (source === 'local_model_call') return '本地计时';
  return undefined;
}

/**
 * 首包拆分：**只要有任一侧采到就渲染**（另一侧原样报「未采集」，不省略、不补零）。
 * 两侧都没采到 ⇒ `undefined`（该行不渲染，避免与「本轮耗时」行重复报未知）。
 */
export function formatTtftSplit(timings?: TurnTimings): string | undefined {
  const reasoning = timings?.providerFirstReasoningMs;
  const content = timings?.providerFirstContentMs;
  const tool = timings?.providerFirstToolDeltaMs;
  if (
    !isCollected(reasoning) &&
    !isCollected(content) &&
    !isCollected(tool)
  ) {
    return undefined;
  }
  const parts = [`推理 ${formatTimingMs(reasoning)}`, `内容 ${formatTimingMs(content)}`];
  if (isCollected(tool)) parts.push(`工具 ${formatTimingMs(tool)}`);
  return parts.join(' · ');
}

/**
 * 请求副本裁剪三态（F4/F5 接缝）。它与「压缩」是**不同操作**：
 *   · 未采集 ⇒ 「未采集」（旧后端/未完成一轮）；
 *   · 采到 0 ⇒ 「0 条」（真的没有裁剪，是好事实，不许折叠成未采集）；
 *   · 采到 >0 ⇒ `N 条 · M 轮`，并明说**完整会话记录仍保留**，避免被读成「历史被删/已压缩」。
 */
export function formatRequestTrim(
  removedMessages: number | null | undefined,
  removedRounds: number | null | undefined,
): string {
  if (!isCollected(removedMessages)) return NOT_COLLECTED_TEXT;
  if (removedMessages <= 0) return '0 条';
  const rounds = isCollected(removedRounds) && removedRounds > 0 ? `${removedRounds} 轮 · ` : '';
  return `${removedMessages} 条（${rounds}完整会话记录仍保留）`;
}

const TurnTimingPanel: React.FC<TurnTimingPanelProps> = ({ timings, usage }) => {
  const { styles } = useChatStyles();

  // 既没有 timing 也没有 usage ⇒ 本轮还没结束（或后端未下发）⇒ 整块不渲染，
  // 而不是渲染一屏「未采集」噪声。
  if (!timings && !usage) return null;

  const row = (key: string, label: string, value: string) => (
    <div
      className={styles.composerStatusDetailRow}
      data-testid={`turn-timing-${key}`}
      key={key}
    >
      <span className={styles.composerStatusDetailLabel}>{label}</span>
      <span className={styles.composerStatusDetailValue}>{value}</span>
    </div>
  );

  const ttftSource = formatTtftSource(timings?.providerTtftSource);
  const ttftValue = isCollected(timings?.providerTtftMs)
    ? `${formatTimingMs(timings?.providerTtftMs)}${ttftSource ? `（${ttftSource}）` : ''}`
    : NOT_COLLECTED_TEXT;
  const ttftSplit = formatTtftSplit(timings);

  return (
    <>
      {row('completed', '本轮耗时', formatTimingMs(timings?.completedMs))}
      {row('model', '模型耗时', formatTimingMs(timings?.modelMs))}
      {row('tool', '工具耗时', formatTimingMs(timings?.toolMs))}
      {row('ttft', 'Provider 首包', ttftValue)}
      {ttftSplit !== undefined && row('ttft-split', '首包拆分', ttftSplit)}
      {row('model-calls', '模型调用', formatTimingCount(timings?.modelCalls))}
      {row('tool-calls', '工具调用', formatTimingCount(timings?.toolCalls))}
      {/* 压缩耗时与请求副本裁剪分列：一个是会话历史被摘要替换，一个只是本次出站少带旧上下文。 */}
      {row('compaction', '同步压缩耗时', formatTimingMs(timings?.compactionMs))}
      {row(
        'request-trim',
        '本次请求裁剪旧上下文',
        formatRequestTrim(timings?.requestTrimmedMessages, timings?.requestTrimmedRounds),
      )}
      {row('input-tokens', '输入 Token', formatTimingTokens(usage?.promptTokens))}
      {row('output-tokens', '输出 Token', formatTimingTokens(usage?.completionTokens))}
    </>
  );
};

export default TurnTimingPanel;

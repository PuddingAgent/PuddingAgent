// ── Slice P1：Admin「索引与检索」页的聚合健康态（纯函数 + 文案常量）──────────
// 规格真源：`Docs/12_features/Index-Status-Panel-UI-Prototype-2026-10-01.md`
//   §2 状态矩阵（**首条命中即生效**）· §3 文案表（逐字）· §4 渲染纪律（三态贯穿）
//   线框：`Docs/12_features/index-status-prototype/admin-wireframe.svg`
//   wire 契约：`./types.ts`（**不改**）
//
// 三条纪律：
// 1) 纯函数：不 import antd/react、不读写 DOM、**不读真实时钟**
//    （需要「现在」的地方一律由调用方经 `nowMs` 注入 ⇒ 断言不随时间漂移）。
// 2) 零后端改动：全部结论由既有 wire 字段在前端推导。
// 3) 三态贯穿：`null` = 未知 · `false` = 否 · `0` = 0；本文件**禁止**把未知折叠成否/零
//    （唯一的 `?? 0` 只出现在已被「未知守卫」拦下的算数求和里，且结果不流向展示层）。

import {
  EMPTY_TEXT,
  UNKNOWN_TEXT,
  formatJobsReason,
  formatRelativeTime,
  formatTriStateBytes,
  formatTriStateCount,
  formatTriStateDurationMs,
  formatTriStateText,
  formatTriStateTimestamp,
  formatUtcTime,
} from './api';
import type {
  CodeIndexMaintenanceStatus,
  CodeIndexProjectStatus,
  CodeIndexStatusDetail,
  FullTextIndexJobStatus,
  FullTextIndexScopeStatus,
  FullTextIndexStatusDetail,
  FullTextIndexStatusSnapshot,
} from './types';

// ══ 状态族（规格 §4：只用 5 族颜色；图形 + 文字双编码，灰度打印可辨）═══════

/** L0 聚合态取值域（规格 §2 的 level 列）。 */
export type IndexHealthLevel = 'ok' | 'off' | 'warn' | 'error' | 'unknown' | 'busy';

/** 视觉族：关闭态（`off`）与未知态（`unknown`）**共用中性灰**（规格 §2「off 不染红」）。 */
export type StatusTone = 'ok' | 'warn' | 'error' | 'neutral' | 'busy';

export interface ToneStyle {
  /** 图形编码（灰度打印 / 色盲可辨）。 */
  glyph: string;
  /** antd Tag 颜色名（只允许这 5 个取值，页面不得自创颜色）。 */
  tagColor: 'success' | 'warning' | 'error' | 'default' | 'processing';
  /** 文字编码（人话标签）。 */
  label: string;
}

export const STATUS_TONES: Record<StatusTone, ToneStyle> = {
  ok: { glyph: '●', tagColor: 'success', label: '就绪' },
  warn: { glyph: '▲', tagColor: 'warning', label: '需处理' },
  error: { glyph: '▲', tagColor: 'error', label: '故障' },
  neutral: { glyph: '○', tagColor: 'default', label: '未知' },
  busy: { glyph: '◐', tagColor: 'processing', label: '进行中' },
};

/** level → 视觉族。`off` 与 `unknown` **同族中性灰**（I2：关闭不是故障）。 */
export const LEVEL_TONE: Record<IndexHealthLevel, StatusTone> = {
  ok: 'ok',
  warn: 'warn',
  error: 'error',
  unknown: 'neutral',
  off: 'neutral',
  busy: 'busy',
};

/** level → 文字标签。`off` 与 `unknown` **必须不同文案**（I6）。 */
export const LEVEL_TEXT: Record<IndexHealthLevel, string> = {
  ok: '就绪',
  off: '已关闭',
  warn: '需处理',
  error: '故障',
  unknown: '未知',
  busy: '进行中',
};

/** L0 条 / L1 卡片的统一取色入口（页面只经此取色）。 */
export function healthToneStyle(level: IndexHealthLevel): ToneStyle {
  return STATUS_TONES[LEVEL_TONE[level]];
}

// ══ 规格 §3 文案（**逐字**，不得改写）══════════════════════════════════

/** 规格 §2 第 7 行阈值：`indexBytes / maxIndexBytes ≥ 80%` 转黄。 */
export const SIZE_WARN_RATIO = 0.8;

export const HEALTH_COPY = {
  okPrefix: '索引就绪',
  offTitle: '全文索引已关闭（enabled=false）',
  offDetail: '配置未开启，不是故障',
  rejectedPrefix: '配置被拒',
  rejectedSuffix: ' 条 scope 未受理',
  scopeMissingPrefix: 'scope 路径不存在：',
  scopeMissingDetail: '检查 D:\\Data\\config\\system.json → FullTextIndex.Scopes',
  unknownTitle: '状态未知 · 探测失败',
  unknownDetail: '不代表「未启用」或「不存在」',
  busyPrefix: '正在重建 · 已清点 ',
  busyDetail: '无进度百分比（后端未提供）',
  emptyTitlePrefix: '索引为空（',
  emptyTitleSuffix: ' 条目）· 尚未建立',
  emptyDetail: 'scope 存在、索引目录存在，但无条目',
  sizeTitlePrefix: '索引体积接近上限（',
  sizeTitleSuffix: '%）',
  jobFailedPrefix: '最近一次供给失败：',
} as const;

/** 规格 §3 台账为空的三种如实说法（`jobsReason`）——**禁止**用空表格代替。 */
export const JOBS_REASON_TEXT: Record<string, string> = {
  'composition-not-created': '供给组件尚未启动（无人触发过预建）',
  'no-jobs-recorded': '供给组件已启动 · 暂无 job',
  'ledger-read-failed': '台账读取失败 ⇒ 内容不可知（≠ 没有 job）',
};

// ══ 时间与 job 的基础工具（全部纯函数）════════════════════════════════

/** 终态 = 状态机已停止流转（`finishedAt` 有值）。 */
export function isTerminalJob(job: FullTextIndexJobStatus): boolean {
  return job.finishedAt !== null && job.finishedAt !== undefined;
}

/** 可解析的时间戳 → 毫秒；`null`/空串/不可解析 ⇒ `null`（未知，不假装成 0）。 */
function parsedTime(value: string | null | undefined): number | null {
  if (value === null || value === undefined || value === '') return null;
  const ms = new Date(value).getTime();
  return Number.isNaN(ms) ? null : ms;
}

/** 进行中的 job（非终态）；多个时取**开始最晚**的一个（不可解析者按数组靠后者优先）。 */
export function pickActiveJob(
  jobs: readonly FullTextIndexJobStatus[],
): FullTextIndexJobStatus | null {
  let picked: FullTextIndexJobStatus | null = null;
  let pickedAt: number | null = null;
  for (const job of jobs) {
    if (isTerminalJob(job)) continue;
    const at = parsedTime(job.startedAt);
    if (picked === null || at === null || pickedAt === null || at >= pickedAt) {
      picked = job;
      pickedAt = at;
    }
  }
  return picked;
}

/** 最近的终态 job（按 `finishedAt`；不可解析者按数组靠后者优先）。 */
export function pickLatestTerminalJob(
  jobs: readonly FullTextIndexJobStatus[],
): FullTextIndexJobStatus | null {
  let picked: FullTextIndexJobStatus | null = null;
  let pickedAt: number | null = null;
  for (const job of jobs) {
    if (!isTerminalJob(job)) continue;
    const at = parsedTime(job.finishedAt);
    if (picked === null || at === null || pickedAt === null || at >= pickedAt) {
      picked = job;
      pickedAt = at;
    }
  }
  return picked;
}

/**
 * 「失败语义」判定（规格 §2 第 8 行）。
 * - `Failed` = 失败；`Cancelled` **不算失败**（人为取消不是故障，报成「失败」会误导）。
 * - 用子串前缀族而非全等：容纳组件侧后续新增的失败态名（`Faulted` / `Error` / `Aborted`）。
 * - 该取舍（Cancelled 不计入）已登记在切片报告 RISKS。
 */
const JOB_FAILURE_STATE_HINTS = ['fail', 'fault', 'error', 'abort'] as const;

export function jobHasFailureSemantics(state: string): boolean {
  const normalized = state.toLowerCase();
  return JOB_FAILURE_STATE_HINTS.some((hint) => normalized.includes(hint));
}

/** 各 scope「索引目录最后写入」里最新的一个（原始字符串；未知 ⇒ `null`）。 */
export function latestIndexWriteUtc(detail: FullTextIndexStatusDetail): string | null {
  let newest: string | null = null;
  let newestAt: number | null = null;
  for (const scope of detail.scopes) {
    const at = parsedTime(scope.indexDirectoryLastWriteUtc);
    if (at === null) continue;
    if (newestAt === null || at > newestAt) {
      newest = scope.indexDirectoryLastWriteUtc;
      newestAt = at;
    }
  }
  return newest;
}

// ══ 规格 §4 的「字段为 null ⇒ unknown」清单 ════════════════════════════

/** §4 清单（scope 侧，逐字对齐 `types.ts` 字段名）。 */
export const SCOPE_UNKNOWN_FIELDS = [
  'hasIndex',
  'indexDirectory',
  'indexDirectoryExists',
  'indexEntryCount',
  'indexBytes',
  'indexDirectoryLastWriteUtc',
] as const satisfies readonly (keyof FullTextIndexScopeStatus)[];

/** 任一 scope 的关键字段为 `null` ⇒ 该 scope 观测不可信。 */
export function hasUnknownScopeField(detail: FullTextIndexStatusDetail): boolean {
  return detail.scopes.some((scope) =>
    SCOPE_UNKNOWN_FIELDS.some(
      (field) => scope[field] === null || scope[field] === undefined,
    ),
  );
}

/**
 * §4 清单的**全集**（scope 侧 ∪ `maintenance.enabled`）⇒ `unknown` 的判定真源。
 * `maintenance.enabled` 也在清单里：它的 `null` 表示「生效值不可知」，与 `false`（明确关闭）不同事实。
 */
export function hasUnknownKeyField(detail: FullTextIndexStatusDetail): boolean {
  if (detail.maintenance.enabled === null || detail.maintenance.enabled === undefined) {
    return true;
  }
  return hasUnknownScopeField(detail);
}

// ══ 体积（三态汇总，I6 的承载点）══════════════════════════════════════

export interface IndexVolumeSummary {
  scopeCount: number;
  /** 各 scope 字节合计；**任一 scope 未知 ⇒ 汇总未知（`null`）**，绝不折叠成 0。 */
  bytes: number | null;
  /** 单库上限（配置值）；不可用 ⇒ `null`。 */
  maxIndexBytes: number | null;
  /** `最大 scope 的 indexBytes / maxIndexBytes`（上限是**单库**上限，故按最大 scope 算）。 */
  ratio: number | null;
  /** 占用最大的那个 scope 的字节（未知 ⇒ `null`）。 */
  largestScopeBytes: number | null;
}

export function summarizeIndexVolume(detail: FullTextIndexStatusDetail): IndexVolumeSummary {
  const scopes = detail.scopes;
  const hasUnknown = scopes.some(
    (scope) => scope.indexBytes === null || scope.indexBytes === undefined,
  );
  // 下面的 `?? 0` 只参与算数求和，**不会**流到展示层：任一未知时 bytes 直接为 null。
  // （变异 M2 正是拆掉这层守卫 ⇒ 「未知」会被折叠成 0。）
  const total = scopes.reduce((sum, scope) => sum + (scope.indexBytes ?? 0), 0);
  const largest = scopes.reduce<number | null>((max, scope) => {
    const value = scope.indexBytes;
    if (value === null || value === undefined) return max;
    return max === null || value > max ? value : max;
  }, null);
  const maxIndexBytes = Number.isFinite(detail.maxIndexBytes) ? detail.maxIndexBytes : null;
  const ratio =
    largest !== null && maxIndexBytes !== null && maxIndexBytes > 0
      ? largest / maxIndexBytes
      : null;
  return {
    scopeCount: scopes.length,
    bytes: hasUnknown ? null : total,
    maxIndexBytes,
    ratio,
    largestScopeBytes: largest,
  };
}

/** 条目数合计（`null` 已被第 4 行 unknown 拦下 ⇒ 此处求和安全）。 */
export function totalEntryCount(detail: FullTextIndexStatusDetail): number {
  return detail.scopes.reduce((sum, scope) => sum + (scope.indexEntryCount ?? 0), 0);
}

/**
 * 规格 §2 第 6 行：全部 scope `hasIndex !== true` **或** 条目合计为 0。
 * 零受理 scope 也判空 —— 「没得可搜」与「索引为空」对用户是同一类事实。
 */
export function isIndexEmpty(detail: FullTextIndexStatusDetail): boolean {
  const scopes = detail.scopes;
  if (scopes.length === 0) return true;
  const noneHasIndex = scopes.every((scope) => scope.hasIndex !== true);
  const noEntries = scopes.every((scope) => scope.indexEntryCount === 0);
  return noneHasIndex || noEntries;
}

// ══ L0 聚合健康态（规格 §2 状态矩阵：首条命中即生效）═══════════════════

export interface IndexHealthVerdict {
  level: IndexHealthLevel;
  /** 命中的矩阵行号（规格 §2 的 `#` 列，1~9）—— 单测直接点它。 */
  rule: number;
  title: string;
  /** 副行 / detail。 */
  detail: string;
}

export function deriveIndexHealth(
  snapshot: FullTextIndexStatusSnapshot | null | undefined,
  nowMs: number = Date.now(),
): IndexHealthVerdict {
  const detail = snapshot?.fullText ?? null;

  // 契约异常（`fullText` 缺失）与 §4 的 null 清单同族 ⇒ 归第 4 行 unknown。
  if (detail === null) {
    return {
      level: 'unknown',
      rule: 4,
      title: HEALTH_COPY.unknownTitle,
      detail: HEALTH_COPY.unknownDetail,
    };
  }

  // 行 1：`enabled === false` ⇒ off。**先于**任何 null/空/故障判定：
  //        关闭是明确状态，不是故障；否则关闭态会被判成「未知」或「空」。
  if (detail.enabled === false) {
    return { level: 'off', rule: 1, title: HEALTH_COPY.offTitle, detail: HEALTH_COPY.offDetail };
  }

  // 行 2：有被拒 scope ⇒ error（逐条原文进 detail）。
  if (detail.rejectedReasons.length > 0) {
    return {
      level: 'error',
      rule: 2,
      title: `${HEALTH_COPY.rejectedPrefix}：${detail.rejectedReasons.length}${HEALTH_COPY.rejectedSuffix}`,
      detail: detail.rejectedReasons.join('；'),
    };
  }

  // 行 3：任一 scope 路径不存在 ⇒ error，标题带该 scope 路径。
  const missingScope = detail.scopes.find((scope) => scope.scopeExists === false);
  if (missingScope !== undefined) {
    return {
      level: 'error',
      rule: 3,
      title: `${HEALTH_COPY.scopeMissingPrefix}${missingScope.scopePath}`,
      detail: HEALTH_COPY.scopeMissingDetail,
    };
  }

  // 行 4：关键字段为 null ⇒ unknown（劣后于 error：已知坏 > 不知道；优先于 warn）。
  if (hasUnknownKeyField(detail)) {
    return {
      level: 'unknown',
      rule: 4,
      title: HEALTH_COPY.unknownTitle,
      detail: HEALTH_COPY.unknownDetail,
    };
  }

  // 行 5：存在非终态 job ⇒ busy（**劣后**于 error/unknown：重建不得掩盖硬错误）。
  const activeJob = pickActiveJob(detail.jobs);
  if (activeJob !== null) {
    return {
      level: 'busy',
      rule: 5,
      title: buildBusyTitle(activeJob, nowMs),
      detail: HEALTH_COPY.busyDetail,
    };
  }

  // 行 6：全部 scope 无索引或条目为 0 ⇒ warn（索引为空 · 尚未建立）。
  if (isIndexEmpty(detail)) {
    return {
      level: 'warn',
      rule: 6,
      title: `${HEALTH_COPY.emptyTitlePrefix}${totalEntryCount(detail)}${HEALTH_COPY.emptyTitleSuffix}`,
      detail: HEALTH_COPY.emptyDetail,
    };
  }

  // 行 7：体积占用 ≥ 80% ⇒ warn。
  const volume = summarizeIndexVolume(detail);
  if (volume.ratio !== null && volume.ratio >= SIZE_WARN_RATIO) {
    return {
      level: 'warn',
      rule: 7,
      title: `${HEALTH_COPY.sizeTitlePrefix}${Math.round(volume.ratio * 100)}${HEALTH_COPY.sizeTitleSuffix}`,
      detail: `${formatTriStateBytes(volume.largestScopeBytes)} / ${formatTriStateBytes(volume.maxIndexBytes)}`,
    };
  }

  // 行 8：最近终态 job 含失败语义 ⇒ warn。
  const latestTerminal = pickLatestTerminalJob(detail.jobs);
  if (latestTerminal !== null && jobHasFailureSemantics(latestTerminal.state)) {
    return {
      level: 'warn',
      rule: 8,
      title: `${HEALTH_COPY.jobFailedPrefix}${latestTerminal.state}`,
      detail: formatTriStateText(latestTerminal.message),
    };
  }

  // 行 9：以上皆否 ⇒ ok（相对时间为主 + 绝对时间 UTC 作副行）。
  const lastWrite = latestIndexWriteUtc(detail);
  return {
    level: 'ok',
    rule: 9,
    title: `${HEALTH_COPY.okPrefix} · ${formatRelativeTime(lastWrite, nowMs)}更新`,
    detail: `最后写入 ${formatUtcTime(lastWrite)}`,
  };
}

/**
 * `busy` 文案（规格 §3 逐字模板）：`正在重建 · 已清点 <n> 文件 · <bytes> · 已用 <s> 秒`。
 * **禁止**百分比 / 进度条（后端没有 processed/total，§2 与 §6 缺口 ①）。
 */
export function buildBusyTitle(
  job: FullTextIndexJobStatus,
  nowMs: number = Date.now(),
): string {
  const seconds = busyElapsedSeconds(job, nowMs);
  return `${HEALTH_COPY.busyPrefix}${formatTriStateCount(job.indexedFileCount)} 文件 · ${formatTriStateBytes(
    job.totalBytes,
  )} · 已用 ${seconds === null ? UNKNOWN_TEXT : seconds} 秒`;
}

/**
 * 进行中 job 的「已用」秒数。
 * 终态才有 `elapsedMs`（后端刻意不为非终态推算，避免漂移）⇒ 进行中由 `startedAt` + 注入的
 * `nowMs` 推算；`startedAt` 不可解析则返回 `null`（渲染「未知」，不编造秒数）。
 * 该取舍已登记在切片报告 RISKS。
 */
export function busyElapsedSeconds(
  job: FullTextIndexJobStatus,
  nowMs: number = Date.now(),
): number | null {
  if (typeof job.elapsedMs === 'number' && Number.isFinite(job.elapsedMs)) {
    return Math.max(0, Math.round(job.elapsedMs / 1000));
  }
  const startedAt = parsedTime(job.startedAt);
  if (startedAt === null) return null;
  return Math.max(0, Math.round((nowMs - startedAt) / 1000));
}

// ══ L0 结论条的三个 chip（全部由既有字段推导）══════════════════════════

export interface L0Chip {
  key: 'freshness' | 'scope' | 'volume';
  text: string;
  /** tooltip：绝对时刻 / 原始字节等「不进首屏但要点得到」的事实。 */
  hint: string;
}

export function deriveL0Chips(
  detail: FullTextIndexStatusDetail | null,
  nowMs: number = Date.now(),
): L0Chip[] {
  if (detail === null) return [];
  const lastWrite = latestIndexWriteUtc(detail);
  const volume = summarizeIndexVolume(detail);
  const pct = volume.ratio === null ? UNKNOWN_TEXT : `${Math.round(volume.ratio * 100)}%`;
  const accepted = detail.acceptedScopes.length;
  const rejected = detail.rejectedReasons.length;
  return [
    {
      key: 'freshness',
      text: `最后更新 ${formatRelativeTime(lastWrite, nowMs)}`,
      hint: formatUtcTime(lastWrite),
    },
    {
      key: 'scope',
      text: `scope ${accepted} 受理 · ${rejected} 拒绝`,
      hint: `受理：${detail.acceptedScopes.join('；') || '（无）'}\n拒绝：${detail.rejectedReasons.join('；') || '（无）'}`,
    },
    {
      key: 'volume',
      text: `${formatTriStateBytes(volume.largestScopeBytes)} / ${formatTriStateBytes(
        volume.maxIndexBytes,
      )} · ${pct}`,
      hint: `最大 scope 字节：${formatTriStateBytes(volume.largestScopeBytes)}（原始 ${
        volume.largestScopeBytes ?? UNKNOWN_TEXT
      } B）\n单库上限：${formatTriStateBytes(volume.maxIndexBytes)}（原始 ${
        volume.maxIndexBytes ?? UNKNOWN_TEXT
      } B）`,
    },
  ];
}

// ══ L1 四张卡片的角标（A 全文索引 / B 符号索引 / C 供给台账 / D 配置与受理）═

export interface CardStatus {
  tone: StatusTone;
  glyph: string;
  text: string;
  hint: string;
}

function cardStatus(tone: StatusTone, text: string, hint: string): CardStatus {
  return { tone, glyph: STATUS_TONES[tone].glyph, text, hint };
}

/** A 卡 · 全文索引（Lucene）：只回答「scope 观测本身对不对」。 */
export function deriveScopeCardStatus(detail: FullTextIndexStatusDetail | null): CardStatus {
  if (detail === null) return cardStatus('neutral', '未知', '探测失败 ⇒ 不代表「未启用」或「不存在」');
  if (detail.scopes.some((scope) => scope.scopeExists === false)) {
    return cardStatus('error', '故障', '存在 scope 路径不存在');
  }
  if (detail.scopes.length === 0) {
    return cardStatus('neutral', '未知', '没有受理到任何 scope ⇒ 无可观测对象');
  }
  if (hasUnknownScopeField(detail)) {
    return cardStatus('neutral', '未知', '关键字段为 null ⇒ 探测失败（读不到 ≠ 不存在）');
  }
  if (isIndexEmpty(detail)) return cardStatus('warn', '需处理', '索引为空 · 尚未建立');
  const volume = summarizeIndexVolume(detail);
  if (volume.ratio !== null && volume.ratio >= SIZE_WARN_RATIO) {
    return cardStatus('warn', '需处理', '索引体积接近上限');
  }
  return cardStatus('ok', '就绪', 'scope 观测正常');
}

// B 卡 · 符号索引（代码）：**P3 起消费真实数据** —— `codeIndex` 块的角标由
// `deriveCodeIndexBlock`（四态：absent / unavailable / empty / observed）给出。
// 旧的**中性占位常量** `SYMBOL_CARD_STATUS` 已删除：「未接入」仍是一条规则（规则 1），
// 只是不再是唯一可能的结果（当时后端 wire 里确实没有这个块）。

/** C 卡 · 供给台账（jobs）。 */
export function deriveLedgerCardStatus(detail: FullTextIndexStatusDetail | null): CardStatus {
  if (detail === null) return cardStatus('neutral', '未知', '探测失败');
  if (detail.jobsReason === 'ledger-read-failed') {
    return cardStatus('error', '故障', '台账读取失败 ⇒ 内容不可知（≠ 没有 job）');
  }
  if (pickActiveJob(detail.jobs) !== null) return cardStatus('busy', '进行中', '存在非终态 job');
  if (detail.jobs.length === 0) {
    return cardStatus('neutral', '暂无 job', describeJobsLedger(detail).text);
  }
  const latest = pickLatestTerminalJob(detail.jobs);
  if (latest !== null && jobHasFailureSemantics(latest.state)) {
    return cardStatus('warn', '需处理', `最近一次供给失败：${latest.state}`);
  }
  return cardStatus('ok', '就绪', `台账 ${detail.jobs.length} 条`);
}

/** D 卡 · 配置与受理。 */
export function deriveConfigCardStatus(detail: FullTextIndexStatusDetail | null): CardStatus {
  if (detail === null) return cardStatus('neutral', '未知', '探测失败');
  if (detail.rejectedReasons.length > 0) {
    return cardStatus('error', '故障', `${detail.rejectedReasons.length} 条 scope 未受理`);
  }
  if (detail.maintenance.enabled === null || detail.maintenance.enabled === undefined) {
    return cardStatus('neutral', '未知', '维护循环是否生效不可知（配置节存在但无绑定器）');
  }
  if (detail.enabled === false) {
    return cardStatus('neutral', '已关闭', 'enabled=false ⇒ 配置未开启，不是故障');
  }
  return cardStatus('ok', '就绪', '配置有效、无被拒 scope');
}

// ══ 台账为空时的如实说法（I8：禁止渲染空表格）══════════════════════════

export interface LedgerNotice {
  isEmpty: boolean;
  /** 台账为空时的**中文如实说法**；有 job 时为空串。 */
  text: string;
}

export function describeJobsLedger(
  detail: FullTextIndexStatusDetail | null | undefined,
): LedgerNotice {
  const jobs = detail?.jobs ?? [];
  if (jobs.length > 0) return { isEmpty: false, text: '' };
  const reason = detail?.jobsReason ?? null;
  if (reason !== null && reason !== undefined) {
    const known = JOBS_REASON_TEXT[reason];
    if (known !== undefined) return { isEmpty: true, text: known };
  }
  // 未登记 / 未提供原因码：交给既有契约函数原样回显（不吞不猜），仍然不是空表格。
  return { isEmpty: true, text: formatJobsReason(reason) };
}

/**
 * I8 的**渲染决策**（纯函数，页面直接调用）：台账为空就绝不渲染表格，
 * 改为渲染 `describeJobsLedger` 给出的如实说法（三种 jobsReason 之一）。
 * 把「渲染什么」也放进可单测的纯函数里 ⇒ 「不得渲染空表格」有具名断言可点。
 */
export function shouldRenderJobsTable(
  detail: FullTextIndexStatusDetail | null | undefined,
): boolean {
  return (detail?.jobs ?? []).length > 0;
}

// ══ L2 原始字段（I7：字段一个不丢；默认折叠）════════════════════════════

/** L2 默认折叠（规格 §1：默认视图是结论，排障时才下钻到字段）。 */
export const RAW_FIELDS_DEFAULT_EXPANDED = false;

/** 字段渲染口径（页面按此选择三态格式化函数）。 */
export type FieldKind =
  | 'path'
  | 'bool'
  | 'count'
  | 'bytes'
  | 'timestamp'
  | 'durationMs'
  | 'text'
  // P3 新增：字符串数组（`lastRemovalPaths`）与「复合结构」（`projects` / `maintenance`）。
  // 复合结构**不得**被渲染成「未知」（那会把「有值」读成「读不到」），故单独一种 kind。
  | 'list'
  | 'nested';

export interface FieldDescriptor<K extends string> {
  /** wire 字段名（逐字对齐 `types.ts`）。 */
  key: K;
  /** 人中文字段名（列标题 = `${label}（${key}）`）。 */
  label: string;
  kind: FieldKind;
}

// ⚠️ `Record<keyof X, …>` 是**编译期穷尽性约束**：`types.ts` 新增字段而这里不补 ⇒ tsc 直接报错。
const SCOPE_FIELD_META: Record<keyof FullTextIndexScopeStatus, { label: string; kind: FieldKind }> = {
  scopePath: { label: 'scope 路径', kind: 'path' },
  scopeExists: { label: '目录存在', kind: 'bool' },
  hasIndex: { label: '已有索引', kind: 'bool' },
  indexDirectory: { label: '索引目录', kind: 'path' },
  indexDirectoryExists: { label: '索引目录存在', kind: 'bool' },
  indexEntryCount: { label: '条目数', kind: 'count' },
  indexBytes: { label: '字节', kind: 'bytes' },
  indexDirectoryLastWriteUtc: { label: '最后写入', kind: 'timestamp' },
};

const JOB_FIELD_META: Record<keyof FullTextIndexJobStatus, { label: string; kind: FieldKind }> = {
  jobId: { label: 'job 标识', kind: 'path' },
  state: { label: '状态', kind: 'text' },
  phase: { label: '阶段', kind: 'text' },
  startedAt: { label: '开始', kind: 'timestamp' },
  finishedAt: { label: '结束', kind: 'timestamp' },
  message: { label: '说明', kind: 'text' },
  indexedFileCount: { label: '可索引文件数', kind: 'count' },
  totalBytes: { label: '语料字节', kind: 'bytes' },
  elapsedMs: { label: '耗时', kind: 'durationMs' },
};

/** 逐 scope **8 列**（规格 §1 / I7）。 */
export const SCOPE_FIELDS: readonly FieldDescriptor<keyof FullTextIndexScopeStatus>[] = (
  Object.keys(SCOPE_FIELD_META) as (keyof FullTextIndexScopeStatus)[]
).map((key) => ({ key, ...SCOPE_FIELD_META[key] }));

/** 逐 job **9 列**（规格 §1 / I7）。 */
export const JOB_FIELDS: readonly FieldDescriptor<keyof FullTextIndexJobStatus>[] = (
  Object.keys(JOB_FIELD_META) as (keyof FullTextIndexJobStatus)[]
).map((key) => ({ key, ...JOB_FIELD_META[key] }));

// ══ Slice P3 · B 卡（符号索引 / codeIndex）══════════════════════════════
// 契约真源：`./types.ts` 的「符号索引块（S-A2 / P3 新增）」一节（字段名/顺序从后端反读，
//   权威清单 = `Tests/PuddingHost.Tests/Hosting/SA2CodeIndexStatusTests.cs` A5/A6 +
//   `Tests/PuddingHost.Tests/Hosting/SA2CodeIndexTestDoubles.cs` 的 `Sa2Samples.MaintenanceStatus`）。
// 与前文同构的三条纪律：
//   ① 纯函数：不碰 DOM / 不读真实时钟（`nowMs` 一律注入）；
//   ② 三态贯穿：`null` = 未知 ≠ `false` ≠ `0`；且**块键缺失（`undefined`）**是**第四种**事实 ——
//      块缺失 / 块降级不可用 / 块在但无项目 / 已观测，四者必须渲染成互不相同的状态，
//      **禁止** `?? { projects: [] }` 式折叠（变异 MUTA-P3-1 正是拆掉这条）；
//   ③ D3：注册态（`registration*`，真源 = 索引注册表 / SQLite 项目记录表）与
//      维护态（`maintenance.*`，真源 = 维护驱动进程内状态）是**两套字段、两套呈现**，
//      不得合成一个「状态」（故本文件给出 `registrationHint` 与 `maintenanceHint` 两条独立文案）。

/** B 卡的四态取值域（互不相同，逐态一渲染）。 */
export type CodeIndexBlockState = 'absent' | 'unavailable' | 'empty' | 'observed';

/** 四态的**唯一短词**（I-P3-1：四个词两两不同 ⇒ 不靠颜色区分状态）。 */
export const CODE_INDEX_BLOCK_TEXT: Record<CodeIndexBlockState, string> = {
  absent: '未接入',
  unavailable: '观测不可用',
  empty: '无项目',
  observed: '已接入',
};

/** 后端整块降级时 `note` 的已知前缀（真源：`CodeIndexStatusProbe` 抛错分支）。 */
export const CODE_INDEX_NOTE_UNAVAILABLE_PREFIX = 'code-index-status-unavailable';

/** 已知 `note` 取值的中文解释（未登记的取值原样回显并标注「未登记」，不吞不猜）。 */
export const CODE_INDEX_NOTE_TEXT: Record<string, string> = {
  [CODE_INDEX_NOTE_UNAVAILABLE_PREFIX]:
    '整块读不出来（探针抛异常）⇒ 内容**不可知**，既不是「没有项目」也不是「都正常」；具体异常见宿主日志。',
};

export interface CodeIndexBlockVerdict {
  state: CodeIndexBlockState;
  /** 命中的规则号（1~4）—— 单测直接点它，顺序即优先级。 */
  rule: number;
  /** 视觉 level（驱动 L1 卡 B 的形状 / 动效 / 容器）。 */
  level: IndexHealthLevel;
  /** 卡片角标（与 A/C/D 卡同构：tone + glyph + 短词 + tooltip）。 */
  status: CardStatus;
  /** 异常态**唯一允许**的长句（§9.6）；非异常态为 `null`（正常态只给短词）。 */
  headline: string | null;
  /** 异常态的修复指引；非异常态为 `null`。 */
  guidance: string | null;
  /** 逐项目条目（`absent` 时为 `null`；工具提示用，不改语义）。 */
  block: CodeIndexStatusDetail | null;
}

/**
 * `note` 三态解释：`null`/空串 ⇒ **不适用**（正常）；已知原因码给中文解释；
 * 未登记的原因码原样回显（不吞）。
 */
export function describeCodeIndexNote(note: string | null | undefined): string {
  if (note === null || note === undefined || note === '') return '';
  // 后端把原因码与具体异常拼在一个串里（`{prefix}: {ExceptionType}: {Message}`）
  // ⇒ 必须**前缀**匹配，否则真实取值会被当成「未登记的原因」。
  const trimmed = note.trim();
  if (trimmed === CODE_INDEX_NOTE_UNAVAILABLE_PREFIX || trimmed.startsWith(`${CODE_INDEX_NOTE_UNAVAILABLE_PREFIX}:`)) {
    return CODE_INDEX_NOTE_TEXT[CODE_INDEX_NOTE_UNAVAILABLE_PREFIX];
  }
  const known = CODE_INDEX_NOTE_TEXT[trimmed];
  if (known !== undefined) return known;
  return `未登记的原因「${note}」—— 请对照后端 CodeIndexStatusProbe 的降级分支补充解释。`;
}

/**
 * B 卡块级四态（**首条命中即生效**）：
 *
 * | # | 条件 | 事实 |
 * |---|---|---|
 * | 1 | 根快照缺失 **或** 响应里**没有** `codeIndex` 键（`undefined`，含显式 `null`） | 本块未接入（端点要重启 Core 才生效）—— **不是故障，也不是「空」** |
 * | 2 | `note` 非空 **或** `maintenanceRunning === null` | 整块降级 / 运行态不可知 ⇒ 观测不可用（**未知 ≠ 健康**） |
 * | 3 | `projects.length === 0` | 块在，但注册表与维护驱动都没有可观测条目 ⇒ **已知的空** |
 * | 4 | 其余 | 已观测到项目（再按陈旧 / 路径失效 / 被拒校准 / 进行中细分为 warn / busy / ok） |
 */
export function deriveCodeIndexBlock(
  snapshot: FullTextIndexStatusSnapshot | null | undefined,
): CodeIndexBlockVerdict {
  const raw: CodeIndexStatusDetail | null | undefined = snapshot?.codeIndex;

  // 规则 1：块**键缺失**（`undefined`）。`undefined` 是**预期状态**：该块由 Core 上的
  // CodeIndexStatusProbe 产出，改动要重启 Core 才生效；未接入 ≠ 故障，也 ≠「没有项目」。
  if (raw === null || raw === undefined) {
    return {
      state: 'absent',
      rule: 1,
      level: 'unknown',
      status: cardStatus(
        'neutral',
        CODE_INDEX_BLOCK_TEXT.absent,
        '端点响应里没有 codeIndex 键（该块要重启 Core 才生效）—— 未接入 ≠ 故障，也不代表「没有项目」',
      ),
      headline: null,
      guidance: null,
      block: null,
    };
  }
  const block = raw;

  // 规则 2：整块降级。`note` 是后端给的**如实原因**；`maintenanceRunning === null` 是
  //「驱动跑没跑」这个事实本身不可知（R5：未知不得画成「没在跑」，也不得画成正常）。
  if (block.note !== null || block.maintenanceRunning === null) {
    const reason =
      block.note !== null
        ? formatTriStateText(block.note)
        : 'maintenanceRunning 为 null ⇒ 维护驱动是否在跑不可知';
    return {
      state: 'unavailable',
      rule: 2,
      level: 'warn',
      status: cardStatus('warn', CODE_INDEX_BLOCK_TEXT.unavailable, `${reason} ⇒ 观测不可用（≠ 没有项目）`),
      headline: `${CODE_INDEX_BLOCK_TEXT.unavailable}：${reason}`,
      guidance: describeCodeIndexNote(block.note) || '维护驱动运行态不可知 —— 运行事实与「注册 / 路径」事实无关',
      block,
    };
  }

  // 规则 3：块在、且确实一条项目都没有 ⇒「已知的空」（与规则 1 的「不知道」明确不同）。
  if (block.projects.length === 0) {
    return {
      state: 'empty',
      rule: 3,
      level: 'off',
      status: cardStatus(
        'neutral',
        CODE_INDEX_BLOCK_TEXT.empty,
        '块已接入且读取成功，但注册表与维护驱动都没有条目 ⇒ 这是**已知的空**（不是「读不到」）',
      ),
      headline: null,
      guidance: null,
      block,
    };
  }

  // 规则 4：已观测到项目。硬问题（路径失效 / 陈旧 / 被拒校准）**劣后于** busy：
  // 「已知坏」优先于「正在进行」——与 §2 既有矩阵同取向。
  const counts = summarizeCodeIndexProjects(block);
  const hasProblem = counts.pathBroken > 0 || counts.stale > 0 || counts.rejectedCalibration > 0;
  const level: IndexHealthLevel = hasProblem ? 'warn' : counts.inFlight > 0 ? 'busy' : 'ok';
  const word = LEVEL_TEXT[level];
  const hint = [
    `项目 ${counts.total}`,
    `陈旧 ${counts.stale}`,
    `未登记 ${counts.unregistered}`,
    `路径失效 ${counts.pathBroken}`,
    `索引中 ${counts.inFlight}`,
    `维护态缺席 ${counts.maintenanceMissing}`,
    `维护驱动：${block.maintenanceRunning === true ? '运行中' : '未运行'}`,
  ].join(' · ');
  return {
    state: 'observed',
    rule: 4,
    level,
    status: cardStatus(hasProblem ? 'warn' : counts.inFlight > 0 ? 'busy' : 'ok', word, hint),
    // 异常态才允许长句（§9.6）：短词 + tooltip 已足够表达「需处理」的原因分布。
    headline: hasProblem ? `符号索引：${counts.stale} 项陈旧 · ${counts.pathBroken} 项路径失效` : null,
    guidance:
      hasProblem && counts.pathBroken > 0
        ? '路径失效 ⇒ 后端 fail-closed 标记 `stale`（D2）：该项目的检索结果不可信'
        : hasProblem
          ? '陈旧 ⇒ 未在注册表登记或根路径不存在（D1/D2）：不得当作「已索引」'
          : null,
    block,
  };
}

/** 逐项目问题计数（全部由既有 wire 字段推导；不读时钟、不猜）。 */
export interface CodeIndexProjectCounts {
  total: number;
  /** 后端 fail-closed 判定为陈旧的项目数（D1/D2 的落点）。 */
  stale: number;
  /** 未在注册表登记的项目数（`registered === false`）。 */
  unregistered: number;
  /** 根路径不存在的项目数（`rootPathExists === false`）。 */
  pathBroken: number;
  /** 维护态为「索引进行中」的项目数。 */
  inFlight: number;
  /** 维护态**缺席**（`maintenance === null`）的项目数 —— 欠一次挂接，不等于陈旧。 */
  maintenanceMissing: number;
  /** 维护态里 `rejectedCalibrationRunCount > 0` 的项目数。 */
  rejectedCalibration: number;
  /** 已挂接但 `watcherAttached === false`（不会被增量感知）的项目数。 */
  watcherDetached: number;
}

export function summarizeCodeIndexProjects(block: CodeIndexStatusDetail): CodeIndexProjectCounts {
  const counts: CodeIndexProjectCounts = {
    total: block.projects.length,
    stale: 0,
    unregistered: 0,
    pathBroken: 0,
    inFlight: 0,
    maintenanceMissing: 0,
    rejectedCalibration: 0,
    watcherDetached: 0,
  };
  for (const entry of block.projects) {
    if (entry.stale) counts.stale += 1;
    if (!entry.registered) counts.unregistered += 1;
    if (!entry.rootPathExists) counts.pathBroken += 1;
    if (entry.maintenance === null) {
      counts.maintenanceMissing += 1;
      continue;
    }
    if (entry.maintenance.indexInFlight) counts.inFlight += 1;
    if (entry.maintenance.rejectedCalibrationRunCount > 0) counts.rejectedCalibration += 1;
    if (!entry.maintenance.watcherAttached) counts.watcherDetached += 1;
  }
  return counts;
}

/** 逐项目行的视觉标记（**全部由健康态推导**，页面不再散落判定分支）。 */
export interface CodeIndexProjectMarks {
  level: IndexHealthLevel;
  /** 主视觉短词（`LEVEL_TEXT`，与 L0/L1 同一取词入口）。 */
  word: string;
  /** 陈旧标记（形状 + 动效 + 短词三重编码的承载点）。 */
  stale: boolean;
  /** 未登记标记（D1）。 */
  unregistered: boolean;
  /** 路径失效标记（D2）。 */
  pathBroken: boolean;
  /** 涟漪：索引**进行中**。 */
  indexInFlight: boolean;
  /** 维护态**缺席**（`maintenance === null`）—— 与「已挂接但空闲」不同。 */
  maintenanceMissing: boolean;
  /** 拨动开关的取值：`null` ⇒ 不可知（≠ 关）。 */
  watcherAttached: boolean | null;
  /** 被拒校准（`> 0`）⇒ warn。 */
  calibrationRejected: boolean;
  /** **注册态** tooltip（D3 第一半；真源 = 索引注册表）。 */
  registrationHint: string;
  /** **维护态** tooltip（D3 第二半；真源 = 维护驱动；23 字段由契约常量生成）。 */
  maintenanceHint: string;
}

export function deriveCodeIndexProjectMarks(entry: CodeIndexProjectStatus): CodeIndexProjectMarks {
  const maintenance = entry.maintenance;
  const rejected = maintenance !== null && maintenance.rejectedCalibrationRunCount > 0;
  // 取证取向（与 §2 矩阵一致）：已知坏 > 进行中 > 正常。顺序不可调换。
  const level: IndexHealthLevel = !entry.rootPathExists
    ? 'error'
    : entry.stale || rejected
      ? 'warn'
      : maintenance?.indexInFlight === true
        ? 'busy'
        : 'ok';
  return {
    level,
    word: LEVEL_TEXT[level],
    stale: entry.stale,
    unregistered: !entry.registered,
    pathBroken: !entry.rootPathExists,
    indexInFlight: maintenance?.indexInFlight === true,
    maintenanceMissing: maintenance === null,
    watcherAttached: maintenance === null ? null : maintenance.watcherAttached,
    calibrationRejected: rejected,
    registrationHint: [
      `注册态（真源：索引注册表）：${entry.registered ? '已登记' : '**未登记**'}`,
      `registrationState = ${formatTriStateText(entry.registrationState)}`,
      `registrationStatus（原始）= ${formatTriStateText(entry.registrationStatus)}`,
      `registrationSource = ${formatTriStateText(entry.registrationSource)}`,
    ].join('\n'),
    maintenanceHint:
      maintenance === null
        ? [
            '维护态（真源：维护驱动）：**缺席**',
            `maintenanceReason = ${formatTriStateText(entry.maintenanceReason)}`,
            '（欠一次挂接 / 驱动未运行 —— 不等于陈旧，也不代表索引坏了）',
          ].join('\n')
        : describeMaintenanceStatus(maintenance),
  };
}

/**
 * 维护态的单字结论（**只在行内用**；23 字段逐条在 tooltip 与 L2 证据层）。
 * 优先级：索引中 > 待重建 > 待索引 > 空闲（先报「正在动」，再报「要动什么」）。
 */
export type MaintenanceWord = '索引中' | '待重建' | '待索引' | '空闲';

export function describeMaintenanceWord(entry: CodeIndexProjectStatus): MaintenanceWord | null {
  const maintenance = entry.maintenance;
  if (maintenance === null) return null;
  if (maintenance.indexInFlight) return '索引中';
  if (maintenance.needsReconcile) return '待重建';
  if (maintenance.indexPending) return '待索引';
  return '空闲';
}

/** 维护态缺席的原因码 → 中文（后端已知两个取值；未登记的取值原样回显）。 */
export const MAINTENANCE_REASON_TEXT: Record<string, string> = {
  'scope-not-attached': '驱动在跑，但这个 scope 没挂上去（欠一次 attach）—— 不等于陈旧',
  'maintenance-driver-not-running': '维护驱动根本没在跑 ⇒ 进程内不存在任何已挂 scope',
};

export function describeMaintenanceReason(reason: string | null | undefined): string {
  const text = formatTriStateText(reason);
  if (reason === null || reason === undefined) return text;
  const known = MAINTENANCE_REASON_TEXT[reason];
  return known !== undefined ? known : `未登记的原因「${reason}」`;
}

/** 校准新鲜度环的**表达层**窗口（后端**没有**校准周期字段 ⇒ 这是约定，不是后端事实）。 */
export const CALIBRATION_FRESH_WINDOW_MS = 24 * 60 * 60 * 1000;

/**
 * 校准新鲜度环填充率：刚校准 = 满环，超过一个窗口 = 空环；**时间戳不可得 ⇒ `null`**（虚线空环，
 * 不假装成 0 填充）。
 */
export function deriveCalibrationFreshnessRatio(
  lastCalibrationAtUtc: string | null | undefined,
  nowMs: number = Date.now(),
): number | null {
  const at = parsedTime(lastCalibrationAtUtc);
  if (at === null) return null;
  const ageMs = nowMs - at;
  if (!Number.isFinite(ageMs)) return null;
  return Math.max(0, Math.min(1, 1 - ageMs / CALIBRATION_FRESH_WINDOW_MS));
}

/** B 卡逐项目行在 L1 的**展示上限**（其余走 L2 证据层；不截断事实，只截断首屏噪声）。 */
export const CODE_INDEX_VISIBLE_PROJECT_LIMIT = 6;

// ── L2 证据层字段清单：**由契约常量生成**（页面不得手写字段名）──────────
// ⚠️ `Record<keyof X, …>` 是**编译期穷尽性约束**：`types.ts` 的符号索引块新增字段而这里不补
//    ⇒ `tsc` 直接报错。**声明顺序 = wire 顺序**（`Object.keys` 保持字符串键的插入序）。

const CODE_INDEX_BLOCK_FIELD_META: Record<
  keyof CodeIndexStatusDetail,
  { label: string; kind: FieldKind }
> = {
  workspaceIds: { label: 'workspace 列表', kind: 'list' },
  maintenanceRunning: { label: '维护驱动运行中', kind: 'bool' },
  batchesProcessed: { label: '合并批次数', kind: 'count' },
  reconcileRequests: { label: '重建请求数', kind: 'count' },
  removalObservations: { label: '删除观测数', kind: 'count' },
  pendingReconcileScopeCount: { label: '待重建 scope 数', kind: 'count' },
  projects: { label: '项目条目', kind: 'nested' },
  note: { label: '降级原因', kind: 'text' },
};

const CODE_INDEX_PROJECT_FIELD_META: Record<
  keyof CodeIndexProjectStatus,
  { label: string; kind: FieldKind }
> = {
  workspaceId: { label: 'workspace', kind: 'path' },
  projectId: { label: '项目标识', kind: 'path' },
  displayName: { label: '展示名', kind: 'text' },
  rootPath: { label: '根路径', kind: 'path' },
  registered: { label: '已登记', kind: 'bool' },
  registrationState: { label: '注册态（投影）', kind: 'text' },
  registrationStatus: { label: '注册态（原始）', kind: 'text' },
  registrationSource: { label: 'scope 来源', kind: 'text' },
  maintenance: { label: '维护态', kind: 'nested' },
  maintenanceReason: { label: '维护态缺席原因', kind: 'text' },
  rootPathExists: { label: '根路径存在', kind: 'bool' },
  stale: { label: '陈旧', kind: 'bool' },
};

/** 维护态 23 字段 —— **顺序逐位对齐后端 A5 断言**（见 `types.ts` 的路径引用）。 */
const CODE_INDEX_MAINTENANCE_FIELD_META: Record<
  keyof CodeIndexMaintenanceStatus,
  { label: string; kind: FieldKind }
> = {
  workspaceId: { label: 'workspace', kind: 'path' },
  scopeId: { label: 'scope 标识', kind: 'path' },
  rootPath: { label: '根路径', kind: 'path' },
  observedVersion: { label: '观测版本', kind: 'count' },
  desiredVersion: { label: '期望版本', kind: 'count' },
  committedVersion: { label: '已提交版本', kind: 'count' },
  markedWhileInFlightCount: { label: '进行中标记数', kind: 'count' },
  indexPending: { label: '待索引', kind: 'bool' },
  indexInFlight: { label: '索引进行中', kind: 'bool' },
  needsReconcile: { label: '需要重建', kind: 'bool' },
  reconcileReason: { label: '重建原因', kind: 'text' },
  reconcileRequestCount: { label: '重建请求数', kind: 'count' },
  removalObservationCount: { label: '删除观测数', kind: 'count' },
  lastRemovalPaths: { label: '最近删除路径', kind: 'list' },
  removedFileCount: { label: '已删除文件数', kind: 'count' },
  incrementallyIndexedFileCount: { label: '增量索引文件数', kind: 'count' },
  scopeEscalationCount: { label: '升级重建次数', kind: 'count' },
  sweptFileCount: { label: '校准清理文件数', kind: 'count' },
  calibrationRunCount: { label: '校准运行次数', kind: 'count' },
  rejectedCalibrationRunCount: { label: '被拒校准次数', kind: 'count' },
  lastCalibrationAtUtc: { label: '最近校准时刻', kind: 'timestamp' },
  recentObservationCount: { label: '宽限窗口观测数', kind: 'count' },
  watcherAttached: { label: 'watcher 已挂接', kind: 'bool' },
};

function fieldDescriptors<K extends string>(
  meta: Record<K, { label: string; kind: FieldKind }>,
): readonly FieldDescriptor<K>[] {
  return (Object.keys(meta) as K[]).map((key) => ({ key, ...meta[key] }));
}

/** `codeIndex` 块**8 列**（顺序 = wire 顺序）。 */
export const CODE_INDEX_BLOCK_FIELDS: readonly FieldDescriptor<keyof CodeIndexStatusDetail>[] =
  fieldDescriptors(CODE_INDEX_BLOCK_FIELD_META);

/** 逐项目**12 列**（顺序 = wire 顺序）。 */
export const CODE_INDEX_PROJECT_FIELDS: readonly FieldDescriptor<keyof CodeIndexProjectStatus>[] =
  fieldDescriptors(CODE_INDEX_PROJECT_FIELD_META);

/** 维护态**23 列**（顺序 = wire 顺序；与后端 A5 断言逐位一致）。 */
export const CODE_INDEX_MAINTENANCE_FIELDS: readonly FieldDescriptor<
  keyof CodeIndexMaintenanceStatus
>[] = fieldDescriptors(CODE_INDEX_MAINTENANCE_FIELD_META);

/**
 * 字段值 → **tooltip / 纯文本**用的字符串。
 * `null`/`undefined` 一律「未知」（绝不折叠成否 / 零）；数组按「；」拼接，空数组给「（空）」；
 * 复合结构（对象）只报「有」（逐字段进 L2 表），既不猜也不丢。
 */
export function rawFieldText(kind: FieldKind, value: unknown): string {
  if (value === null || value === undefined) return UNKNOWN_TEXT;
  switch (kind) {
    case 'bool':
      return value === true ? '是' : '否';
    case 'count':
      return typeof value === 'number' ? formatTriStateCount(value) : UNKNOWN_TEXT;
    case 'bytes':
      return typeof value === 'number' ? formatTriStateBytes(value) : UNKNOWN_TEXT;
    case 'timestamp':
      return typeof value === 'string' ? formatTriStateTimestamp(value) : UNKNOWN_TEXT;
    case 'durationMs':
      return typeof value === 'number' ? formatTriStateDurationMs(value) : UNKNOWN_TEXT;
    case 'list': {
      if (!Array.isArray(value)) return UNKNOWN_TEXT;
      if (value.length === 0) return EMPTY_TEXT;
      return value.map((item) => String(item)).join('；');
    }
    case 'nested':
      if (Array.isArray(value)) return `共 ${value.length} 项（见下方表）`;
      return typeof value === 'object' ? '有（见下方表）' : formatTriStateText(String(value));
    case 'path':
    case 'text':
    default:
      return typeof value === 'string' ? formatTriStateText(value) : UNKNOWN_TEXT;
  }
}

/**
 * 维护态 23 字段的 tooltip（字段名 + 顺序**由契约常量生成**；页面不得手写）。
 * 这是「原始 23 字段只出现在 hover tooltip 与 L2」的**唯一**入口。
 */
export function describeMaintenanceStatus(status: CodeIndexMaintenanceStatus): string {
  return CODE_INDEX_MAINTENANCE_FIELDS.map(
    (field) => `${field.key}: ${rawFieldText(field.kind, status[field.key])}`,
  ).join('\n');
}

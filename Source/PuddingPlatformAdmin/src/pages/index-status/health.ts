// ── Slice P1：Admin「索引与检索」页的聚合健康态（纯函数 + 文案常量）──────────
// 规格真源：`Docs/Features/Index-Status-Panel-UI-Prototype-2026-10-01.md`
//   §2 状态矩阵（**首条命中即生效**）· §3 文案表（逐字）· §4 渲染纪律（三态贯穿）
//   线框：`Docs/Features/index-status-prototype/admin-wireframe.svg`
//   wire 契约：`./types.ts`（**不改**）
//
// 三条纪律：
// 1) 纯函数：不 import antd/react、不读写 DOM、**不读真实时钟**
//    （需要「现在」的地方一律由调用方经 `nowMs` 注入 ⇒ 断言不随时间漂移）。
// 2) 零后端改动：全部结论由既有 wire 字段在前端推导。
// 3) 三态贯穿：`null` = 未知 · `false` = 否 · `0` = 0；本文件**禁止**把未知折叠成否/零
//    （唯一的 `?? 0` 只出现在已被「未知守卫」拦下的算数求和里，且结果不流向展示层）。

import {
  UNKNOWN_TEXT,
  formatJobsReason,
  formatRelativeTime,
  formatTriStateBytes,
  formatTriStateCount,
  formatTriStateText,
  formatUtcTime,
} from './api';
import type {
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

/**
 * B 卡 · 符号索引（代码）：本片**未接入**（后端 wire 无 `codeIndex`，属 `S-A2`）。
 * 未接入 ≠ 故障 ⇒ 不参与 L0 聚合、不染红、**不留空白**（空白会被读成「坏了」）。
 */
export const SYMBOL_CARD_STATUS: CardStatus = cardStatus(
  'neutral',
  '未接入',
  '本块尚未接入（待 S-A2：把 codeIndex 并入同一端点）',
);

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
  | 'text';

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

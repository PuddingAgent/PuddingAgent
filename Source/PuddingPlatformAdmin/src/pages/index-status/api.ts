import { request } from '@umijs/max';
import type { FullTextIndexStatusSnapshot } from './types';

// ── Slice B 全文索引状态 API（同源 Core，登录态 Admin JWT 自动携带）──
// 端点：GET /api/admin/index/status（IndexAdminController）。
// ⚠️ 本文件**只有只读读取**：没有任何写操作能力，也不接收写请求体。

export async function getIndexStatus(): Promise<FullTextIndexStatusSnapshot> {
  return request('/api/admin/index/status', { method: 'GET' });
}

// ── 展示工具（纯函数，便于断言）────────────────────────────────────
// R4/D3 三态纪律：后端用 `null` 表达「**未知**」，它与 `false` / `0` 是不同事实。
// 因此这里**绝不**用 `?? 0` / `|| '否'` / `?? '—'` 这类把未知折叠成否或零的写法：
//   null|undefined ⇒ 「未知」   false ⇒ 「否」   0 ⇒ 「0」
// 所有渲染口径都经这些函数，页面 JSX 内不再散落格式化分支。

/** 「未知」的**唯一**统一文案（避免各页面写「—」「N/A」各说各话）。 */
export const UNKNOWN_TEXT = '未知';

/** 空字符串（**有值但为空**，与 `null` = 未知不同事实）的统一文案。 */
export const EMPTY_TEXT = '（空）';

/**
 * 布尔三态：`true` ⇒ 是 · `false` ⇒ **否** · `null`/`undefined` ⇒ **未知**。
 * 关键：`null` 与 `false` 必须给出**不同**文案，否则「不可知」会被读成「关」。
 */
export function formatTriStateBoolean(value: boolean | null | undefined): string {
  if (value === true) return '是';
  if (value === false) return '否';
  return UNKNOWN_TEXT;
}

/**
 * 计数三态：`null`/`undefined` ⇒ 未知 · `0` ⇒ **`0`**（不得折叠成未知/「—」）。
 */
export function formatTriStateCount(value: number | null | undefined): string {
  if (value === null || value === undefined) return UNKNOWN_TEXT;
  if (!Number.isFinite(value)) return UNKNOWN_TEXT;
  return value.toLocaleString('zh-CN');
}

/**
 * 字节三态：`null`/`undefined` ⇒ 未知 · `0` ⇒ **`0 B`** · 其余按 1024 进制给人类可读值。
 */
export function formatTriStateBytes(value: number | null | undefined): string {
  if (value === null || value === undefined) return UNKNOWN_TEXT;
  if (!Number.isFinite(value)) return UNKNOWN_TEXT;
  if (value < 1024) return `${value} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let scaled = value;
  let unit = -1;
  do {
    scaled /= 1024;
    unit++;
  } while (scaled >= 1024 && unit < units.length - 1);
  return `${scaled >= 100 ? scaled.toFixed(0) : scaled.toFixed(1)} ${units[unit]}`;
}

/**
 * 时间戳三态：`null`/`undefined`/空串 ⇒ 未知（不写「—」，避免与「无此字段」混淆）；
 * 解析不出来的字符串同样是**未知**（事实不可得），不假装成某个时刻。
 */
export function formatTriStateTimestamp(value: string | null | undefined): string {
  if (value === null || value === undefined || value === '') return UNKNOWN_TEXT;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return UNKNOWN_TEXT;
  return parsed.toLocaleString('zh-CN', { hour12: false });
}

/**
 * 耗时三态（毫秒）：`null`/`undefined` ⇒ 未知（非终态 job 不造假事实）· `0` ⇒ `0 ms`。
 */
export function formatTriStateDurationMs(value: number | null | undefined): string {
  if (value === null || value === undefined) return UNKNOWN_TEXT;
  if (!Number.isFinite(value)) return UNKNOWN_TEXT;
  if (value < 1000) return `${value} ms`;
  if (value < 60_000) return `${(value / 1000).toFixed(1)} s`;
  return `${(value / 60_000).toFixed(1)} min`;
}

/**
 * 文本三态：`null`/`undefined` ⇒ 未知 · 空串 ⇒ `（空）`（有值但为空）· 其余原样。
 */
export function formatTriStateText(value: string | null | undefined): string {
  if (value === null || value === undefined) return UNKNOWN_TEXT;
  if (value === '') return EMPTY_TEXT;
  return value;
}

/** 已知的台账原因码 → 中文解释（逐条对应后端 `FullTextIndexStatusJobReasons`）。 */
const JOBS_REASON_TEXT: Record<string, string> = {
  'composition-not-created':
    '供给组合从未被构造（例如全文索引未启用）—— 因此进程内不存在任何 job，而不是「job 都跑完了」。',
  'no-jobs-recorded': '供给组合已经构造且台账可读，但台账里确实一条 job 记录都没有。',
  'ledger-read-failed': 'job 台账读取失败 ⇒ 台账内容不可知（这不等于「没有 job」，请查宿主日志）。',
};

/**
 * 台账原因码的三态解释：`null`/`undefined` ⇒ 明确报「未提供原因码」（契约异常，不静默）；
 * 已登记的三个取值给中文解释；未登记的取值原样回显并标注「未登记」，不吞。
 */
export function formatJobsReason(reason: string | null | undefined): string {
  if (reason === null || reason === undefined || reason === '') {
    return '未提供台账原因码（端点契约异常：jobs 为空时必须给出 jobsReason）。';
  }
  const known = JOBS_REASON_TEXT[reason];
  if (known) return known;
  return `未登记的原因码「${reason}」—— 请对照后端 FullTextIndexStatusJobReasons 补充解释。`;
}

// ── P1 新增展示工具（**只增不改**：上面既有函数的签名与行为一律不动）──────

/**
 * 相对时间（纯函数）：`null`/空串/不可解析 ⇒ 未知。
 * `nowMs` 显式注入（默认取当前时钟）⇒ 断言不随时间漂移，可单测。
 * 未来时刻（时钟回拨 / 跨机时差）一律收敛为「刚刚」，**不外推**成负数时间。
 */
export function formatRelativeTime(
  value: string | null | undefined,
  nowMs: number = Date.now(),
): string {
  if (value === null || value === undefined || value === '') return UNKNOWN_TEXT;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return UNKNOWN_TEXT;
  const deltaMs = nowMs - parsed.getTime();
  if (!Number.isFinite(deltaMs)) return UNKNOWN_TEXT;
  if (deltaMs < 60_000) return '刚刚';
  const minutes = Math.floor(deltaMs / 60_000);
  if (minutes < 60) return `${minutes} 分钟前`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} 小时前`;
  return `${Math.floor(hours / 24)} 天前`;
}

/**
 * 绝对时刻（UTC，逐字）：规格 §3 要求副行给「绝对时间 UTC」。
 * `null`/空串/不可解析 ⇒ 未知（不伪造时刻）。
 */
export function formatUtcTime(value: string | null | undefined): string {
  if (value === null || value === undefined || value === '') return UNKNOWN_TEXT;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return UNKNOWN_TEXT;
  return `${parsed.toISOString().slice(0, 19).replace('T', ' ')} UTC`;
}

// ── P5 新增：失败态诚实化（**只增不改**：上面既有函数的签名与行为一律不动）──────
// 目的：把「宿主尚未部署该端点(404)」「未授权(401/403)」「网络层失败」「其它」区分开，
// 并且**取不到状态码时绝不猜成 404** —— 否则「不知道」会被读成「未部署」这一具体事实。

/** 端点路径的**唯一真源**：页面文案与报错提示都必须引用它，不得各自再写一遍字面量。 */
export const INDEX_STATUS_ENDPOINT = '/api/admin/index/status';

/** 失败分类：`not-deployed` = 端点不存在（宿主没部署）· `unauthorized` = 未授权 · `network` = 网络层 · `unknown` = 其余未知。 */
export type IndexStatusFailureKind = 'not-deployed' | 'unauthorized' | 'network' | 'unknown';

/** `classifyIndexStatusFailure` 的归一化结果（供 Alert 文案与排查使用）。 */
export interface IndexStatusFailure {
  kind: IndexStatusFailureKind;
  /** 能取到就给真实 HTTP 状态码；取不到 ⇒ `null`（**绝不**猜成 404）。 */
  httpStatus: number | null;
  /** 原始错误文本（原样保留，供排查；不得吞）。 */
  rawMessage: string;
}

/** 是否为「非 null 的对象」（含数组/函数外的一切 object，用于安全取字段）。 */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/** 原始错误文本：字符串 / 带 `message` 的对象（含 Error）/ 其余 ⇒ 空串。 */
function readRawMessage(raw: unknown): string {
  if (typeof raw === 'string') return raw;
  if (isRecord(raw) && typeof raw.message === 'string') return raw.message;
  return '';
}

/** 归一化 HTTP 状态码：仅接受 100..599 的整数；其余（含字符串 `"404"`）一律视为「取不到」。 */
function normalizeHttpStatus(value: unknown): number | null {
  if (typeof value === 'number' && Number.isInteger(value) && value >= 100 && value <= 599) {
    return value;
  }
  return null;
}

/**
 * 防御式取码（本仓 umi `request` 的报错形状**未在本机验证** ⇒ 不得只读一条路径）：
 * 依次尝试 `response.status` → `status` → `statusCode` → 从 `message` 抠 `status code NNN` / 裸 `4xx|5xx`。
 * 任一步拿到的不是 100..599 的整数都当作「取不到」⇒ 返回 `null`（**绝不**猜成 404）。
 */
function readHttpStatus(raw: unknown): number | null {
  if (isRecord(raw)) {
    const response = raw.response;
    const candidates: unknown[] = [
      isRecord(response) ? response.status : undefined,
      raw.status,
      raw.statusCode,
    ];
    for (const candidate of candidates) {
      const parsed = normalizeHttpStatus(candidate);
      if (parsed !== null) return parsed;
    }
  }
  const message = readRawMessage(raw);
  const matched = /status code (\d{3})/i.exec(message) ?? /\b([45]\d\d)\b/.exec(message);
  if (matched) return normalizeHttpStatus(Number(matched[1]));
  return null;
}

/** 错误码（如 axios 的 `ERR_NETWORK`）；取不到 ⇒ `null`。 */
function readErrorCode(raw: unknown): string | null {
  return isRecord(raw) && typeof raw.code === 'string' ? raw.code : null;
}

/** 是否**带**一个可用的 HTTP 响应对象（`response` 是非 null 对象）。
 *  `{}` ⇒ false；`{ response: {} }` ⇒ true（响应在，只是状态码不可知）。 */
function hasUsableHttpResponse(raw: unknown): boolean {
  return isRecord(raw) && isRecord(raw.response);
}

/**
 * 把任意请求异常归一成 {@link IndexStatusFailure}（纯函数，可单测）。
 * 硬性分类规则：
 *   · HTTP 404 ⇒ `not-deployed`（端点不存在）
 *   · HTTP 401 / 403 ⇒ `unauthorized`
 *   · **完全没有** HTTP 响应（`response` 缺失 / `code === 'ERR_NETWORK'` / 消息含 `Network Error` / 消息含 `timeout`）⇒ `network`
 *   · 其余（含取不到的 5xx、取不到状态码的一切）⇒ `unknown`
 * 取不到状态码时 `httpStatus` 一律 `null`，**不得**折叠成 404。
 */
export function classifyIndexStatusFailure(raw: unknown): IndexStatusFailure {
  const rawMessage = readRawMessage(raw);
  const httpStatus = readHttpStatus(raw);

  if (httpStatus === 404) return { kind: 'not-deployed', httpStatus, rawMessage };
  if (httpStatus === 401 || httpStatus === 403) return { kind: 'unauthorized', httpStatus, rawMessage };

  // 只有在**完全没有响应对象**时才可能判网络层失败；`{ response: {} }` 属于「响应在但状态码不可知」⇒ unknown。
  if (!hasUsableHttpResponse(raw) && isRecord(raw)) {
    const missingResponseField = !('response' in raw);
    const networkByCode = readErrorCode(raw) === 'ERR_NETWORK';
    const networkByMessage = /network error/i.test(rawMessage) || /timeout/i.test(rawMessage);
    if (missingResponseField || networkByCode || networkByMessage) {
      return { kind: 'network', httpStatus: null, rawMessage };
    }
  }

  return { kind: 'unknown', httpStatus, rawMessage };
}

// ── 「数据库占用（按数据类）」的度量口径 ────────────────────────────
// 这一层的唯一职责：把接口给的「按数据类占用」整理成一张
// **合计等于该库文件大小**、且**百分比一律以该库文件大小为分母**的表。
//
// 为什么需要兜底行：接口只按表（或 dbstat 的 B 树）统计，索引页与 freelist 不在
// 数据类里。不加兜底行时「各子项之和 ≪ 库文件大小」会被读成「页面算错了」，
// 而真实原因是那部分字节没有语义归属。兜底行让每一层的数字能对上。
//
// 为什么分母必须换掉：旧实现用「该库内已归类表页之和」作分母，于是每个库各自
// 归一化到 100%，跨库的进度条长度不可比，且同一库内大项的百分比会被系统性放大。

import type { StorageDatabaseSpace, StorageInventoryDatabase } from './types';

/** 兜底行的稳定 ID（后端目录用点号命名，不会与这两个键撞车）。 */
export const OTHER_USAGE_TARGET_ID = '__other__';

/** 「空闲页（可复用）」行的稳定 ID。 */
export const FREE_PAGES_TARGET_ID = '__free_pages__';

/** 一行占用的种类：数据类 / 差值兜底 / 空闲页。 */
export type UsageRowKind = 'class' | 'other' | 'free';

/** 表里的一行。 */
export interface UsageRow {
  targetId: string;
  displayName: string;
  bytes: number;
  /** 占该库文件大小的百分比（0–100，保留 1 位小数）。 */
  percent: number;
  kind: UsageRowKind;
  /** 字节数来自采样/估算 ⇒ 界面加「≈」前缀与「估算」徽标。 */
  estimated: boolean;
  /** 字节数由差值推算 ⇒ 工具提示要写明推导方式。 */
  inferred: boolean;
  /** 该行命中的物理表数（兜底行与空闲页行为 0）。 */
  tableCount: number;
  /** 该行的补充说明（工具提示文案）；无需说明时为 null。 */
  hint: string | null;
}

/** 一个数据库的完整占用分解。 */
export interface UsageBreakdown {
  /** 该库文件大小（后端口径 = 主文件 + WAL + SHM）。 */
  fileBytes: number;
  /** 实际用作百分比分母的字节数（fileBytes 缺失时回落 pageSize × pageCount）。 */
  denominatorBytes: number;
  /** 分母是否为回落值（说明该库文件大小没拿到）。 */
  denominatorIsFallback: boolean;
  /** 各数据类之和。 */
  classBytes: number;
  /** 空闲页字节（未知时按 0 计，并置 freeBytesKnown = false）。 */
  freeBytes: number;
  /** 空闲页字节是否可知（快照没采到 PRAGMA 时为 false ⇒ 空闲页被并进兜底行）。 */
  freeBytesKnown: boolean;
  /** 兜底行字节（负数表示「归类 + 空闲页」已经超出库大小）。 */
  otherBytes: number;
  /** 展示用行（数据类在前，兜底行与空闲页在后）。 */
  rows: UsageRow[];
  /** rows 的字节合计。 */
  sumBytes: number;
  /** 合计是否精确闭合到分母。 */
  closed: boolean;
  /** 「数据类 + 空闲页」超出库文件大小的字节数（正常为 0）。 */
  overflowBytes: number;
  /** 整个库的字节数是否基于采样估算（spaceSource = rowcount-sample）。 */
  estimated: boolean;
}

/** 采样模式下数据类字节的来源说明（工具提示用）。 */
const SAMPLE_SOURCE_HINT =
  '字节数按「每表精确行数 × 抽样平均行长」推算，不含索引页；因此这是一个估算值。';

/** 兜底行的说明：必须明写「按差值推算」，否则读者会以为它是测出来的。 */
const OTHER_ROW_HINT =
  '按差值推算：库文件大小 − 各数据类之和 − 空闲页。索引页与表外对象没有单独上报，含在这份差额里。';

/** 空闲页行的说明。 */
const FREE_ROW_HINT =
  '已删除数据留下的空闲页，会被后续写入复用，不是可再清理的额度。';

/**
 * 计算占比：`part / whole`，结果钳制在 0–100 并保留 1 位小数。
 *
 * <p>
 * 三条边界都是**故意的**：分母 ≤ 0（库为空、或字段缺失）返回 0 而不是 Infinity/NaN；
 * 分子 ≤ 0 返回 0 而不是负数；分子大于分母时封顶 100 而不是画出超过进度条轨道的长度。
 * </p>
 */
export function percentOf(part?: number | null, whole?: number | null): number {
  if (typeof part !== 'number' || typeof whole !== 'number') return 0;
  if (!Number.isFinite(part) || !Number.isFinite(whole)) return 0;
  if (whole <= 0 || part <= 0) return 0;
  const raw = (part / whole) * 100;
  if (!Number.isFinite(raw)) return 0;
  return Math.min(100, Math.max(0, Math.round(raw * 10) / 10));
}

/**
 * 从快照的数据库元数据里取出该库的**空闲页字节**。
 *
 * <p>
 * 返回 null 表示**未知**（而不是 0）：快照采样当时没读到 `PRAGMA`（后端
 * `ReadDatabaseSnapshot` 的 catch 分支）时 `pageSizeBytes`/`pageCount` 为 0，
 * 此时谎报 0 会让空闲页被算进兜底行且不说明，读者无法分辨。
 * </p>
 */
export function resolveFreeBytes(
  space: StorageDatabaseSpace,
  databases?: readonly StorageInventoryDatabase[] | null,
): number | null {
  if (!space.exists || !databases?.length) return null;
  const database = databases.find((item) => item.databaseId === space.key);
  if (!database) return null;
  if (database.pageSizeBytes <= 0 || database.pageCount <= 0) return null;
  return Math.max(0, toBytes(database.reusableFreeBytes));
}

/**
 * 把单个库的按数据类占用整理成「合计 = 库文件大小」的行列表。
 *
 * @param space 接口返回的单个库占用。
 * @param options.freeBytes 空闲页字节；`null`/省略表示未知（会被并进兜底行）。
 */
export function computeUsageBreakdown(
  space: StorageDatabaseSpace,
  options: { freeBytes?: number | null } = {},
): UsageBreakdown {
  const fileBytes = toBytes(space.fileBytes);
  const pageSpanBytes = toBytes(space.pageSize) * toBytes(space.pageCount);
  const denominatorIsFallback = fileBytes <= 0 && pageSpanBytes > 0;
  const denominatorBytes = fileBytes > 0 ? fileBytes : pageSpanBytes;

  const estimated = space.spaceSource === 'rowcount-sample';
  const classes = space.dataClasses ?? [];
  const classBytes = classes.reduce((sum, item) => sum + toBytes(item.bytes), 0);

  const freeBytesKnown = options.freeBytes !== null && options.freeBytes !== undefined;
  const freeBytes = freeBytesKnown ? Math.max(0, toBytes(options.freeBytes)) : 0;

  // 兜底行 = 分母 − 已归类 − 空闲页。为负说明「归类 + 空闲页」超过了库大小
  // （两处测量时点不同，或数据类本身是估算且高估）；此时不出兜底行，改为如实报超出量。
  const otherBytes = denominatorBytes - classBytes - freeBytes;
  const overflowBytes = otherBytes < 0 ? -otherBytes : 0;

  const rows: UsageRow[] = classes.map((item) => ({
    targetId: item.targetId,
    displayName: item.displayName,
    bytes: toBytes(item.bytes),
    percent: percentOf(item.bytes, denominatorBytes),
    kind: 'class' as const,
    estimated,
    inferred: false,
    tableCount: item.tables?.length ?? 0,
    hint: estimated ? SAMPLE_SOURCE_HINT : null,
  }));

  if (otherBytes > 0) {
    rows.push({
      targetId: OTHER_USAGE_TARGET_ID,
      // 空闲页未知时不能承诺独立成行，因此把它写进兜底行的括号里。
      displayName: freeBytesKnown ? '其它（索引 / 未归类表外对象）' : '其它（索引 / 空闲页 / 未归类表外对象）',
      bytes: otherBytes,
      percent: percentOf(otherBytes, denominatorBytes),
      kind: 'other',
      estimated: false,
      inferred: true,
      tableCount: 0,
      hint: OTHER_ROW_HINT,
    });
  }

  if (freeBytes > 0) {
    rows.push({
      targetId: FREE_PAGES_TARGET_ID,
      displayName: '空闲页（可复用）',
      bytes: freeBytes,
      percent: percentOf(freeBytes, denominatorBytes),
      kind: 'free',
      estimated: false,
      inferred: false,
      tableCount: 0,
      hint: FREE_ROW_HINT,
    });
  }

  const sumBytes = classBytes + Math.max(0, otherBytes) + freeBytes;

  return {
    fileBytes,
    denominatorBytes,
    denominatorIsFallback,
    classBytes,
    freeBytes,
    freeBytesKnown,
    otherBytes,
    rows,
    sumBytes,
    closed: denominatorBytes > 0 && sumBytes === denominatorBytes,
    overflowBytes,
    estimated,
  };
}

/** 把可能为 null/负数/NaN 的字节数收敛成非负有限数。 */
function toBytes(value?: number | null): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 0;
  return value > 0 ? value : 0;
}

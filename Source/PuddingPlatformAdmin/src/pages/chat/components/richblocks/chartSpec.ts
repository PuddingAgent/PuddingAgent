// ── 图表数据引用与校验 ─────────────────────────────────────────
// 为什么要"引用"而不是"内联"：
//
// 26 个房间 × 24 小时 = 624 点；一个月的传感器历史是几万点。让模型把这些
// 数字逐字写进 markdown，既费 token 又必然抄错。所以 spec 只声明**字段与
// 来源**：
//   - inline：小数据（几十行）直接给；
//   - artifact：大数据给 artifactId，由宿主读出 rows 再交给这里校验。
//
// 本模块是纯函数：不读文件、不请求接口。数据怎么来是宿主的事，
// 这样它能在 Web / Desktop / 单测里跑同一套规则。
// ─────────────────────────────────────────────────────────────

export type ChartKind = 'line' | 'bar' | 'area' | 'pie' | 'scatter';

export type ChartSource =
  | { kind: 'inline'; rows: Array<Record<string, unknown>> }
  | { kind: 'artifact'; artifactId: string };

export interface ChartSpec {
  type: ChartKind;
  source: ChartSource;
  /** 分类轴字段名（必须是 rows 的列）。 */
  x: string;
  /** 数值轴字段名（可多个 = 多系列）。 */
  y: string[];
  title?: string;
  /** 渲染点数上限；超出按均匀采样降采样。默认 500。 */
  maxPoints?: number;
}

export interface ChartDataValidation {
  ok: boolean;
  errors: string[];
  /** 校验通过后的行（可能被降采样）。 */
  rows: Array<Record<string, unknown>>;
  /** 被采样丢弃的行数（用于界面提示"已采样显示"）。 */
  droppedRows: number;
}

export const DEFAULT_MAX_POINTS = 500;

/**
 * 均匀降采样，**保留首尾**。
 * 不做均值聚合：图表要的是包络形状，平均值会把峰值抹平，反而让人误判。
 */
export function downsampleRows(
  rows: Array<Record<string, unknown>>,
  maxPoints: number,
): Array<Record<string, unknown>> {
  if (maxPoints <= 0 || rows.length <= maxPoints) return rows;
  if (maxPoints === 1) return [rows[0]];

  const step = (rows.length - 1) / (maxPoints - 1);
  const picked: Array<Record<string, unknown>> = [];
  let lastIndex = -1;
  for (let i = 0; i < maxPoints; i += 1) {
    const index = Math.round(i * step);
    if (index === lastIndex) continue;
    picked.push(rows[index]);
    lastIndex = index;
  }
  return picked;
}

const isNumericLike = (value: unknown): boolean =>
  typeof value === 'number' ? Number.isFinite(value) : typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value));

/**
 * 用 rows 校验 spec 的字段声明，并做降采样。
 *
 * 注意：`source.kind === 'artifact'` 时 rows 同样由调用方传入（宿主已解析）。
 * 如果宿主没解析出来，rows 为空 → 这里报错，而不是画一张空图。
 */
export function validateChartData(
  spec: ChartSpec,
  rows: Array<Record<string, unknown>> | null | undefined,
): ChartDataValidation {
  const errors: string[] = [];

  if (!rows || rows.length === 0) {
    return { ok: false, errors: ['没有数据行'], rows: [], droppedRows: 0 };
  }

  const first = rows[0];
  if (!(spec.x in first)) errors.push(`缺少分类字段：${spec.x}`);
  if (spec.y.length === 0) errors.push('至少需要一个数值字段');

  for (const key of spec.y) {
    if (!(key in first)) {
      errors.push(`缺少数值字段：${key}`);
      continue;
    }
    // 只检查前 50 行：大表全量扫描会卡住主线程，而"前 50 行没有数值"
    // 已经足够说明字段选错了。
    const sample = rows.slice(0, 50);
    const hasNumber = sample.some((row) => isNumericLike(row[key]));
    if (!hasNumber) errors.push(`字段不是数值：${key}`);
  }

  if (errors.length > 0) {
    return { ok: false, errors, rows: [], droppedRows: 0 };
  }

  const maxPoints = spec.maxPoints ?? DEFAULT_MAX_POINTS;
  const sampled = downsampleRows(rows, maxPoints);
  return {
    ok: true,
    errors: [],
    rows: sampled,
    droppedRows: rows.length - sampled.length,
  };
}

// ── 围栏解析 ───────────────────────────────────────────────────
// ```chart 围栏的内容是 **JSON spec**，不是可执行代码。
// 模型只产出数据描述，渲染器由我们控制——这条约定让 XSS 面不会因为
// 新增图表而扩大。
// ─────────────────────────────────────────────────────────────

export interface ChartSpecParseResult {
  ok: boolean;
  spec?: ChartSpec;
  error?: string;
}

const CHART_KINDS: ChartKind[] = ['line', 'bar', 'area', 'pie', 'scatter'];

const fail = (error: string): ChartSpecParseResult => ({ ok: false, error });

/** 从 ```chart 围栏内容解析 spec；任何字段不合法都显式失败并说明原因。 */
export function parseChartSpecJson(code: string): ChartSpecParseResult {
  let raw: unknown;
  try {
    raw = JSON.parse(code);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return fail(`JSON 解析失败：${message}`);
  }

  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
    return fail('spec 必须是 JSON 对象');
  }
  const spec = raw as Record<string, unknown>;

  if (!CHART_KINDS.includes(spec.type as ChartKind)) {
    return fail(`不支持的图表类型：${String(spec.type)}（可用：${CHART_KINDS.join(' / ')}）`);
  }

  if (typeof spec.x !== 'string' || spec.x.trim() === '') {
    return fail('缺少分类字段 x');
  }

  if (!Array.isArray(spec.y) || spec.y.length === 0) {
    return fail('y 必须是非空数组');
  }
  if (!spec.y.every((key) => typeof key === 'string' && key.trim() !== '')) {
    return fail('y 里的每一项都必须是非空字符串');
  }

  const source = spec.source as Record<string, unknown> | undefined;
  if (!source || typeof source !== 'object') {
    return fail('缺少 source');
  }

  let parsedSource: ChartSource;
  if (source.kind === 'inline') {
    if (!Array.isArray(source.rows)) {
      return fail('inline 来源必须提供 rows 数组');
    }
    parsedSource = { kind: 'inline', rows: source.rows as Array<Record<string, unknown>> };
  } else if (source.kind === 'artifact') {
    if (typeof source.artifactId !== 'string' || source.artifactId.trim() === '') {
      return fail('artifact 来源必须提供 artifactId');
    }
    parsedSource = { kind: 'artifact', artifactId: source.artifactId };
  } else {
    return fail(`不支持的 source.kind：${String(source.kind)}（可用：inline / artifact）`);
  }

  return {
    ok: true,
    spec: {
      type: spec.type as ChartKind,
      x: spec.x,
      y: spec.y as string[],
      source: parsedSource,
      title: typeof spec.title === 'string' ? spec.title : undefined,
      maxPoints:
        typeof spec.maxPoints === 'number' && Number.isFinite(spec.maxPoints)
          ? spec.maxPoints
          : undefined,
    },
  };
}

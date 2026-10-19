// ── 交互面板 spec（v1）─────────────────────────────────────────
// 右侧面板与 Agent 之间唯一的"内容契约"。
//
// 设计约束（2026-10-19 与用户对齐）：
// 1. 面板是**会话级**：同一 id 被 Agent 原位更新，不新开 tab；
// 2. 多方案布局固定四档（1/2/3/4 列），不给模型自由发挥的余地；
// 3. HTML/SVG 草稿**禁外链**（防追踪像素与数据外带），图片一律内联；
// 4. 标注是**结构化图元**（rect/arrow/note + 归一化坐标），不是像素涂鸦——
//    模型读不懂像素，纯截图回传等于没回传。
//
// 本文件只做类型 + 校验，不依赖 React / 宿主，可在任何一端复用（Web / Desktop / Core）。
// ─────────────────────────────────────────────────────────────

export const PANEL_SPEC_VERSION = 1;

/** 面板内容类型。 */
export type PanelKind = 'survey' | 'variants' | 'draft';

interface PanelSpecBase {
  v: typeof PANEL_SPEC_VERSION;
  /** 稳定 id：同一会话内 Agent 更新面板时保持不变。 */
  id: string;
  kind: PanelKind;
  title?: string;
  description?: string;
}

export type SurveyFieldType =
  | 'text'
  | 'textarea'
  | 'number'
  | 'select'
  | 'radio'
  | 'checkbox'
  | 'range';

export interface SurveyFieldOption {
  value: string;
  label: string;
}

export interface SurveyField {
  /** 回传 values 里的键名，同一 spec 内唯一。 */
  key: string;
  label: string;
  type: SurveyFieldType;
  required?: boolean;
  placeholder?: string;
  /** select / radio / checkbox 必填。 */
  options?: SurveyFieldOption[];
  min?: number;
  max?: number;
  step?: number;
  /** text / textarea 的字符上限（同时是回传防注入的一道闸）。 */
  maxLength?: number;
}

export interface SurveySpec extends PanelSpecBase {
  kind: 'survey';
  fields: SurveyField[];
  submitLabel?: string;
  /** 提交后是否清空；默认 true。 */
  resetOnSubmit?: boolean;
}

export interface VariantCell {
  id: string;
  title: string;
  /** 简化 HTML 草稿（沙箱渲染）。与 svg 二选一。 */
  html?: string;
  /** 内联 SVG（消毒后内联渲染）。与 html 二选一。 */
  svg?: string;
  /** 方案说明，纯文本。 */
  note?: string;
}

export interface VariantsSpec extends PanelSpecBase {
  kind: 'variants';
  /** 固定四档：1/2/3/4 列。 */
  layout: 1 | 2 | 3 | 4;
  variants: VariantCell[];
  selectMode?: 'single' | 'multi' | 'none';
}

export interface DraftSpec extends PanelSpecBase {
  kind: 'draft';
  format: 'html' | 'svg';
  source: string;
  /** 默认 false：草稿不需要脚本；确有动效需求时才显式打开。 */
  allowScripts?: boolean;
}

export type PanelSpec = SurveySpec | VariantsSpec | DraftSpec;

/** 标注图元：坐标一律归一化到 0..1（与画布尺寸解耦）。 */
export type Annotation =
  | { type: 'rect'; x: number; y: number; w: number; h: number; text?: string; targetId?: string }
  | {
      type: 'arrow';
      from: { x: number; y: number };
      to: { x: number; y: number };
      text?: string;
      targetId?: string;
    }
  | { type: 'note'; at: { x: number; y: number }; text: string; targetId?: string };

export interface PanelValidationIssue {
  path: string;
  message: string;
  severity: 'error' | 'warning';
}

export interface PanelValidationResult {
  ok: boolean;
  issues: PanelValidationIssue[];
  spec?: PanelSpec;
}

/** 体积与数量上限：防止一次面板塞爆消息与内存。 */
export const PANEL_LIMITS = {
  maxIdLength: 64,
  maxTitleLength: 120,
  maxSurveyFields: 20,
  maxVariants: 8,
  maxDraftChars: 256 * 1024,
  maxAnnotations: 200,
  maxTextLength: 2000,
} as const;

/** 外链检测：HTML/SVG 草稿里出现任何跨站资源都拒绝（用户裁定：禁外链）。 */
const EXTERNAL_REFERENCE_PATTERNS: ReadonlyArray<[RegExp, string]> = [
  [/\b(?:src|href|poster)\s*=\s*["'](?:https?:)?\/\//i, '外链资源（src/href 指向 http(s) 或协议相对地址）'],
  [/@import\s+(?:url\()?\s*["']?(?:https?:)?\/\//i, 'CSS @import 外链'],
  [/url\(\s*["']?(?:https?:)?\/\//i, 'CSS url() 外链'],
  [/<link\b/i, '<link> 引入外部资源'],
];

const isNonEmptyString = (value: unknown): value is string =>
  typeof value === 'string' && value.trim().length > 0;

const isPlainObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const inUnitRange = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1;

/**
 * 校验面板 spec。**只接受声明式结构**：任何自由 HTML/JS 都必须落在
 * `draft.source` 且受外链与体积约束。
 */
export function validatePanelSpec(input: unknown): PanelValidationResult {
  const issues: PanelValidationIssue[] = [];
  const fail = (path: string, message: string): void => {
    issues.push({ path, message, severity: 'error' });
  };
  const warn = (path: string, message: string): void => {
    issues.push({ path, message, severity: 'warning' });
  };

  if (!isPlainObject(input)) {
    return { ok: false, issues: [{ path: '$', message: 'spec 必须是对象', severity: 'error' }] };
  }

  if (input.v !== PANEL_SPEC_VERSION) {
    fail('$.v', `不支持的 spec 版本：${String(input.v)}（期望 ${PANEL_SPEC_VERSION}）`);
  }
  if (!isNonEmptyString(input.id) || input.id.length > PANEL_LIMITS.maxIdLength) {
    fail('$.id', `id 必须是非空字符串且不超过 ${PANEL_LIMITS.maxIdLength} 字符`);
  }
  if (
    typeof input.title === 'string' &&
    input.title.length > PANEL_LIMITS.maxTitleLength
  ) {
    fail('$.title', `title 不超过 ${PANEL_LIMITS.maxTitleLength} 字符`);
  }

  const kind = input.kind;
  if (kind !== 'survey' && kind !== 'variants' && kind !== 'draft') {
    fail('$.kind', `未知面板类型：${String(kind)}`);
    return { ok: false, issues };
  }

  if (kind === 'survey') {
    const fields = input.fields;
    if (!Array.isArray(fields) || fields.length === 0) {
      fail('$.fields', 'survey 至少需要 1 个字段');
    } else if (fields.length > PANEL_LIMITS.maxSurveyFields) {
      fail('$.fields', `字段数不超过 ${PANEL_LIMITS.maxSurveyFields}`);
    } else {
      const seen = new Set<string>();
      fields.forEach((field, index) => {
        const path = `$.fields[${index}]`;
        if (!isPlainObject(field)) {
          fail(path, '字段必须是对象');
          return;
        }
        if (!isNonEmptyString(field.key)) return fail(`${path}.key`, 'key 必填');
        if (seen.has(field.key)) fail(`${path}.key`, `key 重复：${field.key}`);
        seen.add(field.key);
        if (!isNonEmptyString(field.label)) fail(`${path}.label`, 'label 必填');
        const type = field.type;
        const allowed: SurveyFieldType[] = [
          'text',
          'textarea',
          'number',
          'select',
          'radio',
          'checkbox',
          'range',
        ];
        if (!allowed.includes(type as SurveyFieldType)) {
          fail(`${path}.type`, `未知字段类型：${String(type)}`);
          return;
        }
        const needsOptions = type === 'select' || type === 'radio' || type === 'checkbox';
        if (needsOptions) {
          if (!Array.isArray(field.options) || field.options.length === 0) {
            fail(`${path}.options`, `${String(type)} 必须提供 options`);
          }
        }
        if (type === 'range' && field.min === undefined && field.max === undefined) {
          warn(`${path}`, 'range 未给 min/max，前端将按 0..100 处理');
        }
        if (
          typeof field.maxLength === 'number' &&
          field.maxLength > PANEL_LIMITS.maxTextLength
        ) {
          fail(`${path}.maxLength`, `maxLength 不超过 ${PANEL_LIMITS.maxTextLength}`);
        }
      });
    }
  }

  if (kind === 'variants') {
    const layout = input.layout;
    if (layout !== 1 && layout !== 2 && layout !== 3 && layout !== 4) {
      fail('$.layout', 'layout 只能是 1/2/3/4（固定四档）');
    }
    const variants = input.variants;
    if (!Array.isArray(variants) || variants.length === 0) {
      fail('$.variants', 'variants 至少 1 项');
    } else if (variants.length > PANEL_LIMITS.maxVariants) {
      fail('$.variants', `方案数不超过 ${PANEL_LIMITS.maxVariants}`);
    } else {
      const seen = new Set<string>();
      variants.forEach((variant, index) => {
        const path = `$.variants[${index}]`;
        if (!isPlainObject(variant)) {
          fail(path, '方案必须是对象');
          return;
        }
        if (!isNonEmptyString(variant.id)) fail(`${path}.id`, 'id 必填');
        else if (seen.has(variant.id)) fail(`${path}.id`, `id 重复：${variant.id}`);
        else seen.add(variant.id);
        if (!isNonEmptyString(variant.title)) fail(`${path}.title`, 'title 必填');
        const hasHtml = isNonEmptyString(variant.html);
        const hasSvg = isNonEmptyString(variant.svg);
        if (hasHtml === hasSvg) {
          fail(`${path}`, 'html 与 svg 必须二选一（不能都给或都不给）');
        }
        if (hasHtml) checkExternalReferences(String(variant.html), `${path}.html`, fail);
        if (hasSvg) {
          checkExternalReferences(String(variant.svg), `${path}.svg`, fail);
          if (/<script/i.test(String(variant.svg))) {
            fail(`${path}.svg`, 'SVG 内不允许 <script>');
          }
        }
      });
    }
  }

  if (kind === 'draft') {
    const format = input.format;
    if (format !== 'html' && format !== 'svg') {
      fail('$.format', 'format 只能是 html 或 svg');
    }
    if (!isNonEmptyString(input.source)) {
      fail('$.source', 'source 必填');
    } else {
      const chars = input.source.length;
      if (chars > PANEL_LIMITS.maxDraftChars) {
        fail('$.source', `草稿超过 ${PANEL_LIMITS.maxDraftChars} 字符`);
      }
      checkExternalReferences(input.source, '$.source', fail);
      if (format === 'svg' && /<script/i.test(input.source)) {
        fail('$.source', 'SVG 草稿内不允许 <script>');
      }
      if (input.allowScripts === true && format === 'svg') {
        fail('$.allowScripts', 'SVG 草稿不支持脚本');
      }
    }
  }

  const ok = !issues.some((issue) => issue.severity === 'error');
  return ok ? { ok, issues, spec: input as unknown as PanelSpec } : { ok, issues };
}

function checkExternalReferences(
  source: string,
  path: string,
  fail: (path: string, message: string) => void,
): void {
  for (const [pattern, label] of EXTERNAL_REFERENCE_PATTERNS) {
    if (pattern.test(source)) {
      fail(path, `禁止${label}：面板内容必须自包含（图片用内联 data: 或 vision-* 引用）`);
      return;
    }
  }
}

/** 校验标注数组。 */
export function validateAnnotations(input: unknown): PanelValidationIssue[] {
  const issues: PanelValidationIssue[] = [];
  if (input === undefined) return issues;
  if (!Array.isArray(input)) {
    return [{ path: '$.annotations', message: '必须是数组', severity: 'error' }];
  }
  if (input.length > PANEL_LIMITS.maxAnnotations) {
    issues.push({
      path: '$.annotations',
      message: `标注数不超过 ${PANEL_LIMITS.maxAnnotations}`,
      severity: 'error',
    });
  }
  input.forEach((item, index) => {
    const path = `$.annotations[${index}]`;
    if (!isPlainObject(item)) {
      issues.push({ path, message: '标注必须是对象', severity: 'error' });
      return;
    }
    if (item.type === 'rect') {
      for (const key of ['x', 'y', 'w', 'h'] as const) {
        if (!inUnitRange(item[key])) {
          issues.push({ path: `${path}.${key}`, message: '必须是 0..1 的归一化坐标', severity: 'error' });
        }
      }
    } else if (item.type === 'arrow') {
      for (const end of ['from', 'to'] as const) {
        const point = item[end];
        if (!isPlainObject(point) || !inUnitRange(point.x) || !inUnitRange(point.y)) {
          issues.push({ path: `${path}.${end}`, message: '必须是 0..1 的归一化点', severity: 'error' });
        }
      }
    } else if (item.type === 'note') {
      const at = item.at;
      if (!isPlainObject(at) || !inUnitRange(at.x) || !inUnitRange(at.y)) {
        issues.push({ path: `${path}.at`, message: '必须是 0..1 的归一化点', severity: 'error' });
      }
      if (!isNonEmptyString(item.text)) {
        issues.push({ path: `${path}.text`, message: 'note 必须有文字', severity: 'error' });
      }
    } else {
      issues.push({ path: `${path}.type`, message: '未知标注类型', severity: 'error' });
    }
    if (typeof item.text === 'string' && item.text.length > PANEL_LIMITS.maxTextLength) {
      issues.push({ path: `${path}.text`, message: `文字不超过 ${PANEL_LIMITS.maxTextLength}`, severity: 'error' });
    }
  });
  return issues;
}

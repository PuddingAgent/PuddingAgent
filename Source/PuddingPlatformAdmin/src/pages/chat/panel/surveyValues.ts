// ── 问卷取值校验 ───────────────────────────────────────────────
// 面板里的"提交"是用户数据进入对话的唯一入口，所以这里做两件事：
// 1. 客户端尽早给反馈（required / 长度 / 范围）；
// 2. **按 spec 白名单归一化**：select/radio/checkbox 的取值必须来自 options，
//    否则一律丢弃——不允许前端把任意字符串塞进回传。
//
// 这里只做纯计算，不依赖 React，便于单测。
// ─────────────────────────────────────────────────────────────

import type { SurveyField, SurveySpec } from './spec';

export interface SurveyValuesValidation {
  ok: boolean;
  /** key → 错误文案（第一条即可，UI 逐字段显示）。 */
  errors: Record<string, string>;
  /** 归一化后的值：只保留 spec 声明过的 key。 */
  values: Record<string, unknown>;
}

const isEmpty = (value: unknown): boolean =>
  value === undefined ||
  value === null ||
  value === '' ||
  (Array.isArray(value) && value.length === 0);

const optionValues = (field: SurveyField): string[] =>
  (field.options ?? []).map((option) => option.value);

export function validateSurveyValues(
  spec: SurveySpec,
  input: Record<string, unknown> | undefined,
): SurveyValuesValidation {
  const errors: Record<string, string> = {};
  const values: Record<string, unknown> = {};
  const source = input ?? {};

  for (const field of spec.fields) {
    const raw = source[field.key];

    if (isEmpty(raw)) {
      if (field.required) errors[field.key] = '这一项是必填的';
      continue;
    }

    switch (field.type) {
      case 'text':
      case 'textarea': {
        if (typeof raw !== 'string') {
          errors[field.key] = '必须是文本';
          break;
        }
        const limit = field.maxLength ?? 2000;
        if (raw.length > limit) {
          errors[field.key] = `不超过 ${limit} 个字符`;
          break;
        }
        values[field.key] = raw;
        break;
      }
      case 'number': {
        const numeric = typeof raw === 'number' ? raw : Number(raw);
        if (!Number.isFinite(numeric)) {
          errors[field.key] = '必须是数字';
          break;
        }
        if (field.min !== undefined && numeric < field.min) {
          errors[field.key] = `不能小于 ${field.min}`;
          break;
        }
        if (field.max !== undefined && numeric > field.max) {
          errors[field.key] = `不能大于 ${field.max}`;
          break;
        }
        values[field.key] = numeric;
        break;
      }
      case 'range': {
        const numeric = typeof raw === 'number' ? raw : Number(raw);
        if (!Number.isFinite(numeric)) {
          errors[field.key] = '必须是数字';
          break;
        }
        values[field.key] = numeric;
        break;
      }
      case 'select':
      case 'radio': {
        // 白名单：只接受 options 里声明过的值。
        if (typeof raw !== 'string' || !optionValues(field).includes(raw)) {
          errors[field.key] = '选项无效';
          break;
        }
        values[field.key] = raw;
        break;
      }
      case 'checkbox': {
        if (!Array.isArray(raw)) {
          errors[field.key] = '必须是多选值';
          break;
        }
        const allowed = optionValues(field);
        const picked = raw.filter(
          (item): item is string => typeof item === 'string' && allowed.includes(item),
        );
        if (picked.length !== raw.length) {
          errors[field.key] = '包含无效选项';
          break;
        }
        values[field.key] = picked;
        break;
      }
      default: {
        // 未知字段类型：不静默放行，交给上层当作 spec 非法处理。
        errors[field.key] = '未知字段类型';
      }
    }
  }

  return { ok: Object.keys(errors).length === 0, errors, values };
}

/** 是否全部必填项已填（用于禁用/启用提交按钮，不替代提交时校验）。 */
export function isSurveyReady(
  spec: SurveySpec,
  values: Record<string, unknown> | undefined,
): boolean {
  const source = values ?? {};
  return spec.fields.every((field) => !field.required || !isEmpty(source[field.key]));
}

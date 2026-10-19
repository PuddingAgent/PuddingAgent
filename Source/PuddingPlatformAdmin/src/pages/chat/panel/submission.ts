// ── 交互面板提交协议（v1）──────────────────────────────────────
// 用户在右侧面板点「提交」后，产生**一条用户消息**回到对话。
//
// 三条设计约束（2026-10-19 与用户对齐）：
// 1. 必须一眼看出"这是用户数据，不是新指令"——用带版本号的包裹块，
//    与工具结果同等待遇，模型不得把它当指令执行；
// 2. `v` 与 `id` 必带，`id` + 相同 values 的重复提交按幂等丢弃
//    （网络重试、用户连点不能算两次）；
// 3. 解析必须稳健：格式不符返回 null，绝不能把半截 JSON 当提交。
// ─────────────────────────────────────────────────────────────

import type { Annotation, PanelKind } from './spec';

export const UI_SUBMISSION_VERSION = 1;

const OPEN_PATTERN = /^\[UI_SUBMISSION\s+v=(\d+)\s+id=(\S+)\s+type=(\w+)\]\s*$/;
const CLOSE_PATTERN = /^\[\/UI_SUBMISSION\]\s*$/;

export interface UiSubmissionPayload {
  panelId: string;
  kind: PanelKind;
  /** 方案对比的选中项（variants）。 */
  selected?: string[];
  /** 问卷字段值（survey）。 */
  values?: Record<string, unknown>;
  /** 标注图元（任何类型都可带）。 */
  annotations?: Annotation[];
  /** 用户附加的自由文字（可选，受长度限制）。 */
  note?: string;
}

/** 服务端/前端都不信任的来源：提交内容的最大字符数。 */
export const MAX_SUBMISSION_CHARS = 64 * 1024;

export function buildUiSubmission(payload: UiSubmissionPayload): string {
  const body = {
    panelId: payload.panelId,
    kind: payload.kind,
    ...(payload.selected ? { selected: payload.selected } : {}),
    ...(payload.values ? { values: payload.values } : {}),
    ...(payload.annotations && payload.annotations.length > 0
      ? { annotations: payload.annotations }
      : {}),
    ...(payload.note ? { note: payload.note } : {}),
  };
  return [
    `[UI_SUBMISSION v=${UI_SUBMISSION_VERSION} id=${payload.panelId} type=${payload.kind}]`,
    JSON.stringify(body, null, 2),
    '[/UI_SUBMISSION]',
  ].join('\n');
}

/**
 * 从一段文本里解析提交块。允许包裹块前后有其它文字（用户可能带了备注）。
 * 解析失败一律返回 null——宁可丢弃，也不要把残缺数据喂给模型。
 */
export function parseUiSubmission(text: string): UiSubmissionPayload | null {
  if (!text || text.length > MAX_SUBMISSION_CHARS) return null;
  const lines = text.split('\n');
  let openIndex = -1;
  let header: { panelId: string; kind: PanelKind } | null = null;

  for (let index = 0; index < lines.length; index += 1) {
    const match = OPEN_PATTERN.exec(lines[index].trim());
    if (!match) continue;
    if (Number(match[1]) !== UI_SUBMISSION_VERSION) return null;
    const kind = match[3] as PanelKind;
    if (kind !== 'survey' && kind !== 'variants' && kind !== 'draft') return null;
    header = { panelId: match[2], kind };
    openIndex = index;
    break;
  }
  if (!header || openIndex < 0) return null;

  let closeIndex = -1;
  for (let index = openIndex + 1; index < lines.length; index += 1) {
    if (CLOSE_PATTERN.test(lines[index].trim())) {
      closeIndex = index;
      break;
    }
  }
  if (closeIndex < 0) return null;

  const json = lines.slice(openIndex + 1, closeIndex).join('\n').trim();
  if (!json) return null;

  try {
    const parsed = JSON.parse(json) as Record<string, unknown>;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
    return {
      panelId: header.panelId,
      kind: header.kind,
      ...(Array.isArray(parsed.selected)
        ? { selected: parsed.selected.filter((item): item is string => typeof item === 'string') }
        : {}),
      ...(parsed.values && typeof parsed.values === 'object' && !Array.isArray(parsed.values)
        ? { values: parsed.values as Record<string, unknown> }
        : {}),
      ...(Array.isArray(parsed.annotations)
        ? { annotations: parsed.annotations as Annotation[] }
        : {}),
      ...(typeof parsed.note === 'string' ? { note: parsed.note } : {}),
    };
  } catch {
    return null;
  }
}

/**
 * 幂等键：同一面板 + 相同选择/值，视为同一次提交。
 * 只对 `values` 做键排序，避免对象字段顺序不同导致重复提交被当成两次。
 */
export function submissionSignature(payload: UiSubmissionPayload): string {
  const selected = [...(payload.selected ?? [])].sort();
  const values = payload.values ? stableStringify(payload.values) : '';
  return `${payload.panelId}|${payload.kind}|${selected.join(',')}|${values}`;
}

function stableStringify(value: unknown): string {
  if (value === null || typeof value !== 'object') return JSON.stringify(value) ?? 'null';
  if (Array.isArray(value)) return `[${value.map(stableStringify).join(',')}]`;
  const entries = Object.entries(value as Record<string, unknown>).sort(([a], [b]) =>
    a < b ? -1 : a > b ? 1 : 0,
  );
  return `{${entries.map(([key, item]) => `${JSON.stringify(key)}:${stableStringify(item)}`).join(',')}}`;
}

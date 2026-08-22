// ── Generic renderer（CU-08 / TR-04b）──
// 通用文本/JSON 展示：入参 = presentation.meta（可选）+ 原始 payload 片段（可用时）。
// 安全截断展示；不伪造工具名、不猜测卡片类型。
import React from 'react';

export interface GenericRendererProps {
  /** presentation.meta：卡片族专属可持久化元数据，原样展示（如有）。 */
  meta?: Record<string, unknown> | null;
  /** 原始 payload 片段（如工具 arguments / result 原文），可用时展示。 */
  payload?: unknown;
}

/** 展示上限：超出截断并追加省略号，避免大 payload 撑爆消息流。 */
export const GENERIC_RENDERER_MAX_CHARS = 4000;

const safeStringify = (value: unknown): string => {
  if (typeof value === 'string') return value;
  try {
    const text = JSON.stringify(value, null, 2);
    return text ?? '';
  } catch {
    return String(value ?? '');
  }
};

const truncate = (text: string, max: number): string =>
  text.length > max ? `${text.slice(0, max)}…` : text;

const toDisplayText = (
  meta?: Record<string, unknown> | null,
  payload?: unknown,
): string => {
  const parts: string[] = [];
  const metaText = meta ? safeStringify(meta) : '';
  if (metaText.trim()) parts.push(metaText);
  const payloadText =
    payload !== undefined && payload !== null ? safeStringify(payload) : '';
  if (payloadText.trim()) parts.push(payloadText);
  return parts.join('\n\n');
};

export function GenericRenderer({ meta, payload }: GenericRendererProps) {
  const text = toDisplayText(meta, payload) || '（无附加信息）';
  return (
    <pre
      className="presentation-renderer presentation-renderer-generic"
      data-testid="presentation-generic"
    >
      {truncate(text, GENERIC_RENDERER_MAX_CHARS)}
    </pre>
  );
}

export default GenericRenderer;

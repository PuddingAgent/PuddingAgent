// ── PresentationRegistry：presentation.kind → renderer 的最小分派表（CU-08 / TR-04b）──
// 按 presentation.kind 分派 renderer；【禁止】按 toolName / 工具名字符串分支猜测卡片类型。
// kind 词表与 Core wire 契约对齐：services/platform/api.ts ToolPresentationKind（八类 intent）。
// 首批只完整消费 generic；其余七类留注册位（未注册时统一回落 Generic renderer）。
import type { ComponentType } from 'react';
import type { ToolPresentationKind } from '@/services/platform/api';
import { GenericRenderer } from './renderers/generic';

/** 八类 intent 键常量（与 Core ToolPresentationIntentKind 词表一一对应）。 */
export const PRESENTATION_KINDS: readonly ToolPresentationKind[] = [
  'generic',
  'terminal',
  'diff',
  'search',
  'read',
  'web',
  'delegation',
  'job',
];

/** renderer 契约：展示组件（入参 = presentation.meta + 原始 payload 片段，均可选）。 */
export type PresentationRenderer = ComponentType<{
  meta?: Record<string, unknown> | null;
  payload?: unknown;
}>;

/** kind → renderer 注册表（通用占位：首批仅 generic 注册，其余七类留注册位）。 */
const rendererRegistry: Partial<Record<ToolPresentationKind, PresentationRenderer>> = {
  generic: GenericRenderer,
  // 注册位（后续 CU 逐个注册专用 renderer；当前未注册 → 回落 Generic）：
  // terminal / diff / search / read / web / delegation / job
};

/** 按 kind 分派 renderer；未注册 / 未知 / undefined / null → 回落 Generic renderer。 */
export function resolveRenderer(
  kind: ToolPresentationKind | undefined | null,
): PresentationRenderer {
  const key = kind ?? 'generic';
  return rendererRegistry[key] ?? GenericRenderer;
}

/** 规范化 presentation.kind：缺失 / 未定义一律回落 'generic'。 */
export function getPresentationKind(
  presentation?: { kind?: ToolPresentationKind | null } | null,
): ToolPresentationKind {
  return presentation?.kind ?? 'generic';
}

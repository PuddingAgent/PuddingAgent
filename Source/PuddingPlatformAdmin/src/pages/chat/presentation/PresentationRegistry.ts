// ── PresentationRegistry：presentation.kind → renderer 的最小分派表（CU-08 / TR-04b）──
// 按 presentation.kind 分派 renderer；【禁止】按 toolName / 工具名字符串分支猜测卡片类型。
// kind 词表与 Core wire 契约对齐：services/platform/api.ts ToolPresentationKind（八类 intent）。
// 行为链 P3：terminal/diff/read/search/web 五类专用 renderer 已注册（§3.5 卡片家族）；
// delegation/job 未注册回落 Generic（delegation 由 DelegationRow 行承载主路径）。
import type { ComponentType } from 'react';
import type { ToolPresentationKind } from '@/services/platform/api';
import { GenericRenderer } from './renderers/generic';
import { TerminalRenderer } from './renderers/terminal';
import { DiffRenderer } from './renderers/diff';
import { ReadRenderer } from './renderers/read';
import { SearchRenderer } from './renderers/search';
import { WebRenderer } from './renderers/web';

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

/** kind → renderer 注册表（行为链 P3：五类专用 renderer 上线）。 */
const rendererRegistry: Partial<Record<ToolPresentationKind, PresentationRenderer>> = {
  generic: GenericRenderer,
  terminal: TerminalRenderer,
  diff: DiffRenderer,
  read: ReadRenderer,
  search: SearchRenderer,
  web: WebRenderer,
  // 注册位（delegation 主路径由 DelegationRow 承载；job 后续按需注册）：
  // delegation / job
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

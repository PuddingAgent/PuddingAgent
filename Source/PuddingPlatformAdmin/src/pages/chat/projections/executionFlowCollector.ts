// ── CU-11 Phase 1: canonical 事件收集器（SSE 信封 → ExecutionEventDto）────
// 单一数据源切换的地基：把 SSE 实时 / 历史归档得来的原始 AdminChatStreamEvent
// 信封过滤并规整为 ExecutionEventDto 形状（仅保留 ExecutionFlowEvent 已知的
// canonical type），供 projectExecutionFlow 消费。纯函数、无副作用。
//
// 设计（temp/task-cu11-data-source-switch-design.md §3.1 / §3.3）：
//   信封来源：SSE 实时经 sseClient → sessionEventStream → useChatState 旁路；
//             历史归档经 listSessionMessages canonical 信封（复用一次）。
//   本收集器不关心来源，只做「信封 → DTO」的确定性规整。
//
// 纯度约束：无 DOM / Store / 时间源 / 日志副作用。协议错误按 canonicalEvents.ts
// 约定聚合到返回值 protocolErrors，由调用方决定上报策略（不 console 输出）。

import type { AdminChatStreamEvent } from '@/services/platform/api';
import type { ExecutionEventDto } from '@/services/platform/api';
import type { ExecutionFlowEvent } from './executionFlowProjector';

/** ExecutionFlowProjector 已知的 canonical type 白名单。 */
const EXECUTION_FLOW_TYPES = new Set<string>([
  'message.thinking_summary.appended',
  'message.content.appended',
  'message.completed',
  'message.failed',
  'tool.call.requested',
  'tool.call.completed',
  'tool.call.failed',
  'subagent.spawned',
  'subagent.completed',
  'subconscious_step',
  'turn.completed',
  'turn.failed',
  'turn.cancelled',
]);

/** 单次收集结果：规整后的 DTO 数组 + 协议错误计数。 */
export interface ExecutionFlowCollectResult {
  events: ExecutionEventDto[];
  /** 收到但未通过 type 白名单，被过滤的事件数。 */
  filteredCount: number;
  /** 白名单内但缺失必需字段（eventId/sequence/occurredAt）的协议错误数。 */
  protocolErrors: number;
}

/**
 * 从原始 AdminChatStreamEvent 信封收集 ExecutionEventDto。
 * 仅保留 ExecutionFlowProjector 已知的 canonical type；缺 eventId / sequence /
 * occurredAt 视为协议错误（不构造 fallback 事实，与 canonicalEvents.ts 一致）。
 */
export function collectExecutionEvents(
  envelope: ReadonlyArray<AdminChatStreamEvent>,
): ExecutionFlowCollectResult {
  const events: ExecutionEventDto[] = [];
  let filteredCount = 0;
  let protocolErrors = 0;

  for (const ev of envelope) {
    const type = ev.type as string;
    if (!EXECUTION_FLOW_TYPES.has(type)) {
      filteredCount += 1;
      continue;
    }
    const eventId =
      typeof ev.eventId === 'string' && ev.eventId.trim() ? ev.eventId : null;
    const occurredAt =
      typeof ev.occurredAt === 'string' && ev.occurredAt.trim()
        ? ev.occurredAt
        : null;
    const sequence = typeof ev.sequence === 'number' ? ev.sequence : null;
    const turnId = typeof ev.turnId === 'string' ? ev.turnId : '';
    const runId = typeof ev.runId === 'string' ? ev.runId : '';
    const step = typeof ev.step === 'number' ? ev.step : undefined;

    if (!eventId || !occurredAt || sequence === null) {
      protocolErrors += 1;
      continue;
    }

    events.push({
      eventId,
      sequence,
      occurredAt,
      runId,
      turnId,
      ...(step !== undefined ? { step } : {}),
      type,
    });
  }

  return { events, filteredCount, protocolErrors };
}

/** 规整后的 DTO 若其 type / 字段满足 ExecutionFlowEvent 派生条件则窄化返回。 */
export function coerceToExecutionFlowEvent(
  event: ExecutionEventDto,
): ExecutionFlowEvent | null {
  if (!EXECUTION_FLOW_TYPES.has(event.type)) return null;
  return event as ExecutionFlowEvent;
}

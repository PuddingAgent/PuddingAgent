// ── TR-01 / CU-02: canonical 事件事实读取器与协议错误守卫 ─────────────
// 消息 UI 方案 §7：eventId/sequence/occurredAt/toolCallId 等事实一律来自
// 服务端 canonical 信封；React hook/component 不生成事件 ID、时间、顺序或
// 业务终态。缺失必需字段 → 记录协议错误（不构造 fallback 事实）。
import { recordPerfEvent } from '@/utils/perfEventRuntime';

/** Turn 级终态事件（canonical 名）。 */
export const CANONICAL_TURN_TERMINAL_EVENTS = new Set<string>([
  'turn.completed',
  'turn.failed',
  'turn.cancelled',
]);

/** 助手内容流事件（canonical 名）。 */
export const CANONICAL_ASSISTANT_STREAM_EVENTS = new Set<string>([
  'message.content.appended',
  'message.thinking_summary.appended',
]);

/** 工具事件（canonical 名）。 */
export const CANONICAL_TOOL_EVENTS = new Set<string>([
  'tool.call.requested',
  'tool.call.completed',
  'tool.call.failed',
]);

/** 读取 canonical eventId；缺失返回 null。 */
export function getCanonicalEventId(event: {
  eventId?: unknown;
}): string | null {
  return typeof event.eventId === 'string' && event.eventId.trim()
    ? event.eventId
    : null;
}

/** 解析 canonical occurredAt（ISO 8601）为 epoch ms；缺失/非法返回 null。 */
export function getCanonicalOccurredAtMs(event: {
  occurredAt?: unknown;
}): number | null {
  if (typeof event.occurredAt !== 'string' || !event.occurredAt.trim()) {
    return null;
  }
  const parsed = Date.parse(event.occurredAt);
  return Number.isFinite(parsed) ? parsed : null;
}

/** 记录一次 canonical 协议错误（服务端缺必需字段）。 */
export function recordEventProtocolError(
  reason: string,
  context: Record<string, unknown>,
): void {
  recordPerfEvent(
    'chat.event.protocolError',
    { reason, ...context },
    { throttleMs: 1_000 },
  );
  console.warn('[Pudding Chat] canonical event protocol error', {
    reason,
    ...context,
  });
}

/**
 * 由服务端事实派生 timeline 条目身份。
 * 优先使用 canonical eventId；occurredAt 提供时间戳。
 * 缺失时记录协议错误，并退化为「服务端事实派生的确定性键」（非本地随机 ID）。
 */
export function buildTimelineItemIdentity(
  event: {
    eventId?: unknown;
    occurredAt?: unknown;
    type?: unknown;
    sequenceNum?: unknown;
  },
  fallbackPrefix: string,
): { id: string; timestamp: number } {
  const eventId = getCanonicalEventId(event);
  const occurredAtMs = getCanonicalOccurredAtMs(event);
  if (!eventId || occurredAtMs === null) {
    recordEventProtocolError('missing-event-identity', {
      eventType: String(event.type ?? ''),
      sequenceNum: event.sequenceNum,
      hasEventId: Boolean(eventId),
      hasOccurredAt: occurredAtMs !== null,
      fallbackPrefix,
    });
  }
  const id =
    eventId ??
    `${fallbackPrefix}:${String(event.type ?? 'unknown')}:${String(
      event.sequenceNum ?? 'na',
    )}`;
  return { id, timestamp: occurredAtMs ?? 0 };
}

/**
 * 工具事件 toolCallId 守卫：缺失时记录协议错误并返回 null，
 * 调用方必须阻止该事件进入工具行渲染（消息 UI 方案 §5.4/§10）。
 */
export function requireToolCallId(event: {
  type?: unknown;
  toolCallId?: unknown;
  eventId?: unknown;
  sequenceNum?: unknown;
}): string | null {
  if (typeof event.toolCallId === 'string' && event.toolCallId.trim()) {
    return event.toolCallId;
  }
  recordEventProtocolError('missing-tool-call-id', {
    eventType: String(event.type ?? ''),
    eventId: getCanonicalEventId(event),
    sequenceNum: event.sequenceNum,
  });
  return null;
}

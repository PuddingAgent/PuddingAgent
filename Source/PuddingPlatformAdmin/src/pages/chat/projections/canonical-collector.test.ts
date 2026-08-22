// ── CU-11 Phase 1: canonical 事件收集器单元测试 ────────────────────────────
// 验收（temp/task-cu11-data-source-switch-design.md §3.3 Phase 1 测试策略）：
//  1. SSE 原始信封 / 历史归档 JSON 两组 fixture → 正确解析 ExecutionEventDto
//     所需字段（eventId/occurredAt/sequence/type）；
//  2. 协议外 type 被过滤（filteredCount）；
//  3. 缺失必需字段（eventId/occurredAt/sequence）→ protocolErrors，不构造
//     fallback 事实（与 canonicalEvents.ts 语义一致，不静默吞）。
import {
  collectExecutionEvents,
  coerceToExecutionFlowEvent,
} from './executionFlowCollector';
import type { AdminChatStreamEvent } from '@/services/platform/api';

const OCCURRED_AT = '2026-08-21T08:00:00.000Z';

/** 构造 AdminChatStreamEvent 信封（基础身份字段 + type 载荷）。 */
function env(
  type: string,
  seq: number,
  over: Record<string, unknown> = {},
): AdminChatStreamEvent {
  return {
    eventId: `e${seq}`,
    sequence: seq,
    occurredAt: OCCURRED_AT,
    runId: 'run-1',
    turnId: 'turn-1',
    type,
    ...over,
  } as AdminChatStreamEvent;
}

describe('executionFlowCollector', () => {
  describe('已知 canonical type 白名单 → ExecutionEventDto', () => {
    it('解析 SSE 原始信封为 DTO，保留核心字段', () => {
      const envelope = [
        env('message.thinking_summary.appended', 1, { delta: '先分析' }),
        env('tool.call.requested', 2, {
          toolCallId: 't1',
          name: 'search',
          arguments: '{"q":"x"}',
        }),
        env('turn.completed', 3, { reply: 'done' }),
      ];
      const res = collectExecutionEvents(envelope);
      expect(res.events).toHaveLength(3);
      expect(res.filteredCount).toBe(0);
      expect(res.protocolErrors).toBe(0);
      expect(res.events[0]).toMatchObject({
        eventId: 'e1',
        sequence: 1,
        occurredAt: OCCURRED_AT,
        runId: 'run-1',
        turnId: 'turn-1',
        type: 'message.thinking_summary.appended',
      });
      expect(res.events[2].type).toBe('turn.completed');
    });

    it('历史归档 JSON 形状解析（同一 canonical 信封复用）', () => {
      const historic = [
        env('subagent.spawned', 1, { sub_agent_id: 's1', model: 'deepseek' }),
        env('subagent.completed', 2, { sub_agent_id: 's1', success: true }),
      ];
      const res = collectExecutionEvents(historic);
      expect(res.events).toHaveLength(2);
      expect(res.events[0].type).toBe('subagent.spawned');
      expect(res.events[1].type).toBe('subagent.completed');
    });

    it('含 step 字段时透传', () => {
      const res = collectExecutionEvents([
        env('message.content.appended', 1, { delta: 'x', step: 5 }),
      ]);
      expect(res.events[0]).toMatchObject({ step: 5, type: 'message.content.appended' });
    });
  });

  describe('协议外 / 缺失必需字段', () => {
    it('白名单外 type 被过滤并计数', () => {
      const res = collectExecutionEvents([
        env('metadata', 1, { messageId: 'm1' }),
        env('voice_capture_status', 2, { status: 'ok' }),
        env('turn.started', 3),
      ]);
      expect(res.events).toHaveLength(0);
      expect(res.filteredCount).toBe(3);
    });

    it('缺失 eventId 计为协议错误，不构造 fallback 事实', () => {
      const res = collectExecutionEvents([
        { sequence: 1, occurredAt: OCCURRED_AT, runId: 'r', turnId: 't', type: 'turn.completed' } as AdminChatStreamEvent,
      ]);
      expect(res.events).toHaveLength(0);
      expect(res.protocolErrors).toBe(1);
    });

    it('缺失 sequence 或 occurredAt 计为协议错误', () => {
      const missSeq = collectExecutionEvents([
        { eventId: 'e1', occurredAt: OCCURRED_AT, runId: 'r', turnId: 't', type: 'turn.completed' } as AdminChatStreamEvent,
      ]);
      const missOcc = collectExecutionEvents([
        { eventId: 'e1', sequence: 1, runId: 'r', turnId: 't', type: 'turn.completed' } as AdminChatStreamEvent,
      ]);
      expect(missSeq.protocolErrors).toBe(1);
      expect(missOcc.protocolErrors).toBe(1);
    });
  });

  describe('coerceToExecutionFlowEvent', () => {
    it('已知 type 窄化返回，未知返回 null', () => {
      const known = collectExecutionEvents([env('tool.call.completed', 1, { toolCallId: 't1' })]);
      expect(coerceToExecutionFlowEvent(known.events[0])).not.toBeNull();
      expect(coerceToExecutionFlowEvent(known.events[0])?.type).toBe('tool.call.completed');
      const unknown = { eventId: 'e1', sequence: 1, occurredAt: OCCURRED_AT, runId: 'r', turnId: 't', type: 'metadata' };
      expect(coerceToExecutionFlowEvent(unknown)).toBeNull();
    });
  });
});

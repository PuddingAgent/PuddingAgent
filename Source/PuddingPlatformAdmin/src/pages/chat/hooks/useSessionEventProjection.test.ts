import { act, renderHook } from '@testing-library/react';
import type { MutableRefObject } from 'react';
import type { AdminChatStreamEvent } from '@/services/platform/api';
import type { ChatTurn, TimelineItem } from '../types';
import { useSessionEventProjection } from './useSessionEventProjection';

// ── CU-03：bootstrap / gap recovery / live SSE 统一输入 ──────────────
// 验收点：重复 eventId 幂等、终态单调（不被迟到 progress 降级）、
// tool result 先于 started 时建占位并补全、缺 eventId 不去重、
// 不同到达顺序（started-first / result-first）收敛到同一时间线。

const makeRunningTurn = (turnId = 'turn-1'): ChatTurn => ({
  turnId,
  source: {
    sourceId: 'agent-1',
    sourceType: 'agent',
    displayName: '助手',
    avatarEmoji: '🤖',
    avatarColor: '#888',
    avatarUrl: undefined,
  },
  userMessage: { id: 'user-1', text: 'hi', timestamp: 1000, status: 'success' },
  assistant: {
    id: 'assistant-1',
    status: 'thinking',
    timelineItems: [],
    answerMarkdown: '',
    isStreaming: true,
    renderMode: 'structured',
  },
});

const event = (overrides: Record<string, unknown>): AdminChatStreamEvent =>
  overrides as unknown as AdminChatStreamEvent;

const makeTurnEvent = (
  type: string,
  overrides: Record<string, unknown> = {},
): AdminChatStreamEvent =>
  event({
    type,
    turnId: 'turn-1',
    sequenceNum: 1,
    occurredAt: '2026-08-21T00:00:00Z',
    ...overrides,
  });

interface Harness {
  result: { current: ReturnType<typeof useSessionEventProjection> };
  turnsRef: MutableRefObject<ChatTurn[]>;
  completedTurnsRef: MutableRefObject<Set<string>>;
  sseSessionIdRef: MutableRefObject<string | null>;
}

function setup(initialTurns: ChatTurn[] = [makeRunningTurn()]): Harness {
  const sseSessionIdRef = {
    current: 'session-1',
  } as MutableRefObject<string | null>;
  const selectedSessionIdRef = {
    current: 'session-1',
  } as MutableRefObject<string | null>;
  const sessionIdRef = {
    current: 'session-1',
  } as MutableRefObject<string | undefined>;
  const turnsRef = { current: initialTurns } as MutableRefObject<ChatTurn[]>;
  const completedTurnsRef = {
    current: new Set<string>(),
  } as MutableRefObject<Set<string>>;
  const latestTurnIdRef = {
    current: initialTurns[0]?.turnId ?? null,
  } as MutableRefObject<string | null>;
  const messageIdToTurnIdRef = {
    current: new Map<string, string>(),
  } as MutableRefObject<Map<string, string>>;
  const lastSequenceNumRef = {
    current: 0,
  } as MutableRefObject<number>;
  const activeMessageIdsRef = {
    current: new Set<string>(),
  } as MutableRefObject<Set<string>>;
  const pendingDeltaRef = {
    current: new Map<string, string>(),
  } as MutableRefObject<Map<string, string>>;
  const pendingThinkingRef = {
    current: new Map<string, string>(),
  } as MutableRefObject<Map<string, string>>;
  const setTurns = jest.fn((updater: unknown) => {
    turnsRef.current =
      typeof updater === 'function'
        ? (updater as (prev: ChatTurn[]) => ChatTurn[])(turnsRef.current)
        : (updater as ChatTurn[]);
  });

  const { result } = renderHook(() =>
    useSessionEventProjection({
      identity: {
        agentId: 'agent-1',
        selectedAgent: undefined,
        mainSessionId: 'session-1',
        selectedSessionId: 'session-1',
        sseSessionIdRef,
        selectedSessionIdRef,
        sessionIdRef,
      },
      turns: {
        turnsRef,
        setTurns,
        completedTurnsRef,
        latestTurnIdRef,
        messageIdToTurnIdRef,
        lastSequenceNumRef,
        activeMessageIdsRef,
      },
      buffers: {
        pendingDeltaRef,
        pendingThinkingRef,
        enqueueDelta: jest.fn(),
        enqueueThinking: jest.fn(),
        flushPendingDeltas: jest.fn(),
        flushPendingThinking: jest.fn(),
        resetSessionEventBuffers: jest.fn(),
      },
      integrations: {
        setLoading: jest.fn(),
        appendRuntimeEvent: jest.fn(),
        markSteeringInjected: jest.fn(),
        handleCompactionLifecycleEvent: jest.fn(),
      },
    }),
  );
  return { result, turnsRef, completedTurnsRef, sseSessionIdRef };
}

/** 只读快照：避免 Jest 打印 timeline 大对象时超长。 */
const callFacts = (items: TimelineItem[]) =>
  items
    .filter((item) => item.type === 'tool_call' || item.type === 'tool_result')
    .map(({ id, eventId, type, toolCallId, name, arguments: args, status }) => ({
      id,
      eventId,
      type,
      toolCallId,
      name,
      args,
      status,
    }));

describe('useSessionEventProjection — CU-03 unified input', () => {
  it('consumes a duplicate eventId only once (idempotent over gap/live overlap)', () => {
    const { result, turnsRef } = setup();
    // 模拟 replay 直通（不走帧缓冲），便于断言 delta 累积。
    result.current.hydrateSessionReplayRef.current = true;

    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('message.content.appended', {
          eventId: 'evt-1',
          sequenceNum: 1,
          delta: 'hello',
        }),
      );
    });
    expect(turnsRef.current[0].assistant.answerMarkdown).toBe('hello');

    // 同一 eventId 再次到达（replay 兜底 + SSE 重放重叠）：必须跳过。
    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('message.content.appended', {
          eventId: 'evt-1',
          sequenceNum: 1,
          delta: 'hello',
        }),
      );
    });
    expect(turnsRef.current[0].assistant.answerMarkdown).toBe('hello');
  });

  it('does not dedupe events without a canonical eventId', () => {
    const { result, turnsRef } = setup();
    result.current.hydrateSessionReplayRef.current = true;

    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('message.content.appended', { sequenceNum: 1, delta: 'a' }),
      );
      result.current.applySessionEvent(
        makeTurnEvent('message.content.appended', { sequenceNum: 2, delta: 'b' }),
      );
    });
    expect(turnsRef.current[0].assistant.answerMarkdown).toBe('ab');
  });

  it('keeps a completed turn terminal against late progress', () => {
    const { result, turnsRef, completedTurnsRef } = setup();
    result.current.hydrateSessionReplayRef.current = true;

    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('message.content.appended', {
          eventId: 'evt-a',
          sequenceNum: 1,
          delta: 'part1',
        }),
      );
      result.current.applySessionEvent(
        makeTurnEvent('turn.completed', {
          eventId: 'evt-done',
          sequenceNum: 2,
          reply: 'final',
        }),
      );
    });
    expect(turnsRef.current[0].assistant.status).toBe('success');
    expect(completedTurnsRef.current.has('turn-1')).toBe(true);

    // 迟到的 progress（新 eventId）不得追加正文，也不得降级终态。
    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('message.content.appended', {
          eventId: 'evt-late',
          sequenceNum: 3,
          delta: 'LATE',
        }),
      );
    });
    expect(turnsRef.current[0].assistant.answerMarkdown).not.toContain('LATE');
    expect(turnsRef.current[0].assistant.status).toBe('success');
  });

  it('keeps a failed turn terminal against late tool progress', () => {
    const { result, turnsRef, completedTurnsRef } = setup();

    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('turn.failed', { eventId: 'evt-fail', message: 'boom' }),
      );
    });
    expect(turnsRef.current[0].assistant.status).toBe('error');
    expect(completedTurnsRef.current.has('turn-1')).toBe(true);

    // 迟到的 tool.call.requested（新 eventId）不得追加工具行、不得降级。
    const itemsBefore = turnsRef.current[0].assistant.timelineItems.length;
    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('tool.call.requested', {
          eventId: 'evt-late-call',
          sequenceNum: 2,
          toolCallId: 'call-1',
          name: 'shell',
          arguments: '{}',
        }),
      );
    });
    expect(turnsRef.current[0].assistant.timelineItems.length).toBe(itemsBefore);
    expect(turnsRef.current[0].assistant.status).toBe('error');
  });

  it('keeps a cancelled turn terminal against late step progress', () => {
    const { result, turnsRef, completedTurnsRef } = setup();

    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('turn.cancelled', {
          eventId: 'evt-cancel',
          message: 'stopped',
        }),
      );
    });
    expect(turnsRef.current[0].assistant.status).toBe('cancelled');
    expect(completedTurnsRef.current.has('turn-1')).toBe(true);

    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('step', { eventId: 'evt-late-step', status: 'executing' }),
      );
    });
    expect(turnsRef.current[0].assistant.status).toBe('cancelled');
  });

  it('creates a placeholder call when tool result precedes started, then completes it', () => {
    const { result, turnsRef } = setup();

    // gap replay：result 先到（started 事件尚未到达）。
    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('tool.call.completed', {
          eventId: 'evt-r',
          sequenceNum: 1,
          toolCallId: 'call-1',
          name: 'shell',
          output: 'ok',
          exitCode: 0,
        }),
      );
    });
    let items = turnsRef.current[0].assistant.timelineItems;
    expect(items.length).toBe(2);
    expect(items[0]).toMatchObject({
      id: 'tool-call:call-1',
      type: 'tool_call',
      toolCallId: 'call-1',
      placeholder: true,
      status: 'tool_call',
    });
    expect(items[1]).toMatchObject({
      type: 'tool_result',
      toolCallId: 'call-1',
      output: 'ok',
      exitCode: 0,
    });

    // started 到达：原位补全占位（不新增条目，id 收敛为 canonical eventId）。
    act(() => {
      result.current.applySessionEvent(
        makeTurnEvent('tool.call.requested', {
          eventId: 'evt-s',
          sequenceNum: 2,
          toolCallId: 'call-1',
          name: 'shell',
          arguments: '{"command":"pwd"}',
        }),
      );
    });
    items = turnsRef.current[0].assistant.timelineItems;
    expect(items.length).toBe(2);
    expect(items[0]).toMatchObject({
      id: 'evt-s',
      eventId: 'evt-s',
      type: 'tool_call',
      toolCallId: 'call-1',
      placeholder: false,
      name: 'shell',
      arguments: '{"command":"pwd"}',
    });
    expect(items[0]).not.toHaveProperty('placeholder', true);
  });

  it('converges to the same tool timeline regardless of arrival order', () => {
    // started-first（live 正常顺序）
    const live = setup();
    act(() => {
      live.result.current.applySessionEvent(
        makeTurnEvent('tool.call.requested', {
          eventId: 'evt-s',
          sequenceNum: 1,
          toolCallId: 'call-1',
          name: 'shell',
          arguments: '{"command":"pwd"}',
        }),
      );
      live.result.current.applySessionEvent(
        makeTurnEvent('tool.call.completed', {
          eventId: 'evt-r',
          sequenceNum: 2,
          toolCallId: 'call-1',
          name: 'shell',
          output: 'ok',
          exitCode: 0,
        }),
      );
    });

    // result-first（gap replay 兜底后 started 补达）
    const gap = setup();
    act(() => {
      gap.result.current.applySessionEvent(
        makeTurnEvent('tool.call.completed', {
          eventId: 'evt-r',
          sequenceNum: 1,
          toolCallId: 'call-1',
          name: 'shell',
          output: 'ok',
          exitCode: 0,
        }),
      );
      gap.result.current.applySessionEvent(
        makeTurnEvent('tool.call.requested', {
          eventId: 'evt-s',
          sequenceNum: 2,
          toolCallId: 'call-1',
          name: 'shell',
          arguments: '{"command":"pwd"}',
        }),
      );
    });

    const liveFacts = callFacts(live.turnsRef.current[0].assistant.timelineItems);
    const gapFacts = callFacts(gap.turnsRef.current[0].assistant.timelineItems);
    expect(gapFacts).toEqual(liveFacts);
    expect(liveFacts.length).toBe(2);
  });
});

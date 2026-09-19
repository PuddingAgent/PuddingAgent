import type { ChatTurn } from '../types';
import {
  confirmOptimisticTurn,
  getChatRouteSelectionFromSearch,
  resolveRunningCompactionId,
  resolveTerminalAssistantMarkdown,
} from './chatStateUtils';

const createTurn = (): ChatTurn => ({
  turnId: 'optimistic-turn',
  userMessage: {
    id: 'optimistic-message',
    text: 'hello',
    timestamp: 1,
    status: 'sending',
  },
  assistant: {
    id: 'assistant-message',
    status: 'thinking',
    timelineItems: [],
    answerMarkdown: '',
    isStreaming: true,
    renderMode: 'structured',
  },
});

describe('chatStateUtils module boundary', () => {
  it('confirms an optimistic turn without mutating the original turn', () => {
    const original = createTurn();

    const [confirmed] = confirmOptimisticTurn(
      [original],
      'optimistic-turn',
      'confirmed-turn',
      'confirmed-message',
    );

    expect(confirmed.turnId).toBe('confirmed-turn');
    expect(confirmed.userMessage).toMatchObject({
      id: 'confirmed-message',
      status: 'success',
    });
    expect(original.turnId).toBe('optimistic-turn');
    expect(original.userMessage.status).toBe('sending');
  });

  it('normalizes route selection and ignores blank query values', () => {
    expect(
      getChatRouteSelectionFromSearch(
        '?workspaceId=default&agentId=%20&sessionId=session-1',
      ),
    ).toEqual({ workspaceId: 'default', sessionId: 'session-1' });
  });

  it('merges a terminal reply with the streamed prefix exactly once', () => {
    expect(resolveTerminalAssistantMarkdown('hello', 'hello world')).toBe(
      'hello world',
    );
    expect(resolveTerminalAssistantMarkdown('hello world', 'hello world')).toBe(
      'hello world',
    );
  });

  it('diverged stream text falls back to the server reply (never duplicates)', () => {
    // 流内任何一次偏差（重叠修剪误删/快照替换）导致 current 与 reply 分叉时，
    // 以服务端 reply 为准；旧实现把 reply 整段拼在 current 之后会让正文显示两遍。
    expect(resolveTerminalAssistantMarkdown('hello wor', 'HELLO WORLD')).toBe(
      'HELLO WORLD',
    );
    expect(
      resolveTerminalAssistantMarkdown('前文流式', '服务端终稿全文'),
    ).toBe('服务端终稿全文');
  });
});

describe('resolveRunningCompactionId', () => {
  it('returns the id when the last lifecycle event is a started with an object payload', () => {
    expect(
      resolveRunningCompactionId([
        {
          type: 'context.compaction.started',
          payload: { compactionId: 'a-1' },
        },
        {
          type: 'context.compaction.completed',
          payload: { compactionId: 'a-1' },
        },
        {
          type: 'context.compaction.started',
          payload: { compactionId: 'b-2' },
        },
      ]),
    ).toBe('b-2');
  });

  it('parses a JSON-string payload and ignores non-lifecycle events', () => {
    expect(
      resolveRunningCompactionId([
        { type: 'turn.completed', payload: '{}' },
        {
          type: 'context.compaction.started',
          payload: JSON.stringify({ compactionId: 'c-3' }),
        },
      ]),
    ).toBe('c-3');
  });

  it('returns null when the last lifecycle event is terminal', () => {
    expect(
      resolveRunningCompactionId([
        {
          type: 'context.compaction.started',
          payload: { compactionId: 'a-1' },
        },
        {
          type: 'context.compaction.failed',
          payload: { compactionId: 'a-1' },
        },
      ]),
    ).toBeNull();
  });

  it.each([
    ['malformed json payload', [{ type: 'context.compaction.started', payload: '{oops' }]],
    ['missing compactionId', [{ type: 'context.compaction.started', payload: {} }]],
    ['blank compactionId', [{ type: 'context.compaction.started', payload: { compactionId: '  ' } }]],
    ['empty event list', []],
    ['null input', null],
  ])('returns null for %s', (_name, events) => {
    expect(resolveRunningCompactionId(events as never)).toBeNull();
  });

  it('reads a flattened payload after normalization', () => {
    expect(
      resolveRunningCompactionId([
        { type: 'context.compaction.started', compactionId: 'd-4' },
      ]),
    ).toBe('d-4');
  });
});

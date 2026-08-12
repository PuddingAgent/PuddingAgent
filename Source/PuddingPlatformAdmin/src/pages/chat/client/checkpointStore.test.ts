// ── checkpointStore 纯模块测试 (P2#7) ─────────────────────────
import {
  buildCheckpointLabel,
  captureCheckpoint,
  checkpointSnapshotToTurns,
  clearCheckpoints,
  createCheckpointId,
  loadCheckpointStore,
  loadSessionCheckpoints,
  MAX_CHECKPOINTS_PER_SESSION,
  persistCheckpointStore,
  removeCheckpoint,
  turnsToCheckpointSnapshot,
  type ChatCheckpoint,
} from './checkpointStore';
import type { ChatTurn } from '../types';

const makeTurn = (index: number, answer = `answer-${index}`): ChatTurn => ({
  turnId: `turn-${index}`,
  userMessage: {
    id: `user-${index}`,
    text: `消息 ${index}`,
    timestamp: index * 1000,
    status: 'success',
  },
  assistant: {
    id: `assistant-${index}`,
    status: 'success',
    timelineItems: [
      {
        id: `item-${index}`,
        type: 'tool_call',
        name: 'shell',
        timestamp: index * 1000 + 1,
        collapsed: true,
      },
    ],
    answerMarkdown: answer,
    isStreaming: false,
    renderMode: 'structured',
  },
});

describe('checkpointStore: captureCheckpoint', () => {
  it('creates a checkpoint from current turns with serialized snapshot', () => {
    const turns = [makeTurn(0), makeTurn(1)];
    const next = captureCheckpoint({
      sessionId: 'session-1',
      workspaceId: 'ws-1',
      agentId: 'agent-1',
      createdAt: 5000,
      label: '消息 2',
      turns,
    });
    expect(next).toHaveLength(1);
    const checkpoint = next[0] as ChatCheckpoint;
    expect(checkpoint.sessionId).toBe('session-1');
    expect(checkpoint.turnIndex).toBe(2);
    expect(checkpoint.label).toBe('消息 2');
    expect(checkpoint.turns).toHaveLength(2);
    // timelineItems 大对象不进入快照
    expect(checkpoint.turns[1]?.assistant.answerMarkdown).toBe('answer-1');
    expect(
      (checkpoint.turns[1]?.assistant as { timelineItems?: unknown })
        .timelineItems,
    ).toBeUndefined();
  });

  it('deduplicates an idempotent retry (same turnIndex + same label)', () => {
    const turns = [makeTurn(0)];
    const first = captureCheckpoint({
      sessionId: 'session-1',
      createdAt: 1000,
      label: '消息 1',
      turns,
    });
    const second = captureCheckpoint(
      {
        sessionId: 'session-1',
        createdAt: 2000,
        label: '消息 1',
        turns,
      },
      first,
    );
    expect(second).toBe(first);
    expect(second).toHaveLength(1);
  });

  it('keeps the latest first and caps at MAX_CHECKPOINTS_PER_SESSION', () => {
    let list: ChatCheckpoint[] = [];
    for (let i = 0; i < MAX_CHECKPOINTS_PER_SESSION + 5; i += 1) {
      const turns = Array.from({ length: i }, (_, k) => makeTurn(k));
      list = captureCheckpoint(
        {
          sessionId: 'session-1',
          createdAt: i,
          label: `消息 ${i}`,
          turns,
        },
        list,
      );
    }
    expect(list).toHaveLength(MAX_CHECKPOINTS_PER_SESSION);
    // 最新在前（createdAt 最大在最前）
    expect(list[0]?.label).toBe(`消息 ${MAX_CHECKPOINTS_PER_SESSION + 4}`);
  });

  it('truncates long labels to 60 chars', () => {
    const long = 'x'.repeat(120);
    expect(buildCheckpointLabel(long)).toBe(`${'x'.repeat(60)}…`);
    expect(buildCheckpointLabel('  短 文本  ')).toBe('短 文本');
  });
});

describe('checkpointStore: removeCheckpoint / clearCheckpoints', () => {
  const seed = (): ChatCheckpoint[] =>
    captureCheckpoint({
      sessionId: 'session-1',
      createdAt: 1,
      label: 'a',
      turns: [],
    });

  it('removes by id', () => {
    const list = [
      {
        ...(seed()[0] as ChatCheckpoint),
        checkpointId: 'cp-1',
      },
      {
        ...(seed()[0] as ChatCheckpoint),
        checkpointId: 'cp-2',
        label: 'b',
      },
    ];
    const next = removeCheckpoint('cp-1', list);
    expect(next).toHaveLength(1);
    expect(next[0]?.checkpointId).toBe('cp-2');
  });

  it('clears all', () => {
    const list = [
      { ...(seed()[0] as ChatCheckpoint), checkpointId: 'cp-1' },
    ];
    expect(clearCheckpoints(list)).toEqual([]);
  });

  it('createCheckpointId is unique', () => {
    const a = createCheckpointId();
    const b = createCheckpointId();
    expect(a).not.toBe(b);
  });
});

describe('checkpointStore: snapshot round-trip', () => {
  it('turnsToCheckpointSnapshot → checkpointSnapshotToTurns preserves content', () => {
    const turns = [makeTurn(0, '第一个回答'), makeTurn(1, '第二个回答')];
    const snapshot = turnsToCheckpointSnapshot(turns);
    expect(snapshot).toHaveLength(2);

    const restored = checkpointSnapshotToTurns(snapshot);
    expect(restored).toHaveLength(2);
    expect(restored[0]?.turnId).toBe('turn-0');
    expect(restored[0]?.userMessage.text).toBe('消息 0');
    expect(restored[0]?.assistant.answerMarkdown).toBe('第一个回答');
    expect(restored[0]?.assistant.status).toBe('success');
    expect(restored[0]?.assistant.isStreaming).toBe(false);
    // timelineItems 不重建（空数组）
    expect(restored[0]?.assistant.timelineItems).toEqual([]);
  });

  it('preserves user metadata and dbMessageId when present', () => {
    const turn: ChatTurn = {
      ...makeTurn(0),
      userMessage: {
        ...makeTurn(0).userMessage,
        metadata: { inputMode: 'voice' },
        dbMessageId: 42,
      },
    };
    const snapshot = turnsToCheckpointSnapshot([turn]);
    const restored = checkpointSnapshotToTurns(snapshot);
    expect(restored[0]?.userMessage.metadata).toEqual({ inputMode: 'voice' });
    expect(restored[0]?.userMessage.dbMessageId).toBe(42);
  });
});

describe('checkpointStore: localStorage persistence', () => {
  const storageKey = 'pudding-chat-checkpoints-v1';

  beforeEach(() => {
    window.localStorage.clear();
  });

  it('persists and loads the full map per session', () => {
    const list = captureCheckpoint({
      sessionId: 'session-1',
      createdAt: 1,
      label: '快照A',
      turns: [makeTurn(0)],
    });
    persistCheckpointStore({ 'session-1': list, 'session-2': [] });

    const map = loadCheckpointStore();
    expect(map['session-1']).toHaveLength(1);
    expect(map['session-1']?.[0]?.label).toBe('快照A');
    expect(loadSessionCheckpoints('session-2')).toEqual([]);
    expect(loadSessionCheckpoints('missing')).toEqual([]);
  });

  it('tolerates corrupted JSON', () => {
    window.localStorage.setItem(storageKey, '{not-json');
    expect(loadCheckpointStore()).toEqual({});
  });

  it('is safe in non-window environments', () => {
    // 模拟 SSR：删除 window.localStorage 引用后仍不抛错
    const original = window.localStorage;
    (window as unknown as { localStorage?: unknown }).localStorage = undefined;
    try {
      expect(loadCheckpointStore()).toEqual({});
      expect(() => persistCheckpointStore({})).not.toThrow();
    } finally {
      (window as unknown as { localStorage?: unknown }).localStorage = original;
    }
  });
});

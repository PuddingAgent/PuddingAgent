// ── useCheckpointTimeline hook 测试 (P2#7) ────────────────────
import { act, renderHook } from '@testing-library/react';
import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useRef, useState } from 'react';
import { useCheckpointTimeline } from './useCheckpointTimeline';
import type { ChatCheckpoint } from '../client/checkpointStore';
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
    timelineItems: [],
    answerMarkdown: answer,
    isStreaming: false,
    renderMode: 'structured',
  },
});

/** 用真实 localStorage（jsdom 自带）替代 setupTests 的 jest mock。 */
function useRealLocalStorage() {
  beforeAll(() => {
    (globalThis as { localStorage?: unknown }).localStorage = (() => {
      let store: Record<string, string> = {};
      return {
        getItem: (key: string) => store[key] ?? null,
        setItem: (key: string, value: string) => {
          store[key] = String(value);
        },
        removeItem: (key: string) => {
          delete store[key];
        },
        clear: () => {
          store = {};
        },
      };
    })();
  });
  beforeEach(() => {
    window.localStorage.clear();
  });
}

describe('useCheckpointTimeline', () => {
  useRealLocalStorage();

  const setup = (initialTurns: ChatTurn[] = []) => {
    const hook: {
      current: {
        turnsRef: MutableRefObject<ChatTurn[]>;
        setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
      };
    } = { current: null as never };
    const rendered = renderHook(
      () => {
        const turnsRef = useRef<ChatTurn[]>(initialTurns);
        const [turns, setTurns] = useState<ChatTurn[]>(initialTurns);
        hook.current = { turnsRef, setTurns };
        return useCheckpointTimeline({
          sessionId: 'session-1',
          workspaceId: 'ws-1',
          agentId: 'agent-1',
          turnsRef,
          setTurns,
          onForkCheckpoint: jest.fn(async () => 'forked-session'),
        });
      },
      { initialProps: {} },
    );
    return { rendered, hook };
  };

  it('captures a snapshot before a turn and lists it newest-first', () => {
    const { rendered, hook } = setup([makeTurn(0)]);
    expect(rendered.result.current.checkpoints).toHaveLength(0);

    act(() => {
      hook.current.turnsRef.current = [...hook.current.turnsRef.current, makeTurn(1)];
      rendered.result.current.captureBeforeTurn('session-1', '消息 2');
    });

    expect(rendered.result.current.checkpoints).toHaveLength(1);
    const cp = rendered.result.current.checkpoints[0];
    expect(cp?.sessionId).toBe('session-1');
    expect(cp?.turnIndex).toBe(2);
    expect(cp?.label).toBe('消息 2');
    expect(cp?.turns).toHaveLength(2);
  });

  it('skips duplicate snapshot (same turnIndex + label)', () => {
    const { rendered, hook } = setup([makeTurn(0)]);
    act(() => {
      hook.current.turnsRef.current = [...hook.current.turnsRef.current, makeTurn(1)];
      rendered.result.current.captureBeforeTurn('session-1', '消息 2');
    });
    act(() => {
      rendered.result.current.captureBeforeTurn('session-1', '消息 2');
    });
    expect(rendered.result.current.checkpoints).toHaveLength(1);
  });

  it('restoreCheckpoint restores the view turns to the snapshot', () => {
    const { rendered, hook } = setup([makeTurn(0), makeTurn(1)]);
    act(() => {
      rendered.result.current.captureBeforeTurn('session-1', '消息 3');
    });
    const cp = rendered.result.current.checkpoints[0];
    expect(cp).toBeDefined();

    // 模拟还原：hook 的 setTurns 会更新组件 state
    act(() => {
      rendered.result.current.restoreCheckpoint(cp?.checkpointId as string);
    });
    expect(rendered.result.current.restoredCheckpointId).toBe(
      cp?.checkpointId,
    );
  });

  it('restoreCheckpoint is a no-op for unknown id', () => {
    const { rendered } = setup([makeTurn(0)]);
    act(() => {
      rendered.result.current.restoreCheckpoint('missing');
    });
    expect(rendered.result.current.restoredCheckpointId).toBeNull();
  });

  it('forkCheckpoint delegates to onForkCheckpoint with the checkpoint', async () => {
    const onFork = jest.fn(async (_checkpoint: ChatCheckpoint) => 'forked-session');
    const rendered = renderHook(() => {
      const turnsRef = useRef<ChatTurn[]>([makeTurn(0)]);
      const setTurns = (): void => undefined;
      return useCheckpointTimeline({
        sessionId: 'session-1',
        turnsRef,
        setTurns: setTurns as Dispatch<SetStateAction<ChatTurn[]>>,
        onForkCheckpoint: onFork,
      });
    });
    act(() => {
      rendered.result.current.captureBeforeTurn('session-1', '消息 2');
    });
    const cp = rendered.result.current.checkpoints[0];

    let forked: string | undefined;
    await act(async () => {
      forked = await rendered.result.current.forkCheckpoint(
        cp?.checkpointId as string,
      );
    });
    expect(forked).toBe('forked-session');
    expect(onFork).toHaveBeenCalledTimes(1);
    const forkedArg = onFork.mock.calls[0]?.[0] as ChatCheckpoint | undefined;
    expect(forkedArg?.checkpointId).toBe(cp?.checkpointId);
  });

  it('deleteCheckpoint removes a snapshot and clears the restored marker', () => {
    const { rendered, hook } = setup([makeTurn(0)]);
    act(() => {
      rendered.result.current.captureBeforeTurn('session-1', '消息 2');
    });
    const cp = rendered.result.current.checkpoints[0];

    act(() => {
      rendered.result.current.restoreCheckpoint(cp?.checkpointId as string);
    });
    expect(rendered.result.current.restoredCheckpointId).toBe(
      cp?.checkpointId,
    );

    act(() => {
      rendered.result.current.deleteCheckpoint(cp?.checkpointId as string);
    });
    expect(rendered.result.current.checkpoints).toHaveLength(0);
    expect(rendered.result.current.restoredCheckpointId).toBeNull();
  });

  it('clearAllCheckpoints empties the list', () => {
    const { rendered, hook } = setup([makeTurn(0)]);
    act(() => {
      hook.current.turnsRef.current = [...hook.current.turnsRef.current, makeTurn(1)];
      rendered.result.current.captureBeforeTurn('session-1', '消息 2');
      hook.current.turnsRef.current = [...hook.current.turnsRef.current, makeTurn(2)];
      rendered.result.current.captureBeforeTurn('session-1', '消息 3');
    });
    expect(rendered.result.current.checkpoints).toHaveLength(2);

    act(() => {
      rendered.result.current.clearAllCheckpoints();
    });
    expect(rendered.result.current.checkpoints).toHaveLength(0);
  });

  it('reloads checkpoints when the session changes', () => {
    const rendered = renderHook(
      ({ sessionId }: { sessionId: string | null }) =>
        useCheckpointTimeline({
          sessionId,
          turnsRef: { current: [] } as MutableRefObject<ChatTurn[]>,
          setTurns: (() => undefined) as Dispatch<SetStateAction<ChatTurn[]>>,
          onForkCheckpoint: jest.fn(async () => 'forked'),
        }),
      { initialProps: { sessionId: 'session-a' } },
    );
    act(() => {
      rendered.result.current.captureBeforeTurn('session-a', 'A');
    });
    expect(rendered.result.current.checkpoints).toHaveLength(1);

    act(() => {
      rendered.rerender({ sessionId: 'session-b' });
    });
    expect(rendered.result.current.checkpoints).toHaveLength(0);
  });
});

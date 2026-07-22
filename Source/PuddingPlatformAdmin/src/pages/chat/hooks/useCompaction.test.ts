import { act, renderHook } from '@testing-library/react';
import { useRef, useState } from 'react';
import { useCompaction } from './useCompaction';

jest.mock('@/services/platform/api', () => ({
  compactSession: jest.fn(),
}));

const messageApi = {
  destroy: jest.fn(),
  error: jest.fn(),
  info: jest.fn(),
  loading: jest.fn(),
  success: jest.fn(),
};

const compactionEvent = (type: string, extra: Record<string, unknown> = {}) =>
  ({
    type,
    compactionId: 'compact-1',
    ...extra,
  }) as never;

function useCompactionHarness() {
  const [turns, setTurns] = useState<Array<{ assistant: { status: string } }>>(
    [],
  );
  const turnsRef = useRef<Array<{ assistant: { status: string } }>>([]);
  const latestTurnIdRef = useRef<string | null>(null);
  const sessionIdRef = useRef<string | undefined>('session-1');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const compaction = useCompaction({
    identity: {
      workspaceId: 'default',
      agentId: 'agent-1',
      selectedSessionId: 'session-1',
      sessionIdRef,
    },
    turns: {
      turnsRef: turnsRef as never,
      latestTurnIdRef,
      setTurns: setTurns as never,
    },
    status: { loading, setLoading, setError },
    messageApi: messageApi as never,
  });
  return { ...compaction, turns, loading, error };
}

describe('useCompaction', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('projects compaction lifecycle events into one stable turn', () => {
    const onSwitchSession = jest.fn();
    const { result } = renderHook(() => useCompactionHarness());

    act(() => result.current.bindCompactedSessionSwitch(onSwitchSession));
    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.started'),
      ),
    );
    expect(result.current.turns).toHaveLength(1);
    expect(result.current.turns[0].assistant.status).toBe('executing');
    expect(result.current.loading).toBe(true);

    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.completed', {
          newSessionId: 'session-2',
          newSessionTitle: '压缩后的会话',
        }),
      ),
    );
    expect(result.current.turns).toHaveLength(1);
    expect(result.current.turns[0].assistant.status).toBe('success');
    expect(result.current.loading).toBe(false);
    expect(onSwitchSession).toHaveBeenCalledWith('session-2', '压缩后的会话');
  });

  it('resets lifecycle turns without mutating ordinary turns', () => {
    const { result } = renderHook(() => useCompactionHarness());

    act(() =>
      result.current.handleCompactionLifecycleEvent(
        compactionEvent('context.compaction.started'),
        { notify: false },
      ),
    );
    expect(result.current.mergeCompactionLifecycleTurns([])).toHaveLength(1);

    act(() => result.current.resetCompaction());
    expect(result.current.mergeCompactionLifecycleTurns([])).toEqual([]);
  });
});

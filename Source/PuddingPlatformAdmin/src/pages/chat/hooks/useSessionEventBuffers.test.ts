import { act, renderHook } from '@testing-library/react';
import {
  Dispatch,
  SetStateAction,
  useRef,
  useState,
} from 'react';
import { useSessionEventBuffers } from './useSessionEventBuffers';

const initialTurn = {
  turnId: 'turn-1',
  userMessage: {
    id: 'user-1',
    text: 'hello',
    timestamp: 1,
    status: 'success',
  },
  assistant: {
    id: 'assistant-1',
    status: 'executing',
    timelineItems: [],
    answerMarkdown: '',
    isStreaming: true,
    renderMode: 'structured',
  },
};

function useBufferHarness() {
  const [turns, setTurns] = useState([initialTurn]);
  const completedTurnsRef = useRef(new Set<string>());
  const buffers = useSessionEventBuffers({
    setTurns: setTurns as never,
    completedTurnsRef,
  });
  return {
    ...buffers,
    turns,
    setTurns: setTurns as Dispatch<SetStateAction<typeof initialTurn[]>>,
  };
}

describe('useSessionEventBuffers', () => {
  beforeEach(() => {
    jest.useFakeTimers();
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('batches delta frames into the matching turn', () => {
    const { result } = renderHook(() => useBufferHarness());

    act(() => {
      result.current.enqueueDelta('turn-1', 'hello', 0);
      result.current.enqueueDelta('turn-1', ' world', 0);
      jest.runOnlyPendingTimers();
    });

    expect(result.current.turns[0].assistant.answerMarkdown).toBe(
      'hello world',
    );
  });

  it('drops buffered deltas when the answer base drifted before flush (snapshot race)', () => {
    // 直播竞态：增量入队后、flush 前，activeRun 快照把 answerMarkdown
    // 替换为服务端全量文本（已包含这些增量）。flush 必须丢弃缓冲，不得重复追加。
    const { result } = renderHook(() => useBufferHarness());

    act(() => {
      result.current.enqueueDelta('turn-1', 'BC', 1);
      result.current.setTurns((previous: typeof initialTurn[]) =>
        previous.map((turn) => ({
          ...turn,
          assistant: { ...turn.assistant, answerMarkdown: 'ABC' },
        })),
      );
      jest.runOnlyPendingTimers();
    });

    expect(result.current.turns[0].assistant.answerMarkdown).toBe('ABC');
  });

  it('applies sequential buffered batches at their own base lengths', () => {
    const { result } = renderHook(() => useBufferHarness());

    act(() => {
      result.current.enqueueDelta('turn-1', 'A', 0);
      jest.runOnlyPendingTimers();
    });
    act(() => {
      result.current.enqueueDelta('turn-1', 'BC', 1);
      jest.runOnlyPendingTimers();
    });

    expect(result.current.turns[0].assistant.answerMarkdown).toBe('ABC');
  });

  it('flushes thinking frames on demand', () => {
    const { result } = renderHook(() => useBufferHarness());

    act(() => {
      result.current.enqueueThinking('turn-1', 'reasoning');
      result.current.flushPendingThinking();
    });

    expect(result.current.turns[0].assistant.timelineItems[0]).toMatchObject({
      type: 'thinking',
      text: 'reasoning',
    });
  });
});

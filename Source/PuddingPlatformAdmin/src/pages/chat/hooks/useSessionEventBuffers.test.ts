import { act, renderHook } from '@testing-library/react';
import { useRef, useState } from 'react';
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
  return { ...buffers, turns };
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
      result.current.enqueueDelta('turn-1', 'hello');
      result.current.enqueueDelta('turn-1', ' world');
      jest.runOnlyPendingTimers();
    });

    expect(result.current.turns[0].assistant.answerMarkdown).toBe(
      'hello world',
    );
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

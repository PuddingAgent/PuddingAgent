import { act, renderHook } from '@testing-library/react';
import type { ChatInteractionRuntimeEvent } from '../types/chatStateTypes';
import { MAX_CHAT_INTERACTION_RUNTIME_EVENTS } from '../types/chatStateTypes';
import { useChatRuntimeEvents } from './useChatRuntimeEvents';

const runtimeEvent = (index: number): ChatInteractionRuntimeEvent =>
  ({
    type: 'voice_capture_status',
    agentId: 'agent-1',
    status: 'listening',
    now: index,
  }) as ChatInteractionRuntimeEvent;

describe('useChatRuntimeEvents', () => {
  it('keeps only the newest bounded runtime events', () => {
    const { result } = renderHook(() => useChatRuntimeEvents());

    act(() => {
      for (
        let index = 0;
        index < MAX_CHAT_INTERACTION_RUNTIME_EVENTS + 3;
        index += 1
      ) {
        result.current.appendChatInteractionRuntimeEvent(runtimeEvent(index));
      }
    });

    expect(result.current.chatInteractionRuntimeEvents).toHaveLength(
      MAX_CHAT_INTERACTION_RUNTIME_EVENTS,
    );
    expect(result.current.chatInteractionRuntimeEvents[0].now).toBe(3);
  });

  it('clears the runtime-event channel', () => {
    const { result } = renderHook(() => useChatRuntimeEvents());

    act(() =>
      result.current.appendChatInteractionRuntimeEvent(runtimeEvent(1)),
    );
    expect(result.current.chatInteractionRuntimeEvents).toHaveLength(1);

    act(() => result.current.clearChatInteractionRuntimeEvents());
    expect(result.current.chatInteractionRuntimeEvents).toEqual([]);
  });
});

import { useCallback, useState } from 'react';
import {
  type ChatInteractionRuntimeEvent,
  MAX_CHAT_INTERACTION_RUNTIME_EVENTS,
} from '../types/chatStateTypes';

/** Owns the bounded voice/camera/vision runtime-event channel. */
export function useChatRuntimeEvents() {
  const [chatInteractionRuntimeEvents, setChatInteractionRuntimeEvents] =
    useState<ChatInteractionRuntimeEvent[]>([]);

  const appendChatInteractionRuntimeEvent = useCallback(
    (event: ChatInteractionRuntimeEvent) => {
      setChatInteractionRuntimeEvents((previous) =>
        [...previous, event].slice(-MAX_CHAT_INTERACTION_RUNTIME_EVENTS),
      );
    },
    [],
  );

  const clearChatInteractionRuntimeEvents = useCallback(() => {
    setChatInteractionRuntimeEvents([]);
  }, []);

  return {
    chatInteractionRuntimeEvents,
    appendChatInteractionRuntimeEvent,
    clearChatInteractionRuntimeEvents,
  };
}

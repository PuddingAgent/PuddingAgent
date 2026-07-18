// ── ADR-057 Phase 6: useConversation Hook ─────────────────────
// 替代 useChatState 的运行时：连接管理、事件分发、状态订阅。
// 组件通过此 hook 订阅 canonical store，不再依赖 activeMessageIds。
// ─────────────────────────────────────────────────────────────

import { useCallback, useEffect, useRef } from 'react';
import {
  useConversationStore,
  applyEvent,
  applyBootstrap,
  updateConnection,
  selectIsLoading,
  selectCanSend,
  selectActiveTurn,
  getCurrentState,
} from '../state/conversationStore';
import { createConnectionManager } from '../connection/connectionManager';
import { recoverGap } from '../connection/gapRecoveryEngine';
import { getConversationBootstrap, sendChatMessage } from '@/services/platform/api';
import type { ConversationEvent, ConversationTurn } from '../reducer/conversationReducer';

export function useConversation(conversationId: string | null) {
  const store = useConversationStore();
  const managerRef = useRef<ReturnType<typeof createConnectionManager> | null>(null);

  // Bootstrap on conversation switch
  useEffect(() => {
    if (!conversationId) return;

    // Cancel previous connection
    managerRef.current?.disconnect();
    managerRef.current = null;

    // Fetch bootstrap (server is authoritative for snapshot cursor)
    getConversationBootstrap(conversationId)
      .then((bootstrap) => {
        const snapshotCursor = bootstrap.snapshotCursor ?? 0;
        applyBootstrap(
          bootstrap.conversation?.conversationId ?? conversationId,
          snapshotCursor,
          bootstrap.messages ?? [],
          bootstrap.turns as ConversationTurn[] | undefined,
        );

        // Start SSE from snapshotCursor (server's projection checkpoint),
        // NOT from local cursor (which may reference events without persisted state).
        const after = snapshotCursor;

        // Start SSE connection
        ensureManager(conversationId, after);
      })
      .catch((err) => {
        console.error('[useConversation] Bootstrap failed:', err);
        updateConnection({
          lastError: err instanceof Error ? err.message : String(err),
        });
      });

    return () => {
      managerRef.current?.disconnect();
      managerRef.current = null;
    };
  }, [conversationId]);

  function ensureManager(sessionId: string, afterSequence: number) {
    if (managerRef.current) {
      managerRef.current.disconnect();
    }

    managerRef.current = createConnectionManager({
      onEvent: (event: unknown, sequenceNum?: number) => {
        const sseEvent = event as ConversationEvent;
        const seq = sequenceNum ?? sseEvent.sequence ?? 0;

        // Gap detection
        const state = getCurrentState();
        if (seq > state.entities.cursor + 1) {
          console.warn(`[useConversation] Gap detected: cursor=${state.entities.cursor}, event=${seq}`);
          recoverGap(sessionId, seq).catch(console.error);
          return;
        }

        applyEvent({ ...sseEvent, sequence: seq });
      },
      onStateChange: (connState) => {
        updateConnection(connState);
      },
    });

    managerRef.current.connect(sessionId, afterSequence);
  }

  const sendMessageFn = useCallback(
    async (text: string) => {
      if (!conversationId || !selectCanSend(store)) return;

      try {
        const result = await sendChatMessage('default', {
          sessionId: conversationId,
          messageText: text,
        });
        return result;
      } catch (err) {
        console.error('[useConversation] Send failed:', err);
        throw err;
      }
    },
    [conversationId, store],
  );

  return {
    store,
    isLoading: selectIsLoading(store),
    canSend: selectCanSend(store),
    activeTurnId: selectActiveTurn(store),
    sendMessage: sendMessageFn,
    disconnect: () => managerRef.current?.disconnect(),
  };
}

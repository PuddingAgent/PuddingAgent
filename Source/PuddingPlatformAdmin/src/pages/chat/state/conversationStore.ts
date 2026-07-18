// ── ADR-057 Phase 5: Conversation Store ──────────────────────
// 使用 useSyncExternalStore 暴露 canonical state。
// Reducer + Connection Manager + Outbox 全部通过此 Store 协调。
// ─────────────────────────────────────────────────────────────

import { useSyncExternalStore } from 'react';
import {
  reduceConversationEvent,
  createInitialState,
  setSnapshotCursor,
  type ConversationState,
  type ConversationEvent,
} from '../reducer/conversationReducer';
import type { ConnectionState } from '../connection/connectionManager';
import type { OutboxRecord } from '../outbox/commandOutbox';

// ── Canonical Store State ────────────────────────────────────

export interface CanonicalConversationState {
  entities: ConversationState;
  connection: ConnectionState;
  outbox: OutboxRecord[];
  bootstrapped: boolean;
  snapshotCursor: number;
}

// ── Subscriber Pattern ───────────────────────────────────────

type Listener = () => void;

let canonicalState: CanonicalConversationState = {
  entities: createInitialState(),
  connection: {
    connected: false,
    generation: 0,
    sessionId: null,
    cursor: 0,
    reconnectCount: 0,
    lastError: null,
  },
  outbox: [],
  bootstrapped: false,
  snapshotCursor: 0,
};

const listeners = new Set<Listener>();

function getSnapshot(): CanonicalConversationState {
  return canonicalState;
}

function subscribe(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function emit() {
  canonicalState = { ...canonicalState };
  listeners.forEach((l) => l());
}

// ── Actions ──────────────────────────────────────────────────

export function applyEvent(event: ConversationEvent): void {
  const { state: nextEntities, applied } = reduceConversationEvent(
    canonicalState.entities,
    event,
  );

  if (!applied) {
    if (nextEntities.gapDetected) {
      // Gap detected — pause live apply, trigger recovery
      canonicalState = {
        ...canonicalState,
        entities: nextEntities,
      };
      emit();
    }
    return;
  }

  canonicalState = {
    ...canonicalState,
    entities: nextEntities,
    connection: {
      ...canonicalState.connection,
      cursor: nextEntities.cursor,
    },
  };
  emit();
}

export function applyBootstrap(
  conversationId: string,
  snapshotCursor: number,
  messages: Array<{
    id: number;
    role: string;
    content: string;
    createdAt: number;
  }>,
  turns?: Array<{
    turnId: string;
    status: 'active' | 'completed' | 'failed' | 'cancelled';
    userMessageId: string;
    assistantMessageId: string;
    createdAt: number;
  }>,
): void {
  const state = createInitialState(conversationId);
  const withSnapshot = setSnapshotCursor(state, snapshotCursor, messages, turns);

  canonicalState = {
    ...canonicalState,
    entities: withSnapshot,
    snapshotCursor,
    bootstrapped: true,
    connection: {
      ...canonicalState.connection,
      cursor: snapshotCursor,
    },
  };
  emit();
}

export function updateConnection(partial: Partial<ConnectionState>): void {
  canonicalState = {
    ...canonicalState,
    connection: {
      ...canonicalState.connection,
      ...partial,
    },
  };
  emit();
}

export function updateOutbox(records: OutboxRecord[]): void {
  canonicalState = {
    ...canonicalState,
    outbox: records,
  };
  emit();
}

export function replaceState(next: Partial<CanonicalConversationState>): void {
  canonicalState = {
    ...canonicalState,
    ...next,
  };
  emit();
}

export function getCurrentState(): CanonicalConversationState {
  return canonicalState;
}

// ── Hook ─────────────────────────────────────────────────────

export function useConversationStore(): CanonicalConversationState {
  return useSyncExternalStore(subscribe, getSnapshot);
}

// ── Selectors ────────────────────────────────────────────────

export function selectActiveTurn(
  state: CanonicalConversationState,
): string | null {
  const turns = state.entities.turns;
  for (let i = turns.length - 1; i >= 0; i--) {
    if (turns[i].status === 'active') return turns[i].turnId;
  }
  return null;
}

export function selectIsLoading(state: CanonicalConversationState): boolean {
  return selectActiveTurn(state) !== null && state.connection.connected;
}

export function selectCanSend(state: CanonicalConversationState): boolean {
  return !selectIsLoading(state) && state.bootstrapped;
}

export function selectOutboxPending(state: CanonicalConversationState): boolean {
  return state.outbox.some((r) => r.status === 'pending');
}

export function selectConnectionHealth(
  state: CanonicalConversationState,
): { connected: boolean; generation: number; lastError: string | null } {
  return {
    connected: state.connection.connected,
    generation: state.connection.generation,
    lastError: state.connection.lastError,
  };
}

export function selectTurnById(
  state: CanonicalConversationState,
  turnId: string,
) {
  return state.entities.turns.find((t) => t.turnId === turnId);
}

export function selectMessageById(
  state: CanonicalConversationState,
  messageId: string,
) {
  return state.entities.messages.get(messageId);
}

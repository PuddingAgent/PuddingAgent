// ── ADR-057 Phase 5: Gap Recovery Engine ─────────────────────
// 当 sequence > cursor + 1 时，暂停 live 应用，
// 通过 Events API 读取缺失事件并批量应用到 Reducer。
// ─────────────────────────────────────────────────────────────

import { getSessionEvents } from '@/services/platform/api';
import type { ConversationEvent } from '../reducer/conversationReducer';
import { applyEvent, getCurrentState, updateConnection } from '../state/conversationStore';

export async function recoverGap(
  sessionId: string,
  expectedNextSequence: number,
  signal?: AbortSignal,
): Promise<{ recovered: number; remaining: boolean }> {
  const state = getCurrentState();
  const cursor = state.entities.cursor;
  const gapStart = cursor + 1;

  if (gapStart >= expectedNextSequence) {
    // No gap actually — race condition resolved
    return { recovered: 0, remaining: false };
  }

  let recovered = 0;
  let after = cursor;
  let hasMore = true;

  while (hasMore) {
    const page = await getSessionEvents(sessionId, {
      afterSequence: after,
      limit: 200,
    });

    const events = page.events as ConversationEvent[];
    if (!events || events.length === 0) break;

    for (const event of events) {
      const seq = event.sequence;
      if (seq <= cursor) continue; // already applied
      if (seq > expectedNextSequence) break; // past the gap boundary

      applyEvent(event);

      if (event.sequence === expectedNextSequence) {
        // Gap fully resolved
        hasMore = false;
        break;
      }
    }

    recovered += events.length;
    after = events[events.length - 1]?.sequence ?? after;
    hasMore = hasMore && events.length >= 200;
  }

  // Clear gap detection
  const currentState = getCurrentState();
  if (!currentState.entities.gapDetected) {
    updateConnection({ connected: true });
  }

  return {
    recovered,
    remaining: getCurrentState().entities.gapDetected,
  };
}

export function detectGap(cursor: number, eventSequence: number): boolean {
  return eventSequence > cursor + 1;
}

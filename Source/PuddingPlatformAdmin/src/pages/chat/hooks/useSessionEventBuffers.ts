import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useRef } from 'react';
import { recordPerfEvent } from '@/utils/debug';
import type { ChatTurn } from '../types';
import { applyBufferedDeltaToTurn, createId } from '../utils/chatStateUtils';

interface UseSessionEventBuffersOptions {
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  completedTurnsRef: MutableRefObject<Set<string>>;
}

/** Owns frame-batched delta and thinking buffers for the session stream. */
export function useSessionEventBuffers({
  setTurns,
  completedTurnsRef,
}: UseSessionEventBuffersOptions) {
  const pendingDeltaRef = useRef<Map<string, string>>(new Map());
  const deltaFlushTimerRef = useRef<number | null>(null);
  const deltaHasFlushedRef = useRef(false);
  const pendingThinkingRef = useRef<Map<string, string>>(new Map());
  const thinkingFlushTimerRef = useRef<number | null>(null);

  const applyPendingThinking = useCallback(
    (pending: Map<string, string>) => {
      setTurns((previous) =>
        previous.map((turn) => {
          const thinkingDelta = pending.get(turn.turnId);
          if (!thinkingDelta || completedTurnsRef.current.has(turn.turnId)) {
            return turn;
          }
          const nextItems = [
            ...(turn.assistant.timelineItems ?? []),
            {
              id: createId(),
              type: 'thinking' as const,
              text: thinkingDelta,
              status: 'streaming',
              timestamp: Date.now(),
              collapsed: true,
            },
          ];
          return {
            ...turn,
            assistant: {
              ...turn.assistant,
              status: 'thinking' as const,
              renderMode: 'structured' as const,
              timelineItems: nextItems,
            },
          };
        }),
      );
    },
    [completedTurnsRef, setTurns],
  );

  const enqueueDelta = useCallback(
    (turnId: string, delta: string) => {
      pendingDeltaRef.current.set(
        turnId,
        (pendingDeltaRef.current.get(turnId) ?? '') + delta,
      );
      if (deltaFlushTimerRef.current != null) return;
      const scheduledAt = performance.now();
      const delayMs = deltaHasFlushedRef.current ? 80 : 0;
      deltaFlushTimerRef.current = window.setTimeout(() => {
        deltaHasFlushedRef.current = true;
        const flushStart = performance.now();
        const pending = new Map(pendingDeltaRef.current);
        const chars = [...pending.values()].reduce(
          (sum, value) => sum + value.length,
          0,
        );
        pendingDeltaRef.current.clear();
        deltaFlushTimerRef.current = null;
        setTurns((previous) => {
          let changed = false;
          const next = previous.map((turn) => {
            const bufferedDelta = pending.get(turn.turnId);
            if (!bufferedDelta) return turn;
            changed = true;
            return applyBufferedDeltaToTurn(turn, bufferedDelta);
          });
          return changed ? next : previous;
        });
        recordPerfEvent('chat.delta.flush', {
          turns: pending.size,
          chars,
          waitMs: Math.round(flushStart - scheduledAt),
          applyMs: Math.round(performance.now() - flushStart),
        });
      }, delayMs);
    },
    [setTurns],
  );

  const flushPendingDeltas = useCallback(() => {
    if (deltaFlushTimerRef.current != null) {
      window.clearTimeout(deltaFlushTimerRef.current);
      deltaFlushTimerRef.current = null;
    }
    if (pendingDeltaRef.current.size === 0) return;
    const flushStart = performance.now();
    const pending = new Map(pendingDeltaRef.current);
    const chars = [...pending.values()].reduce(
      (sum, value) => sum + value.length,
      0,
    );
    pendingDeltaRef.current.clear();
    setTurns((previous) => {
      let changed = false;
      const next = previous.map((turn) => {
        const bufferedDelta = pending.get(turn.turnId);
        if (!bufferedDelta) return turn;
        changed = true;
        return applyBufferedDeltaToTurn(turn, bufferedDelta);
      });
      return changed ? next : previous;
    });
    recordPerfEvent('chat.delta.flushNow', {
      turns: pending.size,
      chars,
      applyMs: Math.round(performance.now() - flushStart),
    });
  }, [setTurns]);

  const enqueueThinking = useCallback(
    (turnId: string, thinkingDelta: string) => {
      pendingThinkingRef.current.set(
        turnId,
        (pendingThinkingRef.current.get(turnId) ?? '') + thinkingDelta,
      );
      if (thinkingFlushTimerRef.current != null) return;
      const scheduledAt = performance.now();
      thinkingFlushTimerRef.current = window.setTimeout(() => {
        const flushStart = performance.now();
        const pending = new Map(pendingThinkingRef.current);
        pendingThinkingRef.current.clear();
        thinkingFlushTimerRef.current = null;
        if (pending.size > 0) applyPendingThinking(pending);
        recordPerfEvent('chat.thinking.flush', {
          turns: pending.size,
          chars: [...pending.values()].reduce(
            (sum, value) => sum + value.length,
            0,
          ),
          waitMs: Math.round(flushStart - scheduledAt),
          applyMs: Math.round(performance.now() - flushStart),
        });
      }, 120);
    },
    [applyPendingThinking],
  );

  const flushPendingThinking = useCallback(() => {
    if (thinkingFlushTimerRef.current != null) {
      window.clearTimeout(thinkingFlushTimerRef.current);
      thinkingFlushTimerRef.current = null;
    }
    if (pendingThinkingRef.current.size === 0) return;
    const flushStart = performance.now();
    const pending = new Map(pendingThinkingRef.current);
    pendingThinkingRef.current.clear();
    applyPendingThinking(pending);
    recordPerfEvent('chat.thinking.flushNow', {
      turns: pending.size,
      chars: [...pending.values()].reduce(
        (sum, value) => sum + value.length,
        0,
      ),
      applyMs: Math.round(performance.now() - flushStart),
    });
  }, [applyPendingThinking]);

  const resetSessionEventBuffers = useCallback(() => {
    pendingDeltaRef.current.clear();
    pendingThinkingRef.current.clear();
    if (deltaFlushTimerRef.current != null) {
      window.clearTimeout(deltaFlushTimerRef.current);
      deltaFlushTimerRef.current = null;
    }
    if (thinkingFlushTimerRef.current != null) {
      window.clearTimeout(thinkingFlushTimerRef.current);
      thinkingFlushTimerRef.current = null;
    }
  }, []);

  const prepareForNewMessage = useCallback(() => {
    deltaHasFlushedRef.current = false;
  }, []);

  return {
    pendingDeltaRef,
    deltaFlushTimerRef,
    pendingThinkingRef,
    thinkingFlushTimerRef,
    enqueueDelta,
    flushPendingDeltas,
    enqueueThinking,
    flushPendingThinking,
    resetSessionEventBuffers,
    prepareForNewMessage,
  };
}

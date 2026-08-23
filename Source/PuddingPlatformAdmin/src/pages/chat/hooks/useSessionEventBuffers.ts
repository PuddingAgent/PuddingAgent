import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useRef } from 'react';
import { recordPerfEvent } from '@/utils/perfEventRuntime';
import type { ChatTurn } from '../types';
import { applyBufferedDeltaToTurn } from '../utils/chatStateUtils';

/** TR-01/CU-02：thinking 缓冲的服务端事实锚点（eventId/occurredAt）。 */
interface ThinkingBatchFacts {
  eventId?: string;
  occurredAtMs?: number;
}

interface UseSessionEventBuffersOptions {
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  completedTurnsRef: MutableRefObject<Set<string>>;
}

/** Owns frame-batched delta and thinking buffers for the session stream. */
export function useSessionEventBuffers({
  setTurns,
  completedTurnsRef,
}: UseSessionEventBuffersOptions) {
  /** 回答增量缓冲：delta 文本 + 入队时 answerMarkdown 的基准长度（幂等 flush 依据）。 */
  const pendingDeltaRef = useRef<
    Map<string, { delta: string; baseLength: number }>
  >(new Map());
  const deltaFlushTimerRef = useRef<number | null>(null);
  const deltaHasFlushedRef = useRef(false);
  const pendingThinkingRef = useRef<Map<string, string>>(new Map());
  const thinkingFlushTimerRef = useRef<number | null>(null);
  const pendingThinkingFactsRef = useRef<Map<string, ThinkingBatchFacts>>(
    new Map(),
  );

  const applyPendingThinking = useCallback(
    (
      pending: Map<string, string>,
      factsByTurn: Map<string, ThinkingBatchFacts>,
    ) => {
      setTurns((previous) =>
        previous.map((turn) => {
          const thinkingDelta = pending.get(turn.turnId);
          if (!thinkingDelta || completedTurnsRef.current.has(turn.turnId)) {
            return turn;
          }
          const facts = factsByTurn.get(turn.turnId) ?? {};
          // 事实来自 canonical 信封；缺失时记录协议错误（见 canonicalEvents）
          // 并退化为服务端事实派生的确定性键。
          const itemId = facts.eventId ?? `thinking:${turn.turnId}:pending`;
          const timestamp = facts.occurredAtMs ?? 0;
          const items = turn.assistant.timelineItems ?? [];
          const lastIndex = items.length - 1;
          const last = lastIndex >= 0 ? items[lastIndex] : undefined;
          // 幂等：同一 eventId 的 thinking 批次只产生一条时间线条目。
          if (last && last.type === 'thinking' && last.id === itemId) {
            const merged = [
              ...items.slice(0, lastIndex),
              { ...last, text: `${last.text ?? ''}${thinkingDelta}` },
            ];
            return {
              ...turn,
              assistant: {
                ...turn.assistant,
                status: 'thinking' as const,
                renderMode: 'structured' as const,
                timelineItems: merged,
              },
            };
          }
          const nextItems = [
            ...items,
            {
              id: itemId,
              eventId: facts.eventId,
              type: 'thinking' as const,
              text: thinkingDelta,
              status: 'streaming',
              timestamp,
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
    (turnId: string, delta: string, baseLength: number) => {
      const pending = pendingDeltaRef.current.get(turnId);
      // 基准取批内首个增量入队时的长度；后续增量只拼接文本。
      pendingDeltaRef.current.set(turnId, {
        delta: (pending?.delta ?? '') + delta,
        baseLength: pending ? pending.baseLength : baseLength,
      });
      if (deltaFlushTimerRef.current != null) return;
      const scheduledAt = performance.now();
      const delayMs = deltaHasFlushedRef.current ? 80 : 0;
      deltaFlushTimerRef.current = window.setTimeout(() => {
        deltaHasFlushedRef.current = true;
        const flushStart = performance.now();
        const pendingMap = new Map(pendingDeltaRef.current);
        const chars = [...pendingMap.values()].reduce(
          (sum, value) => sum + value.delta.length,
          0,
        );
        pendingDeltaRef.current.clear();
        deltaFlushTimerRef.current = null;
        setTurns((previous) => {
          let changed = false;
          const next = previous.map((turn) => {
            const buffered = pendingMap.get(turn.turnId);
            if (!buffered) return turn;
            changed = true;
            return applyBufferedDeltaToTurn(
              turn,
              buffered.delta,
              buffered.baseLength,
            );
          });
          return changed ? next : previous;
        });
        recordPerfEvent('chat.delta.flush', {
          turns: pendingMap.size,
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
    const pendingMap = new Map(pendingDeltaRef.current);
    const chars = [...pendingMap.values()].reduce(
      (sum, value) => sum + value.delta.length,
      0,
    );
    pendingDeltaRef.current.clear();
    setTurns((previous) => {
      let changed = false;
      const next = previous.map((turn) => {
        const buffered = pendingMap.get(turn.turnId);
        if (!buffered) return turn;
        changed = true;
        return applyBufferedDeltaToTurn(
          turn,
          buffered.delta,
          buffered.baseLength,
        );
      });
      return changed ? next : previous;
    });
    recordPerfEvent('chat.delta.flushNow', {
      turns: pendingMap.size,
      chars,
      applyMs: Math.round(performance.now() - flushStart),
    });
  }, [setTurns]);

  const enqueueThinking = useCallback(
    (turnId: string, thinkingDelta: string, facts?: ThinkingBatchFacts) => {
      pendingThinkingRef.current.set(
        turnId,
        (pendingThinkingRef.current.get(turnId) ?? '') + thinkingDelta,
      );
      // 批内事实锚点取首个携带事实的事件（确定性，重放等价）。
      if (facts && !pendingThinkingFactsRef.current.has(turnId)) {
        pendingThinkingFactsRef.current.set(turnId, facts);
      }
      if (thinkingFlushTimerRef.current != null) return;
      const scheduledAt = performance.now();
      thinkingFlushTimerRef.current = window.setTimeout(() => {
        const flushStart = performance.now();
        const pending = new Map(pendingThinkingRef.current);
        const factsSnapshot = new Map(pendingThinkingFactsRef.current);
        pendingThinkingRef.current.clear();
        pendingThinkingFactsRef.current.clear();
        thinkingFlushTimerRef.current = null;
        if (pending.size > 0) applyPendingThinking(pending, factsSnapshot);
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
    const factsSnapshot = new Map(pendingThinkingFactsRef.current);
    pendingThinkingRef.current.clear();
    pendingThinkingFactsRef.current.clear();
    applyPendingThinking(pending, factsSnapshot);
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
    pendingThinkingFactsRef.current.clear();
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

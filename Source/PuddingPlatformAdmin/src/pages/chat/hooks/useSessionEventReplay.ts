import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useEffect, useRef } from 'react';
import {
  type AdminChatStreamEvent,
  getConversationBootstrap,
  getSessionSubAgents,
} from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/debug';
import {
  reconcileSubAgentRunStatuses,
  reduceSubAgentRunEvent,
  type SubAgentRunMap,
} from '../reducer/subAgentReducer';
import type { ChatTurn } from '../types';
import { SESSION_EVENT_PAGE_SIZE } from '../types/chatStateTypes';
import { logChatDiag } from '../utils/chatDiagnostics';
import {
  countCompletedAssistantTurns,
  getSessionEventSequenceNum,
  HISTORICAL_REPLAY_TERMINAL_EVENTS,
  resolveActiveSessionReplayFromSequence,
  shouldHydrateSessionEventReplay,
  shouldRunSessionReplayCompensation,
} from '../utils/chatStateUtils';
import {
  listSessionEventsPage,
  type NormalizedSessionEvent,
  normalizeSessionEvent,
} from '../utils/sessionEventReplay';
import type { CompactionLifecycleOptions } from './useCompaction';

interface SessionReplayIdentityPort {
  lastSequenceNumRef: MutableRefObject<number>;
  sseSessionIdRef: MutableRefObject<string | null>;
  lastSseEventAtRef: MutableRefObject<number | null>;
  activeMessageIdsRef: MutableRefObject<Set<string>>;
  selectedSessionIdRef: MutableRefObject<string | null>;
  sessionIdRef: MutableRefObject<string | undefined>;
  hydrateSessionReplayRef: MutableRefObject<boolean>;
}

interface SessionReplayProjectionPort {
  applySessionEvent: (event: AdminChatStreamEvent) => void;
  handleCompactionLifecycleEvent: (
    event: AdminChatStreamEvent,
    options?: CompactionLifecycleOptions,
  ) => void;
  setSubAgentRuns: Dispatch<SetStateAction<SubAgentRunMap>>;
  subAgentRuns: SubAgentRunMap;
  pruneTrackedActiveMessages: (reason: string) => boolean;
}

interface UseSessionEventReplayOptions {
  identity: SessionReplayIdentityPort;
  projection: SessionReplayProjectionPort;
}

const SUB_AGENT_STATUS_RECONCILE_MS = 10_000;

const pageEvents = (page: {
  events?: unknown[];
  Events?: unknown[];
}): unknown[] =>
  Array.isArray(page.events)
    ? page.events
    : Array.isArray(page.Events)
      ? page.Events
      : [];

/** Owns persisted-event normalization, cursor sync, and gap replay. */
export function useSessionEventReplay({
  identity,
  projection,
}: UseSessionEventReplayOptions) {
  const {
    lastSequenceNumRef,
    sseSessionIdRef,
    lastSseEventAtRef,
    activeMessageIdsRef,
    selectedSessionIdRef,
    sessionIdRef,
    hydrateSessionReplayRef,
  } = identity;
  const {
    applySessionEvent,
    handleCompactionLifecycleEvent,
    setSubAgentRuns,
    subAgentRuns,
    pruneTrackedActiveMessages,
  } = projection;

  const isReplayingRef = useRef(false);
  const pendingEventsQueueRef = useRef<AdminChatStreamEvent[]>([]);

  const selectedSessionId =
    selectedSessionIdRef.current ?? sessionIdRef.current ?? null;
  const hasActiveSubAgentRuns = Object.values(subAgentRuns).some(
    (run) => run.status === 'created' || run.status === 'running',
  );

  useEffect(() => {
    if (!selectedSessionId || !hasActiveSubAgentRuns) return undefined;

    let disposed = false;
    let timer: number | undefined;
    const reconcileStatuses = async () => {
      try {
        const statuses = await getSessionSubAgents(selectedSessionId);
        if (disposed) return;
        setSubAgentRuns((current) =>
          reconcileSubAgentRunStatuses(current, statuses),
        );
      } catch (error) {
        if (!disposed) {
          logChatDiag('subagents.statusReconcile.failed', {
            sessionId: selectedSessionId,
            error: error instanceof Error ? error.message : String(error),
          });
        }
      } finally {
        if (!disposed) {
          timer = window.setTimeout(
            reconcileStatuses,
            SUB_AGENT_STATUS_RECONCILE_MS,
          );
        }
      }
    };

    void reconcileStatuses();
    return () => {
      disposed = true;
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [hasActiveSubAgentRuns, selectedSessionId, setSubAgentRuns]);

  const syncCompletedHistoryEventCursor = useCallback(
    async (sessionId: string, signal?: AbortSignal) => {
      try {
        const [bootstrap, statuses] = await Promise.all([
          getConversationBootstrap(sessionId, 1),
          getSessionSubAgents(sessionId).catch(() => []),
        ]);
        if (signal?.aborted) return;
        for (const rawEvent of bootstrap.lifecycleEvents ?? []) {
          const event = normalizeSessionEvent(rawEvent);
          if (!event) continue;
          handleCompactionLifecycleEvent(event, {
            allowSessionSwitch: false,
            notify: false,
          });
        }
        let snapshotRuns: SubAgentRunMap = {};
        for (const rawEvent of bootstrap.subAgentEvents ?? []) {
          if (!rawEvent || typeof rawEvent !== 'object') continue;
          const event = rawEvent as Record<string, unknown>;
          if (typeof event.type !== 'string') continue;
          snapshotRuns = reduceSubAgentRunEvent(snapshotRuns, {
            ...event,
            type: event.type,
          });
        }
        setSubAgentRuns((current) => {
          const merged = { ...snapshotRuns };
          for (const [runId, run] of Object.entries(current)) {
            const snapshot = merged[runId];
            if (!snapshot || run.lastActivityAt > snapshot.lastActivityAt) {
              merged[runId] = run;
            }
          }
          return reconcileSubAgentRunStatuses(merged, statuses);
        });
        const cursor = Number(bootstrap.snapshotCursor);
        if (!Number.isFinite(cursor) || cursor < 0) return;
        lastSequenceNumRef.current = Math.max(
          lastSequenceNumRef.current,
          cursor,
        );
        recordPerfEvent('chat.replay.cursorSynced', {
          sessionId,
          lastSequenceNum: lastSequenceNumRef.current,
        });
      } catch (error) {
        recordPerfEvent(
          'chat.replay.cursorSyncFailed',
          {
            sessionId,
            aborted: signal?.aborted === true,
            error: error instanceof Error ? error.message : String(error),
          },
          { throttleMs: 1_000 },
        );
      }
    },
    [handleCompactionLifecycleEvent, lastSequenceNumRef, setSubAgentRuns],
  );

  const replayMissedSessionEvents = useCallback(
    async (
      sessionId: string,
      signal?: AbortSignal,
      options?: { backfillActiveTerminalEvents?: boolean },
    ) => {
      const backfillActiveTerminalEvents =
        options?.backfillActiveTerminalEvents === true;
      // Snapshot lastSequenceNumRef to prevent SSE updates during replay
      // from causing event-skip (RC-7).
      const snapshotSequence = lastSequenceNumRef.current;
      let from = backfillActiveTerminalEvents
        ? resolveActiveSessionReplayFromSequence(
            snapshotSequence,
            SESSION_EVENT_PAGE_SIZE,
          )
        : Math.max(0, snapshotSequence + 1);
      const startedAt = performance.now();
      const initialFrom = from;
      let pageCount = 0;
      let eventCount = 0;
      let appliedCount = 0;
      let hasMore = true;
      if (isReplayingRef.current) {
        return;
      }
      isReplayingRef.current = true;
      try {
        if (lastSequenceNumRef.current === 0) {
          let offset = 1;
          const maxPages = 20;
          for (let pageIndex = 0; pageIndex < maxPages; pageIndex += 1) {
            const page = await listSessionEventsPage(
              sessionId,
              offset,
              SESSION_EVENT_PAGE_SIZE,
              signal,
            );
            const list = pageEvents(page);
            if (list.length === 0) break;
            const maxSequence = list
              .map(getSessionEventSequenceNum)
              .filter((value): value is number => value !== null)
              .reduce((maximum, value) => Math.max(maximum, value), 0);
            if (maxSequence > 0) {
              lastSequenceNumRef.current = Math.max(
                lastSequenceNumRef.current,
                maxSequence,
              );
              offset = maxSequence + 1;
            }
            if (!(page.hasMore ?? page.HasMore)) break;
          }
          from =
            lastSequenceNumRef.current > 0 ? lastSequenceNumRef.current + 1 : 1;
        }

        while (hasMore) {
          const pageStartedAt = performance.now();
          const page = await listSessionEventsPage(
            sessionId,
            from,
            SESSION_EVENT_PAGE_SIZE,
            signal,
          );
          pageCount += 1;
          const list = pageEvents(page);
          eventCount += list.length;
          recordPerfEvent('chat.replay.page', {
            sessionId,
            from,
            limit: SESSION_EVENT_PAGE_SIZE,
            events: list.length,
            hasMore: Boolean(page.hasMore ?? page.HasMore),
            elapsedMs: Math.round(performance.now() - pageStartedAt),
          });
          if (list.length === 0) break;

          for (const item of list) {
            const event = normalizeSessionEvent(item);
            if (!event) continue;
            const sequence = event.sequenceNum;
            if (
              typeof sequence === 'number' &&
              Number.isFinite(sequence) &&
              sequence <= snapshotSequence &&
              (!backfillActiveTerminalEvents ||
                !HISTORICAL_REPLAY_TERMINAL_EVENTS.has(event.type))
            ) {
              continue;
            }
            applySessionEvent(event);
            appliedCount += 1;
          }

          const maxSequence = list
            .map(getSessionEventSequenceNum)
            .filter((value): value is number => value !== null)
            .reduce((maximum, value) => Math.max(maximum, value), Number.NaN);
          if (Number.isFinite(maxSequence)) {
            from = Math.max(from, Number(maxSequence) + 1);
          } else {
            hasMore = false;
          }
          hasMore = hasMore && Boolean(page.hasMore ?? page.HasMore);
        }

        const completePayload = {
          sessionId,
          from: initialFrom,
          nextFrom: from,
          pages: pageCount,
          events: eventCount,
          applied: appliedCount,
          lastSequenceNum: lastSequenceNumRef.current,
          elapsedMs: Math.round(performance.now() - startedAt),
        };
        recordPerfEvent('chat.replay.complete', completePayload);
        logChatDiag('events.replay.complete', completePayload);
      } catch (error) {
        const errorPayload = {
          sessionId,
          from: initialFrom,
          pages: pageCount,
          events: eventCount,
          applied: appliedCount,
          aborted: signal?.aborted === true,
          error: error instanceof Error ? error.message : String(error),
          elapsedMs: Math.round(performance.now() - startedAt),
        };
        recordPerfEvent('chat.replay.error', errorPayload);
        logChatDiag('events.replay.error', { ...errorPayload, error });
        throw error;
      } finally {
        isReplayingRef.current = false;
        const pending = pendingEventsQueueRef.current;
        pendingEventsQueueRef.current = [];
        for (const event of pending) {
          applySessionEvent(event);
        }
      }
    },
    [applySessionEvent, lastSequenceNumRef],
  );

  const replayMissedSessionEventsIfNeeded = useCallback(
    async (
      sessionId: string,
      options: {
        signal?: AbortSignal;
        reason: string;
        hasActiveMessages?: boolean;
      },
    ) => {
      const hasActiveMessages =
        options.hasActiveMessages ??
        pruneTrackedActiveMessages(`replay-${options.reason}`);
      const lastSseEventAt =
        sseSessionIdRef.current === sessionId
          ? lastSseEventAtRef.current
          : null;
      const now = performance.now();
      if (
        !shouldRunSessionReplayCompensation({
          hasActiveMessages,
          lastSseEventAt,
          now,
        })
      ) {
        const skippedPayload = {
          sessionId,
          reason: options.reason,
          hasActiveMessages,
          activeMessageCount: activeMessageIdsRef.current.size,
          sinceLastSseEventMs:
            lastSseEventAt == null ? null : Math.round(now - lastSseEventAt),
        };
        recordPerfEvent('chat.replay.skipped', skippedPayload, {
          throttleMs: 2_000,
        });
        logChatDiag('events.replay.skipped', {
          ...skippedPayload,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
        });
        return false;
      }

      await replayMissedSessionEvents(sessionId, options.signal, {
        backfillActiveTerminalEvents: hasActiveMessages,
      });
      return true;
    },
    [
      activeMessageIdsRef,
      lastSseEventAtRef,
      pruneTrackedActiveMessages,
      replayMissedSessionEvents,
      selectedSessionIdRef,
      sessionIdRef,
      sseSessionIdRef,
    ],
  );

  const replayLatestTurnSessionEvents = useCallback(
    async (
      sessionId: string,
      loadedTurns: ChatTurn[],
      signal?: AbortSignal,
    ) => {
      const completedAssistantTurns = countCompletedAssistantTurns(loadedTurns);
      const normalizedEvents: NormalizedSessionEvent[] = [];
      let from = 1;
      let hasMore = true;

      while (hasMore) {
        const page = await listSessionEventsPage(
          sessionId,
          from,
          SESSION_EVENT_PAGE_SIZE,
          signal,
        );
        const list = pageEvents(page);
        if (list.length === 0) break;
        let maxSequence = Number.NaN;
        for (const item of list) {
          const event = normalizeSessionEvent(item);
          if (!event) continue;
          normalizedEvents.push(event);
          if (
            typeof event.sequenceNum === 'number' &&
            Number.isFinite(event.sequenceNum)
          ) {
            maxSequence = Math.max(maxSequence, event.sequenceNum);
          }
        }
        if (Number.isFinite(maxSequence)) {
          from = Number(maxSequence) + 1;
        } else {
          hasMore = false;
        }
        hasMore = hasMore && Boolean(page.hasMore ?? page.HasMore);
      }

      let doneCount = 0;
      let tailStart = 0;
      for (let index = 0; index < normalizedEvents.length; index += 1) {
        if (normalizedEvents[index].type === 'done') {
          doneCount += 1;
          if (doneCount <= completedAssistantTurns) tailStart = index + 1;
        }
      }

      const previous = normalizedEvents[tailStart - 1];
      if (
        previous &&
        typeof previous.sequenceNum === 'number' &&
        Number.isFinite(previous.sequenceNum)
      ) {
        lastSequenceNumRef.current = Math.max(
          lastSequenceNumRef.current,
          previous.sequenceNum,
        );
      }

      const replayTail = normalizedEvents.slice(tailStart);
      const previousHydrateMode = hydrateSessionReplayRef.current;
      hydrateSessionReplayRef.current =
        shouldHydrateSessionEventReplay(replayTail);
      try {
        for (const event of replayTail) {
          if (
            typeof event.sequenceNum === 'number' &&
            Number.isFinite(event.sequenceNum) &&
            event.sequenceNum <= lastSequenceNumRef.current
          ) {
            continue;
          }
          applySessionEvent(event);
        }
      } finally {
        hydrateSessionReplayRef.current = previousHydrateMode;
      }
    },
    [applySessionEvent, hydrateSessionReplayRef, lastSequenceNumRef],
  );

  return {
    syncCompletedHistoryEventCursor,
    replayMissedSessionEvents,
    replayMissedSessionEventsIfNeeded,
    replayLatestTurnSessionEvents,
    isReplayingRef,
  };
}

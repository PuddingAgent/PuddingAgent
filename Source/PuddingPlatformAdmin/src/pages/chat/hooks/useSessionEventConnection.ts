import type { MutableRefObject } from 'react';
import { useCallback, useRef } from 'react';
import {
  type AdminChatStreamEvent,
  subscribeSessionEvents,
} from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/debug';
import type { ChatTurn } from '../types';
import { logChatDiag } from '../utils/chatDiagnostics';
import { resolveSessionReplayPollInterval } from '../utils/chatStateUtils';
import { isSessionNotFoundError } from './sessionRuntimeCleanup';

interface SessionEventConnectionPorts {
  applySessionEvent: (event: AdminChatStreamEvent) => void;
  handleSessionNotFound: (sessionId: string, reason: string) => void;
  pruneTrackedActiveMessages: (reason: string) => boolean;
  replayMissedSessionEvents: (
    sessionId: string,
    signal?: AbortSignal,
  ) => Promise<void>;
  replayMissedSessionEventsIfNeeded: (
    sessionId: string,
    options: {
      signal?: AbortSignal;
      reason: string;
      hasActiveMessages?: boolean;
    },
  ) => Promise<boolean>;
  resetStreamCursorForSessionChange: (
    previousSessionId?: string | null,
    nextSessionId?: string | null,
  ) => void;
  flushPendingDeltas: () => void;
  syncSessionIdentity: () => void;
  activeMessageIdsRef: MutableRefObject<Set<string>>;
  lastSequenceNumRef: MutableRefObject<number>;
  streamStartAtRef: MutableRefObject<Map<string, number>>;
  selectedSessionIdRef: MutableRefObject<string | null>;
  sessionIdRef: MutableRefObject<string | undefined>;
  turnsRef: MutableRefObject<ChatTurn[]>;
}

const noop = () => {};
const defaultPorts = {
  applySessionEvent: noop,
  handleSessionNotFound: noop,
  pruneTrackedActiveMessages: () => false,
  replayMissedSessionEvents: async () => {},
  replayMissedSessionEventsIfNeeded: async () => false,
  resetStreamCursorForSessionChange: noop,
  flushPendingDeltas: noop,
  syncSessionIdentity: noop,
  activeMessageIdsRef: { current: new Set<string>() },
  lastSequenceNumRef: { current: 0 },
  streamStartAtRef: { current: new Map<string, number>() },
  selectedSessionIdRef: { current: null },
  sessionIdRef: { current: undefined },
  turnsRef: { current: [] },
} satisfies SessionEventConnectionPorts;

/** Owns live SSE connection refs, reconnect scheduling, and replay polling. */
export function useSessionEventConnection() {
  const portsRef = useRef<SessionEventConnectionPorts>(defaultPorts);
  const sessionEventsAbortRef = useRef<AbortController | null>(null);
  const sessionEventsPollTimerRef = useRef<number | null>(null);
  const sessionEventsReconnectTimerRef = useRef<number | null>(null);
  const sessionEventsWatchdogTimerRef = useRef<number | null>(null);
  const sseSessionIdRef = useRef<string | null>(null);
  const lastSseEventAtRef = useRef<number | null>(null);
  const reconnectCountRef = useRef(0);

  const bindSessionEventConnection = useCallback(
    (ports: SessionEventConnectionPorts) => {
      portsRef.current = ports;
    },
    [],
  );

  const clearSessionEventTimers = useCallback(() => {
    if (sessionEventsPollTimerRef.current != null) {
      window.clearTimeout(sessionEventsPollTimerRef.current);
      sessionEventsPollTimerRef.current = null;
    }
    if (sessionEventsReconnectTimerRef.current != null) {
      window.clearTimeout(sessionEventsReconnectTimerRef.current);
      sessionEventsReconnectTimerRef.current = null;
    }
    if (sessionEventsWatchdogTimerRef.current != null) {
      window.clearTimeout(sessionEventsWatchdogTimerRef.current);
      sessionEventsWatchdogTimerRef.current = null;
    }
  }, []);

  const stopSessionEventStream = useCallback(() => {
    const ports = portsRef.current;
    ports.flushPendingDeltas();
    clearSessionEventTimers();
    sessionEventsAbortRef.current?.abort();
    sessionEventsAbortRef.current = null;
    sseSessionIdRef.current = null;
    lastSseEventAtRef.current = null;
    reconnectCountRef.current = 0;
    ports.syncSessionIdentity();
  }, [clearSessionEventTimers]);

  const startSessionEventStream = useCallback(
    (sessionId: string) => {
      if (!sessionId) return;
      const ports = portsRef.current;
      const previousStreamSessionId = sseSessionIdRef.current;
      stopSessionEventStream();
      ports.resetStreamCursorForSessionChange(
        previousStreamSessionId,
        sessionId,
      );
      sseSessionIdRef.current = sessionId;
      ports.syncSessionIdentity();
      recordPerfEvent('chat.sse.start', { sessionId });
      logChatDiag('sse.start', {
        sessionId,
        previousStreamSessionId,
        selectedSessionId: ports.selectedSessionIdRef.current,
        sessionIdRef: ports.sessionIdRef.current,
        lastSequenceNum: ports.lastSequenceNumRef.current,
        activeMessageCount: ports.activeMessageIdsRef.current.size,
        turnCount: ports.turnsRef.current.length,
      });

      const controller = new AbortController();
      sessionEventsAbortRef.current = controller;

      const scheduleReconnect = () => {
        if (sessionEventsReconnectTimerRef.current != null) return;
        reconnectCountRef.current += 1;
        recordPerfEvent('chat.sse.reconnectScheduled', { sessionId, attempt: reconnectCountRef.current });
        sessionEventsReconnectTimerRef.current = window.setTimeout(async () => {
          sessionEventsReconnectTimerRef.current = null;
          if (sseSessionIdRef.current !== sessionId) return;
          try {
            await portsRef.current.replayMissedSessionEvents(sessionId);
          } catch {
            // Retry through the next compensation cycle.
          }
          if (sseSessionIdRef.current === sessionId) {
            startSessionEventStream(sessionId);
          }
        }, 1200);
      };

      const onOnline = () => scheduleReconnect();
      window.addEventListener('online', onOnline);

      const scheduleReplayPoll = () => {
        const currentPorts = portsRef.current;
        if (
          sseSessionIdRef.current !== sessionId ||
          controller.signal.aborted
        ) {
          return;
        }
        const hasActiveMessages = currentPorts.pruneTrackedActiveMessages(
          'replay-poll-schedule',
        );
        const delayMs = resolveSessionReplayPollInterval(hasActiveMessages);
        recordPerfEvent(
          'chat.replay.pollScheduled',
          {
            sessionId,
            delayMs,
            activeMessageCount: currentPorts.activeMessageIdsRef.current.size,
            activeTurn: hasActiveMessages,
          },
          { throttleMs: 2_000 },
        );
        sessionEventsPollTimerRef.current = window.setTimeout(async () => {
          sessionEventsPollTimerRef.current = null;
          if (
            sseSessionIdRef.current !== sessionId ||
            controller.signal.aborted
          ) {
            return;
          }
          const pollStartedAt = performance.now();
          let shouldContinue = true;
          const pollPorts = portsRef.current;
          try {
            const ranReplay = await pollPorts.replayMissedSessionEventsIfNeeded(
              sessionId,
              {
                signal: controller.signal,
                reason: 'poll',
                hasActiveMessages: pollPorts.pruneTrackedActiveMessages(
                  'replay-poll-execute',
                ),
              },
            );
            recordPerfEvent('chat.replay.pollComplete', {
              sessionId,
              delayMs,
              activeMessageCount: pollPorts.activeMessageIdsRef.current.size,
              skipped: !ranReplay,
              elapsedMs: Math.round(performance.now() - pollStartedAt),
            });
          } catch (error) {
            recordPerfEvent('chat.replay.pollError', {
              sessionId,
              delayMs,
              activeMessageCount: pollPorts.activeMessageIdsRef.current.size,
              aborted: controller.signal.aborted,
              error: error instanceof Error ? error.message : String(error),
              elapsedMs: Math.round(performance.now() - pollStartedAt),
            });
            if (isSessionNotFoundError(error)) {
              logChatDiag('events.replay.sessionNotFound', {
                sessionId,
                error: error.message,
              });
              pollPorts.handleSessionNotFound(sessionId, 'replay-poll-404');
              shouldContinue = false;
            }
          } finally {
            if (
              shouldContinue &&
              !controller.signal.aborted &&
              sseSessionIdRef.current === sessionId
            ) {
              scheduleReplayPoll();
            }
          }
        }, delayMs);
      };
      scheduleReplayPoll();

      const scheduleWatchdog = () => {
        sessionEventsWatchdogTimerRef.current = window.setTimeout(() => {
          if (
            sseSessionIdRef.current !== sessionId ||
            controller.signal.aborted
          ) {
            return;
          }
          const lastEvent = lastSseEventAtRef.current;
          if (lastEvent && performance.now() - lastEvent > 90_000) {
            recordPerfEvent('chat.sse.watchdog', { sessionId, idleMs: Math.round(performance.now() - lastEvent) });
            scheduleReconnect();
            return;
          }
          scheduleWatchdog();
        }, 30_000);
      };
      scheduleWatchdog();

      try {
        subscribeSessionEvents(
          sessionId,
          (event) => {
            if (
              controller.signal.aborted ||
              sseSessionIdRef.current !== sessionId
            ) {
              return;
            }
            lastSseEventAtRef.current = performance.now();
            const currentPorts = portsRef.current;
            const rawEvent = event as Record<string, unknown>;
            const messageId =
              typeof rawEvent.messageId === 'string'
                ? rawEvent.messageId
                : null;
            if (
              messageId &&
              !currentPorts.streamStartAtRef.current.has(messageId)
            ) {
              currentPorts.streamStartAtRef.current.set(
                messageId,
                performance.now(),
              );
              recordPerfEvent('chat.sse.firstEvent', {
                sessionId,
                messageId,
                eventType: event.type,
                sequenceNum: (event as { sequenceNum?: number }).sequenceNum,
              });
            }
            currentPorts.applySessionEvent(event);
          },
          controller.signal,
          {
            onError: (_error, httpStatus) => {
              if (
                controller.signal.aborted ||
                sseSessionIdRef.current !== sessionId
              ) {
                return;
              }
              const currentPorts = portsRef.current;
              if (httpStatus === 404 || httpStatus === 410) {
                logChatDiag('sse.sessionTerminal', { sessionId, httpStatus });
                currentPorts.handleSessionNotFound(
                  sessionId,
                  `sse-${httpStatus}`,
                );
                return;
              }
              logChatDiag('sse.error.reconnect', { sessionId, httpStatus });
              scheduleReconnect();
            },
          },
        );
      } catch {
        scheduleReconnect();
      }

      const originalAbort = controller.abort.bind(controller);
      controller.abort = () => {
        window.removeEventListener('online', onOnline);
        if (sessionEventsWatchdogTimerRef.current != null) {
          window.clearTimeout(sessionEventsWatchdogTimerRef.current);
          sessionEventsWatchdogTimerRef.current = null;
        }
        recordPerfEvent('chat.sse.stop', { sessionId });
        logChatDiag('sse.stop', {
          sessionId,
          selectedSessionId: portsRef.current.selectedSessionIdRef.current,
          sessionIdRef: portsRef.current.sessionIdRef.current,
        });
        originalAbort();
      };
    },
    [stopSessionEventStream],
  );

  return {
    sessionEventsAbortRef,
    sessionEventsPollTimerRef,
    sessionEventsReconnectTimerRef,
    sseSessionIdRef,
    lastSseEventAtRef,
    reconnectCountRef,
    startSessionEventStream,
    stopSessionEventStream,
    bindSessionEventConnection,
  };
}

import type { MutableRefObject } from 'react';
import { useCallback, useRef, useState } from 'react';
import { message } from 'antd';
import {
  type AdminChatStreamEvent,
  subscribeSessionEvents,
} from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/perfEventRuntime';
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
  const [reconnectCount, setReconnectCount] = useState(0);

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
    setReconnectCount(0);
    ports.syncSessionIdentity();
  }, [clearSessionEventTimers]);

  const startSessionEventStream = useCallback(
    (sessionId: string) => {
      if (!sessionId) return;
      const ports = portsRef.current;
      const previousStreamSessionId = sseSessionIdRef.current;
      const previousReconnectCount =
        previousStreamSessionId === sessionId ? reconnectCountRef.current : 0;
      stopSessionEventStream();
      reconnectCountRef.current = previousReconnectCount;
      setReconnectCount(previousReconnectCount);
      // A first connection is opened only after history/bootstrap has advanced
      // lastSequenceNumRef. Do not erase that authoritative cursor merely
      // because there was no previous SSE instance. Explicit session switches
      // already reset the cursor before loading the new session.
      if (previousStreamSessionId) {
        ports.resetStreamCursorForSessionChange(
          previousStreamSessionId,
          sessionId,
        );
      }
      sseSessionIdRef.current = sessionId;
      ports.syncSessionIdentity();
      const afterSequence = Math.max(0, ports.lastSequenceNumRef.current);
      recordPerfEvent('chat.sse.start', { sessionId });
      logChatDiag('sse.start', {
        sessionId,
        previousStreamSessionId,
        selectedSessionId: ports.selectedSessionIdRef.current,
        sessionIdRef: ports.sessionIdRef.current,
        lastSequenceNum: afterSequence,
        activeMessageCount: ports.activeMessageIdsRef.current.size,
        turnCount: ports.turnsRef.current.length,
      });

      const controller = new AbortController();
      sessionEventsAbortRef.current = controller;

      const stopForAuthError = (status?: number) => {
        if (status !== 401 && status !== 403) return false;
        if (controller.signal.aborted || sseSessionIdRef.current !== sessionId)
          return true;
        logChatDiag('sse.authRequired', { sessionId, httpStatus: status });
        stopSessionEventStream();
        // Do not dispose the conversation or erase unsent text on an auth failure.
        if (status === 401) localStorage.removeItem('pudding_token');
        message.error(
          status === 401
            ? '登录已失效，请重新登录后继续。'
            : '无权访问此会话，已停止自动重连。',
        );
        return true;
      };
      const errorStatus = (error: unknown): number | undefined => {
        const value = error as {
          status?: number;
          response?: { status?: number };
        } | null;
        return value?.response?.status ?? value?.status;
      };

      const scheduleReconnect = () => {
        if (controller.signal.aborted || sseSessionIdRef.current !== sessionId)
          return;
        if (sessionEventsReconnectTimerRef.current != null) return;
        reconnectCountRef.current += 1;
        setReconnectCount(reconnectCountRef.current);
        recordPerfEvent('chat.sse.reconnectScheduled', {
          sessionId,
          attempt: reconnectCountRef.current,
        });
        sessionEventsReconnectTimerRef.current = window.setTimeout(
          async () => {
            sessionEventsReconnectTimerRef.current = null;
            if (sseSessionIdRef.current !== sessionId) return;
            try {
              await portsRef.current.replayMissedSessionEvents(
                sessionId,
                controller.signal,
              );
            } catch (error) {
              if (stopForAuthError(errorStatus(error))) return;
              // Retry through the next compensation cycle.
            }
            if (
              !controller.signal.aborted &&
              sseSessionIdRef.current === sessionId
            ) {
              startSessionEventStream(sessionId);
            }
          },
          Math.min(
            30_000,
            1200 * 2 ** Math.min(reconnectCountRef.current - 1, 5),
          ),
        );
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
            if (stopForAuthError(errorStatus(error))) {
              shouldContinue = false;
            } else if (isSessionNotFoundError(error)) {
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
            recordPerfEvent('chat.sse.watchdog', {
              sessionId,
              idleMs: Math.round(performance.now() - lastEvent),
            });
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
            if (reconnectCountRef.current !== 0) {
              reconnectCountRef.current = 0;
              setReconnectCount(0);
            }
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
              if (stopForAuthError(httpStatus)) return;
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
            afterSequence,
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
    reconnectCount,
    startSessionEventStream,
    stopSessionEventStream,
    bindSessionEventConnection,
  };
}

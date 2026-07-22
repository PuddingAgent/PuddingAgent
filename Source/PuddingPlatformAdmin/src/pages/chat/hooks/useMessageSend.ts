import type { MessageInstance } from 'antd/es/message/interface';
import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useRef } from 'react';
import {
  createSession,
  ensureMainSession,
  executeConversationSystemCommand,
  submitConversationTurn,
  type WorkspaceAgentDto,
} from '@/services/platform/api';
import {
  markPerf,
  measurePerf,
  recordPerfEvent,
  writeDebugSessionState,
} from '@/utils/debug';
import {
  dequeueCommand,
  enqueueCommand,
  markSending,
} from '../outbox/commandOutbox';
import type { ChatTurn, SessionListItem } from '../types';
import type { ChatSendOptions } from '../types/chatStateTypes';
import { logChatDiag } from '../utils/chatDiagnostics';
import {
  buildAgentMainSessionRequest,
  COMPACT_COMMAND,
  confirmOptimisticTurn,
  createAssistant,
  createId,
  getAgentName,
  hasBlockingActiveTurn,
  stringToColor,
} from '../utils/chatStateUtils';
import type { ScrollIntent } from '../viewport/types';
import { getChatRouteLabel, resolveChatRoute } from './chatRouting';

interface MessageSendIdentityPort {
  workspaceId?: string;
  agentId?: string;
  agents: WorkspaceAgentDto[];
  mainSessionId: string | null;
  routeSessionId?: string;
  sessionIdRef: MutableRefObject<string | undefined>;
  selectedSessionIdRef: MutableRefObject<string | null>;
  mainSessionIdRef: MutableRefObject<string | null>;
  forceNewSessionRef: MutableRefObject<boolean>;
  resetMainSessionEnsureSuppression: (reason: string) => void;
}

interface MessageSendTurnPort {
  loading: boolean;
  setLoading: Dispatch<SetStateAction<boolean>>;
  loadingRef: MutableRefObject<boolean>;
  turnsRef: MutableRefObject<ChatTurn[]>;
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  activeMessageIdsRef: MutableRefObject<Set<string>>;
  messageIdToTurnIdRef: MutableRefObject<Map<string, string>>;
  latestTurnIdRef: MutableRefObject<string | null>;
  completedTurnsRef: MutableRefObject<Set<string>>;
  setViewportScrollIntent: Dispatch<SetStateAction<ScrollIntent>>;
}

interface MessageSendSessionPort {
  setMainSessionId: Dispatch<SetStateAction<string | null>>;
  setSelectedSessionId: Dispatch<SetStateAction<string | null>>;
  setSessions: Dispatch<SetStateAction<SessionListItem[]>>;
  refreshSessions: (options?: { preserveSessionId?: string }) => Promise<void>;
}

interface MessageSendStreamPort {
  startSessionEventStream: (sessionId: string) => void;
  resetStreamCursorForSessionChange: (
    previousSessionId?: string | null,
    nextSessionId?: string | null,
  ) => void;
  replayMissedSessionEvents: (
    sessionId: string,
    signal?: AbortSignal,
  ) => Promise<unknown>;
  replayMissedSessionEventsIfNeeded: (
    sessionId: string,
    options: { reason: string; hasActiveMessages: boolean },
  ) => Promise<unknown>;
  reconcileCompletedSessionMessages: (sessionId: string) => Promise<void>;
  pendingDeltaRef: MutableRefObject<Map<string, string>>;
  pendingThinkingRef: MutableRefObject<Map<string, string>>;
  duplicateDeltaReplayOffsetRef: MutableRefObject<Map<string, number>>;
  streamStartAtRef: MutableRefObject<Map<string, number>>;
  prepareForNewMessage: () => void;
}

interface MessageSendAgentActivityPort {
  setAgentIdsWorking: (
    agentIds: Iterable<string | undefined>,
    isWorking: boolean,
  ) => void;
  messageIdToAgentIdsRef: MutableRefObject<Map<string, string[]>>;
  sessionIdToAgentIdsRef: MutableRefObject<Map<string, string[]>>;
}

interface MessageSendFeedbackPort {
  setError: Dispatch<SetStateAction<string | null>>;
  messageApi: MessageInstance;
  handleCompactCommand: () => Promise<void>;
}

interface UseMessageSendOptions {
  identity: MessageSendIdentityPort;
  turns: MessageSendTurnPort;
  sessions: MessageSendSessionPort;
  stream: MessageSendStreamPort;
  activity: MessageSendAgentActivityPort;
  feedback: MessageSendFeedbackPort;
}

/** Owns the send transaction from optimistic Turn creation through durable acceptance. */
export function useMessageSend({
  identity,
  turns,
  sessions,
  stream,
  activity,
  feedback,
}: UseMessageSendOptions) {
  const {
    workspaceId,
    agentId,
    agents,
    mainSessionId,
    routeSessionId,
    sessionIdRef,
    selectedSessionIdRef,
    mainSessionIdRef,
    forceNewSessionRef,
    resetMainSessionEnsureSuppression,
  } = identity;
  const {
    loading,
    setLoading,
    loadingRef,
    turnsRef,
    setTurns,
    activeMessageIdsRef,
    messageIdToTurnIdRef,
    latestTurnIdRef,
    completedTurnsRef,
    setViewportScrollIntent,
  } = turns;
  const {
    setMainSessionId,
    setSelectedSessionId,
    setSessions,
    refreshSessions,
  } = sessions;
  const {
    startSessionEventStream,
    resetStreamCursorForSessionChange,
    replayMissedSessionEvents,
    replayMissedSessionEventsIfNeeded,
    reconcileCompletedSessionMessages,
    pendingDeltaRef,
    pendingThinkingRef,
    duplicateDeltaReplayOffsetRef,
    streamStartAtRef,
    prepareForNewMessage,
  } = stream;
  const { setAgentIdsWorking, messageIdToAgentIdsRef, sessionIdToAgentIdsRef } =
    activity;
  const { setError, messageApi, handleCompactCommand } = feedback;
  const abortRef = useRef<AbortController | null>(null);

  // T-102: fire-and-forget POST + 持久 SSE 方案。
  const sendMessage = useCallback(
    async (text: string, options?: ChatSendOptions) => {
      if (!text || !workspaceId || !agentId) return;
      const localBusy =
        loadingRef.current ||
        hasBlockingActiveTurn(
          turnsRef.current,
          activeMessageIdsRef.current,
          messageIdToTurnIdRef.current,
        );
      if (localBusy) {
        recordPerfEvent(
          'chat.post.localBusyForwarded',
          {
            loading,
            activeMessageCount: activeMessageIdsRef.current.size,
            turnCount: turnsRef.current.length,
          },
          { throttleMs: 1_000 },
        );
      }
      if (text.trim().toLowerCase() === COMPACT_COMMAND) {
        await handleCompactCommand();
        return;
      }
      if (options?.metadata && Object.keys(options.metadata).length > 0) {
        const unsupportedMessage =
          '当前 Conversation 命令链尚未支持视觉或其他 metadata 输入，消息未发送。';
        setError(unsupportedMessage);
        messageApi.error(unsupportedMessage);
        return;
      }
      const route = resolveChatRoute(text, agents, agentId);
      const routedText = route.messageText.trim();
      if (!routedText || route.targetAgentIds.length === 0) return;
      const isDirectSystemCommand = /^\/yolo$/i.test(routedText);
      resetMainSessionEnsureSuppression('send-message');
      setError(null);
      const perfStart = performance.now();
      const now = Date.now();
      const routeLabel = getChatRouteLabel(route, agents);
      const targetAgentId = route.primaryAgentId ?? agentId;
      const targetAgent = agents.find((item) => item.agentId === targetAgentId);
      const workingTargetAgentIds = isDirectSystemCommand
        ? []
        : Array.from(
            new Set(
              route.targetAgentIds.length > 0
                ? route.targetAgentIds
                : [targetAgentId],
            ),
          );

      let sendConversationId =
        sessionIdRef.current ??
        selectedSessionIdRef.current ??
        mainSessionIdRef.current;
      if (forceNewSessionRef.current && !isDirectSystemCommand) {
        const created = await createSession(
          workspaceId,
          targetAgent?.sourceTemplateId || `global:${targetAgentId}`,
          undefined,
          targetAgent ? getAgentName(targetAgent) : targetAgentId,
        );
        sendConversationId = created.sessionId;
      } else if (!sendConversationId) {
        const ensureRequest = buildAgentMainSessionRequest(
          workspaceId,
          targetAgent,
        );
        if (!ensureRequest) throw new Error('无法解析 Agent 主会话');
        const ensured = await ensureMainSession(ensureRequest);
        sendConversationId = ensured.sessionId;
        setMainSessionId(ensured.sessionId);
      }

      sessionIdRef.current = sendConversationId;
      setSelectedSessionId(sendConversationId);
      startSessionEventStream(sendConversationId);

      const turnId = createId();
      if (isDirectSystemCommand) {
        const clientRequestId = createId();
        const clientMessageId = createId();
        const responseMessageId = createId();
        const systemTurn: ChatTurn = {
          turnId: clientRequestId,
          source: {
            sourceId: 'system',
            sourceType: 'system_command',
            displayName: 'System',
            avatarEmoji: '⚙',
            avatarColor: stringToColor('system'),
          },
          userMessage: {
            id: clientMessageId,
            text: route.originalText,
            timestamp: now,
            status: 'sending',
          },
          assistant: createAssistant(
            responseMessageId,
            'legacy',
            'thinking',
            true,
          ),
        };
        turnsRef.current = [...turnsRef.current, systemTurn];
        setTurns((current) => [...current, systemTurn]);
        setViewportScrollIntent({
          type: 'user-send',
          itemId: `message:user:${clientMessageId}`,
          createdAt: now,
        });

        const updateSystemTurn = (
          status: 'success' | 'error',
          answerMarkdown: string,
        ) => {
          const apply = (current: ChatTurn[]): ChatTurn[] =>
            current.map((turn) =>
              turn.turnId === clientRequestId
                ? {
                    ...turn,
                    userMessage: {
                      ...turn.userMessage,
                      status:
                        status === 'success'
                          ? ('success' as const)
                          : ('error' as const),
                    },
                    assistant: {
                      ...turn.assistant,
                      status,
                      isStreaming: false,
                      answerMarkdown,
                    },
                  }
                : turn,
            );
          turnsRef.current = apply(turnsRef.current);
          setTurns(apply);
        };

        try {
          const result = await executeConversationSystemCommand(
            workspaceId,
            sendConversationId,
            {
              agentId: targetAgentId,
              clientRequestId,
              clientMessageId,
              responseMessageId,
              commandText: routedText,
            },
          );
          updateSystemTurn('success', result.message);
          forceNewSessionRef.current = false;
        } catch (error) {
          const errorMessage =
            error instanceof Error ? error.message : '系统指令执行失败';
          updateSystemTurn('error', errorMessage);
          setError(errorMessage);
          messageApi.error(errorMessage);
        }
        return;
      }

      markPerf(`chat.post.${turnId}.start`);
      // ADR-058: Generate stable idempotency keys at send initiation.
      // - clientRequestId: reused across retries; same send action = same ID.
      // - clientMessageId: stable user message ID = userMessage.id.
      const clientRequestId = createId();
      const clientMessageId = createId();
      const optimisticTurn: ChatTurn = {
        turnId,
        source: {
          sourceId: targetAgentId,
          sourceType: 'agent',
          displayName:
            route.audience === 'all'
              ? 'all'
              : targetAgent
                ? getAgentName(targetAgent)
                : targetAgentId,
          avatarEmoji: '🤖',
          avatarColor: stringToColor(targetAgentId),
          avatarUrl: targetAgent?.avatarUrl,
        },
        userMessage: {
          id: clientMessageId,
          text: route.originalText,
          timestamp: now,
          status: 'sending',
        },
        assistant: createAssistant(createId(), 'structured', 'thinking', true),
      };
      turnsRef.current = [...turnsRef.current, optimisticTurn];
      setTurns((p) => [...p, optimisticTurn]);
      setViewportScrollIntent({
        type: 'user-send',
        itemId: `message:user:${optimisticTurn.userMessage.id}`,
        createdAt: now,
      });
      const ctrl = new AbortController();
      abortRef.current = ctrl;
      setLoading(true);
      setAgentIdsWorking(workingTargetAgentIds, true);

      // 注册当前 turn 为最新，供持久 SSE 事件路由
      latestTurnIdRef.current = turnId;
      const previousSessionId = sendConversationId;
      logChatDiag('post.optimistic.appended', {
        turnId,
        previousSessionId,
        selectedSessionId: selectedSessionIdRef.current,
        sessionIdRef: sessionIdRef.current,
        mainSessionId,
        routeSessionId: routeSessionId,
        targetAgentId,
        forceNewSession: forceNewSessionRef.current,
        turnCountAfterAppend: turnsRef.current.length,
        text: route.originalText,
      });

      try {
        // Persist before the HTTP request, but only after the optimistic state is
        // synchronously attached to the conversation that initiated the send.
        // This closes the selection race where IndexedDB yielded and the turn was
        // appended to whichever conversation the user selected in the meantime.
        await enqueueCommand({
          clientRequestId,
          clientMessageId,
          workspaceId,
          conversationId: sendConversationId,
          messageText: routedText,
          agentIds:
            route.targetAgentIds.length > 0
              ? route.targetAgentIds
              : [targetAgentId],
        });
        await markSending(clientRequestId);
        // T-102: 非流式 POST — 202 Accepted + { success, status, commandId, messageId, turnId, sessionId, eventCursor }
        // ADR-056: turnId 由后端分配（非前端生成），确保事件系统一致。
        const acceptance = await submitConversationTurn(
          workspaceId,
          sendConversationId,
          {
            clientRequestId,
            clientMessageId,
            recipients: {
              type: 'agent',
              agentIds:
                route.targetAgentIds.length > 0
                  ? route.targetAgentIds
                  : [targetAgentId],
            },
            content: [{ type: 'text', text: routedText }],
          },
          ctrl.signal,
        );
        const messageId = acceptance.messageId;
        const returnedSessionId = acceptance.conversationId;
        const serverTurnId = acceptance.turnIds[0];
        await dequeueCommand(clientRequestId);

        const stillViewingSendSession =
          (sessionIdRef.current ?? null) === previousSessionId;
        const effectiveTurnId = serverTurnId ?? turnId;
        logChatDiag('post.returned.beforeApply', {
          turnId: effectiveTurnId,
          serverTurnId,
          messageId,
          returnedSessionId,
          previousSessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          stillViewingSendSession,
          activeMessageCount: activeMessageIdsRef.current.size,
        });

        // The POST acknowledgement confirms the durable Turn identity. Migrate
        // every turn-keyed frontend structure before restarting replay/SSE so a
        // fast terminal event cannot target a stale optimistic ID.
        if (stillViewingSendSession) {
          resetStreamCursorForSessionChange(
            previousSessionId,
            returnedSessionId,
          );
          sessionIdRef.current = returnedSessionId;
          setSelectedSessionId(returnedSessionId);

          const confirmedTurns = confirmOptimisticTurn(
            turnsRef.current,
            turnId,
            effectiveTurnId,
            messageId,
          );
          turnsRef.current = confirmedTurns;
          setTurns((current) =>
            confirmOptimisticTurn(current, turnId, effectiveTurnId, messageId),
          );
          if (latestTurnIdRef.current === turnId)
            latestTurnIdRef.current = effectiveTurnId;

          const migrateTurnKey = <T>(map: Map<string, T>) => {
            if (turnId === effectiveTurnId || !map.has(turnId)) return;
            const value = map.get(turnId) as T;
            map.delete(turnId);
            map.set(effectiveTurnId, value);
          };
          migrateTurnKey(pendingDeltaRef.current);
          migrateTurnKey(pendingThinkingRef.current);
          migrateTurnKey(duplicateDeltaReplayOffsetRef.current);
          if (completedTurnsRef.current.delete(turnId))
            completedTurnsRef.current.add(effectiveTurnId);
        }
        forceNewSessionRef.current = false;
        messageIdToTurnIdRef.current.set(messageId, effectiveTurnId);
        messageIdToAgentIdsRef.current.set(messageId, workingTargetAgentIds);
        const previousSessionAgentIds =
          sessionIdToAgentIdsRef.current.get(returnedSessionId) ?? [];
        sessionIdToAgentIdsRef.current.set(
          returnedSessionId,
          Array.from(
            new Set([...previousSessionAgentIds, ...workingTargetAgentIds]),
          ),
        );
        activeMessageIdsRef.current.add(messageId);
        if (stillViewingSendSession) {
          startSessionEventStream(returnedSessionId);
        }

        // 埋点
        console.debug('[Pudding Chat] post returned', {
          turnId: effectiveTurnId,
          serverTurnId,
          messageId,
          sessionId: returnedSessionId,
          stillViewingSendSession,
          previousSessionId,
          activeMessageCount: activeMessageIdsRef.current.size,
        });
        logChatDiag('post.returned.afterApply', {
          turnId: effectiveTurnId,
          serverTurnId,
          messageId,
          returnedSessionId,
          previousSessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          stillViewingSendSession,
          activeMessageCount: activeMessageIdsRef.current.size,
          messageIdToTurnId: Object.fromEntries(messageIdToTurnIdRef.current),
        });

        // ADR: 首条消息隐式会话的前端物化 — 将返回的 sessionId 同步到左侧 sessions 列表
        const optimisticTitle =
          route.audience === 'all'
            ? `all · ${routedText.slice(0, 24).trim() || '群聊'}`
            : routeLabel || routedText.slice(0, 30).trim() || '对话';
        setSessions((prev) => {
          const idx = prev.findIndex((s) => s.sessionId === returnedSessionId);
          if (idx >= 0) {
            const updated = { ...prev[idx], timestamp: now };
            if (!prev[idx].title || prev[idx].title === '对话')
              updated.title = optimisticTitle;
            return [updated, ...prev.slice(0, idx), ...prev.slice(idx + 1)];
          }
          return [
            {
              sessionId: returnedSessionId,
              title: optimisticTitle,
              timestamp: now,
            },
            ...prev,
          ];
        });

        // ADR-026: debug mode 写入 sessionId/messageId
        writeDebugSessionState(returnedSessionId, messageId);
        streamStartAtRef.current.set(messageId, perfStart);
        prepareForNewMessage();

        const ttfm = (performance.now() - perfStart).toFixed(0);
        markPerf(`chat.post.${turnId}.returned`);
        measurePerf(
          'chat.post.roundtrip',
          `chat.post.${turnId}.start`,
          `chat.post.${turnId}.returned`,
        );
        recordPerfEvent('chat.post.returned', {
          elapsedMs: Number(ttfm),
          messageId,
          sessionId: returnedSessionId,
          workspaceId,
          agentId: route.primaryAgentId,
          audience: route.audience,
          targetAgentCount: route.targetAgentIds.length,
          localBusyAtSubmit: localBusy,
        });

        // 主动拉取已生成的事件（fire-and-forget，不与 SSE 串行等待）
        if (stillViewingSendSession) {
          void replayMissedSessionEvents(returnedSessionId, ctrl.signal).catch(
            () => {
              /* SSE 会重连补偿 */
            },
          );
        }
        const replayAndReconcile = (delayMs: number) => {
          window.setTimeout(() => {
            if (sessionIdRef.current !== returnedSessionId) {
              logChatDiag('post.reconcileTimer.skipped.sessionChanged', {
                delayMs,
                returnedSessionId,
                sessionIdRef: sessionIdRef.current,
                selectedSessionId: selectedSessionIdRef.current,
              });
              return;
            }
            logChatDiag('post.reconcileTimer.fire', {
              delayMs,
              returnedSessionId,
              selectedSessionId: selectedSessionIdRef.current,
              sessionIdRef: sessionIdRef.current,
              activeMessageCount: activeMessageIdsRef.current.size,
              turnCount: turnsRef.current.length,
            });
            void (async () => {
              try {
                await replayMissedSessionEventsIfNeeded(returnedSessionId, {
                  reason: `reconcile-${delayMs}`,
                  hasActiveMessages: true,
                });
              } catch {
                /* 下次补偿 */
              }
              try {
                await reconcileCompletedSessionMessages(returnedSessionId);
              } catch {
                /* 后续轮询/刷新补偿 */
              }
            })();
          }, delayMs);
        };
        window.setTimeout(() => {
          if (sessionIdRef.current === returnedSessionId) {
            logChatDiag('post.replayTimer.fire', {
              delayMs: 800,
              returnedSessionId,
              selectedSessionId: selectedSessionIdRef.current,
              sessionIdRef: sessionIdRef.current,
            });
            void replayMissedSessionEventsIfNeeded(returnedSessionId, {
              reason: 'post-800',
              hasActiveMessages: true,
            });
          } else {
            logChatDiag('post.replayTimer.skipped.sessionChanged', {
              delayMs: 800,
              returnedSessionId,
              selectedSessionId: selectedSessionIdRef.current,
              sessionIdRef: sessionIdRef.current,
            });
          }
        }, 800);
        replayAndReconcile(2000);
        replayAndReconcile(5000);

        // ADR: 后台刷新会话列表（merge 模式保留乐观项不被覆盖）
        refreshSessions({ preserveSessionId: returnedSessionId });
      } catch (e: unknown) {
        logChatDiag('post.error', {
          turnId,
          previousSessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          error: e,
        });
        setAgentIdsWorking(workingTargetAgentIds, false);
        if (e instanceof Error && e.name === 'AbortError') {
          setTurns((p) =>
            p.map((t) =>
              t.turnId === turnId
                ? {
                    ...t,
                    assistant: {
                      ...t.assistant,
                      status: 'cancelled' as const,
                      isStreaming: false,
                    },
                  }
                : t,
            ),
          );
        } else {
          setError(e instanceof Error ? e.message : '请求失败');
          setTurns((p) =>
            p.map((t) =>
              t.turnId === turnId
                ? {
                    ...t,
                    assistant: {
                      ...t.assistant,
                      status: 'error' as const,
                      isStreaming: false,
                    },
                  }
                : t,
            ),
          );
          // ADR: POST 已成功，会话由后端持久化，不做前端回滚
        }
        setLoading(false);
      } finally {
        if (abortRef.current === ctrl) abortRef.current = null;
        // T-102: loading 由持久 SSE 的 done/error/cancelled 事件管理，不在此处关闭
      }
    },
    [
      workspaceId,
      agentId,
      agents,
      replayMissedSessionEvents,
      replayMissedSessionEventsIfNeeded,
      refreshSessions,
      handleCompactCommand,
      startSessionEventStream,
      reconcileCompletedSessionMessages,
      resetStreamCursorForSessionChange,
      setAgentIdsWorking,
      mainSessionId,
      resetMainSessionEnsureSuppression,
      routeSessionId,
      messageApi,
      prepareForNewMessage,
    ],
  );

  return { abortRef, sendMessage };
}

import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback } from 'react';
import {
  listSessionMessages,
  type MessageListResponse,
} from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/debug';
import type { ChatTurn, TimelineItem } from '../types';
import { MESSAGE_PAGE_SIZE } from '../types/chatStateTypes';
import { logChatDiag } from '../utils/chatDiagnostics';
import {
  createAssistant,
  getHistoryReconcileBlockReason,
  normalizeUsage,
  shouldReplayEventsAfterHistory,
  stringToColor,
} from '../utils/chatStateUtils';

interface SessionHistoryIdentityPort {
  selectedSessionIdRef: MutableRefObject<string | null>;
  sessionIdRef: MutableRefObject<string | undefined>;
}

interface SessionHistoryTurnPort {
  turnsRef: MutableRefObject<ChatTurn[]>;
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  latestTurnIdRef: MutableRefObject<string | null>;
  setLoading: Dispatch<SetStateAction<boolean>>;
}

interface SessionHistoryIntegrationPort {
  mergeCompactionLifecycleTurns: (turns: ChatTurn[]) => ChatTurn[];
  syncCompletedHistoryEventCursor: (sessionId: string) => Promise<void>;
  bindHistoryProjector: (
    projector: (response: MessageListResponse) => ChatTurn[],
  ) => void;
}

interface UseSessionHistoryProjectionOptions {
  identity: SessionHistoryIdentityPort;
  turns: SessionHistoryTurnPort;
  integrations: SessionHistoryIntegrationPort;
}

/** Owns persisted message projection and safe completed-history reconciliation. */
export function useSessionHistoryProjection({
  identity,
  turns,
  integrations,
}: UseSessionHistoryProjectionOptions) {
  const { selectedSessionIdRef, sessionIdRef } = identity;
  const { turnsRef, setTurns, latestTurnIdRef, setLoading } = turns;
  const {
    mergeCompactionLifecycleTurns,
    syncCompletedHistoryEventCursor,
    bindHistoryProjector,
  } = integrations;

  // ── toTurnsFromHistory ─────────────────────────────────────
  const toTurnsFromHistory = useCallback(
    (res: MessageListResponse): ChatTurn[] => {
      const mapped: ChatTurn[] = [];
      let pendingUserIndex: number | null = null;
      for (const item of res.items || []) {
        if (item.role === 'user') {
          mapped.push({
            turnId: item.turnId || `hist-turn-${item.id}`,
            userMessage: {
              id: item.messageId || `hist-user-${item.id}`,
              text: item.content,
              timestamp: item.createdAt,
              status: 'success',
              dbMessageId: item.id,
            },
            assistant: createAssistant(
              `hist-assistant-${item.id}`,
              'legacy',
              'success',
              false,
            ),
          });
          pendingUserIndex = mapped.length - 1;
          continue;
        }
        const thinkingItems: TimelineItem[] = (item.thinking || []).map(
          (t: { text: string }, idx: number) => ({
            id: `hist-think-${item.id}-${idx}`,
            type: 'thinking' as const,
            text: t.text,
            timestamp: item.createdAt,
            collapsed: true,
          }),
        );
        let targetIndex = pendingUserIndex;
        if (targetIndex === null) {
          mapped.push({
            turnId: `hist-turn-orphan-${item.id}`,
            userMessage: {
              id: `hist-user-orphan-${item.id}`,
              text: '',
              timestamp: item.createdAt,
              status: 'success',
            },
            assistant: createAssistant(
              `hist-assistant-orphan-${item.id}`,
              'legacy',
              'success',
              false,
            ),
          });
          targetIndex = mapped.length - 1;
        }
        mapped[targetIndex] = {
          ...mapped[targetIndex],
          turnId: item.turnId || mapped[targetIndex].turnId,
          source:
            item.sourceType === 'system_command'
              ? {
                  sourceId: item.sourceId || 'system',
                  sourceType: 'system_command',
                  displayName: item.sourceName || 'System',
                  avatarEmoji: '⚙',
                  avatarColor: stringToColor(item.sourceId || 'system'),
                }
              : mapped[targetIndex].source,
          assistant: {
            ...mapped[targetIndex].assistant,
            id: item.messageId || mapped[targetIndex].assistant.id,
            status: 'success',
            isStreaming: false,
            usage: normalizeUsage(item.usage),
            answerMarkdown: item.content,
            timelineItems: thinkingItems,
            renderMode: thinkingItems.length > 0 ? 'structured' : 'legacy',
          },
        };
        pendingUserIndex = null;
      }
      return mapped;
    },
    [],
  );
  bindHistoryProjector(toTurnsFromHistory);

  const reconcileCompletedSessionMessages = useCallback(
    async (sessionId: string) => {
      const res = await listSessionMessages(
        sessionId,
        undefined,
        MESSAGE_PAGE_SIZE,
      );
      const loadedTurns = toTurnsFromHistory(res);
      if (
        loadedTurns.length === 0 ||
        shouldReplayEventsAfterHistory(loadedTurns)
      )
        return;
      const blockReason = getHistoryReconcileBlockReason(
        turnsRef.current,
        loadedTurns,
      );
      if (blockReason) {
        recordPerfEvent('chat.history.reconcile.skipped', {
          reason: blockReason,
          sessionId,
          currentTurns: turnsRef.current.length,
          loadedTurns: loadedTurns.length,
          currentLatestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          loadedLatestUser:
            loadedTurns[loadedTurns.length - 1]?.userMessage.text,
        });
        // 埋点：对账被阻止，记录原因（消息被吞的关键诊断点）
        console.debug('[Pudding Chat] reconcile skipped', {
          reason: blockReason,
          sessionId,
          currentTurns: turnsRef.current.length,
          loadedTurns: loadedTurns.length,
          currentLatestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          loadedLatestUser:
            loadedTurns[loadedTurns.length - 1]?.userMessage.text,
        });
        logChatDiag('history.reconcile.skipped', {
          reason: blockReason,
          sessionId,
          selectedSessionId: selectedSessionIdRef.current,
          sessionIdRef: sessionIdRef.current,
          currentTurns: turnsRef.current.length,
          loadedTurns: loadedTurns.length,
          currentLatestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          loadedLatestUser:
            loadedTurns[loadedTurns.length - 1]?.userMessage.text,
        });
        return;
      }

      // RC-4: MERGE mode — preserve in-flight / just-completed turns that
      // the server hasn't persisted yet. Without this, SSE-delivered "done"
      // turns disappear when reconcile runs before the server persists them.
      //
      // Strategy:
      // 1. Build a lookup map from loadedTurns keyed by turnId
      // 2. For each current turn: keep it (upgrade with loaded version when
      //    available); never drop — even non-terminal turns may be in-flight
      // 3. Append any loaded turns not yet in the merged set
      // 4. Sort by timestamp to maintain chronological order
      const loadedMap = new Map(loadedTurns.map((t) => [t.turnId, t]));
      const merged: ChatTurn[] = [];
      const seen = new Set<string>();

      for (const turn of turnsRef.current) {
        const loaded = loadedMap.get(turn.turnId);
        if (loaded) {
          merged.push(loaded);
          seen.add(loaded.turnId);
        } else {
          merged.push(turn);
          seen.add(turn.turnId);
        }
      }

      for (const turn of loadedTurns) {
        if (!seen.has(turn.turnId)) {
          merged.push(turn);
        }
      }

      merged.sort((a, b) => a.userMessage.timestamp - b.userMessage.timestamp);

      // 埋点：对账合并 turns（RC-4 merge mode）
      console.debug('[Pudding Chat] reconcile MERGED turns', {
        sessionId,
        before: {
          count: turnsRef.current.length,
          latestUser:
            turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
          latestStatus:
            turnsRef.current[turnsRef.current.length - 1]?.assistant.status,
        },
        after: {
          count: merged.length,
          latestUser: merged[merged.length - 1]?.userMessage.text,
          latestStatus: merged[merged.length - 1]?.assistant.status,
        },
      });
      logChatDiag('history.reconcile.merged', {
        sessionId,
        selectedSessionId: selectedSessionIdRef.current,
        sessionIdRef: sessionIdRef.current,
        beforeCount: turnsRef.current.length,
        afterCount: merged.length,
        beforeLatestUser:
          turnsRef.current[turnsRef.current.length - 1]?.userMessage.text,
        afterLatestUser: merged[merged.length - 1]?.userMessage.text,
        beforeLatestStatus:
          turnsRef.current[turnsRef.current.length - 1]?.assistant.status,
        afterLatestStatus: merged[merged.length - 1]?.assistant.status,
      });

      const reconciledTurns = mergeCompactionLifecycleTurns(merged);
      setTurns(reconciledTurns);
      turnsRef.current = reconciledTurns;
      latestTurnIdRef.current =
        reconciledTurns[reconciledTurns.length - 1]?.turnId ?? null;
      setLoading(false);
      await syncCompletedHistoryEventCursor(sessionId);
    },
    [
      mergeCompactionLifecycleTurns,
      syncCompletedHistoryEventCursor,
      toTurnsFromHistory,
    ],
  );

  return { toTurnsFromHistory, reconcileCompletedSessionMessages };
}

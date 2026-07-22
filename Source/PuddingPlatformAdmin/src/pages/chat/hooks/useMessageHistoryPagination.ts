import type { MessageInstance } from 'antd/es/message/interface';
import type { Dispatch, MutableRefObject, SetStateAction } from 'react';
import { useCallback, useRef, useState } from 'react';
import {
  listSessionMessages,
  type MessageListResponse,
} from '@/services/platform/api';
import type { ChatTurn } from '../types';
import { MESSAGE_PAGE_SIZE } from '../types/chatStateTypes';

type HistoryProjector = (response: MessageListResponse) => ChatTurn[];

interface UseMessageHistoryPaginationOptions {
  selectedSessionId: string | null;
  turnsRef: MutableRefObject<ChatTurn[]>;
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  messageApi: MessageInstance;
}

/** Owns history-page cursors and the prepend command. */
export function useMessageHistoryPagination({
  selectedSessionId,
  turnsRef,
  setTurns,
  messageApi,
}: UseMessageHistoryPaginationOptions) {
  const [historyLoading, setHistoryLoading] = useState(false);
  const [hasMoreMessages, setHasMoreMessages] = useState(false);
  const [oldestMessageCursor, setOldestMessageCursor] = useState<number | null>(
    null,
  );
  const [loadingMore, setLoadingMore] = useState(false);
  const historyProjectorRef = useRef<HistoryProjector>(() => []);

  const bindHistoryProjector = useCallback((projector: HistoryProjector) => {
    historyProjectorRef.current = projector;
  }, []);

  const resetHistoryPagination = useCallback(() => {
    setHistoryLoading(false);
    setHasMoreMessages(false);
    setOldestMessageCursor(null);
    setLoadingMore(false);
  }, []);

  const loadMoreMessages = useCallback(async () => {
    if (
      !selectedSessionId ||
      !hasMoreMessages ||
      loadingMore ||
      oldestMessageCursor == null
    ) {
      return;
    }
    setLoadingMore(true);
    try {
      const response = await listSessionMessages(
        selectedSessionId,
        oldestMessageCursor,
        MESSAGE_PAGE_SIZE,
      );
      const olderTurns = historyProjectorRef.current(response);
      const existingIds = new Set(turnsRef.current.map((turn) => turn.turnId));
      const uniqueOlderTurns = olderTurns.filter(
        (turn) => !existingIds.has(turn.turnId),
      );
      const nextTurns = [...uniqueOlderTurns, ...turnsRef.current];
      turnsRef.current = nextTurns;
      setTurns(nextTurns);
      setHasMoreMessages(response.hasMore);
      if (response.oldestCreatedAt != null) {
        setOldestMessageCursor(response.oldestCreatedAt);
      }
    } catch {
      messageApi.error('加载更早消息失败');
    } finally {
      setLoadingMore(false);
    }
  }, [
    hasMoreMessages,
    loadingMore,
    messageApi,
    oldestMessageCursor,
    selectedSessionId,
    setTurns,
    turnsRef,
  ]);

  return {
    historyLoading,
    setHistoryLoading,
    hasMoreMessages,
    setHasMoreMessages,
    oldestMessageCursor,
    setOldestMessageCursor,
    loadingMore,
    loadMoreMessages,
    resetHistoryPagination,
    bindHistoryProjector,
  };
}

import type { MessageInstance } from 'antd/es/message/interface';
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  archiveSession,
  deleteSession,
  listSessions,
  renameSession,
  type SessionRecord,
} from '@/services/platform/api';
import type { SessionListItem } from '../types';
import { toSessionListItem } from '../utils/chatStateUtils';

type SessionNotFoundHandler = (sessionId: string, reason: string) => void;

interface UseSessionCatalogOptions {
  workspaceId?: string;
  renameSessionId: string | null;
  renameTitle: string;
  closeRenameModal: () => void;
  messageApi: MessageInstance;
}

/** Owns session catalog state, identity refs, and list-level CRUD. */
export function useSessionCatalog({
  workspaceId,
  renameSessionId,
  renameTitle,
  closeRenameModal,
  messageApi,
}: UseSessionCatalogOptions) {
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [sessions, setSessions] = useState<SessionListItem[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(
    null,
  );
  const [sessionsLoading] = useState(false);
  const [mainSessionId, setMainSessionId] = useState<string | null>(null);
  const sessionIdRef = useRef<string | undefined>(undefined);
  const selectedSessionIdRef = useRef<string | null>(null);
  const mainSessionIdRef = useRef<string | null>(null);
  const forceNewSessionRef = useRef(false);
  const sessionNotFoundHandlerRef = useRef<SessionNotFoundHandler>(() => {});

  useEffect(() => {
    selectedSessionIdRef.current = selectedSessionId;
  }, [selectedSessionId]);

  useEffect(() => {
    mainSessionIdRef.current = mainSessionId;
  }, [mainSessionId]);

  const bindSessionNotFoundHandler = useCallback(
    (handler: SessionNotFoundHandler) => {
      sessionNotFoundHandlerRef.current = handler;
    },
    [],
  );

  const refreshSessions = useCallback(
    async (options?: { preserveSessionId?: string }) => {
      if (!workspaceId) return;
      try {
        const list = await listSessions(workspaceId);
        const serverMapped = (list || [])
          .filter((session: SessionRecord) => session.status !== 'Frozen')
          .map((session: SessionRecord) => toSessionListItem(session))
          .sort((left, right) => right.timestamp - left.timestamp);

        setSessions((previous) => {
          if (
            options?.preserveSessionId &&
            !serverMapped.some(
              (session) => session.sessionId === options.preserveSessionId,
            )
          ) {
            const localItem = previous.find(
              (session) => session.sessionId === options.preserveSessionId,
            );
            if (localItem) return [localItem, ...serverMapped];
          }
          return serverMapped;
        });
      } catch {
        // Keep the current catalog when refresh fails.
      }
    },
    [workspaceId],
  );

  useEffect(() => {
    void refreshSessions();
  }, [refreshSessions]);

  const handleDeleteSession = useCallback(
    async (sessionId: string) => {
      try {
        await deleteSession(sessionId);
        messageApi.success('会话已删除');
        sessionNotFoundHandlerRef.current(sessionId, 'delete');
      } catch {
        messageApi.error('删除失败');
      }
    },
    [messageApi],
  );

  const handleArchiveSession = useCallback(
    async (sessionId: string) => {
      try {
        await archiveSession(sessionId);
        messageApi.success('会话已归档');
        sessionNotFoundHandlerRef.current(sessionId, 'archive');
      } catch {
        messageApi.error('归档失败');
      }
    },
    [messageApi],
  );

  const handleRenameSubmit = useCallback(async () => {
    const trimmed = renameTitle.trim();
    if (!renameSessionId || !trimmed) return;
    try {
      await renameSession(renameSessionId, trimmed);
      messageApi.success('重命名成功');
      setSessions((previous) =>
        previous.map((session) =>
          session.sessionId === renameSessionId
            ? { ...session, title: trimmed }
            : session,
        ),
      );
      closeRenameModal();
    } catch {
      messageApi.error('重命名失败');
    }
  }, [closeRenameModal, messageApi, renameSessionId, renameTitle]);

  return {
    sidebarOpen,
    setSidebarOpen,
    sessions,
    setSessions,
    selectedSessionId,
    setSelectedSessionId,
    sessionsLoading,
    mainSessionId,
    setMainSessionId,
    sessionIdRef,
    selectedSessionIdRef,
    mainSessionIdRef,
    forceNewSessionRef,
    refreshSessions,
    handleDeleteSession,
    handleArchiveSession,
    handleRenameSubmit,
    bindSessionNotFoundHandler,
  };
}

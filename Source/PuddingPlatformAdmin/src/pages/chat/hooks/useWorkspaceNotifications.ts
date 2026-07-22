import { notification } from 'antd';
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  subscribeWorkspaceNotifications,
  type WorkspaceNotification,
} from '@/services/platform/api';

interface UseWorkspaceNotificationsOptions {
  onSessionCreated: () => void;
}

/** Owns the page-level workspace notification stream and unread projection. */
export function useWorkspaceNotifications({
  onSessionCreated,
}: UseWorkspaceNotificationsOptions) {
  const [sessionUnreadCounts, setSessionUnreadCounts] = useState<
    Record<string, number>
  >({});
  const workspaceNotifyAbortRef = useRef<AbortController | null>(null);
  const workspaceNotifyReconnectRef = useRef<number | null>(null);
  const workspaceNotifyWorkspaceIdRef = useRef<string | null>(null);

  const clearSessionUnread = useCallback((sessionId: string) => {
    setSessionUnreadCounts((previous) => {
      if (!previous[sessionId]) return previous;
      const next = { ...previous };
      delete next[sessionId];
      return next;
    });
  }, []);

  const stopWorkspaceNotificationStream = useCallback(() => {
    if (workspaceNotifyReconnectRef.current != null) {
      window.clearTimeout(workspaceNotifyReconnectRef.current);
      workspaceNotifyReconnectRef.current = null;
    }
    workspaceNotifyAbortRef.current?.abort();
    workspaceNotifyAbortRef.current = null;
    workspaceNotifyWorkspaceIdRef.current = null;
  }, []);

  const startWorkspaceNotificationStream = useCallback(
    (workspaceId: string) => {
      if (!workspaceId) return;
      stopWorkspaceNotificationStream();
      workspaceNotifyWorkspaceIdRef.current = workspaceId;

      const controller = new AbortController();
      workspaceNotifyAbortRef.current = controller;

      subscribeWorkspaceNotifications(
        workspaceId,
        (event: WorkspaceNotification) => {
          if (
            controller.signal.aborted ||
            workspaceNotifyWorkspaceIdRef.current !== workspaceId
          ) {
            return;
          }

          if (event.type === 'notification.sub_agent_completed') {
            setSessionUnreadCounts((previous) => ({
              ...previous,
              [event.sessionId]: (previous[event.sessionId] || 0) + 1,
            }));
            notification.info({
              message: '子代理完成',
              description: event.sessionTitle
                ? `会话「${event.sessionTitle}」的子代理任务已完成`
                : `会话 ${event.sessionId} 的子代理任务已完成`,
              placement: 'bottomRight',
              duration: 4,
            });
          }

          if (event.type === 'notification.session_created') {
            onSessionCreated();
          }
        },
        controller.signal,
      );
    },
    [onSessionCreated, stopWorkspaceNotificationStream],
  );

  useEffect(
    () => () => {
      stopWorkspaceNotificationStream();
    },
    [stopWorkspaceNotificationStream],
  );

  return {
    sessionUnreadCounts,
    startWorkspaceNotificationStream,
    stopWorkspaceNotificationStream,
    clearSessionUnread,
  };
}

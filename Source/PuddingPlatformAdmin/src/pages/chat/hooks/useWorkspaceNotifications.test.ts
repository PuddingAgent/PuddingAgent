import { act, renderHook } from '@testing-library/react';
import { notification } from 'antd';
import { subscribeWorkspaceNotifications } from '@/services/platform/api';
import { useWorkspaceNotifications } from './useWorkspaceNotifications';

interface WorkspaceNotificationFixture {
  type: string;
  sessionId: string;
  workspaceId: string;
  timestamp: string;
}

jest.mock('@/services/platform/api', () => ({
  subscribeWorkspaceNotifications: jest.fn(),
}));

jest.mock('antd', () => ({
  notification: { info: jest.fn() },
}));

const workspaceEvent = (
  type: string,
  sessionId = 'session-1',
): WorkspaceNotificationFixture => ({
  type,
  sessionId,
  workspaceId: 'default',
  timestamp: '2026-07-21T00:00:00Z',
});

describe('useWorkspaceNotifications', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('projects completion notifications into unread counts', () => {
    const onSessionCreated = jest.fn();
    const { result } = renderHook(() =>
      useWorkspaceNotifications({ onSessionCreated }),
    );

    act(() => result.current.startWorkspaceNotificationStream('default'));
    const onNotification = (subscribeWorkspaceNotifications as jest.Mock).mock
      .calls[0][1] as (event: WorkspaceNotificationFixture) => void;

    act(() => {
      onNotification(workspaceEvent('notification.sub_agent_completed'));
      onNotification(workspaceEvent('notification.sub_agent_completed'));
    });

    expect(result.current.sessionUnreadCounts).toEqual({ 'session-1': 2 });
    expect(notification.info).toHaveBeenCalledTimes(2);

    act(() => result.current.clearSessionUnread('session-1'));
    expect(result.current.sessionUnreadCounts).toEqual({});
  });

  it('refreshes sessions and ignores events from a stopped stream', () => {
    const onSessionCreated = jest.fn();
    const { result } = renderHook(() =>
      useWorkspaceNotifications({ onSessionCreated }),
    );

    act(() => result.current.startWorkspaceNotificationStream('default'));
    const call = (subscribeWorkspaceNotifications as jest.Mock).mock.calls[0];
    const onNotification = call[1] as (
      event: WorkspaceNotificationFixture,
    ) => void;
    const signal = call[2] as AbortSignal;

    act(() => onNotification(workspaceEvent('notification.session_created')));
    expect(onSessionCreated).toHaveBeenCalledTimes(1);

    act(() => result.current.stopWorkspaceNotificationStream());
    expect(signal.aborted).toBe(true);

    act(() => onNotification(workspaceEvent('notification.session_created')));
    expect(onSessionCreated).toHaveBeenCalledTimes(1);
  });
});

import { act, renderHook, waitFor } from '@testing-library/react';
import {
  archiveSession,
  deleteSession,
  listSessions,
  renameSession,
} from '@/services/platform/api';
import { useSessionCatalog } from './useSessionCatalog';

jest.mock('@/services/platform/api', () => ({
  archiveSession: jest.fn(),
  deleteSession: jest.fn(),
  listSessions: jest.fn(),
  renameSession: jest.fn(),
}));

const messageApi = {
  error: jest.fn(),
  success: jest.fn(),
};

const session = {
  sessionId: 'session-1',
  title: '旧标题',
  status: 'Active',
  createdAt: 10,
  updatedAt: 20,
};

describe('useSessionCatalog', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (listSessions as jest.Mock).mockResolvedValue([session]);
    (archiveSession as jest.Mock).mockResolvedValue(undefined);
    (deleteSession as jest.Mock).mockResolvedValue(undefined);
    (renameSession as jest.Mock).mockResolvedValue(undefined);
  });

  it('loads the workspace catalog and delegates terminal cleanup', async () => {
    const onSessionNotFound = jest.fn();
    const { result } = renderHook(() =>
      useSessionCatalog({
        workspaceId: 'default',
        renameSessionId: null,
        renameTitle: '',
        closeRenameModal: jest.fn(),
        messageApi: messageApi as never,
      }),
    );
    await waitFor(() => expect(result.current.sessions).toHaveLength(1));
    act(() => result.current.bindSessionNotFoundHandler(onSessionNotFound));

    await act(async () => result.current.handleDeleteSession('session-1'));

    expect(deleteSession).toHaveBeenCalledWith('session-1');
    expect(onSessionNotFound).toHaveBeenCalledWith('session-1', 'delete');
  });

  it('renames the matching catalog item and closes the modal', async () => {
    const closeRenameModal = jest.fn();
    const { result } = renderHook(() =>
      useSessionCatalog({
        workspaceId: 'default',
        renameSessionId: 'session-1',
        renameTitle: '  新标题  ',
        closeRenameModal,
        messageApi: messageApi as never,
      }),
    );
    await waitFor(() => expect(result.current.sessions).toHaveLength(1));

    await act(async () => result.current.handleRenameSubmit());

    expect(renameSession).toHaveBeenCalledWith('session-1', '新标题');
    expect(result.current.sessions[0].title).toBe('新标题');
    expect(closeRenameModal).toHaveBeenCalled();
  });
});

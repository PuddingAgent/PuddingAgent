import { act, renderHook } from '@testing-library/react';
import { useChatModals } from './useChatModals';

describe('useChatModals', () => {
  it('owns create-scene visibility and form state', () => {
    const { result } = renderHook(() => useChatModals());

    expect(result.current.createSceneOpen).toBe(false);
    expect(result.current.createSceneLoading).toBe(false);
    expect(result.current.createSceneForm).toBeDefined();

    act(() => result.current.setCreateSceneOpen(true));
    expect(result.current.createSceneOpen).toBe(true);
  });

  it('opens and closes the rename modal as one state transition', () => {
    const { result } = renderHook(() => useChatModals());

    act(() => result.current.openRenameModal('session-1', '旧标题'));
    expect(result.current.renameSessionId).toBe('session-1');
    expect(result.current.renameTitle).toBe('旧标题');
    expect(result.current.renameModalOpen).toBe(true);

    act(() => result.current.setRenameTitle('新标题'));
    expect(result.current.renameTitle).toBe('新标题');

    act(() => result.current.closeRenameModal());
    expect(result.current.renameModalOpen).toBe(false);
  });
});

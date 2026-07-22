import { Form } from 'antd';
import { useCallback, useState } from 'react';

/**
 * Owns the chat page's modal-only state.
 *
 * Business effects (creating a scene or persisting a rename) intentionally stay
 * in the composition layer; this hook only exposes UI state and modal commands.
 */
export function useChatModals() {
  const [createSceneOpen, setCreateSceneOpen] = useState(false);
  const [createSceneLoading] = useState(false);
  const [createSceneForm] = Form.useForm<{ name: string }>();
  const [renameModalOpen, setRenameModalOpen] = useState(false);
  const [renameTitle, setRenameTitle] = useState('');
  const [renameSessionId, setRenameSessionId] = useState<string | null>(null);

  const openRenameModal = useCallback((sessionId: string, title: string) => {
    setRenameSessionId(sessionId);
    setRenameTitle(title);
    setRenameModalOpen(true);
  }, []);

  const closeRenameModal = useCallback(() => {
    setRenameModalOpen(false);
  }, []);

  return {
    createSceneOpen,
    setCreateSceneOpen,
    createSceneLoading,
    createSceneForm,
    renameModalOpen,
    setRenameModalOpen,
    renameTitle,
    setRenameTitle,
    renameSessionId,
    openRenameModal,
    closeRenameModal,
  };
}

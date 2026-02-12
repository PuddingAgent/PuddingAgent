import { useRef, useSyncExternalStore } from 'react';
import {
  type AgentChatApiPort,
  createAgentChatClientStore,
} from '../client/chatClientStore';
import type { AgentChatLocalCache } from '../client/localCache';

export function useAgentChatClient(input: {
  cache: AgentChatLocalCache;
  api: AgentChatApiPort;
  ownerUserId?: string;
}) {
  const storeRef = useRef<ReturnType<typeof createAgentChatClientStore> | null>(
    null,
  );
  if (!storeRef.current) {
    storeRef.current = createAgentChatClientStore(input);
  }

  const store = storeRef.current;
  const snapshot = useSyncExternalStore(
    store.subscribe,
    store.getSnapshot,
    store.getSnapshot,
  );

  return {
    snapshot,
    refreshStatuses: store.refreshStatuses,
    syncStatuses: store.syncStatuses,
    selectAgent: store.selectAgent,
    syncSelectedAgent: store.syncSelectedAgent,
  };
}

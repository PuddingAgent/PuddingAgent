import { DEFAULT_AGENT_CHAT_OWNER_ID } from './clientIdentity';
import type { AgentConversationView, AgentStatusProjection } from './types';

export interface AgentChatLocalCache {
  loadConversation(
    workspaceId: string,
    agentId: string,
    ownerUserId?: string,
  ): Promise<AgentConversationView | null>;
  saveConversation(view: AgentConversationView): Promise<void>;
  loadStatuses(
    workspaceId: string,
    ownerUserId?: string,
  ): Promise<AgentStatusProjection[]>;
  saveStatuses(
    workspaceId: string,
    statuses: AgentStatusProjection[],
    ownerUserId?: string,
  ): Promise<void>;
}

const normalizeOwner = (ownerUserId?: string) =>
  ownerUserId || DEFAULT_AGENT_CHAT_OWNER_ID;
const conversationKey = (
  workspaceId: string,
  ownerUserId: string | undefined,
  agentId: string,
) => `${workspaceId}::${normalizeOwner(ownerUserId)}::${agentId}`;
const statusKey = (workspaceId: string, ownerUserId?: string) =>
  `${workspaceId}::${normalizeOwner(ownerUserId)}`;

export function createMemoryAgentChatCache(): AgentChatLocalCache {
  const conversations = new Map<string, AgentConversationView>();
  const statuses = new Map<string, AgentStatusProjection[]>();

  return {
    async loadConversation(
      workspaceId,
      agentId,
      ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID,
    ) {
      return (
        conversations.get(conversationKey(workspaceId, ownerUserId, agentId)) ??
        null
      );
    },
    async saveConversation(view) {
      conversations.set(
        conversationKey(view.workspaceId, view.ownerUserId, view.agentId),
        view,
      );
    },
    async loadStatuses(workspaceId, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      return statuses.get(statusKey(workspaceId, ownerUserId)) ?? [];
    },
    async saveStatuses(
      workspaceId,
      list,
      ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID,
    ) {
      statuses.set(statusKey(workspaceId, ownerUserId), list);
    },
  };
}

export function createIndexedDbAgentChatCache(): AgentChatLocalCache {
  if (typeof indexedDB === 'undefined') return createMemoryAgentChatCache();

  let dbPromise: Promise<IDBDatabase> | null = null;

  const getDb = (): Promise<IDBDatabase> => {
    if (dbPromise) return dbPromise;
    dbPromise = new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open('pudding-agent-chat', 1);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains('conversations'))
          db.createObjectStore('conversations');
        if (!db.objectStoreNames.contains('statuses'))
          db.createObjectStore('statuses');
      };
      request.onsuccess = () => {
        const db = request.result;
        // 连接意外关闭时重建
        db.onclose = () => { dbPromise = null; };
        resolve(db);
      };
      request.onerror = () => { dbPromise = null; reject(request.error); };
      request.onblocked = () => { dbPromise = null; reject(new Error('indexedDB blocked')); };
    });
    return dbPromise;
  };

  const read = async <T>(storeName: string, key: string): Promise<T | null> => {
    const db = await getDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(storeName, 'readonly');
      const request = tx.objectStore(storeName).get(key);
      request.onsuccess = () =>
        resolve((request.result as T | undefined) ?? null);
      request.onerror = () => reject(request.error);
    });
  };

  const write = async <T>(
    storeName: string,
    key: string,
    value: T,
  ): Promise<void> => {
    const db = await getDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(storeName, 'readwrite');
      tx.objectStore(storeName).put(value, key);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  };

  return {
    loadConversation(
      workspaceId,
      agentId,
      ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID,
    ) {
      return read<AgentConversationView>(
        'conversations',
        conversationKey(workspaceId, ownerUserId, agentId),
      );
    },
    saveConversation(view) {
      return write(
        'conversations',
        conversationKey(view.workspaceId, view.ownerUserId, view.agentId),
        view,
      );
    },
    async loadStatuses(workspaceId, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      return (
        (await read<AgentStatusProjection[]>(
          'statuses',
          statusKey(workspaceId, ownerUserId),
        )) ?? []
      );
    },
    saveStatuses(workspaceId, list, ownerUserId = DEFAULT_AGENT_CHAT_OWNER_ID) {
      return write('statuses', statusKey(workspaceId, ownerUserId), list);
    },
  };
}

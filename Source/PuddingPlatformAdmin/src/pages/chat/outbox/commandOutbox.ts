// ── P1: Command Outbox (IndexedDB) ────────────────────────────
// 离线恢复的第二条链路：用户命令通过 IndexedDB 持久化，重连后重发。
// 每条命令携带稳定的 clientRequestId，后端幂等处理。
// ───────────────────────────────────────────────────────────────

const DB_NAME = 'pudding-command-outbox';
const DB_VERSION = 3;
const STORE_NAME = 'pending-commands';

export interface OutboxRecord {
  id: string; // clientRequestId
  clientMessageId: string;
  workspaceId: string;
  conversationId: string;
  messageText: string;
  agentIds: string[];
  metadata?: Record<string, string>;
  createdAt: number;
  attemptCount: number;
  lastAttemptAt?: number;
  status: 'pending' | 'sending' | 'sent' | 'failed';
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (db.objectStoreNames.contains(STORE_NAME))
        db.deleteObjectStore(STORE_NAME);
      db.createObjectStore(STORE_NAME, { keyPath: 'id' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

export async function enqueueCommand(params: {
  clientRequestId: string;
  clientMessageId: string;
  workspaceId: string;
  conversationId: string;
  messageText: string;
  agentIds: string[];
  metadata?: Record<string, string>;
}): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readwrite');
  const store = tx.objectStore(STORE_NAME);

  const record: OutboxRecord = {
    id: params.clientRequestId,
    clientMessageId: params.clientMessageId,
    workspaceId: params.workspaceId,
    conversationId: params.conversationId,
    messageText: params.messageText,
    agentIds: params.agentIds,
    metadata: params.metadata,
    createdAt: Date.now(),
    attemptCount: 0,
    status: 'pending',
  };

  await new Promise<void>((resolve, reject) => {
    const request = store.put(record);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error);
  });
}

export async function dequeueCommand(clientRequestId: string): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readwrite');
  const store = tx.objectStore(STORE_NAME);

  await new Promise<void>((resolve, reject) => {
    const request = store.delete(clientRequestId);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error);
  });
}

export async function listPendingCommands(): Promise<OutboxRecord[]> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readonly');
  const store = tx.objectStore(STORE_NAME);

  return new Promise((resolve, reject) => {
    const request = store.getAll();
    request.onsuccess = () => resolve(request.result as OutboxRecord[]);
    request.onerror = () => reject(request.error);
  });
}

export async function markSending(clientRequestId: string): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(STORE_NAME, 'readwrite');
  const store = tx.objectStore(STORE_NAME);

  await new Promise<void>((resolve, reject) => {
    const getReq = store.get(clientRequestId);
    getReq.onsuccess = () => {
      const record = getReq.result as OutboxRecord | undefined;
      if (record) {
        record.status = 'sending';
        record.attemptCount += 1;
        record.lastAttemptAt = Date.now();
        store.put(record);
      }
      resolve();
    };
    getReq.onerror = () => reject(getReq.error);
  });
}

export async function flushOutbox(
  sendFn: (record: OutboxRecord) => Promise<void>,
): Promise<{ sent: number; failed: number }> {
  const pending = await listPendingCommands();
  let sent = 0;
  let failed = 0;

  for (const record of pending) {
    try {
      await markSending(record.id);
      await sendFn(record);
      await dequeueCommand(record.id);
      sent++;
    } catch {
      failed++;
    }
  }

  return { sent, failed };
}

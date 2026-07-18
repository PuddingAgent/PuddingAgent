// ── ADR-057 Phase 5: Outbox Persistence ──────────────────────
// IndexedDB 持久化 Snapshot cursor + Outbox records。
// cursor 必须与 canonical state 一起提交，不能只保存 cursor。
// ─────────────────────────────────────────────────────────────

const DB_NAME = 'pudding-conversation-state';
const DB_VERSION = 1;
const SNAPSHOT_STORE = 'snapshots';
const OUTBOX_STORE = 'outbox';

interface SnapshotRecord {
  conversationId: string;
  cursor: number;
  savedAt: number;
}

interface OutboxRecord {
  clientRequestId: string;
  workspaceId: string;
  messageText: string;
  sessionId?: string;
  agentId?: string;
  createdAt: number;
  attemptCount: number;
  status: 'pending' | 'sending' | 'accepted' | 'rejected';
  commandId?: string;
  turnId?: string;
  messageId?: string;
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(SNAPSHOT_STORE)) {
        db.createObjectStore(SNAPSHOT_STORE, { keyPath: 'conversationId' });
      }
      if (!db.objectStoreNames.contains(OUTBOX_STORE)) {
        const store = db.createObjectStore(OUTBOX_STORE, {
          keyPath: 'clientRequestId',
        });
        store.createIndex('byStatus', 'status');
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

// ── Snapshot Persistence ─────────────────────────────────────

export async function saveCursor(
  conversationId: string,
  cursor: number,
): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(SNAPSHOT_STORE, 'readwrite');
  const store = tx.objectStore(SNAPSHOT_STORE);

  await new Promise<void>((resolve, reject) => {
    const request = store.put({
      conversationId,
      cursor,
      savedAt: Date.now(),
    } as SnapshotRecord);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error);
  });
}

export async function loadCursor(
  conversationId: string,
): Promise<SnapshotRecord | null> {
  const db = await openDb();
  const tx = db.transaction(SNAPSHOT_STORE, 'readonly');
  const store = tx.objectStore(SNAPSHOT_STORE);

  return new Promise((resolve, reject) => {
    const request = store.get(conversationId);
    request.onsuccess = () =>
      resolve((request.result as SnapshotRecord) ?? null);
    request.onerror = () => reject(request.error);
  });
}

// ── Outbox Persistence ───────────────────────────────────────

export async function saveOutboxRecord(
  record: OutboxRecord,
): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(OUTBOX_STORE, 'readwrite');
  const store = tx.objectStore(OUTBOX_STORE);

  await new Promise<void>((resolve, reject) => {
    const request = store.put(record);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error);
  });
}

export async function removeOutboxRecord(
  clientRequestId: string,
): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(OUTBOX_STORE, 'readwrite');
  const store = tx.objectStore(OUTBOX_STORE);

  await new Promise<void>((resolve, reject) => {
    const request = store.delete(clientRequestId);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error);
  });
}

export async function listOutboxRecords(): Promise<OutboxRecord[]> {
  const db = await openDb();
  const tx = db.transaction(OUTBOX_STORE, 'readonly');
  const store = tx.objectStore(OUTBOX_STORE);

  return new Promise((resolve, reject) => {
    const request = store.getAll();
    request.onsuccess = () => resolve((request.result as OutboxRecord[]) ?? []);
    request.onerror = () => reject(request.error);
  });
}

export async function markOutboxAccepted(
  clientRequestId: string,
  commandId: string,
  turnId: string,
  messageId: string,
): Promise<void> {
  const db = await openDb();
  const tx = db.transaction(OUTBOX_STORE, 'readwrite');
  const store = tx.objectStore(OUTBOX_STORE);

  await new Promise<void>((resolve, reject) => {
    const getReq = store.get(clientRequestId);
    getReq.onsuccess = () => {
      const record = getReq.result as OutboxRecord | undefined;
      if (record) {
        record.status = 'accepted';
        record.commandId = commandId;
        record.turnId = turnId;
        record.messageId = messageId;
        store.put(record);
      }
      resolve();
    };
    getReq.onerror = () => reject(getReq.error);
  });
}

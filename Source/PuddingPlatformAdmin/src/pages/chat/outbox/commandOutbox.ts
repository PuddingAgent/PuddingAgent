// ── P1: Command Outbox (IndexedDB) ────────────────────────────
// 离线恢复的第二条链路：用户命令通过 IndexedDB 持久化，重连后重发。
// 每条命令携带稳定的 clientRequestId，后端幂等处理。
// ───────────────────────────────────────────────────────────────

const DB_NAME = 'pudding-command-outbox';
const DB_VERSION = 3;
const STORE_NAME = 'pending-commands';

/** 网络/5xx 类可重试失败的最大重放次数，超过即丢弃。4xx 不看次数，直接丢弃。 */
const MAX_FLUSH_ATTEMPTS = 5;

/**
 * 服务端明确拒绝（4xx）属不可重试：重放多少次结果都一样。
 * 408（超时）/429（限流）例外 —— 它们是短时间内可恢复的。
 * 不依赖 axios 类型，用宽松结构判定，避免把非 axios 错误误判成可重试。
 */
export function isNonRetryableFailure(error: unknown): boolean {
  const status = (error as { response?: { status?: unknown } } | null)?.response
    ?.status;
  if (typeof status !== 'number') return false;
  if (status === 408 || status === 429) return false;
  return status >= 400 && status < 500;
}

export interface OutboxRecord {
  id: string; // clientRequestId
  clientMessageId: string;
  workspaceId: string;
  conversationId: string;
  messageText: string;
  agentIds: string[];
  metadata?: Record<string, string>;
  /** ADR-077：typed 图片内容部件；离线重放时不丢图片事实。 */
  imageParts?: { type: 'image'; artifactId: string; detail?: 'original' | 'low' | 'high' | 'auto' }[];
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
  imageParts?: { type: 'image'; artifactId: string; detail?: 'original' | 'low' | 'high' | 'auto' }[];
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
    imageParts: params.imageParts,
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

  const all = await new Promise<OutboxRecord[]>((resolve, reject) => {
    const request = store.getAll();
    request.onsuccess = () => resolve(request.result as OutboxRecord[]);
    request.onerror = () => reject(request.error);
  });

  // `sent` 是终态：正常情况下 dequeueCommand 已删除，但中断/异常可能留下残留，
  // 重放它们只会重复提交。
  return all.filter((record) => record.status !== 'sent');
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
): Promise<{ sent: number; failed: number; discarded: number }> {
  const pending = await listPendingCommands();
  let sent = 0;
  let failed = 0;
  let discarded = 0;

  for (const record of pending) {
    try {
      await markSending(record.id);
      await sendFn(record);
      await dequeueCommand(record.id);
      sent++;
    } catch (error) {
      failed++;
      const attempts = record.attemptCount + 1;
      const giveUp = isNonRetryableFailure(error) || attempts >= MAX_FLUSH_ATTEMPTS;
      if (giveUp) {
        // 不留垃圾：4xx（服务端明确拒绝，如「请求体无效」）重试多少次都一样；
        // 网络/5xx 连续失败到上限也不再抱有幻想。两者都丢弃，否则每次开页
        // 都会重放同一条并再次失败，console 里永远刷同一个错误。
        console.error(
          '[command-outbox] 放弃重放命令',
          { id: record.id, attempts, conversationId: record.conversationId },
          error,
        );
        await dequeueCommand(record.id);
        discarded++;
      } else {
        console.warn('[command-outbox] 重放失败，保留待下次重试', {
          id: record.id,
          attempts,
          error,
        });
      }
    }
  }

  return { sent, failed, discarded };
}

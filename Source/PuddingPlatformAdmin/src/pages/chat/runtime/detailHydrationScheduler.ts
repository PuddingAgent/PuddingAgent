// ── detailHydrationScheduler：完成 turn 明细懒水合的页面级并发调度器 ────────
//
// 与 React 解耦的并发事实源（useTurnSurfaceStore 只做订阅衔接）：
//  - capacity 限制的是「整个页面尚未结束的明细请求数」，而不是单次 effect
//    新启动的数量；被新代取消的请求在其 promise 自然结束前继续占用物理
//    槽位，避免快速会话切换反复叠加请求。
//  - 请求 key = workspace|agent|conversation|message|detailsRevision。每个
//    在飞请求持有 owner token；成功/失败/finally 回调先校验所有权，旧代
//    迟到回调不得删除新请求的槽位或修改新会话状态。
//  - 失败分类：auth（401/403）等待显式认证恢复；gone（404/410）对该
//    detailsRevision 终止；transient 指数退避 + jitter，有界尝试次数。
//    sync（projection 更新）不重置失败预算，retryAll/认证恢复才重新开启。
//  - 失败记忆（F01/AU-F01-2）：请求队列与失败记忆分离。离开视口只取消
//    调度意图，attempts/retryAfter/gone/auth 状态保留在 failureMemory 与
//    authPaused 中；重新进入视口沿用原预算与剩余退避。失败记忆按会话
//    有界（MAX_FAILURE_MEMORY），退出 generation 即释放；认证暂停独立于
//    记忆淘汰，只有 notifyAuthRecovered（真实登录/认证版本变化）解除。
import type { MessageProcessDetailsView } from '../client/types';

export type DetailFailureKind = 'auth' | 'gone' | 'transient';

export interface DetailHydrationRequest {
  workspaceId: string;
  agentId: string;
  conversationId: string;
  messageId: string;
  /** 明细内容修订；服务端暂未提供时省略（视同 0）。内容变化时递增即新 key。 */
  detailsRevision?: number | string;
  /** 调用方路由元数据（仅回调透传），不参与请求 key 身份。 */
  turnId?: string;
}

type DetailFetcher = (
  request: DetailHydrationRequest,
  signal: AbortSignal,
) => Promise<MessageProcessDetailsView>;

type EntryState =
  | 'queued'
  | 'running'
  | 'backoff'
  | 'auth'
  | 'gone'
  | 'exhausted';

interface SchedulerEntry {
  request: DetailHydrationRequest;
  key: string;
  state: EntryState;
  /** owner token；null 表示已被新代取消，回调不得再改共享状态。 */
  token: { generation: number } | null;
  controller: AbortController | null;
  attempts: number;
  retryTimer: ReturnType<typeof setTimeout> | null;
  /** backoff 到期时刻（epoch ms）；非 backoff 态为 0。离开视口后据此续剩。 */
  retryAt: number;
}

/** 离开视口后保留的失败记忆（auth 走独立的 authPaused，不占此表）。 */
interface FailureMemory {
  state: 'auth' | 'gone' | 'exhausted' | 'backoff';
  attempts: number;
  retryAt: number;
}

export interface DetailHydrationSchedulerOptions {
  /** 全页未完成明细请求上限（含被取消但未结束的请求）。默认 2。 */
  capacity?: number;
  /** transient 失败的最大尝试次数（含首次）。默认 4。 */
  maxAttempts?: number;
  fetch: DetailFetcher;
  onSuccess: (
    request: DetailHydrationRequest,
    details: MessageProcessDetailsView,
  ) => void;
  onFailure: (request: DetailHydrationRequest, kind: DetailFailureKind) => void;
}

const DEFAULT_CAPACITY = 2;
const DEFAULT_MAX_ATTEMPTS = 4;
const RETRY_BASE_DELAY_MS = 500;
const RETRY_MAX_DELAY_MS = 8000;
/** 失败记忆上限（同一 generation 内）。超出按插入序淘汰最老记录；
 *  auth 暂停不在此表、不受淘汰影响，避免 LRU 淘汰变相解除认证门禁。 */
const MAX_FAILURE_MEMORY = 256;

export const detailHydrationRequestKey = (
  request: DetailHydrationRequest,
): string =>
  [
    request.workspaceId,
    request.agentId,
    request.conversationId,
    request.messageId,
    request.detailsRevision ?? 0,
  ].join('|');

/** 从 transport 错误提取 HTTP 状态（umi request / axios 风格）。 */
const statusOfError = (error: unknown): number | null => {
  const candidate = error as { response?: { status?: unknown }; status?: unknown };
  const status = Number(candidate?.response?.status ?? candidate?.status);
  return Number.isFinite(status) && status > 0 ? status : null;
};

export const classifyDetailError = (error: unknown): DetailFailureKind => {
  const status = statusOfError(error);
  if (status === 401 || status === 403) return 'auth';
  if (status === 404 || status === 410) return 'gone';
  return 'transient';
};

const backoffDelayMs = (attempt: number): number => {
  const base = Math.min(
    RETRY_BASE_DELAY_MS * 2 ** Math.max(0, attempt - 1),
    RETRY_MAX_DELAY_MS,
  );
  // ±20% jitter，避免同批失败在同一瞬间齐发重试。
  return Math.round(base * (0.8 + Math.random() * 0.4));
};

export class DetailHydrationScheduler {
  private readonly capacity: number;
  private readonly maxAttempts: number;
  private readonly fetcher: DetailFetcher;
  private readonly reportSuccess: DetailHydrationSchedulerOptions['onSuccess'];
  private readonly reportFailure: DetailHydrationSchedulerOptions['onFailure'];
  private generation = 0;
  private readonly entries = new Map<string, SchedulerEntry>();
  private readonly desired = new Map<string, DetailHydrationRequest>();
  /** 被新代取消但 promise 尚未结束的请求：继续占物理槽位直到自然结束。 */
  private readonly settling: SchedulerEntry[] = [];
  /** 认证作用域暂停（401/403）：滚动/视口变化不是认证恢复事件，只有
   *  notifyAuthRecovered 或 generation 切换才解除；独立于失败记忆淘汰。 */
  private readonly authPaused = new Set<string>();
  /** 视口外的失败记忆（gone/exhausted/backoff）：重新进入视口时还原状态、
   *  预算与剩余退避；同一 generation 内有界，beginGeneration 时清空。 */
  private readonly failureMemory = new Map<string, FailureMemory>();
  private disposed = false;

  constructor(options: DetailHydrationSchedulerOptions) {
    this.capacity = Math.max(1, options.capacity ?? DEFAULT_CAPACITY);
    this.maxAttempts = Math.max(1, options.maxAttempts ?? DEFAULT_MAX_ATTEMPTS);
    this.fetcher = options.fetch;
    this.reportSuccess = options.onSuccess;
    this.reportFailure = options.onFailure;
  }

  /** 会话切换：递增 generation；终止排队/退避/失败项；在飞请求 abort 后转入
   *  settling（仍占槽位）。新会话获得全新失败预算，失败记忆与认证暂停一并
   *  释放。被 abort 的请求因 token 已置 null，其 AbortError 不会消耗预算。 */
  beginGeneration(): void {
    this.generation += 1;
    for (const entry of this.entries.values()) {
      this.clearRetryTimer(entry);
      if (entry.state === 'running' && entry.controller) {
        entry.token = null;
        entry.controller.abort();
        this.settling.push(entry);
      }
    }
    this.entries.clear();
    this.desired.clear();
    this.authPaused.clear();
    this.failureMemory.clear();
  }

  /** 以最新期望集合对账：新 key 入队；不再期望且未在飞的项转入失败记忆
   *  （不抹掉 attempts/retryAfter/gone/auth）；重进视口的 key 按记忆还原。
   *  在飞项无法撤回，其 canonical 结果照常应用。不重置任何失败预算。 */
  sync(desiredRequests: DetailHydrationRequest[]): void {
    if (this.disposed) return;
    const nextDesired = new Map<string, DetailHydrationRequest>();
    for (const request of desiredRequests) {
      const key = detailHydrationRequestKey(request);
      if (!nextDesired.has(key)) nextDesired.set(key, request);
    }
    this.desired.clear();
    for (const [key, request] of nextDesired) this.desired.set(key, request);
    for (const [key, entry] of [...this.entries]) {
      if (nextDesired.has(key) || entry.state === 'running') continue;
      this.clearRetryTimer(entry);
      this.rememberFailure(key, entry);
      this.entries.delete(key);
    }
    for (const [key, request] of nextDesired) {
      if (this.entries.has(key)) continue;
      const memory = this.restoreFailure(key);
      if (memory) {
        // 还原视口外记忆：沿用原状态、已耗预算与剩余退避；auth/gone/
        // exhausted 不重新发起请求，backoff 只在剩余退避到期后重排。
        const entry: SchedulerEntry = {
          request,
          key,
          state: memory.state,
          token: null,
          controller: null,
          attempts: memory.attempts,
          retryTimer: null,
          retryAt: memory.retryAt,
        };
        this.entries.set(key, entry);
        if (memory.state === 'backoff') this.resumeBackoff(entry);
        continue;
      }
      this.entries.set(key, {
        request,
        key,
        state: 'queued',
        token: null,
        controller: null,
        attempts: 0,
        retryTimer: null,
        retryAt: 0,
      });
    }
    this.drain();
  }

  /** 认证恢复（重新登录等）由调用方显式触发；auth 等待项重新排队，视口外
   *  的认证暂停一并解除。滚动/重新可见不是认证恢复事件。 */
  notifyAuthRecovered(): void {
    this.authPaused.clear();
    for (const entry of this.entries.values()) {
      if (entry.state === 'auth' && this.desired.has(entry.key)) {
        entry.state = 'queued';
      }
    }
    this.drain();
  }

  /** 用户手动重试：重新开启 transient 预算与 auth 等待；gone 对该
   *  revision 保持终止（新 revision 是新 key）。 */
  retryAll(): void {
    for (const entry of this.entries.values()) {
      if (
        (entry.state === 'exhausted' || entry.state === 'auth') &&
        this.desired.has(entry.key)
      ) {
        entry.attempts = 0;
        entry.state = 'queued';
      }
    }
    this.drain();
  }

  getStats(): { activeCount: number; queuedCount: number } {
    let queued = 0;
    for (const entry of this.entries.values()) {
      if (entry.state === 'queued') queued += 1;
    }
    return { activeCount: this.activeCount(), queuedCount: queued };
  }

  /** 卸载：终止全部工作并停止调度；dispose 后 sync 为 no-op。被 abort 的
   *  请求因 token 已置 null，其 AbortError 不消耗预算、不报业务失败。 */
  dispose(): void {
    this.disposed = true;
    this.beginGeneration();
    this.settling.length = 0;
  }

  /** 离开视口时把失败状态存档：auth 进独立暂停表；gone/exhausted/backoff
   *  进有界失败记忆（queued/running 不存档——它们本就应重新调度）。 */
  private rememberFailure(key: string, entry: SchedulerEntry): void {
    if (entry.state === 'auth') {
      this.authPaused.add(key);
      return;
    }
    if (entry.state !== 'gone' && entry.state !== 'exhausted' && entry.state !== 'backoff') {
      return;
    }
    if (!this.failureMemory.has(key) && this.failureMemory.size >= MAX_FAILURE_MEMORY) {
      for (const oldest of this.failureMemory.keys()) {
        this.failureMemory.delete(oldest);
        break;
      }
    }
    this.failureMemory.set(key, {
      state: entry.state,
      attempts: entry.attempts,
      retryAt: entry.retryAt,
    });
  }

  /** 重进视口时消费失败记忆：命中则返回并清除存档（再次滚出会重新存档）。 */
  private restoreFailure(key: string): FailureMemory | null {
    if (this.authPaused.has(key)) {
      this.authPaused.delete(key);
      return { state: 'auth', attempts: 0, retryAt: 0 };
    }
    const memory = this.failureMemory.get(key);
    if (!memory) return null;
    this.failureMemory.delete(key);
    return memory;
  }

  /** 从存档的 retryAt 续跑剩余退避：到期则立即重排，未到期按剩余时间挂定时器。 */
  private resumeBackoff(entry: SchedulerEntry): void {
    if (this.disposed) return;
    const remaining = entry.retryAt - Date.now();
    if (remaining <= 0) {
      entry.retryAt = 0;
      entry.state = 'queued';
      return;
    }
    entry.retryTimer = setTimeout(() => {
      entry.retryTimer = null;
      entry.retryAt = 0;
      if (this.disposed || entry.state !== 'backoff') return;
      entry.state = 'queued';
      this.drain();
    }, remaining);
  }

  private activeCount(): number {
    let running = 0;
    for (const entry of this.entries.values()) {
      if (entry.state === 'running') running += 1;
    }
    return running + this.settling.length;
  }

  private drain(): void {
    if (this.disposed) return;
    for (const entry of this.entries.values()) {
      if (this.activeCount() >= this.capacity) return;
      if (entry.state !== 'queued') continue;
      this.start(entry);
    }
  }

  private start(entry: SchedulerEntry): void {
    const token = { generation: this.generation };
    entry.state = 'running';
    entry.token = token;
    const controller = new AbortController();
    entry.controller = controller;
    void this.fetcher(entry.request, controller.signal).then(
      (details) => {
        if (!this.owns(entry, token)) return;
        this.entries.delete(entry.key);
        this.reportSuccess(entry.request, details);
      },
      (error: unknown) => {
        if (!this.owns(entry, token)) return;
        this.handleFailure(entry, error);
      },
    ).finally(() => {
      // 槽位释放：仅被取消的旧代请求还在 settling 列表里（splice 对在飞项
      // 是 no-op）；成功项已离开 entries，失败项已转入退避/终止态，两种
      // 情况的空槽都由 drain 统一补位。
      const index = this.settling.indexOf(entry);
      if (index >= 0) this.settling.splice(index, 1);
      this.drain();
    });
  }

  /** 仅当 entry 仍是该 key 的在飞 owner 时才允许回调改共享状态。 */
  private owns(entry: SchedulerEntry, token: { generation: number }): boolean {
    return (
      entry.token === token &&
      this.entries.get(entry.key) === entry &&
      entry.state === 'running'
    );
  }

  private handleFailure(entry: SchedulerEntry, error: unknown): void {
    const kind = classifyDetailError(error);
    this.reportFailure(entry.request, kind);
    entry.controller = null;
    if (kind === 'auth') {
      entry.state = 'auth';
      return;
    }
    if (kind === 'gone') {
      entry.state = 'gone';
      return;
    }
    entry.attempts += 1;
    if (entry.attempts >= this.maxAttempts) {
      entry.state = 'exhausted';
      return;
    }
    entry.state = 'backoff';
    const delay = backoffDelayMs(entry.attempts);
    entry.retryAt = Date.now() + delay;
    entry.retryTimer = setTimeout(() => {
      entry.retryTimer = null;
      entry.retryAt = 0;
      if (this.disposed || entry.state !== 'backoff') return;
      entry.state = 'queued';
      this.drain();
    }, delay);
  }

  private clearRetryTimer(entry: SchedulerEntry): void {
    if (entry.retryTimer === null) return;
    clearTimeout(entry.retryTimer);
    entry.retryTimer = null;
  }
}

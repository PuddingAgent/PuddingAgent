// ── DetailHydrationScheduler 单元回归（F01 2026-09-11）──────────────────────
// 验证页面级并发事实源：总在飞上限（含被取消未结束的请求）、代际 owner 校验、
// 失败分类（auth/gone/transient）与有界退避、手动重试与认证恢复。
import {
  DetailHydrationScheduler,
  classifyDetailError,
  detailHydrationRequestKey,
  type DetailHydrationRequest,
} from './detailHydrationScheduler';
import type { MessageProcessDetailsView } from '../client/types';

interface Deferred {
  promise: Promise<MessageProcessDetailsView>;
  resolve: (value: MessageProcessDetailsView) => void;
  reject: (error: unknown) => void;
}

const createDeferred = (): Deferred => {
  let resolve!: (value: MessageProcessDetailsView) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<MessageProcessDetailsView>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
};

const requestOf = (
  messageId: string,
  overrides: Partial<DetailHydrationRequest> = {},
): DetailHydrationRequest => ({
  workspaceId: 'default',
  agentId: 'agent-a',
  conversationId: 'session-1',
  messageId,
  ...overrides,
});

const detailsOf = (messageId: string): MessageProcessDetailsView => ({
  messageId,
  runId: null,
  processItems: [
    {
      id: `event-${messageId}`,
      kind: 'text',
      status: 'done',
      text: `answer-${messageId}`,
      timestamp: '2026-09-11T00:00:00.000Z',
      sequence: 1,
      turnId: `t-${messageId}`,
      runId: null,
    },
  ],
});

const flushMicrotasks = async () => {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
};

describe('detailHydrationRequestKey', () => {
  it('separates revisions and conversations', () => {
    const base = detailHydrationRequestKey(requestOf('m1'));
    expect(detailHydrationRequestKey(requestOf('m1'))).toBe(base);
    expect(
      detailHydrationRequestKey(requestOf('m1', { detailsRevision: 2 })),
    ).not.toBe(base);
    expect(
      detailHydrationRequestKey(requestOf('m1', { conversationId: 's2' })),
    ).not.toBe(base);
  });
});

describe('classifyDetailError', () => {
  it('maps auth/gone/transient', () => {
    expect(classifyDetailError({ response: { status: 401 } })).toBe('auth');
    expect(classifyDetailError({ response: { status: 403 } })).toBe('auth');
    expect(classifyDetailError({ response: { status: 404 } })).toBe('gone');
    expect(classifyDetailError({ response: { status: 410 } })).toBe('gone');
    expect(classifyDetailError({ response: { status: 500 } })).toBe('transient');
    expect(classifyDetailError(new Error('network down'))).toBe('transient');
  });
});

describe('DetailHydrationScheduler', () => {
  it('keeps total in-flight at capacity and starts one per released slot', async () => {
    const deferreds: Record<string, Deferred> = {};
    const started: string[] = [];
    const successes: string[] = [];
    const scheduler = new DetailHydrationScheduler({
      fetch: async (request) => {
        started.push(request.messageId);
        deferreds[request.messageId] = createDeferred();
        return deferreds[request.messageId].promise;
      },
      onSuccess: (request) => successes.push(request.messageId),
      onFailure: () => {},
    });

    scheduler.sync(['m1', 'm2', 'm3', 'm4'].map((id) => requestOf(id)));
    await flushMicrotasks();
    expect(started).toEqual(['m1', 'm2']);

    deferreds.m1.resolve(detailsOf('m1'));
    await flushMicrotasks();
    expect(started).toEqual(['m1', 'm2', 'm3']);

    deferreds.m2.resolve(detailsOf('m2'));
    await flushMicrotasks();
    expect(started).toEqual(['m1', 'm2', 'm3', 'm4']);
    expect(scheduler.getStats().activeCount).toBe(2);
    expect(scheduler.getStats().queuedCount).toBe(0);

    deferreds.m3.resolve(detailsOf('m3'));
    deferreds.m4.resolve(detailsOf('m4'));
    await flushMicrotasks();
    expect(successes).toEqual(['m1', 'm2', 'm3', 'm4']);
    expect(scheduler.getStats().activeCount).toBe(0);
  });

  it('ignores late callbacks from superseded requests and starts queued work when a zombie settles', async () => {
    const deferreds: Deferred[] = [];
    const successes: string[] = [];
    const failures: Array<{ id: string; kind: string }> = [];
    const scheduler = new DetailHydrationScheduler({
      fetch: async () => {
        const deferred = createDeferred();
        deferreds.push(deferred);
        return deferred.promise;
      },
      onSuccess: (request) => successes.push(request.messageId),
      onFailure: (request, kind) => failures.push({ id: request.messageId, kind }),
    });

    scheduler.sync([requestOf('m1'), requestOf('m2')]);
    await flushMicrotasks();
    expect(deferreds).toHaveLength(2);

    // 会话切换：在飞请求被取消，但 promise 未结束前仍占满物理槽位。
    scheduler.beginGeneration();
    scheduler.sync([requestOf('m3', { conversationId: 'session-2' })]);
    await flushMicrotasks();
    expect(deferreds).toHaveLength(2);
    expect(scheduler.getStats().activeCount).toBe(2);
    expect(scheduler.getStats().queuedCount).toBe(1);

    // 旧代迟到成功：不得触发 onSuccess；但其 promise 结束后槽位释放，
    // 新会话排队项立即补位。
    deferreds[0].resolve(detailsOf('m1'));
    await flushMicrotasks();
    expect(successes).toEqual([]);
    expect(deferreds).toHaveLength(3);

    // 旧代迟到失败：同样被忽略。
    deferreds[1].reject(new Error('late network failure'));
    await flushMicrotasks();
    expect(failures).toEqual([]);

    // 新会话请求正常完成。
    deferreds[2].resolve(detailsOf('m3'));
    await flushMicrotasks();
    expect(successes).toEqual(['m3']);
  });

  it('stops retrying after auth failure until notified, and keeps 404 terminal per revision', async () => {
    let attempts = 0;
    let authRecovered = false;
    const scheduler = new DetailHydrationScheduler({
      fetch: async (request) => {
        attempts += 1;
        if (request.detailsRevision === 1 && !authRecovered) {
          throw { response: { status: 401 } };
        }
        if (request.detailsRevision !== 1) {
          throw { response: { status: 404 } };
        }
        return detailsOf(request.messageId);
      },
      onSuccess: () => {},
      onFailure: () => {},
    });

    scheduler.sync([requestOf('m1', { detailsRevision: 1 })]);
    await flushMicrotasks();
    expect(attempts).toBe(1);
    // projection 更新（sync）不得触发重试。
    scheduler.sync([requestOf('m1', { detailsRevision: 1 })]);
    scheduler.sync([requestOf('m1', { detailsRevision: 1 })]);
    await flushMicrotasks();
    expect(attempts).toBe(1);

    // 404 的 revision 终止；换新 revision 立即可取。
    scheduler.sync([
      requestOf('m1', { detailsRevision: 1 }),
      requestOf('m1', { detailsRevision: 2 }),
    ]);
    await flushMicrotasks();
    expect(attempts).toBe(2);

    // 认证恢复后 auth 项重新排队。
    authRecovered = true;
    scheduler.sync([requestOf('m1', { detailsRevision: 1 })]);
    await flushMicrotasks();
    expect(attempts).toBe(2);
    scheduler.notifyAuthRecovered();
    await flushMicrotasks();
    expect(attempts).toBe(3);
  });

  describe('transient backoff', () => {
    beforeEach(() => {
      jest.useFakeTimers();
    });
    afterEach(() => {
      jest.useRealTimers();
    });

    it('limits attempts and reopens the budget only via manual retry', async () => {
      let attempts = 0;
      let fail = true;
      const scheduler = new DetailHydrationScheduler({
        fetch: async () => {
          attempts += 1;
          if (fail) throw new Error('transient network');
          return detailsOf('m1');
        },
        onSuccess: () => {},
        onFailure: () => {},
        maxAttempts: 3,
      });

      scheduler.sync([requestOf('m1')]);
      await jest.advanceTimersByTimeAsync(0);
      expect(attempts).toBe(1);
      // 预算 3：退避重试两次后耗尽。
      await jest.advanceTimersByTimeAsync(8000);
      await jest.advanceTimersByTimeAsync(8000);
      expect(attempts).toBe(3);
      await jest.advanceTimersByTimeAsync(60000);
      expect(attempts).toBe(3);

      // projection 更新不重置预算。
      scheduler.sync([requestOf('m1')]);
      await jest.advanceTimersByTimeAsync(60000);
      expect(attempts).toBe(3);

      // 用户手动重试重新开启预算，此时网络恢复 → 成功。
      fail = false;
      scheduler.retryAll();
      await jest.advanceTimersByTimeAsync(0);
      expect(attempts).toBe(4);
    });
  });

  it('dispose aborts in-flight fetches and stops scheduling', async () => {
    const signals: AbortSignal[] = [];
    const deferreds: Deferred[] = [];
    const scheduler = new DetailHydrationScheduler({
      fetch: async (_request, signal) => {
        signals.push(signal);
        const deferred = createDeferred();
        deferreds.push(deferred);
        return deferred.promise;
      },
      onSuccess: () => {},
      onFailure: () => {},
    });

    scheduler.sync([requestOf('m1'), requestOf('m2')]);
    await flushMicrotasks();
    scheduler.dispose();
    expect(signals.map((s) => s.aborted)).toEqual([true, true]);

    scheduler.sync([requestOf('m3')]);
    await flushMicrotasks();
    expect(deferreds).toHaveLength(2);
  });
});

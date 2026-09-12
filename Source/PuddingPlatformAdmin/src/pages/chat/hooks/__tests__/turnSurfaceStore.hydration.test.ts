// ── useTurnSurfaceStore 真实数据回归（chat UI 行为链重构 2026-08-24）─────────
// 数据来自生产 API 实测快照（默认助手主线会话 + b03b6f1f turn 的 686 项明细），
// 验证：懒水合触发 → store 合流 → getSurfaceProjection 产出含 message/reasoning/
// tool 节点的投影（刷新后完成态轨迹可恢复的端到端前提）。
import { act, renderHook, waitFor } from '@testing-library/react';
import { useTurnSurfaceStore } from '../useTurnSurfaceStore';

const conversation = require('./fixtures-conversation.json');

jest.mock('../../client/agentChatApi', () => ({
  // 仅 b03b turn 的 agent 消息（be550eff…）返回真实明细；其余消息返回空，
  // 避免同一份 fixture 的 eventId 被全局去重记到首个 turn 名下（生产中
  // 每条消息明细的 eventId 天然互斥）。
  getAgentMessageProcessItems: jest.fn(
    async (_ws: string, _agent: string, messageId: string) => {
      const payload = require('./fixtures-process-items.json');
      const details = payload.data ?? payload;
      if (messageId !== details.messageId) {
        return { messageId, runId: null, processItems: [] };
      }
      return details;
    },
  ),
}));

const mockedGetAgentMessageProcessItems = jest.requireMock(
  '../../client/agentChatApi',
).getAgentMessageProcessItems as jest.Mock;

describe('useTurnSurfaceStore (real fixture)', () => {
  beforeEach(() => {
    mockedGetAgentMessageProcessItems.mockClear();
    mockedGetAgentMessageProcessItems.mockImplementation(
      async (_ws: string, _agent: string, messageId: string) => {
        const payload = require('./fixtures-process-items.json');
        const details = payload.data ?? payload;
        if (messageId !== details.messageId) {
          return { messageId, runId: null, processItems: [] };
        }
        return details;
      },
    );
  });

  it('hydrates completed agent turns and exposes node projections by turnId', async () => {
    const { result } = renderHook(() =>
      useTurnSurfaceStore({
        workspaceId: 'default',
        agentId: 'default.global_general-assistant.6a8',
        conversationView: conversation,
      }),
    );
    // 有界水合：只有注册为「可见」的 turn 才会拉取明细（MessageRow 挂载
    // 即注册；此处模拟 b03b 回合进入近视口）。
    act(() => {
      result.current.registerVisibleTurn('b03b6f1fbd5843f992fd150a07dd7e75');
    });
    await waitFor(
      () => {
        const projection = result.current.getSurfaceProjection('b03b6f1fbd5843f992fd150a07dd7e75');
        expect(projection).toBeDefined();
        expect(projection!.nodes.length).toBeGreaterThan(10);
      },
      { timeout: 3000 },
    );
    const projection = result.current.getSurfaceProjection(
      'b03b6f1fbd5843f992fd150a07dd7e75',
    )!;
    const kinds = new Set(projection.nodes.map((n) => n.kind));
    expect(kinds.has('message')).toBe(true);
    expect(kinds.has('reasoning')).toBe(true);
    expect(kinds.has('tool')).toBe(true);
  });

  it('drains more than two visible turns while keeping hydration concurrency bounded', async () => {
    const messages = ['m1', 'm2', 'm3'].map((messageId, index) => ({
      messageId,
      turnId: `t${index + 1}`,
      runId: `r${index + 1}`,
      role: 'agent' as const,
      sourceId: 'agent-a',
      sourceName: 'Agent A',
      createdAt: `2026-08-25T00:00:0${index}.000Z`,
      content: `answer-${index + 1}`,
      status: 'succeeded' as const,
      processItems: [],
      processSummary: {
        totalItems: 1,
        thinkingRounds: 0,
        thinkingSteps: 0,
        toolCalls: 0,
        toolResults: 0,
        failedTools: 0,
        durationMs: 0,
        hasDetails: true,
      },
    }));
    mockedGetAgentMessageProcessItems.mockImplementation(
      async (_ws: string, _agent: string, messageId: string) => {
        const index = Number(messageId.slice(1));
        return {
          messageId,
          runId: `r${index}`,
          processItems: [
            {
              id: `event-${messageId}`,
              kind: 'text',
              status: 'done',
              text: `answer-${index}`,
              timestamp: `2026-08-25T00:00:0${index}.000Z`,
              sequence: index,
              turnId: `t${index}`,
              runId: `r${index}`,
            },
          ],
        };
      },
    );
    const { result } = renderHook(() =>
      useTurnSurfaceStore({
        workspaceId: 'default',
        agentId: 'agent-a',
        conversationView: {
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-drain',
          messages,
          activeRun: null,
          eventCursor: 3,
          updatedAt: '2026-08-25T00:00:03.000Z',
        },
      }),
    );

    act(() => {
      result.current.registerVisibleTurn('t1');
      result.current.registerVisibleTurn('t2');
      result.current.registerVisibleTurn('t3');
    });

    await waitFor(() => {
      expect(result.current.getSurfaceProjection('t1')?.nodes).toHaveLength(1);
      expect(result.current.getSurfaceProjection('t2')?.nodes).toHaveLength(1);
      expect(result.current.getSurfaceProjection('t3')?.nodes).toHaveLength(1);
    });
    expect(mockedGetAgentMessageProcessItems).toHaveBeenCalledTimes(3);
  });
});

// ── F01（2026-09-11）：页面级并发上限与生命周期回归 ─────────────────────────
// 之前的测试用立即 resolve 的 mock，只检查总调用次数，无法暴露「effect 级
// slice(0,2) 限制的是单批新启动数而非总并发」。以下用例用手动 resolve 的
// deferred promise 锁住在飞请求，验证真正的并发事实。

interface DeferredDetail {
  messageId: string;
  runId: string | null;
  processItems: unknown[];
}

interface Deferred {
  promise: Promise<DeferredDetail>;
  resolve: (value: DeferredDetail) => void;
  reject: (error: unknown) => void;
}

const createDeferred = (): Deferred => {
  let resolve!: (value: DeferredDetail) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<DeferredDetail>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
};

const makeAgentMessage = (
  index: number,
  overrides: Record<string, unknown> = {},
) => ({
  messageId: `m${index}`,
  turnId: `t${index}`,
  runId: `r${index}`,
  role: 'agent' as const,
  sourceId: 'agent-a',
  sourceName: 'Agent A',
  createdAt: `2026-09-11T00:00:${String(index).padStart(2, '0')}.000Z`,
  content: `answer-${index}`,
  status: 'succeeded' as const,
  processItems: [],
  processSummary: {
    totalItems: 1,
    thinkingRounds: 0,
    thinkingSteps: 0,
    toolCalls: 0,
    toolResults: 0,
    failedTools: 0,
    durationMs: 0,
    hasDetails: true,
  },
  ...overrides,
});

const makeView = (
  mainSessionId: string,
  messages: ReturnType<typeof makeAgentMessage>[],
  eventCursor = messages.length,
) => ({
  workspaceId: 'default',
  ownerUserId: 'single-user',
  agentId: 'agent-a',
  mainSessionId,
  messages,
  activeRun: null,
  eventCursor,
  updatedAt: '2026-09-11T00:00:00.000Z',
});

const flushAsync = async () => {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
};

describe('useTurnSurfaceStore hydration concurrency (F01)', () => {
  beforeEach(() => {
    mockedGetAgentMessageProcessItems.mockClear();
  });

  it('keeps total in-flight detail requests ≤ 2 across repeated projection updates', async () => {
    const deferreds = new Map<string, Deferred>();
    mockedGetAgentMessageProcessItems.mockImplementation(
      (_ws: string, _agent: string, messageId: string) => {
        const deferred = createDeferred();
        deferreds.set(messageId, deferred);
        return deferred.promise;
      },
    );
    const messages = Array.from({ length: 8 }, (_, i) => makeAgentMessage(i + 1));
    const props = {
      workspaceId: 'default',
      agentId: 'agent-a',
      conversationView: makeView('session-bound', messages),
    };
    const { result, rerender } = renderHook(() => useTurnSurfaceStore(props));

    act(() => {
      for (let i = 1; i <= 8; i += 1) {
        result.current.registerVisibleTurn(`t${i}`);
      }
    });
    // 初始只允许 2 个在飞。
    expect(deferreds.size).toBe(2);

    // 连续 20 次新投影对象到达（模拟 1.2s 轮询）：不得新增第三、第四个请求。
    for (let round = 0; round < 20; round += 1) {
      props.conversationView = makeView(
        'session-bound',
        messages,
        messages.length + round + 1,
      );
      rerender();
    }
    expect(deferreds.size).toBe(2);

    // 释放一个槽位只补一个。
    await act(async () => {
      deferreds.get('m1')!.resolve({
        messageId: 'm1',
        runId: 'r1',
        processItems: [
          { id: 'event-m1', kind: 'text', status: 'done', text: 'answer-1', timestamp: '', sequence: 1, turnId: 't1' },
        ],
      });
      await flushAsync();
    });
    expect(deferreds.size).toBe(3);

    // 排空后全部 8 条明细各取一次，且投影全部可取得。
    for (let i = 2; i <= 8; i += 1) {
      const id = `m${i}`;
      await act(async () => {
        deferreds.get(id)!.resolve({
          messageId: id,
          runId: `r${i}`,
          processItems: [
            { id: `event-${id}`, kind: 'text', status: 'done', text: `answer-${i}`, timestamp: '', sequence: 1, turnId: `t${i}` },
          ],
        });
        await flushAsync();
      });
    }
    await waitFor(() => {
      for (let i = 1; i <= 8; i += 1) {
        expect(result.current.getSurfaceProjection(`t${i}`)?.nodes).toHaveLength(1);
      }
    });
    expect(mockedGetAgentMessageProcessItems).toHaveBeenCalledTimes(8);
  });

  it('abandons old conversation work on switch and late callbacks never pollute the new conversation', async () => {
    const deferreds: Deferred[] = [];
    mockedGetAgentMessageProcessItems.mockImplementation(
      (_ws: string, _agent: string, _messageId: string) => {
        const deferred = createDeferred();
        deferreds.push(deferred);
        return deferred.promise;
      },
    );
    const viewA = () =>
      makeView('session-A', [makeAgentMessage(1), makeAgentMessage(2)]);
    const viewB = () => makeView('session-B', [makeAgentMessage(11)]);
    const props = {
      workspaceId: 'default',
      agentId: 'agent-a',
      conversationView: viewA(),
    };
    const { result, rerender } = renderHook(() => useTurnSurfaceStore(props));

    act(() => {
      result.current.registerVisibleTurn('t1');
      result.current.registerVisibleTurn('t2');
    });
    expect(deferreds).toHaveLength(2);

    // A → B：旧会话在飞请求被取消但 promise 未结束，B 的请求排队等待。
    props.conversationView = viewB();
    rerender();
    act(() => {
      result.current.registerVisibleTurn('t11');
    });
    expect(deferreds).toHaveLength(2);

    // 旧 A 代迟到成功：不得写入 store。
    await act(async () => {
      deferreds[0].resolve({
        messageId: 'm1',
        runId: 'r1',
        processItems: [
          { id: 'event-m1-stale', kind: 'text', status: 'done', text: 'stale-A', timestamp: '', sequence: 1, turnId: 't1' },
        ],
      });
      await flushAsync();
    });
    expect(result.current.getSurfaceProjection('t1')).toBeUndefined();
    // 槽位释放后 B 的请求立即补位。
    expect(deferreds).toHaveLength(3);

    // B → A：回到会话 A（新一代），旧 B 请求成为僵尸占位。会话切换清空
    // 可见集合，真实 UI 由重新挂载的 MessageRow 重新上报，此处等价注册。
    props.conversationView = viewA();
    rerender();
    act(() => {
      result.current.registerVisibleTurn('t1');
      result.current.registerVisibleTurn('t2');
    });
    // 两个未结束的旧代僵尸（旧 A m2 + 旧 B m11）占满物理槽位：新代请求
    // 只能排队，快速切换不得反复叠加请求。
    expect(deferreds).toHaveLength(3);

    // 旧 B 代迟到成功：不得写入 store；槽位释放后新 A 的 m1 立即补位。
    await act(async () => {
      deferreds[2].resolve({
        messageId: 'm11',
        runId: 'r11',
        processItems: [
          { id: 'event-m11-stale', kind: 'text', status: 'done', text: 'stale-B', timestamp: '', sequence: 1, turnId: 't11' },
        ],
      });
      await flushAsync();
    });
    expect(result.current.getSurfaceProjection('t11')).toBeUndefined();
    expect(deferreds).toHaveLength(4);

    // 新 A 的 m1 完成后释放槽位，m2 才补位（旧 A m2 僵尸仍占 1 槽）。
    await act(async () => {
      deferreds[3].resolve({
        messageId: 'm1',
        runId: 'r1',
        processItems: [
          { id: 'event-m1-fresh', kind: 'text', status: 'done', text: 'fresh-A1', timestamp: '', sequence: 1, turnId: 't1' },
        ],
      });
      await flushAsync();
    });
    expect(deferreds).toHaveLength(5);
    await act(async () => {
      deferreds[4].resolve({
        messageId: 'm2',
        runId: 'r2',
        processItems: [
          { id: 'event-m2-fresh', kind: 'text', status: 'done', text: 'fresh-A2', timestamp: '', sequence: 1, turnId: 't2' },
        ],
      });
      await flushAsync();
    });
    expect(result.current.getSurfaceProjection('t1')?.nodes).toHaveLength(1);
    expect(result.current.getSurfaceProjection('t2')?.nodes).toHaveLength(1);
    expect(result.current.getSurfaceProjection('t11')).toBeUndefined();
  });

  it('drops queued prefetch for turns that leave the viewport and resumes on re-entry', async () => {
    const deferreds: Deferred[] = [];
    mockedGetAgentMessageProcessItems.mockImplementation(
      (_ws: string, _agent: string, _messageId: string) => {
        const deferred = createDeferred();
        deferreds.push(deferred);
        return deferred.promise;
      },
    );
    const props = {
      workspaceId: 'default',
      agentId: 'agent-a',
      conversationView: makeView('session-viewport', [
        makeAgentMessage(1),
        makeAgentMessage(2),
        makeAgentMessage(3),
      ]),
    };
    const { result } = renderHook(() => useTurnSurfaceStore(props));

    act(() => {
      result.current.registerVisibleTurn('t1');
      result.current.registerVisibleTurn('t2');
      result.current.registerVisibleTurn('t3');
    });
    expect(deferreds).toHaveLength(2);

    // t3 离开视口：未开始的预取被剪枝；释放槽位不得补拉 t3。
    act(() => {
      result.current.unregisterVisibleTurn('t3');
    });
    await act(async () => {
      deferreds[0].resolve({
        messageId: 'm1',
        runId: 'r1',
        processItems: [
          { id: 'event-m1', kind: 'text', status: 'done', text: 'answer-1', timestamp: '', sequence: 1, turnId: 't1' },
        ],
      });
      await flushAsync();
    });
    expect(deferreds).toHaveLength(2);

    // 重新进入视口：t3 恢复预取并完成。
    act(() => {
      result.current.registerVisibleTurn('t3');
    });
    expect(deferreds).toHaveLength(3);
    await act(async () => {
      deferreds[1].resolve({
        messageId: 'm2',
        runId: 'r2',
        processItems: [
          { id: 'event-m2', kind: 'text', status: 'done', text: 'answer-2', timestamp: '', sequence: 1, turnId: 't2' },
        ],
      });
      deferreds[2].resolve({
        messageId: 'm3',
        runId: 'r3',
        processItems: [
          { id: 'event-m3', kind: 'text', status: 'done', text: 'answer-3', timestamp: '', sequence: 1, turnId: 't3' },
        ],
      });
      await flushAsync();
    });
    await waitFor(() => {
      expect(result.current.getSurfaceProjection('t3')?.nodes).toHaveLength(1);
    });
  });

  it('stops fetching after auth failures instead of retrying on every projection update', async () => {
    mockedGetAgentMessageProcessItems.mockImplementation(
      async (_ws: string, _agent: string, messageId: string) => {
        throw { response: { status: 401 }, messageId };
      },
    );
    const messages = [makeAgentMessage(1), makeAgentMessage(2)];
    const props = {
      workspaceId: 'default',
      agentId: 'agent-a',
      conversationView: makeView('session-auth', messages),
    };
    const { result, rerender } = renderHook(() => useTurnSurfaceStore(props));
    act(() => {
      result.current.registerVisibleTurn('t1');
      result.current.registerVisibleTurn('t2');
    });
    // 认证失效：两条各请求一次即停。
    expect(mockedGetAgentMessageProcessItems).toHaveBeenCalledTimes(2);
    await flushAsync();

    // 旧实现会在每次投影更新时清空失败集合并随 1.2s 轮询反复重试；
    // 新合同下 auth 失败等待显式恢复，projection 更新不再触发重试。
    for (let round = 0; round < 5; round += 1) {
      props.conversationView = makeView('session-auth', messages, 10 + round);
      rerender();
    }
    await flushAsync();
    expect(mockedGetAgentMessageProcessItems).toHaveBeenCalledTimes(2);
  });

  it('completes hydration after unmount without leaking state updates', async () => {
    const deferreds: Deferred[] = [];
    mockedGetAgentMessageProcessItems.mockImplementation(
      (_ws: string, _agent: string, _messageId: string) => {
        const deferred = createDeferred();
        deferreds.push(deferred);
        return deferred.promise;
      },
    );
    const props = {
      workspaceId: 'default',
      agentId: 'agent-a',
      conversationView: makeView('session-unmount', [
        makeAgentMessage(1),
        makeAgentMessage(2),
      ]),
    };
    const { result, unmount } = renderHook(() => useTurnSurfaceStore(props));
    act(() => {
      result.current.registerVisibleTurn('t1');
      result.current.registerVisibleTurn('t2');
    });
    expect(deferreds).toHaveLength(2);

    expect(() => unmount()).not.toThrow();
    // 卸载后迟到完成不得触发任何状态更新（调度器已 dispose，无 owner）。
    await act(async () => {
      deferreds[0].resolve({
        messageId: 'm1',
        runId: 'r1',
        processItems: [
          { id: 'event-m1', kind: 'text', status: 'done', text: 'answer-1', timestamp: '', sequence: 1, turnId: 't1' },
        ],
      });
      deferreds[1].resolve({
        messageId: 'm2',
        runId: 'r2',
        processItems: [
          { id: 'event-m2', kind: 'text', status: 'done', text: 'answer-2', timestamp: '', sequence: 1, turnId: 't2' },
        ],
      });
      await flushAsync();
    });
    expect(mockedGetAgentMessageProcessItems).toHaveBeenCalledTimes(2);
  });
});

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

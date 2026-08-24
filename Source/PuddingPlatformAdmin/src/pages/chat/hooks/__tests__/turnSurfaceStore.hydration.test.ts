// ── useTurnSurfaceStore 真实数据回归（chat UI 行为链重构 2026-08-24）─────────
// 数据来自生产 API 实测快照（默认助手主线会话 + b03b6f1f turn 的 686 项明细），
// 验证：懒水合触发 → store 合流 → getSurfaceProjection 产出含 message/reasoning/
// tool 节点的投影（刷新后完成态轨迹可恢复的端到端前提）。
import { renderHook, waitFor } from '@testing-library/react';
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

describe('useTurnSurfaceStore (real fixture)', () => {
  it('hydrates completed agent turns and exposes node projections by turnId', async () => {
    const { result } = renderHook(() =>
      useTurnSurfaceStore({
        workspaceId: 'default',
        agentId: 'default.global_general-assistant.6a8',
        conversationView: conversation,
      }),
    );
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
});

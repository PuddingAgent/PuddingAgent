// ── MessageRow.focus 测试夹具 ─────────────────────────────
// 注意：该文件不得包含 jest.mock（umi esbuild+babel 管道无法剥离其中的类型导入），
// 类型化工厂集中在 .ts 文件中由测试文件复用。
import type { ChatMessageBlock } from '../types';

export const makeAgentBlock = (
  overrides: Partial<ChatMessageBlock> = {},
): ChatMessageBlock => ({
  id: 'assistant-1',
  turnId: 'turn-1',
  role: 'agent',
  content: '这是一段完整的 Agent 回复内容，用于验证展开后完整渲染。',
  status: 'success',
  createdAt: 1000,
  agentName: 'Pudding',
  agentAvatarEmoji: '🤖',
  agentAvatarColor: '#7c3aed',
  ...overrides,
});

export const makeUserBlock = (
  overrides: Partial<ChatMessageBlock> = {},
): ChatMessageBlock => ({
  id: 'user-1',
  turnId: 'turn-1',
  role: 'user',
  content: '用户提问内容预览',
  status: 'success',
  createdAt: 1000,
  ...overrides,
});

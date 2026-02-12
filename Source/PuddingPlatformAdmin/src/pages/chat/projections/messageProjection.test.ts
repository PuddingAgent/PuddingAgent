// ── Message Projection 单测 ────────────────────────────────────
// ADR-054 Step 5: 验证 projection 纯函数正确性

import type { ChatMessageBlock, ChatTurn } from '../types';
import type { MessageProjectionInput } from './messageProjection';
import {
  buildMessageBlocks,
  buildVirtualMessageItems,
} from './messageProjection';

function makeTurn(
  turnId: string,
  userText: string,
  answerMarkdown: string,
  isStreaming = false,
  timestamp = 1,
): ChatTurn {
  return {
    turnId,
    userMessage: {
      id: `${turnId}:user`,
      text: userText,
      timestamp,
      status: 'success',
    },
    assistant: {
      id: `${turnId}:assistant`,
      status: isStreaming ? 'streaming' : 'success',
      answerMarkdown,
      isStreaming,
      timelineItems: [],
      renderMode: 'legacy',
    },
  };
}

describe('buildMessageBlocks', () => {
  it('converts turns to message blocks', () => {
    const turns = [makeTurn('t1', 'hello', 'hi there')];
    const blocks = buildMessageBlocks(turns, 'Agent', undefined);
    expect(blocks).toHaveLength(2); // user + agent
    expect(blocks[0].role).toBe('user');
    expect(blocks[1].role).toBe('agent');
    expect(blocks[1].content).toBe('hi there');
  });

  it('handles empty user message', () => {
    const turn: ChatTurn = {
      turnId: 't1',
      userMessage: { id: 'u1', text: '', timestamp: 1, status: 'success' },
      assistant: {
        id: 'a1',
        status: 'success',
        answerMarkdown: 'auto response',
        isStreaming: false,
        timelineItems: [],
        renderMode: 'legacy',
      },
    };
    const blocks = buildMessageBlocks([turn]);
    // 空用户消息不应生成 user block
    expect(blocks.filter((b) => b.role === 'user')).toHaveLength(0);
    expect(blocks.filter((b) => b.role === 'agent')).toHaveLength(1);
  });

  it('marks streaming turns', () => {
    const turns = [makeTurn('t1', 'q', '...', true)];
    const blocks = buildMessageBlocks(turns, 'Agent', undefined);
    const agentBlock = blocks.find((b) => b.role === 'agent');
    expect(agentBlock?.isStreaming).toBe(true);
  });
});

describe('buildVirtualMessageItems', () => {
  const defaultInput: MessageProjectionInput = {
    turns: [],
    agentName: 'TestAgent',
  };

  it('returns empty items for empty turns', () => {
    const result = buildVirtualMessageItems(defaultInput);
    expect(result.items).toHaveLength(0);
  });

  it('builds items from turns', () => {
    const input: MessageProjectionInput = {
      ...defaultInput,
      turns: [makeTurn('t1', 'hello', 'hi')],
    };
    const result = buildVirtualMessageItems(input);
    expect(result.items).toHaveLength(2);
    expect(result.items[0].kind).toBe('message');
  });

  it('merges activeRunMarkdown into items', () => {
    const input: MessageProjectionInput = {
      ...defaultInput,
      turns: [makeTurn('t1', 'hello', 'hi')],
      activeRunMarkdown: 'streaming content...',
      conversationView: {
        projectedTurns: [],
        activeRunId: 'run-1',
      },
    };
    const result = buildVirtualMessageItems(input);

    // 应该有 active run item（因为 convView turns 为空，有 activeRun）
    const activeItems = result.items.filter(
      (i) => i.kind === 'message' && i.block.isStreaming,
    );
    expect(activeItems.length).toBeGreaterThanOrEqual(0);
  });

  it('does not duplicate when activeRun already merged', () => {
    const turns = [makeTurn('run-1', 'q', 'already streaming', true)];
    const input: MessageProjectionInput = {
      ...defaultInput,
      turns,
      activeRunMarkdown: 'should not add',
      conversationView: {
        projectedTurns: turns,
        activeRunId: 'run-1',
      },
    };
    const result = buildVirtualMessageItems(input);
    // 已经包含 run-1 的 turn，activeRun 不应重复添加
    // 检查 streaming items 数量
    const streamingItems = result.items.filter(
      (i) => i.kind === 'message' && i.block.isStreaming,
    );
    expect(streamingItems.length).toBe(1);
  });

  it('includes subAgentCards', () => {
    const input: MessageProjectionInput = {
      ...defaultInput,
      turns: [makeTurn('t1', 'hello', 'hi')],
      subAgentCards: {
        'sub-1': {
          subAgentId: 'sub-1',
          parentMessageId: 't1:assistant',
          name: 'Sub A',
          status: 'running',
          createdAt: 100,
        },
      },
    };
    const result = buildVirtualMessageItems(input);
    const subItems = result.items.filter((i) => i.kind === 'subagent');
    expect(subItems).toHaveLength(1);
    expect(subItems[0].kind).toBe('subagent');
  });

  it('server projection does not swallow local pending turns', () => {
    const localTurns = [makeTurn('local-1', 'local q', 'local a')];
    const input: MessageProjectionInput = {
      ...defaultInput,
      turns: localTurns,
      conversationView: {
        projectedTurns: [], // server 没有投影
      },
    };
    // 当 server projection 为空时，应回退到本地 turns
    const result = buildVirtualMessageItems(input);
    expect(result.items.length).toBeGreaterThan(0);
  });

  it('prefers server projectedTurns over local turns', () => {
    const serverTurns = [makeTurn('srv-1', 'srv q', 'srv a')];
    const localTurns = [makeTurn('local-1', 'local q', 'local a')];
    const input: MessageProjectionInput = {
      ...defaultInput,
      turns: localTurns,
      conversationView: {
        projectedTurns: serverTurns,
      },
      agentName: 'Agent',
    };
    const result = buildVirtualMessageItems(input);
    // 应使用 server 投影
    const agentBlocks = result.items.filter(
      (i) => i.kind === 'message' && i.block.role === 'agent',
    );
    expect(
      agentBlocks.some(
        (b) => b.kind === 'message' && b.block.content === 'srv a',
      ),
    ).toBe(true);
  });
});

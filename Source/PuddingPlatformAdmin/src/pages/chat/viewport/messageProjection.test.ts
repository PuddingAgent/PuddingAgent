import { buildVirtualMessageItems } from './messageProjection';
import type { ChatTurn } from '../types';

const makeTurn = (turnId: string, timestamp: number, answer = 'answer'): ChatTurn => ({
  turnId,
  source: {
    sourceId: 'agent-1',
    sourceType: 'agent',
    displayName: 'Agent',
    avatarColor: '#7c3aed',
    avatarEmoji: '🤖',
  },
  userMessage: {
    id: `user-${turnId}`,
    text: `Question ${turnId}`,
    timestamp,
    status: 'success',
  },
  assistant: {
    id: `assistant-${turnId}`,
    status: 'success',
    timelineItems: [],
    answerMarkdown: answer,
    isStreaming: false,
    renderMode: 'structured',
  },
});

describe('buildVirtualMessageItems', () => {
  it('creates stable message-level ids for user and agent blocks', () => {
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
    });

    expect(result.items.map((item) => item.id)).toEqual([
      'message:user:t1:user',
      'message:agent:t1:assistant:0',
    ]);
  });

  it('preserves system-command source identity through message projection', () => {
    const turn = makeTurn('system-turn', 1000, 'Runtime mode is now Yolo');
    turn.source = {
      sourceId: 'system',
      sourceType: 'system_command',
      displayName: 'System',
      avatarColor: '#1677ff',
      avatarEmoji: '⚙',
    };

    const result = buildVirtualMessageItems({
      turns: [turn],
      agentName: 'Agent',
    });
    const systemResponse = result.items.find(
      (item) => item.kind === 'message' && item.block.role === 'agent',
    );

    expect(systemResponse).toMatchObject({
      kind: 'message',
      block: {
        agentId: 'system',
        sourceType: 'system_command',
        agentName: 'System',
        agentAvatarEmoji: '⚙',
      },
    });
  });

  it('adds loader before messages when older history exists', () => {
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
      sessionId: 'session-1',
      hasMoreBefore: true,
    });

    expect(result.items[0]).toMatchObject({
      kind: 'loader',
      id: 'loader:before:session-1',
      direction: 'before',
    });
  });

});

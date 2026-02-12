import { buildVirtualMessageItems } from './messageProjection';
import type { ChatTurn, SubAgentCardMap } from '../types';

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

  it('keeps sub-agent cards as independent virtual items ordered by time', () => {
    const subAgentCards: SubAgentCardMap = {
      sub1: {
        turnId: 'sub1',
        subSessionId: 'sub-session-1',
        taskSummary: 'Sub task',
        status: 'running',
        spawnedAt: 1500,
      },
    };

    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
      subAgentCards,
    });

    expect(result.items.map((item) => item.id)).toEqual([
      'message:user:t1:user',
      'message:agent:t1:assistant:0',
      'subagent:sub1',
    ]);
  });
});

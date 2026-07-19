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

  it('projects sub-agent runs into a stable lightweight anchor', () => {
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
      'subagent-anchor:sub1',
    ]);
  });

  it('groups batch child runs into one anchor', () => {
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
      subAgentCards: {
        one: {
          turnId: 'one',
          runId: 'run-1',
          batchId: 'batch-1',
          subSessionId: 'sub-1',
          taskSummary: 'one',
          status: 'running',
          spawnedAt: 1500,
        },
        two: {
          turnId: 'two',
          runId: 'run-2',
          batchId: 'batch-1',
          subSessionId: 'sub-2',
          taskSummary: 'two',
          status: 'running',
          spawnedAt: 1600,
        },
      },
    });

    const anchor = result.items.find(
      (item) => item.kind === 'subagent-anchor',
    );
    expect(anchor).toMatchObject({
      id: 'subagent-anchor:batch-1',
      cards: [{ runId: 'run-1' }, { runId: 'run-2' }],
    });
  });
});

import type { ChatTurn } from '../types';
import {
  confirmOptimisticTurn,
  removeTrackedActiveMessageIdsForTurn,
} from './useChatState';

describe('canonical Turn identity convergence', () => {
  it('reconciles the optimistic Turn from turn.accepted identity', () => {
    const turns: ChatTurn[] = [
      {
        turnId: 'optimistic-turn',
        userMessage: {
          id: 'client-message',
          text: 'hello',
          timestamp: 1,
          status: 'sending',
        },
        assistant: {
          id: 'optimistic-assistant',
          status: 'thinking',
          timelineItems: [],
          answerMarkdown: '',
          isStreaming: true,
        },
      },
    ];

    const result = confirmOptimisticTurn(
      turns,
      'optimistic-turn',
      'server-turn',
      'client-message',
    );

    expect(result[0].turnId).toBe('server-turn');
    expect(result[0].userMessage.status).toBe('success');
  });

  it('clears both acceptance and assistant message identities by Turn', () => {
    const active = new Set([
      'client-user-message',
      'assistant-message',
      'other-message',
    ]);
    const identities = new Map([
      ['client-user-message', 'turn-1'],
      ['assistant-message', 'turn-1'],
      ['other-message', 'turn-2'],
    ]);

    const removed = removeTrackedActiveMessageIdsForTurn(
      active,
      identities,
      'turn-1',
      'assistant-message',
    );

    expect(removed).toBe(2);
    expect([...active]).toEqual(['other-message']);
  });
});

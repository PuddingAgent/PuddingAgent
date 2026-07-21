import type { ChatTurn } from '../types';
import {
  confirmOptimisticTurn,
  getChatRouteSelectionFromSearch,
  resolveTerminalAssistantMarkdown,
} from './chatStateUtils';

const createTurn = (): ChatTurn => ({
  turnId: 'optimistic-turn',
  userMessage: {
    id: 'optimistic-message',
    text: 'hello',
    timestamp: 1,
    status: 'sending',
  },
  assistant: {
    id: 'assistant-message',
    status: 'thinking',
    timelineItems: [],
    answerMarkdown: '',
    isStreaming: true,
    renderMode: 'structured',
  },
});

describe('chatStateUtils module boundary', () => {
  it('confirms an optimistic turn without mutating the original turn', () => {
    const original = createTurn();

    const [confirmed] = confirmOptimisticTurn(
      [original],
      'optimistic-turn',
      'confirmed-turn',
      'confirmed-message',
    );

    expect(confirmed.turnId).toBe('confirmed-turn');
    expect(confirmed.userMessage).toMatchObject({
      id: 'confirmed-message',
      status: 'success',
    });
    expect(original.turnId).toBe('optimistic-turn');
    expect(original.userMessage.status).toBe('sending');
  });

  it('normalizes route selection and ignores blank query values', () => {
    expect(
      getChatRouteSelectionFromSearch(
        '?workspaceId=default&agentId=%20&sessionId=session-1',
      ),
    ).toEqual({ workspaceId: 'default', sessionId: 'session-1' });
  });

  it('merges a terminal reply with the streamed prefix exactly once', () => {
    expect(resolveTerminalAssistantMarkdown('hello', 'hello world')).toBe(
      'hello world',
    );
    expect(resolveTerminalAssistantMarkdown('hello world', 'hello world')).toBe(
      'hello world',
    );
  });
});

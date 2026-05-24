import type { ChatTurn } from '../types';
import {
  shouldAdvanceSequenceForSessionEvent,
  shouldHydrateSessionEventReplay,
  shouldReplayEventsAfterHistory,
} from './useChatState';

const turn = (answerMarkdown: string): ChatTurn => ({
  turnId: `turn-${answerMarkdown || 'empty'}`,
  userMessage: {
    id: 'user-1',
    text: '如何进行评估',
    timestamp: 1,
    status: 'success',
  },
  assistant: {
    id: 'assistant-1',
    status: answerMarkdown ? 'success' : 'thinking',
    timelineItems: [],
    answerMarkdown,
    isStreaming: !answerMarkdown,
    renderMode: 'structured',
  },
});

describe('chat session recovery decisions', () => {
  it('does not advance the event sequence when a turn-scoped event has no target turn yet', () => {
    expect(shouldAdvanceSequenceForSessionEvent('delta', false)).toBe(false);
    expect(shouldAdvanceSequenceForSessionEvent('thinking', false)).toBe(false);
    expect(shouldAdvanceSequenceForSessionEvent('done', false)).toBe(false);
  });

  it('does not let terminal session events skip replay before the target turn exists', () => {
    expect(shouldAdvanceSequenceForSessionEvent('session.closed', false)).toBe(false);
    expect(shouldAdvanceSequenceForSessionEvent('session.closed', true)).toBe(true);
  });

  it('replays event history after loading a session whose latest assistant answer is still empty', () => {
    expect(shouldReplayEventsAfterHistory([turn('')])).toBe(true);
    expect(shouldReplayEventsAfterHistory([turn('完整回答')])).toBe(false);
    expect(shouldReplayEventsAfterHistory([])).toBe(false);
  });

  it('hydrates completed event replays instead of visually streaming them again', () => {
    expect(shouldHydrateSessionEventReplay([
      { type: 'delta' },
      { type: 'done' },
    ])).toBe(true);
    expect(shouldHydrateSessionEventReplay([
      { type: 'delta' },
      { type: 'thinking' },
    ])).toBe(false);
  });
});

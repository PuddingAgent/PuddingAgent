import { chronologicalHistory, getAgentConversationFromSnapshot, mapSnapshotHistoryItem } from './conversationSnapshotAdapter';
import type {
  ConversationOpenSnapshot,
  ConversationTurnSummaryView,
} from './conversationOpenTypes';
import type { AgentConversationView } from './types';

const summary = (turnId: string): ConversationTurnSummaryView => ({
  turnId,
  rootRunId: 'run-1',
  userMessageId: 'msg-user',
  assistantMessageId: 'msg-agent',
  terminalState: 1,
  hasAssistantBody: true,
  process: {
    contentAppendCount: 3,
    thinkingAppendCount: 2,
    toolCallRequestedCount: 1,
    toolCallCompletedCount: 1,
    toolCallFailedCount: 0,
    subAgentRunCount: 0,
    completeness: 2,
    incompleteReason: null,
    revision: 1,
  },
  activePreview: 'preview',
  activePreviewTruncated: false,
  firstSequence: 1,
  lastSequence: 2,
  revision: 1,
});

const snapshot = (overrides: Partial<ConversationOpenSnapshot> = {}): ConversationOpenSnapshot => ({
  conversationId: 'conv-1',
  snapshotCursor: 10,
  eventHead: 12,
  initialization: 3,
  syncState: 0,
  retryAfterMs: null,
  representationTag: 'tag',
  history: {
    items: [
      {
        messageId: 'msg-user',
        role: 'user',
        turnId: 'turn-1',
        createdAt: 1_000,
        preview: 'hello',
        hasMoreContent: false,
        contentLength: 5,
        hasContentParts: false,
      },
      {
        messageId: 'msg-agent',
        role: 'agent',
        turnId: 'turn-1',
        createdAt: 1_100,
        preview: 'answer',
        hasMoreContent: false,
        contentLength: 6,
        hasContentParts: false,
      },
    ].reverse(),
    nextBeforeCreatedAt: null,
    nextBeforeMessageId: null,
    hasMore: false,
  },
  activeTurn: null,
  visibleTurns: [summary('turn-1')],
  runtime: {
    compactionRunning: false,
    compactionRunId: null,
    compactionLastAttemptFailed: false,
    subAgentRuns: [],
    subAgentRunsOmitted: 0,
    initialization: 3,
    readModelVersion: 1,
    revision: 1,
  },
  completeness: 2,
  completenessReason: null,
  ...overrides,
});

const legacyView: AgentConversationView = {
  workspaceId: 'ws',
  ownerUserId: 'single-user',
  agentId: 'agent-1',
  mainSessionId: 'conv-legacy',
  messages: [],
  eventCursor: 99,
  updatedAt: '2026-10-18T00:00:00.000Z',
};

describe('getAgentConversationFromSnapshot', () => {
  it('maps snapshot history and read-model turn summaries to the render contract', async () => {
    const getContent = jest.fn();
    const getLegacyConversation = jest.fn();

    const view = await getAgentConversationFromSnapshot(
      'ws',
      'agent-1',
      'single-user',
      undefined,
      {
        getSnapshot: async () => snapshot(),
        getContent,
        getLegacyConversation,
      },
    );

    expect(getLegacyConversation).not.toHaveBeenCalled();
    expect(getContent).not.toHaveBeenCalled();
    expect(view).toMatchObject({
      workspaceId: 'ws',
      agentId: 'agent-1',
      mainSessionId: 'conv-1',
      eventCursor: 10,
    });
    expect(view!.messages).toHaveLength(2);
    expect(view!.messages[0]).toMatchObject({
      messageId: 'msg-user',
      role: 'user',
      content: 'hello',
      turnId: 'turn-1',
      status: 'succeeded',
      createdAt: new Date(1_000).toISOString(),
    });
    expect(view!.messages[1]).toMatchObject({
      messageId: 'msg-agent',
      role: 'agent',
      content: 'answer',
      processSummary: {
        totalItems: 7,
        thinkingSteps: 2,
        toolCalls: 1,
        toolResults: 1,
        failedTools: 0,
        hasDetails: true,
      },
      turnOutcome: { status: 'succeeded' },
    });
    expect(view).toMatchObject({
      hasMoreHistory: false,
      historyNextBeforeCreatedAt: null,
      historyNextBeforeMessageId: null,
    });
  });

  it('keeps expansion as metadata without injecting markup into the message body', async () => {
    const longItem = {
      messageId: 'msg-long',
      role: 'agent' as const,
      turnId: 'turn-1',
      createdAt: 1_000,
      preview: 'a'.repeat(50),
      hasMoreContent: true,
      contentLength: 100,
      hasContentParts: false,
    };
    const getContent = jest.fn();

    const view = await getAgentConversationFromSnapshot(
      'ws',
      'agent-1',
      'single-user',
      undefined,
      {
        getSnapshot: async () =>
          snapshot({
            history: {
              items: [longItem],
              nextBeforeCreatedAt: null,
              nextBeforeMessageId: null,
              hasMore: false,
            },
            visibleTurns: [summary('turn-1')],
          }),
        getContent,
      },
    );

    expect(getContent).not.toHaveBeenCalled();
    expect(view!.messages[0].content).toBe(longItem.preview);
    expect(view!.messages[0].hasMoreContent).toBe(true);
    expect(view!.messages[0].contentLength).toBe(100);
  });

  it('reverses server tie order without sorting or mutating the wire page', () => {
    const items = ['last-id', 'first-id'].map((messageId) => ({
      ...snapshot().history.items[0], messageId, createdAt: 1000,
    }));
    expect(chronologicalHistory(items).map((item) => item.messageId)).toEqual(['first-id', 'last-id']);
    expect(items[0].messageId).toBe('last-id');
  });

  it('preserves normalized system identity and canonical image parts', () => {
    const item = { ...snapshot().history.items[0], role: 'system',
      sourceKind: 'system' as const, sourceId: 'heartbeat', sourceName: '系统心跳',
      messageType: 'heartbeat', llmRole: 'user', preview: '检查任务',
      contentParts: [{ type: 'image', artifactId: 'image-1', detail: 'original' }],
    };
    expect(mapSnapshotHistoryItem(item, undefined)).toMatchObject({
      role: 'system', sourceKind: 'system', sourceId: 'heartbeat',
      sourceName: '系统心跳', messageType: 'heartbeat', llmRole: 'user',
      content: '检查任务', contentParts: item.contentParts,
    });
  });

  it('leaves ordinary assistant names to the selected Agent while preserving explicit identities', () => {
    const item = { ...snapshot().history.items[0], role: 'assistant' };
    expect(mapSnapshotHistoryItem(item, undefined).sourceName).toBe('');
    expect(mapSnapshotHistoryItem({ ...item, sourceName: '审阅助手' }, undefined).sourceName).toBe('审阅助手');
  });

  it('falls back to the legacy projection while an active turn is not terminal', async () => {
    const active = summary('turn-active');
    active.terminalState = 0;
    const getLegacyConversation = jest.fn().mockResolvedValue(legacyView);

    const view = await getAgentConversationFromSnapshot(
      'ws',
      'agent-1',
      'single-user',
      undefined,
      {
        getSnapshot: async () => snapshot({ activeTurn: active }),
        getLegacyConversation,
      },
    );

    expect(getLegacyConversation).toHaveBeenCalledTimes(1);
    expect(view).toBe(legacyView);
  });

  it('falls back to the legacy projection when the new endpoint is unavailable', async () => {
    const getLegacyConversation = jest.fn().mockResolvedValue(legacyView);

    const view = await getAgentConversationFromSnapshot(
      'ws',
      'agent-1',
      'single-user',
      undefined,
      {
        getSnapshot: async () => {
          throw new Error('404');
        },
        getLegacyConversation,
      },
    );

    expect(view).toBe(legacyView);
  });
});

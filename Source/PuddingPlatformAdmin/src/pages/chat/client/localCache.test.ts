import { createMemoryAgentChatCache } from './localCache';

describe('agent chat local cache', () => {
  it('hydrates the last cached conversation for an agent', async () => {
    const cache = createMemoryAgentChatCache();
    await cache.saveConversation({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-a',
      messages: [],
      eventCursor: 12,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });

    await expect(
      cache.loadConversation('default', 'agent-a'),
    ).resolves.toMatchObject({
      ownerUserId: 'single-user',
      mainSessionId: 'session-a',
      eventCursor: 12,
    });
  });

  it('keeps owner scopes separate without exposing users in UI calls', async () => {
    const cache = createMemoryAgentChatCache();
    await cache.saveConversation({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-default',
      messages: [],
      eventCursor: 1,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });
    await cache.saveConversation({
      workspaceId: 'default',
      ownerUserId: 'future-owner',
      agentId: 'agent-a',
      mainSessionId: 'session-future',
      messages: [],
      eventCursor: 2,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });

    await expect(
      cache.loadConversation('default', 'agent-a'),
    ).resolves.toMatchObject({
      mainSessionId: 'session-default',
    });
    await expect(
      cache.loadConversation('default', 'agent-a', 'future-owner'),
    ).resolves.toMatchObject({
      mainSessionId: 'session-future',
    });
  });
});

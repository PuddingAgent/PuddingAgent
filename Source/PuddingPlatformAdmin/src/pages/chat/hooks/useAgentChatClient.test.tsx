import { act, renderHook, waitFor } from '@testing-library/react';
import { createMemoryAgentChatCache } from '../client/localCache';
import { useAgentChatClient } from './useAgentChatClient';

describe('useAgentChatClient', () => {
  it('exposes selected cached conversation through React', async () => {
    const cache = createMemoryAgentChatCache();
    await cache.saveConversation({
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-a',
      messages: [],
      eventCursor: 1,
      updatedAt: '2026-06-07T00:00:00.000Z',
    });

    const api = {
      listStatuses: async () => [],
      getConversation: async () => ({
        workspaceId: 'default',
        ownerUserId: 'single-user',
        agentId: 'agent-a',
        mainSessionId: 'session-a',
        messages: [],
        eventCursor: 2,
        updatedAt: '2026-06-07T00:00:01.000Z',
      }),
    };

    const { result } = renderHook(() => useAgentChatClient({ cache, api }));

    await act(async () => {
      await result.current.selectAgent('default', 'agent-a');
    });

    await waitFor(() =>
      expect(result.current.snapshot.conversation?.mainSessionId).toBe(
        'session-a',
      ),
    );
    expect(result.current.snapshot.conversation?.eventCursor).toBe(2);
  });

  it('exposes status refresh through React', async () => {
    const cache = createMemoryAgentChatCache();
    const { result } = renderHook(() =>
      useAgentChatClient({
        cache,
        api: {
          listStatuses: async () => [
            {
              workspaceId: 'default',
              ownerUserId: 'single-user',
              agentId: 'agent-a',
              mainSessionId: 'session-a',
              status: 'running',
              summary: '执行中',
              unreadCount: 0,
              eventCursor: 1,
              updatedAt: '2026-06-07T00:00:00.000Z',
            },
          ],
          getConversation: async () => {
            throw new Error('not used');
          },
        },
      }),
    );

    await act(async () => {
      await result.current.refreshStatuses('default');
    });

    expect(result.current.snapshot.statuses[0]?.status).toBe('running');
  });

  it('exposes selected Agent background sync through React', async () => {
    const cache = createMemoryAgentChatCache();
    const { result } = renderHook(() =>
      useAgentChatClient({
        cache,
        api: {
          listStatuses: async () => [],
          getConversation: async (_workspaceId, agentId) => ({
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId,
            mainSessionId: `session-${agentId}`,
            messages: [
              {
                messageId: 'agent-1',
                role: 'agent',
                sourceId: agentId,
                sourceName: 'Agent A',
                createdAt: '2026-06-07T00:00:01.000Z',
                content: 'hook background output',
                status: 'streaming',
                processItems: [],
              },
            ],
            eventCursor: 3,
            updatedAt: '2026-06-07T00:00:01.000Z',
          }),
        },
      }),
    );

    await act(async () => {
      await result.current.selectAgent('default', 'agent-a');
      await result.current.syncSelectedAgent();
    });

    expect(result.current.snapshot.conversation?.messages[0]?.content).toBe(
      'hook background output',
    );
  });
});

import { createAgentChatSyncEngine } from './syncEngine';

describe('agent chat sync engine', () => {
  it('requests events after the current cursor', async () => {
    const calls: string[] = [];
    const engine = createAgentChatSyncEngine({
      fetchEvents: async (_workspaceId, _agentId, after) => {
        calls.push(String(after));
        return { events: [], nextCursor: after };
      },
      applyEvents: () => undefined,
    });

    await engine.replay('default', 'agent-a', 42);

    expect(calls).toEqual(['42']);
  });
});

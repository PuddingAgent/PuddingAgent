import { resolveConversationExecution } from './conversationExecution';
import type { AgentConversationView } from './types';

const conversation = (status: string = 'running', turnId: string | undefined = 'turn-1') => ({
  workspaceId: 'ws', agentId: 'agent', mainSessionId: 'session',
  activeRun: { runId: 'not-a-turn', status, outputSnapshot: { window: turnId ? { turnId } : undefined } },
}) as AgentConversationView;

describe('conversation execution ownership', () => {
  it.each(['queued', 'running', 'waiting'])('restores controls for %s after refresh', (status) => {
    expect(resolveConversationExecution(conversation(status), 'ws', 'agent', 'session'))
      .toEqual({ running: true, turnId: 'turn-1' });
  });
  it.each(['succeeded', 'failed', 'cancelled'])('removes controls after %s', (status) => {
    expect(resolveConversationExecution(conversation(status), 'ws', 'agent', 'session'))
      .toEqual({ running: false, turnId: null });
  });
  it('never uses a run identity as a cancellation target', () => {
    const view = conversation();
    view.activeRun!.outputSnapshot.window = null;
    expect(resolveConversationExecution(view, 'ws', 'agent', 'session')).toEqual({ running: true, turnId: null });
  });
  it.each([['other', 'agent', 'session'], ['ws', 'other', 'session'], ['ws', 'agent', 'history']])(
    'does not leak execution controls across scope %s/%s/%s', (ws, agent, session) => {
      expect(resolveConversationExecution(conversation(), ws, agent, session)).toBeUndefined();
    },
  );
  it('idle snapshots are authoritative, absent snapshots are not', () => {
    expect(resolveConversationExecution({ ...conversation(), activeRun: null }, 'ws', 'agent', 'session'))
      .toEqual({ running: false, turnId: null });
    expect(resolveConversationExecution(null, 'ws', 'agent', 'session')).toBeUndefined();
  });
});

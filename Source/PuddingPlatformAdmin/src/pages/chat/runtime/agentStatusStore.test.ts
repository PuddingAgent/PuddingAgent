// ── Agent Status Store 单测 ────────────────────────────────────
// ADR-054 Step 7: 验证 agent status 更新和通知行为

import { createAgentStatusStore } from './agentStatusStore';

describe('AgentStatusStore', () => {
  it('initial state is empty statuses', () => {
    const store = createAgentStatusStore();
    expect(Object.keys(store.getSnapshot().statuses)).toHaveLength(0);
  });

  it('updateStatus adds new status', () => {
    const store = createAgentStatusStore();
    store.updateStatus('agent-a', true, false);
    expect(store.getSnapshot().statuses['agent-a']).toEqual({
      agentId: 'agent-a',
      isWorking: true,
      hasPending: false,
    });
  });

  it('updateStatus does not notify on same values', () => {
    const store = createAgentStatusStore();
    const listener = jest.fn();
    store.subscribe(listener);

    store.updateStatus('agent-a', true, false);
    expect(listener).toHaveBeenCalledTimes(1);

    store.updateStatus('agent-a', true, false); // 相同值
    expect(listener).toHaveBeenCalledTimes(1); // 仍未 1
  });

  it('updateStatus notifies on different values', () => {
    const store = createAgentStatusStore();
    const listener = jest.fn();
    store.subscribe(listener);

    store.updateStatus('agent-a', true, false);
    store.updateStatus('agent-a', false, false);
    expect(listener).toHaveBeenCalledTimes(2);
  });

  it('clearStatus removes agent', () => {
    const store = createAgentStatusStore();
    store.updateStatus('agent-a', true, false);
    store.clearStatus('agent-a');
    expect(store.getSnapshot().statuses['agent-a']).toBeUndefined();
  });

  it('clearAll removes all statuses', () => {
    const store = createAgentStatusStore();
    store.updateStatus('agent-a', true, false);
    store.updateStatus('agent-b', false, true);
    store.clearAll();
    expect(Object.keys(store.getSnapshot().statuses)).toHaveLength(0);
  });

  it('setStatuses replaces all statuses', () => {
    const store = createAgentStatusStore();
    store.updateStatus('agent-a', true, false);
    store.setStatuses({
      'agent-b': { agentId: 'agent-b', isWorking: true, hasPending: true },
    });
    expect(store.getSnapshot().statuses['agent-a']).toBeUndefined();
    expect(store.getSnapshot().statuses['agent-b']).toBeDefined();
  });

  it('subscribe and unsubscribe work correctly', () => {
    const store = createAgentStatusStore();
    const listener = jest.fn();
    const unsub = store.subscribe(listener);

    store.updateStatus('a', true, false);
    expect(listener).toHaveBeenCalledTimes(1);

    unsub();
    store.updateStatus('a', false, false);
    expect(listener).toHaveBeenCalledTimes(1);
  });
});

import { shouldIgnoreAgentContactClick } from './agentMainSessionSelection';

describe('agent main session selection', () => {
  it('does not ignore clicking the selected agent while viewing a non-main session', () => {
    expect(
      shouldIgnoreAgentContactClick({
        clickedAgentId: 'agent-a',
        currentAgentId: 'agent-a',
        selectedSessionId: 'task-session',
        mainSessionId: 'main-session',
        turnCount: 2,
      }),
    ).toBe(false);
  });

  it('ignores clicking the selected agent only when already viewing its main session', () => {
    expect(
      shouldIgnoreAgentContactClick({
        clickedAgentId: 'agent-a',
        currentAgentId: 'agent-a',
        selectedSessionId: 'main-session',
        mainSessionId: 'main-session',
        turnCount: 2,
      }),
    ).toBe(true);
  });
});

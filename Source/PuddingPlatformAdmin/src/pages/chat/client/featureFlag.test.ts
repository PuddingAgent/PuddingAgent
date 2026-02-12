import { isAgentClientArchitectureEnabled } from './featureFlag';

describe('isAgentClientArchitectureEnabled', () => {
  beforeEach(() => {
    localStorage.removeItem('pudding-agent-client-arch');
  });

  it('enables the client architecture by default', () => {
    expect(isAgentClientArchitectureEnabled()).toBe(true);
  });

  it('allows local rollback when explicitly disabled', () => {
    localStorage.setItem('pudding-agent-client-arch', '0');

    expect(isAgentClientArchitectureEnabled()).toBe(false);
  });
});

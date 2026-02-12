const AGENT_CLIENT_ARCHITECTURE_KEY = 'pudding-agent-client-arch';

export const isAgentClientArchitectureEnabled = () => {
  if (typeof localStorage === 'undefined') return true;
  return localStorage.getItem(AGENT_CLIENT_ARCHITECTURE_KEY) !== '0';
};

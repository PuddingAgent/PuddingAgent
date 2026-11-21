import { agentAdapterPrompt, agentApiDocumentation } from './onboarding';

test('copies registration and inbox instructions without including the secret', () => {
  const binding = { agentId: 'external-agent-a', workspaceId: 'dev', ak: 'test-public-ak', sk: 'test-secret' };
  const prompt = agentAdapterPrompt('http://127.0.0.1:8080/', binding);
  expect(prompt).toContain('AK test-public-ak');
  expect(prompt).not.toContain(binding.sk);
  expect(prompt).toContain('/agent-adapters/register');
  expect(prompt).toContain('/agent-adapters/inbox');
  expect(prompt).toContain('nextCursor');
  expect(prompt).toContain('/agent-adapters/delegations');
  expect(prompt).toContain('/delegations/<messageId>/result');
  expect(agentApiDocumentation('http://127.0.0.1:8080/'))
    .toBe('http://127.0.0.1:8080/agent-adapters/docs/agent-adapters-v1.json');
});

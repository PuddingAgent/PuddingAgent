import {
  buildGlobalAgentTemplateRequest,
  type GlobalAgentTemplateRequestDefaults,
} from './index';
import type { UpsertGlobalAgentTemplateRequest } from '@/services/platform/api';

describe('buildGlobalAgentTemplateRequest', () => {
  it('normalizes capability, skill, model, prompt, and guardrail fields before saving', () => {
    const values: UpsertGlobalAgentTemplateRequest = {
      templateId: 'assistant',
      name: 'Assistant',
      role: 'Service',
      systemPrompt: 'system',
      userPromptTemplate: '{{input}}',
      maxContextTokens: undefined as unknown as number,
      maxReplyTokens: undefined as unknown as number,
      selectedCapabilityIds: [],
      selectedSkillPackageIds: [],
      isEnabled: undefined as unknown as boolean,
      sortOrder: undefined as unknown as number,
      maxRounds: undefined,
      maxElapsedSeconds: undefined,
      maxToolCallsTotal: undefined,
      memorySearchMode: undefined,
      agentsPrompt: 'agents',
      memoryPrompt: 'memory',
      consciousProfileId: 'profile.conscious',
      subconsciousProfileId: 'profile.subconscious',
    };

    const defaults: GlobalAgentTemplateRequestDefaults = {
      defaultCapIds: ['cap-http-fetch'],
      grantTargetKeys: ['cap-python', 'cap-python'],
      skillTargetKeys: ['skill-a'],
    };

    const normalized = buildGlobalAgentTemplateRequest(values, defaults);

    expect(normalized.selectedCapabilityIds).toEqual(['cap-http-fetch', 'cap-python']);
    expect(normalized.selectedSkillPackageIds).toEqual(['skill-a']);
    expect(normalized.memorySearchMode).toBe('deep');
    expect(normalized.maxRounds).toBe(200);
    expect(normalized.maxElapsedSeconds).toBe(1200);
    expect(normalized.maxToolCallsTotal).toBe(100);
    expect(normalized.maxContextTokens).toBe(8192);
    expect(normalized.maxReplyTokens).toBe(2048);
    expect(normalized.isEnabled).toBe(true);
    expect(normalized.sortOrder).toBe(100);
    expect(normalized.agentsPrompt).toBe('agents');
    expect(normalized.memoryPrompt).toBe('memory');
    expect(normalized.consciousProfileId).toBe('profile.conscious');
    expect(normalized.subconsciousProfileId).toBe('profile.subconscious');
  });
});

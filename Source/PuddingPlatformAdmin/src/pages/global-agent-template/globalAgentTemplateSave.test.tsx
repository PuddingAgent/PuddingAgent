import {
  buildGlobalAgentTemplateRequest,
  resolvePresetImportStates,
  type GlobalAgentTemplateRequestDefaults,
} from './index';
import type { GlobalAgentTemplateDto, UpsertGlobalAgentTemplateRequest } from '@/services/platform/api';

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
      legacyMaxContextTokens: 131_072,
      legacyMaxReplyTokens: 4096,
    };

    const normalized = buildGlobalAgentTemplateRequest(values, defaults);

    expect(normalized.selectedCapabilityIds).toEqual(['cap-http-fetch', 'cap-python']);
    expect(normalized.selectedSkillPackageIds).toEqual(['skill-a']);
    expect(normalized.memorySearchMode).toBe('deep');
    expect(normalized.maxRounds).toBe(200);
    expect(normalized.maxElapsedSeconds).toBe(1200);
    expect(normalized.maxToolCallsTotal).toBe(100);
    expect(normalized.maxContextTokens).toBe(131_072);
    expect(normalized.maxReplyTokens).toBe(4096);
    expect(normalized.isEnabled).toBe(true);
    expect(normalized.sortOrder).toBe(100);
    expect(normalized.agentsPrompt).toBe('agents');
    expect(normalized.memoryPrompt).toBe('memory');
    expect(normalized.consciousProfileId).toBe('profile.conscious');
    expect(normalized.subconsciousProfileId).toBe('profile.subconscious');
  });
});

describe('resolvePresetImportStates', () => {
  it('marks software preset templates that already exist in the editable template store', () => {
    const presets = [
      { templateId: 'general-assistant', name: '通用助手' },
      { templateId: 'workspace-audit-assistant', name: '审计助手' },
    ] as GlobalAgentTemplateDto[];
    const templates = [
      { templateId: 'general-assistant', name: '通用助手' },
    ] as GlobalAgentTemplateDto[];

    const result = resolvePresetImportStates(presets, templates);

    expect(result.map((item) => [item.templateId, item.imported])).toEqual([
      ['general-assistant', true],
      ['workspace-audit-assistant', false],
    ]);
  });
});

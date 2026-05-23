// ── Section Registry — 定义设置分组与表单字段映射 ──────────

export type AgentTemplateSectionKey =
  | 'basic'
  | 'capabilities'
  | 'prompts'
  | 'models'
  | 'guardrails';

export interface SettingsSectionMeta {
  key: AgentTemplateSectionKey;
  label: string;
  description?: string;
  fieldNames: string[];
}

export type SectionStatus = 'normal' | 'active' | 'error';

export type AgentTemplateScope = 'global' | 'workspace';

/** 分组注册表：各分组 ID、标题、归属字段列表 */
export const AGENT_TEMPLATE_SECTIONS: SettingsSectionMeta[] = [
  {
    key: 'basic',
    label: '基础信息',
    fieldNames: [
      'workspaceId',
      'baseGlobalTemplateId',
      'templateId',
      'name',
      'role',
      'description',
      'avatarId',
      'avatarEmoji',
      'isEnabled',
      'sortOrder',
    ],
  },
  {
    key: 'capabilities',
    label: '能力与 Skill',
    fieldNames: ['selectedCapabilityIds', 'selectedSkillPackageIds'],
  },
  {
    key: 'prompts',
    label: 'Prompt 与个性',
    fieldNames: [
      'systemPrompt',
      'personaPrompt',
      'toolsDescription',
      'bootstrapTemplate',
      'userPromptTemplate',
    ],
  },
  {
    key: 'models',
    label: '模型与记忆',
    fieldNames: [
      'preferredProviderId',
      'preferredModelId',
      'memoryLlmProviderId',
      'memoryLlmModelId',
      'memorySearchMode',
      'reasoningEffort',
    ],
  },
  {
    key: 'guardrails',
    label: '执行护栏',
    fieldNames: [
      'maxRounds',
      'maxElapsedSeconds',
      'maxToolCallsTotal',
      'containerImage',
      'maxContextTokens',
      'maxReplyTokens',
    ],
  },
];

/** 根据字段名查找所属分组 */
export function findSectionByField(fieldName: string): SettingsSectionMeta {
  return (
    AGENT_TEMPLATE_SECTIONS.find((section) =>
      section.fieldNames.includes(fieldName),
    ) ?? AGENT_TEMPLATE_SECTIONS[0]
  );
}

/** 从错误字段列表收集所有含错分组 key */
export function collectErrorSections(
  errorFields: { name: (string | number)[] }[],
): Set<AgentTemplateSectionKey> {
  const keys = new Set<AgentTemplateSectionKey>();
  for (const ef of errorFields) {
    const name = String(ef.name[0]);
    const section = findSectionByField(name);
    keys.add(section.key);
  }
  return keys;
}

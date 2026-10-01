import {
  PROMPT_DOCUMENTS,
  PROMPT_DOCUMENT_FIELDS,
  WORKSPACE_AGENT_SECTION_FIELDS,
  collectDirtyFields,
  collectDirtyPromptDocuments,
  collectDirtySections,
  estimateScrollTop,
  extractTemplateVariables,
  findPromptDocument,
  findSectionByWorkspaceField,
  findTextMatches,
  groupPromptDocuments,
  isSameFieldValue,
  summarizeValidationErrors,
} from './promptDocuments';

describe('promptDocuments 目录元数据', () => {
  it('覆盖角色与 Prompt 的八个字段，且与分区字段表一致', () => {
    expect(PROMPT_DOCUMENT_FIELDS).toEqual([
      'systemPrompt',
      'userPromptTemplate',
      'soulMdContent',
      'agentsMdContent',
      'toolsMdContent',
      'memoryMdContent',
      'heartbeatPrompt',
      'bootstrapMdContent',
    ]);
    expect([...WORKSPACE_AGENT_SECTION_FIELDS.prompts].sort()).toEqual(
      [...PROMPT_DOCUMENT_FIELDS].sort(),
    );
  });

  it('按常用 / 行为与协作 / 生命周期分组，且目录内字段名唯一', () => {
    const groups = groupPromptDocuments();
    expect(groups.map((group) => group.label)).toEqual(['常用', '行为与协作', '生命周期']);
    expect(groups.flatMap((group) => group.documents)).toHaveLength(PROMPT_DOCUMENTS.length);
    expect(new Set(PROMPT_DOCUMENT_FIELDS).size).toBe(PROMPT_DOCUMENT_FIELDS.length);
  });

  it('每份文档都有中文职责、次级标签、用途说明与空值提示', () => {
    for (const document of PROMPT_DOCUMENTS) {
      expect(document.label.trim()).not.toBe('');
      expect(document.tag.trim()).not.toBe('');
      expect(document.description.trim()).not.toBe('');
      expect(document.emptyHint.trim()).not.toBe('');
    }
  });

  it('只有心跳提示词声明运行时回退，其他文档空值不得宣称继承默认', () => {
    const fallbackDocuments = PROMPT_DOCUMENTS.filter((document) =>
      document.emptyHint.includes('回退'),
    );
    expect(fallbackDocuments.map((document) => document.field)).toEqual(['heartbeatPrompt']);
  });

  it('字段归属分区可反查', () => {
    expect(findSectionByWorkspaceField('soulMdContent')).toBe('prompts');
    expect(findSectionByWorkspaceField('maxRounds')).toBe('guardrails');
    expect(findSectionByWorkspaceField('unknownField')).toBeUndefined();
    expect(findPromptDocument('agentsMdContent')?.tag).toBe('AGENTS.md');
    expect(findPromptDocument('name')).toBeUndefined();
  });
});

describe('collectDirtyFields 草稿比较', () => {
  it('内容改回原值后不再算作修改', () => {
    const baseline = { systemPrompt: '原始内容', maxRounds: 200 };
    expect(collectDirtyFields({ ...baseline }, baseline, ['systemPrompt', 'maxRounds'])).toEqual([]);
    expect(
      collectDirtyFields({ ...baseline, systemPrompt: '改过了' }, baseline, [
        'systemPrompt',
        'maxRounds',
      ]),
    ).toEqual(['systemPrompt']);
    expect(
      collectDirtyFields({ ...baseline, systemPrompt: '原始内容' }, baseline, [
        'systemPrompt',
        'maxRounds',
      ]),
    ).toEqual([]);
  });

  it('未挂载字段缺失与空字符串等价', () => {
    expect(isSameFieldValue(undefined, '')).toBe(true);
    expect(isSameFieldValue(null, '')).toBe(true);
    expect(isSameFieldValue(0, '')).toBe(false);
    expect(isSameFieldValue([], null)).toBe(false);
    expect(collectDirtyFields({}, { soulMdContent: '' }, ['soulMdContent'])).toEqual([]);
    expect(collectDirtyFields({ soulMdContent: 'x' }, { soulMdContent: '' }, ['soulMdContent'])).toEqual([
      'soulMdContent',
    ]);
  });

  it('数组按元素比较，顺序不同视为修改', () => {
    expect(isSameFieldValue(['a', 'b'], ['a', 'b'])).toBe(true);
    expect(isSameFieldValue(['a', 'b'], ['b', 'a'])).toBe(false);
    expect(isSameFieldValue(['a'], ['a', 'b'])).toBe(false);
  });

  it('统计修改字段数并归类到分区与文档', () => {
    const baseline = {
      systemPrompt: 'A',
      soulMdContent: 'B',
      maxRounds: 200,
      name: '审计员',
    };
    const dirty = collectDirtyFields(
      { ...baseline, systemPrompt: 'A2', soulMdContent: 'B2', maxRounds: 400 },
      baseline,
      ['systemPrompt', 'soulMdContent', 'maxRounds', 'name'],
    );
    expect(dirty).toHaveLength(3);
    expect([...collectDirtySections(dirty)].sort()).toEqual(['guardrails', 'prompts']);
    expect([...collectDirtyPromptDocuments(dirty)].sort()).toEqual([
      'soulMdContent',
      'systemPrompt',
    ]);
  });

  it('多个分区同时修改时计数准确（模型 / 授权 / Prompt）', () => {
    const baseline = {
      systemPrompt: 'A',
      preferredModelId: 'gpt-4',
      selectedCapabilityIds: ['cap-1'],
    };
    const dirty = collectDirtyFields(
      {
        systemPrompt: 'A+',
        preferredModelId: 'gpt-5',
        selectedCapabilityIds: ['cap-1', 'cap-2'],
      },
      baseline,
      ['systemPrompt', 'preferredModelId', 'selectedCapabilityIds'],
    );
    expect(dirty).toHaveLength(3);
    expect([...collectDirtySections(dirty)].sort()).toEqual([
      'capabilities',
      'models',
      'prompts',
    ]);
  });
});

describe('summarizeValidationErrors 错误定位', () => {
  it('归类分区、文档与计数，并给出第一个错误', () => {
    const summary = summarizeValidationErrors([
      { name: ['name'] },
      { name: ['maxRounds'] },
      { name: ['systemPrompt'] },
      { name: ['systemPrompt'] },
    ]);
    expect([...summary.sections].sort()).toEqual(['basic', 'guardrails', 'prompts']);
    expect(summary.countsBySection).toEqual({ basic: 1, guardrails: 1, prompts: 2 });
    expect([...summary.promptDocuments]).toEqual(['systemPrompt']);
    expect(summary.firstField).toBe('name');
    expect(summary.firstSection).toBe('basic');
  });

  it('无法识别归属的字段回退到基础信息', () => {
    const summary = summarizeValidationErrors([{ name: ['mysteryField'] }]);
    expect([...summary.sections]).toEqual(['basic']);
    expect(summary.promptDocuments.size).toBe(0);
  });
});

describe('模板变量与文档内查找', () => {
  it('提取已出现的占位符名称，不重复且不替换原文', () => {
    const text = '你好 {{ user_name }}，今天是 {{date}}，再次欢迎 {{user_name}}。';
    expect(extractTemplateVariables(text)).toEqual(['user_name', 'date']);
    expect(extractTemplateVariables(undefined)).toEqual([]);
    expect(extractTemplateVariables('没有变量')).toEqual([]);
  });

  it('查找命中忽略大小写且不重叠', () => {
    expect(findTextMatches('Abc abc ABC', 'abc')).toEqual([0, 4, 8]);
    expect(findTextMatches('aaaa', 'aa')).toEqual([0, 2]);
    expect(findTextMatches('内容', '')).toEqual([]);
    expect(findTextMatches('内容', '缺失')).toEqual([]);
  });

  it('按行高估算滚动位置，命中在首行时不为负', () => {
    const metrics = { fontSize: 14, lineHeight: 24, paddingTop: 16 };
    expect(estimateScrollTop('第一行', 0, metrics)).toBe(0);
    const text = '第一行\n第二行\n第三行';
    const offset = text.indexOf('第三行');
    expect(estimateScrollTop(text, offset, metrics)).toBe(16 + 2 * 24 - 48);
  });
});

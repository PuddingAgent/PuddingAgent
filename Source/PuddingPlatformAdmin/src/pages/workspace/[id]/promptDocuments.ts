import type { AgentTemplateSectionKey } from '../../agent-template-settings/types';

/**
 * 角色与 Prompt 分区的文档目录元数据。
 *
 * 设计依据：Docs/12_features/Agent-Settings-Redesign-2026-10-01.md 第 4 节。
 * 目录只是现有表单字段的编辑入口，调整显示顺序不改变 Runtime 的 Prompt 组合顺序。
 */

export type PromptDocumentGroupKey = 'common' | 'behavior' | 'lifecycle';

/** 编辑器呈现方式：Markdown 文档可切换预览，纯文本提示词只做原文展示。 */
export type PromptDocumentKind = 'markdown' | 'text';

export interface PromptDocumentMeta {
  /** 表单字段名，必须与 WorkspaceAgentFormValues 的键一致。 */
  field: string;
  /** 目录与编辑器标题：中文职责优先。 */
  label: string;
  /** 次级标签：磁盘文件名或短说明。 */
  tag: string;
  group: PromptDocumentGroupKey;
  kind: PromptDocumentKind;
  /** 编辑器副标题，说明这份内容的用途。 */
  description: string;
  /** 为空时的输入提示。 */
  placeholder: string;
  /**
   * 空值在界面上的说明。
   *
   * 只有心跳提示词存在明确的运行时回退（WorkspaceAgentFileService：留空 → embedded
   * heartbeatPrompt.md → 编译期常量），其他 Markdown 留空后不会回退模板，故统一显示「未填写」。
   */
  emptyHint: string;
  /** 是否可选（可选文档在目录中标注「可选」）。 */
  optional?: boolean;
}

export const PROMPT_DOCUMENT_GROUP_LABELS: Record<PromptDocumentGroupKey, string> = {
  common: '常用',
  behavior: '行为与协作',
  lifecycle: '生命周期',
};

export const PROMPT_DOCUMENT_GROUP_ORDER: PromptDocumentGroupKey[] = [
  'common',
  'behavior',
  'lifecycle',
];

export const PROMPT_DOCUMENTS: PromptDocumentMeta[] = [
  {
    field: 'systemPrompt',
    label: '系统提示词',
    tag: '核心职责与行为',
    group: 'common',
    kind: 'text',
    description: '定义这个 Agent 的核心职责、能力边界与行为准则。',
    placeholder: '定义 Agent 的核心职责、能力边界和行为准则',
    emptyHint: '未填写：不追加系统提示词，仅使用 Runtime 的通用约束。',
  },
  {
    field: 'userPromptTemplate',
    label: '用户消息模板',
    tag: '可选 · 占位符模板',
    group: 'common',
    kind: 'text',
    description: '可选。仅作文本辅助展示，变量不会被替换成猜测值，也不代表运行时一定能解析。',
    placeholder: '可选，支持 {{variable}} 占位符',
    emptyHint: '未填写：直接使用用户的原始消息。',
    optional: true,
  },
  {
    field: 'soulMdContent',
    label: '人设与边界',
    tag: 'SOUL.md',
    group: 'behavior',
    kind: 'markdown',
    description: 'Agent 的语气、立场与行为边界，写入实例目录的 SOUL.md。',
    placeholder: '描述这个 Agent 的表达风格与不可越过的边界',
    emptyHint: '未填写：不生成 SOUL.md，Agent 只使用其他已配置内容。',
  },
  {
    field: 'agentsMdContent',
    label: '协作规范',
    tag: 'AGENTS.md',
    group: 'behavior',
    kind: 'markdown',
    description: '多 Agent 协作、交付与验收约定，写入实例目录的 AGENTS.md。',
    placeholder: '约定协作分工、交付标准与验收方式',
    emptyHint: '未填写：不生成 AGENTS.md，协作只依赖平台默认约定。',
  },
  {
    field: 'toolsMdContent',
    label: '工具约定',
    tag: 'TOOLS.md',
    group: 'behavior',
    kind: 'markdown',
    description: '何时调用工具、如何解释结果与失败降级，写入实例目录的 TOOLS.md。',
    placeholder: '约定工具调用的时机、参数习惯与失败处理',
    emptyHint: '未填写：不生成 TOOLS.md，工具使用只依赖工具自身的说明。',
  },
  {
    field: 'memoryMdContent',
    label: '记忆策略',
    tag: 'MEMORY.md',
    group: 'behavior',
    kind: 'markdown',
    description: '记忆写入、检索与优先级策略，写入实例目录的 MEMORY.md。',
    placeholder: '约定哪些信息值得记住、如何检索与取舍',
    emptyHint: '未填写：不生成 MEMORY.md，记忆行为使用平台默认策略。',
  },
  {
    field: 'heartbeatPrompt',
    label: '心跳恢复',
    tag: 'heartbeatPrompt.md',
    group: 'lifecycle',
    kind: 'markdown',
    description: 'Agent 空闲心跳时收到的提示词，写入实例目录的 heartbeatPrompt.md。',
    placeholder: 'Agent 空闲心跳时收到的提示词；留空使用默认提示词',
    emptyHint: '未填写：运行时回退到平台默认心跳提示词。',
  },
  {
    field: 'bootstrapMdContent',
    label: '首次引导',
    tag: 'BOOTSTRAP.md',
    group: 'lifecycle',
    kind: 'markdown',
    description: '首次会话的开场与引导，写入实例目录的 BOOTSTRAP.md。',
    placeholder: '定义首次对话的开场与引导步骤',
    emptyHint: '未填写：不生成 BOOTSTRAP.md，首次会话直接开始任务。',
  },
];

export const PROMPT_DOCUMENT_FIELDS: string[] = PROMPT_DOCUMENTS.map((doc) => doc.field);

export interface PromptDocumentGroup {
  key: PromptDocumentGroupKey;
  label: string;
  documents: PromptDocumentMeta[];
}

/** 按分组返回目录条目，用于左侧文档目录渲染。 */
export const groupPromptDocuments = (
  documents: PromptDocumentMeta[] = PROMPT_DOCUMENTS,
): PromptDocumentGroup[] =>
  PROMPT_DOCUMENT_GROUP_ORDER.map((key) => ({
    key,
    label: PROMPT_DOCUMENT_GROUP_LABELS[key],
    documents: documents.filter((doc) => doc.group === key),
  })).filter((group) => group.documents.length > 0);

/** 按字段名查文档元数据；字段不属于 Prompt 分区时返回 undefined。 */
export const findPromptDocument = (field: string): PromptDocumentMeta | undefined =>
  PROMPT_DOCUMENTS.find((doc) => doc.field === field);

// ── 工作台分区字段归属 ────────────────────────────────────────────────
// 与 WorkspaceAgentSettingsDrawer 的 SECTION_FIELDS 保持一致，抽出为纯数据便于测试。

export const WORKSPACE_AGENT_SECTION_FIELDS: Record<AgentTemplateSectionKey, string[]> = {
  basic: ['name', 'role', 'description', 'sourceTemplateId', 'avatarId', 'isEnabled'],
  capabilities: ['selectedCapabilityIds', 'skillPackageIds'],
  prompts: PROMPT_DOCUMENT_FIELDS,
  models: [
    'preferredProviderId',
    'preferredModelId',
    'memoryLlmProviderId',
    'memoryLlmModelId',
    'embeddingProviderId',
    'embeddingModelId',
    'memorySearchMode',
    'reasoningEffort',
  ],
  smartModels: [
    'explorerModel',
    'researcherModel',
    'plannerModel',
    'reviewerModel',
    'developerModel',
    'deployerModel',
    'testerModel',
  ],
  guardrails: ['maxRounds', 'maxElapsedSeconds', 'maxToolCallsTotal', 'containerImage'],
};

export const findSectionByWorkspaceField = (
  field: string,
): AgentTemplateSectionKey | undefined =>
  (Object.keys(WORKSPACE_AGENT_SECTION_FIELDS) as AgentTemplateSectionKey[]).find((key) =>
    WORKSPACE_AGENT_SECTION_FIELDS[key].includes(field),
  );

// ── 草稿与基线比较 ──────────────────────────────────────────────────

const isPlainObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

/** 归一化空值：undefined / null 视为空字符串，避免「清空字段」被判为无修改。 */
const normalizeValue = (value: unknown, seen: WeakSet<object> = new WeakSet()): unknown => {
  if (value === undefined || value === null) return '';
  if (Array.isArray(value)) {
    if (seen.has(value)) return '[circular]';
    seen.add(value);
    return value.map((item) => normalizeValue(item, seen));
  }
  if (isPlainObject(value)) {
    if (seen.has(value)) return '[circular]';
    seen.add(value);
    const result: Record<string, unknown> = {};
    for (const key of Object.keys(value).sort()) {
      result[key] = normalizeValue(value[key], seen);
    }
    return result;
  }
  return value;
};

/** 比较两个字段值是否等价（空值与空字符串等价，对象按键排序后深比较）。 */
export const isSameFieldValue = (left: unknown, right: unknown): boolean => {
  const a = normalizeValue(left);
  const b = normalizeValue(right);
  if (a === b) return true;
  if (Array.isArray(a) && Array.isArray(b)) {
    return a.length === b.length && a.every((item, index) => isSameFieldValue(item, b[index]));
  }
  if (isPlainObject(a) && isPlainObject(b)) {
    const aKeys = Object.keys(a);
    const bKeys = Object.keys(b);
    return (
      aKeys.length === bKeys.length && aKeys.every((key) => isSameFieldValue(a[key], b[key]))
    );
  }
  return false;
};

export type AgentFormSnapshot = Record<string, unknown>;

/**
 * 计算相对基线的修改字段。
 *
 * 只比较内容，不依赖 onChange 事件：内容改回原值后修改标记自动消失。
 */
export const collectDirtyFields = (
  current: AgentFormSnapshot,
  baseline: AgentFormSnapshot,
  fields: string[],
): string[] => fields.filter((field) => !isSameFieldValue(current[field], baseline[field]));

/** 未保存修改涉及的设置分区。 */
export const collectDirtySections = (dirtyFields: string[]): Set<AgentTemplateSectionKey> => {
  const sections = new Set<AgentTemplateSectionKey>();
  for (const field of dirtyFields) {
    const section = findSectionByWorkspaceField(field);
    if (section) sections.add(section);
  }
  return sections;
};

/** 未保存修改涉及的 Prompt 文档字段。 */
export const collectDirtyPromptDocuments = (dirtyFields: string[]): Set<string> =>
  new Set(dirtyFields.filter((field) => findPromptDocument(field) !== undefined));

// ── 校验错误定位 ────────────────────────────────────────────────────

export interface WorkspaceAgentErrorField {
  name?: (string | number)[];
}

export interface WorkspaceAgentErrorSummary {
  sections: Set<AgentTemplateSectionKey>;
  promptDocuments: Set<string>;
  countsBySection: Record<string, number>;
  firstField?: string;
  firstSection?: AgentTemplateSectionKey;
}

/** 把 validateFields 的 errorFields 归类到分区、Prompt 文档与错误计数。 */
export const summarizeValidationErrors = (
  errorFields: WorkspaceAgentErrorField[],
): WorkspaceAgentErrorSummary => {
  const sections = new Set<AgentTemplateSectionKey>();
  const promptDocuments = new Set<string>();
  const countsBySection: Record<string, number> = {};
  let firstField: string | undefined;
  let firstSection: AgentTemplateSectionKey | undefined;

  for (const errorField of errorFields) {
    const field = String(errorField.name?.[0] ?? '');
    if (!field) continue;
    const section = findSectionByWorkspaceField(field) ?? 'basic';
    sections.add(section);
    countsBySection[section] = (countsBySection[section] ?? 0) + 1;
    if (findPromptDocument(field)) promptDocuments.add(field);
    if (!firstField) {
      firstField = field;
      firstSection = section;
    }
  }

  return { sections, promptDocuments, countsBySection, firstField, firstSection };
};

// ── 占位符模板辅助 ──────────────────────────────────────────────────

const PLACEHOLDER_PATTERN = /\{\{\s*([^{}\s]+)\s*\}\}/g;

/**
 * 提取模板中已出现的变量名（仅展示，不做替换）。
 *
 * 允许列表、缺失变量行为与转义规则以 Core 实现为准，前端不宣称变量一定可解析。
 */
export const extractTemplateVariables = (text: string | undefined | null): string[] => {
  if (!text) return [];
  const names = new Set<string>();
  PLACEHOLDER_PATTERN.lastIndex = 0;
  let match = PLACEHOLDER_PATTERN.exec(text);
  while (match) {
    names.add(match[1]);
    match = PLACEHOLDER_PATTERN.exec(text);
  }
  return Array.from(names);
};

// ── 文档内查找 ──────────────────────────────────────────────────────

/** 返回关键词在文本中的所有匹配下标；空关键词返回空数组。 */
export const findTextMatches = (text: string, keyword: string): number[] => {
  if (!keyword) return [];
  const haystack = text.toLowerCase();
  const needle = keyword.toLowerCase();
  const matches: number[] = [];
  let index = haystack.indexOf(needle);
  while (index !== -1) {
    matches.push(index);
    index = haystack.indexOf(needle, index + needle.length);
  }
  return matches;
};

export interface TextareaMetrics {
  fontSize: number;
  lineHeight: number;
  paddingTop: number;
}

/**
 * 估算字符偏移所在行的滚动位置，用于把查找命中滚动到可视区域。
 *
 * 采用按行数 × 行高的近似计算：纯前端无法测量 textarea 内部排版，
 * 这里只要求「滚到附近并选中命中」，不追求像素级定位。
 */
export const estimateScrollTop = (
  text: string,
  offset: number,
  metrics: TextareaMetrics,
): number => {
  const safeOffset = Math.max(0, Math.min(offset, text.length));
  const lineIndex = text.slice(0, safeOffset).split('\n').length - 1;
  const lineTop = metrics.paddingTop + lineIndex * metrics.lineHeight;
  return Math.max(0, lineTop - metrics.lineHeight * 2);
};

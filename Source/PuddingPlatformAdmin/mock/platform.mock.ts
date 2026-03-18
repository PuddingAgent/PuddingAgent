import type { Request, Response } from 'express';

// ── Mock data ─────────────────────────────────────────────────────

const workspaces = [
  {
    workspaceId: 'default',
    name: '默认工作空间',
    description: '系统内置工作空间，不可删除',
    isEnabled: true,
    isFrozen: false,
    channelBindings: [
      {
        channelId: 'ch-telegram-01',
        channelType: 'Telegram',
        defaultAgentTemplateId: 'tpl-general',
        allowedAgentTemplateIds: ['tpl-general', 'tpl-coder'],
      },
    ],
    agentTemplateIds: ['tpl-general', 'tpl-coder'],
    auditAgentTemplateIds: ['tpl-audit'],
  },
  {
    workspaceId: 'ws-dev-team',
    name: '开发团队',
    description: '供开发团队内部使用',
    isEnabled: true,
    isFrozen: false,
    channelBindings: [],
    agentTemplateIds: ['tpl-coder'],
    auditAgentTemplateIds: [],
  },
  {
    workspaceId: 'ws-trial',
    name: '试用空间',
    description: '外部试用用户隔离环境',
    isEnabled: true,
    isFrozen: true,
    channelBindings: [],
    agentTemplateIds: ['tpl-general'],
    auditAgentTemplateIds: ['tpl-audit'],
  },
];

const agentTemplates = [
  {
    templateId: 'tpl-general',
    name: '通用助手',
    description: '面向普通用户的对话智能体，支持知识问答和任务辅助',
    templateType: 'Service',
    skillIds: ['skill-search', 'skill-summarize'],
    systemPrompt: '你是一个友好的助手，帮助用户解答问题。',
    runtime: {
      preferredModel: 'gpt-4o-mini',
      maxContextTokens: 8192,
      maxTurnsPerSession: 40,
    },
    capability: {
      allowShellExecution: false,
      allowFileWrite: false,
      allowNetworkAccess: true,
      allowedToolNames: ['web_search', 'calculator'],
    },
  },
  {
    templateId: 'tpl-coder',
    name: '代码助手',
    description: '专注于编程任务，支持代码生成、调试和重构',
    templateType: 'Task',
    skillIds: ['skill-code-review', 'skill-refactor', 'skill-shell'],
    systemPrompt: '你是一个经验丰富的软件工程师，精通多种编程语言。',
    runtime: {
      preferredModel: 'gpt-4o',
      maxContextTokens: 32768,
      maxTurnsPerSession: 200,
    },
    capability: {
      allowShellExecution: true,
      allowFileWrite: true,
      allowNetworkAccess: true,
      allowedToolNames: ['bash', 'read_file', 'write_file', 'search_code'],
    },
  },
  {
    templateId: 'tpl-audit',
    name: '合规审计',
    description: '对会话内容进行合规检查和风险评估',
    templateType: 'Audit',
    skillIds: ['skill-compliance'],
    systemPrompt: '你是合规审计员，负责检查对话内容是否符合安全规范。',
    runtime: {
      preferredModel: 'gpt-4o-mini',
      maxContextTokens: 4096,
      maxTurnsPerSession: 10,
    },
    capability: {
      allowShellExecution: false,
      allowFileWrite: false,
      allowNetworkAccess: false,
      allowedToolNames: [],
    },
  },
];

const sessions = [
  {
    sessionId: 'sess-001',
    workspaceId: 'default',
    agentTemplateId: 'tpl-general',
    channelId: 'ch-telegram-01',
    ownerUserId: 'user-alice',
    sessionType: 'ServiceSession',
    status: 'Active',
    runtimeNodeId: 'runtime-01',
    agentInstanceId: 'agent-inst-001',
    createdAt: new Date(Date.now() - 3600_000).toISOString(),
    lastActiveAt: new Date(Date.now() - 60_000).toISOString(),
  },
  {
    sessionId: 'sess-002',
    workspaceId: 'default',
    agentTemplateId: 'tpl-coder',
    channelId: 'ch-telegram-01',
    ownerUserId: 'user-bob',
    sessionType: 'TaskSession',
    status: 'Idle',
    runtimeNodeId: 'runtime-01',
    agentInstanceId: 'agent-inst-002',
    createdAt: new Date(Date.now() - 86400_000).toISOString(),
    lastActiveAt: new Date(Date.now() - 7200_000).toISOString(),
  },
  {
    sessionId: 'sess-003',
    workspaceId: 'ws-dev-team',
    agentTemplateId: 'tpl-coder',
    channelId: 'ch-cli-01',
    ownerUserId: 'user-dev',
    sessionType: 'TaskSession',
    status: 'Completed',
    createdAt: new Date(Date.now() - 172800_000).toISOString(),
    lastActiveAt: new Date(Date.now() - 86400_000).toISOString(),
  },
  {
    sessionId: 'sess-004',
    workspaceId: 'ws-trial',
    agentTemplateId: 'tpl-general',
    channelId: 'ch-web-01',
    ownerUserId: 'user-trial',
    sessionType: 'ServiceSession',
    status: 'Frozen',
    createdAt: new Date(Date.now() - 259200_000).toISOString(),
    lastActiveAt: new Date(Date.now() - 172800_000).toISOString(),
  },
];

// ── LLM Resource Pool mock data ─────────────────────────────────

const llmProviders: any[] = [
  {
    id: 1,
    providerId: 'openai',
    name: 'OpenAI',
    protocol: 'openai',
    baseUrl: 'https://api.openai.com/v1',
    description: 'OpenAI official API',
    isEnabled: true,
    hasApiKey: true,
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
    quota: { dailyTokenLimit: 1000000, monthlyTokenLimit: 20000000, dailyTokensUsed: 320000, monthlyTokensUsed: 8500000, isSuspended: false, updatedAt: '2024-01-01T00:00:00Z' },
  },
  {
    id: 2,
    providerId: 'deepseek',
    name: 'DeepSeek',
    protocol: 'openai',
    baseUrl: 'https://api.deepseek.com/v1',
    description: 'DeepSeek API（兼容 OpenAI 协议）',
    isEnabled: true,
    hasApiKey: true,
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
    quota: { dailyTokenLimit: null, monthlyTokenLimit: 5000000, dailyTokensUsed: 0, monthlyTokensUsed: 1200000, isSuspended: false, updatedAt: '2024-01-01T00:00:00Z' },
  },
];

const llmModels: any[] = [
  { id: 1, providerId: 1, modelId: 'gpt-4o-mini', name: 'GPT-4o Mini', description: '高性价比小模型', maxContextTokens: 128000, inputPricePer1MTokens: 0.15, outputPricePer1MTokens: 0.6, capabilityTags: ['chat', 'code', 'function-calling'], isDeprecated: false, isDefault: true, sortOrder: 0, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
  { id: 2, providerId: 1, modelId: 'gpt-4o', name: 'GPT-4o', description: '最强多模态旗舰模型', maxContextTokens: 128000, inputPricePer1MTokens: 5.0, outputPricePer1MTokens: 15.0, capabilityTags: ['chat', 'code', 'vision', 'function-calling', 'reasoning'], isDeprecated: false, isDefault: false, sortOrder: 1, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
  { id: 3, providerId: 2, modelId: 'deepseek-chat', name: 'DeepSeek Chat', description: 'DeepSeek V3 对话模型', maxContextTokens: 64000, inputPricePer1MTokens: 0.27, outputPricePer1MTokens: 1.1, capabilityTags: ['chat', 'code', 'function-calling'], isDeprecated: false, isDefault: true, sortOrder: 0, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
  { id: 4, providerId: 2, modelId: 'deepseek-reasoner', name: 'DeepSeek Reasoner', description: 'DeepSeek R1 推理模型', maxContextTokens: 64000, inputPricePer1MTokens: 0.55, outputPricePer1MTokens: 2.19, capabilityTags: ['chat', 'reasoning'], isDeprecated: false, isDefault: false, sortOrder: 1, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
];

// ── Global Agent Template mock data ──────────────────────────────

const globalAgentTemplates: any[] = [
  { id: 1, templateId: 'general-assistant', name: '通用助手', description: '面向普通用户的对话助手', role: 'Service', systemPrompt: '你是一个友好、专业的 AI 助手，帮助用户解答问题。', userPromptTemplate: null, preferredProviderId: 'openai', preferredModelId: 'gpt-4o-mini', maxContextTokens: 8192, maxReplyTokens: 2048, isBuiltIn: true, isEnabled: true, sortOrder: 0, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
  { id: 2, templateId: 'code-assistant', name: '代码助手', description: '专注编程任务的智能体', role: 'Task', systemPrompt: '你是一名高级软件工程师，精通多种编程语言，擅长代码生成、调试和重构。', userPromptTemplate: null, preferredProviderId: 'openai', preferredModelId: 'gpt-4o', maxContextTokens: 32768, maxReplyTokens: 4096, isBuiltIn: true, isEnabled: true, sortOrder: 1, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
  { id: 3, templateId: 'compliance-auditor', name: '合规审计', description: '对会话内容进行合规风险评估', role: 'Audit', systemPrompt: '你是合规审计员，请评估对话内容是否存在违规风险并给出报告。', userPromptTemplate: null, preferredProviderId: null, preferredModelId: null, maxContextTokens: 4096, maxReplyTokens: 1024, isBuiltIn: true, isEnabled: true, sortOrder: 2, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
];

// ── Workspace Agent Template mock data ───────────────────────────

const workspaceAgentTemplates: any[] = [
  { id: 1, workspaceId: 'default', templateId: 'ws-general', name: '工作空间助手', description: '默认工作空间专用助手', role: 'Service', systemPrompt: '你是默认工作空间的助手，熟悉平台内部流程。', userPromptTemplate: null, baseGlobalTemplateId: 'general-assistant', preferredProviderId: 'openai', preferredModelId: 'gpt-4o-mini', maxContextTokens: 8192, maxReplyTokens: 2048, isEnabled: true, sortOrder: 0, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
  { id: 2, workspaceId: 'ws-dev-team', templateId: 'dev-coder', name: '开发团队代码助手', description: '为开发团队定制的代码精灵', role: 'Task', systemPrompt: '你是开发团队的代码助手，熟悉团队的技术栈（.NET 10 / React / TypeScript）。', userPromptTemplate: null, baseGlobalTemplateId: 'code-assistant', preferredProviderId: 'deepseek', preferredModelId: 'deepseek-chat', maxContextTokens: 32768, maxReplyTokens: 4096, isEnabled: true, sortOrder: 0, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
  { id: 3, workspaceId: 'ws-trial', templateId: 'trial-simple', name: '试用版助手', description: '功能受限的试用体验助手', role: 'Service', systemPrompt: '你是试用版 AI 助手，功能有限，如需完整体验请升级套餐。', userPromptTemplate: null, baseGlobalTemplateId: 'general-assistant', preferredProviderId: 'deepseek', preferredModelId: 'deepseek-chat', maxContextTokens: 4096, maxReplyTokens: 512, isEnabled: true, sortOrder: 0, createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z' },
];

// ── Runtime 节点 Mock 数据 ─────────────────────────────────────────
const now = () => new Date().toISOString();
const ago = (seconds: number) => new Date(Date.now() - seconds * 1000).toISOString();

let runtimeNodes: any[] = [
  {
    nodeId: 'runtime-std-001',
    endpoint: 'http://pudding-runtime:8080',
    status: 'Online',
    lastHeartbeat: ago(12),
    activeSessionCount: 3,
    embeddedMode: false,
    hostType: null,
    nativeCapabilities: [],
    isFrozen: false,
  },
  {
    nodeId: 'runtime-embedded-dev',
    endpoint: 'http://192.168.1.42:5100',
    status: 'Online',
    lastHeartbeat: ago(28),
    activeSessionCount: 1,
    embeddedMode: true,
    hostType: 'PuddingCode/CLI',
    nativeCapabilities: [
      { capabilityId: 'cap-run-test', name: '运行单元测试', description: '在宿主进程内执行 dotnet test 并返回结果', category: 'RunTest', requiresApproval: false },
      { capabilityId: 'cap-exec-cmd', name: '执行 Shell 命令', description: '在宿主机上执行受限 Shell 命令', category: 'ExecuteCommand', requiresApproval: true },
      { capabilityId: 'cap-query-state', name: '查询内存状态', description: '读取宿主进程当前内存快照', category: 'QueryState', requiresApproval: false },
    ],
    isFrozen: false,
  },
  {
    nodeId: 'runtime-offline-003',
    endpoint: 'http://192.168.1.100:5100',
    status: 'Offline',
    lastHeartbeat: ago(300),
    activeSessionCount: 0,
    embeddedMode: false,
    hostType: null,
    nativeCapabilities: [],
    isFrozen: false,
  },
];

// ── Routes ────────────────────────────────────────────────────────

export default {
  'GET /api/workspaces': (_req: Request, res: Response) => {
    res.json(workspaces);
  },

  'GET /api/workspaces/:id': (req: Request, res: Response) => {
    const ws = workspaces.find((w) => w.workspaceId === req.params.id);
    if (!ws) return res.status(404).json({ message: 'Not found' });
    return res.json(ws);
  },

  'POST /api/workspaces/:id/freeze': (req: Request, res: Response) => {
    const ws = workspaces.find((w) => w.workspaceId === req.params.id);
    if (ws) ws.isFrozen = true;
    res.json({ success: true });
  },

  'POST /api/workspaces/:id/unfreeze': (req: Request, res: Response) => {
    const ws = workspaces.find((w) => w.workspaceId === req.params.id);
    if (ws) ws.isFrozen = false;
    res.json({ success: true });
  },

  'DELETE /api/workspaces/:id': (req: Request, res: Response) => {
    const idx = workspaces.findIndex((w) => w.workspaceId === req.params.id);
    if (idx !== -1) workspaces.splice(idx, 1);
    res.json({ success: true });
  },

  'GET /api/agent-templates': (_req: Request, res: Response) => {
    res.json(agentTemplates);
  },

  'GET /api/agent-templates/:id': (req: Request, res: Response) => {
    const tpl = agentTemplates.find((t) => t.templateId === req.params.id);
    if (!tpl) return res.status(404).json({ message: 'Not found' });
    return res.json(tpl);
  },

  'GET /api/sessions': (req: Request, res: Response) => {
    const { workspaceId } = req.query;
    if (workspaceId) {
      return res.json(sessions.filter((s) => s.workspaceId === workspaceId));
    }
    return res.json(sessions);
  },

  'GET /api/sessions/:id': (req: Request, res: Response) => {
    const sess = sessions.find((s) => s.sessionId === req.params.id);
    if (!sess) return res.status(404).json({ message: 'Not found' });
    return res.json(sess);
  },

  // ── LLM Provider Mock ─────────────────────────────────────────

  'GET /api/llm/providers': (_req: Request, res: Response) => {
    res.json(llmProviders);
  },

  'GET /api/llm/providers/:providerId': (req: Request, res: Response) => {
    const p = llmProviders.find((x) => x.providerId === req.params.providerId);
    if (!p) return res.status(404).json({ message: 'Not found' });
    return res.json({ ...p, models: llmModels.filter((m) => m.providerId === p.id) });
  },

  'POST /api/llm/providers': (req: Request, res: Response) => {
    const body = req.body;
    const newp = { ...body, id: llmProviders.length + 1, hasApiKey: !!body.apiKey, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() };
    llmProviders.push(newp);
    res.status(201).json(newp);
  },

  'PUT /api/llm/providers/:providerId': (req: Request, res: Response) => {
    const p = llmProviders.find((x) => x.providerId === req.params.providerId);
    if (!p) return res.status(404).json({ message: 'Not found' });
    Object.assign(p, req.body, { updatedAt: new Date().toISOString() });
    return res.json(p);
  },

  'DELETE /api/llm/providers/:providerId': (req: Request, res: Response) => {
    const idx = llmProviders.findIndex((x) => x.providerId === req.params.providerId);
    if (idx !== -1) llmProviders.splice(idx, 1);
    res.status(204).end();
  },

  'GET /api/llm/providers/:providerId/quota': (req: Request, res: Response) => {
    const p = llmProviders.find((x) => x.providerId === req.params.providerId);
    if (!p) return res.status(404).json({ message: 'Not found' });
    return res.json(p.quota ?? { dailyTokenLimit: null, monthlyTokenLimit: null, dailyTokensUsed: 0, monthlyTokensUsed: 0, isSuspended: false, updatedAt: new Date().toISOString() });
  },

  'PUT /api/llm/providers/:providerId/quota': (req: Request, res: Response) => {
    const p = llmProviders.find((x) => x.providerId === req.params.providerId);
    if (!p) return res.status(404).json({ message: 'Not found' });
    p.quota = { ...p.quota, ...req.body, updatedAt: new Date().toISOString() };
    return res.json(p.quota);
  },

  'POST /api/llm/providers/:providerId/quota/reset-daily': (req: Request, res: Response) => {
    const p = llmProviders.find((x) => x.providerId === req.params.providerId);
    if (p?.quota) { p.quota.dailyTokensUsed = 0; p.quota.isSuspended = false; }
    res.status(204).end();
  },

  'GET /api/llm/providers/:providerId/models': (req: Request, res: Response) => {
    const p = llmProviders.find((x) => x.providerId === req.params.providerId);
    if (!p) return res.status(404).json({ message: 'Not found' });
    return res.json(llmModels.filter((m) => m.providerId === p.id));
  },

  'POST /api/llm/providers/:providerId/models': (req: Request, res: Response) => {
    const p = llmProviders.find((x) => x.providerId === req.params.providerId);
    if (!p) return res.status(404).json({ message: 'Not found' });
    const m = { ...req.body, id: llmModels.length + 1, providerId: p.id, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() };
    llmModels.push(m);
    return res.status(201).json(m);
  },

  'PUT /api/llm/providers/:providerId/models/:modelId': (req: Request, res: Response) => {
    const m = llmModels.find((x) => x.modelId === req.params.modelId);
    if (!m) return res.status(404).json({ message: 'Not found' });
    Object.assign(m, req.body, { updatedAt: new Date().toISOString() });
    return res.json(m);
  },

  'DELETE /api/llm/providers/:providerId/models/:modelId': (req: Request, res: Response) => {
    const idx = llmModels.findIndex((x) => x.modelId === req.params.modelId);
    if (idx !== -1) llmModels.splice(idx, 1);
    res.status(204).end();
  },

  // ── Global Agent Template Mock ────────────────────────────────

  'GET /api/global-agent-templates': (_req: Request, res: Response) => {
    res.json(globalAgentTemplates);
  },

  'GET /api/global-agent-templates/:templateId': (req: Request, res: Response) => {
    const t = globalAgentTemplates.find((x) => x.templateId === req.params.templateId);
    if (!t) return res.status(404).json({ message: 'Not found' });
    return res.json(t);
  },

  'POST /api/global-agent-templates': (req: Request, res: Response) => {
    const t = { ...req.body, id: globalAgentTemplates.length + 1, isBuiltIn: false, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() };
    globalAgentTemplates.push(t);
    res.status(201).json(t);
  },

  'PUT /api/global-agent-templates/:templateId': (req: Request, res: Response) => {
    const t = globalAgentTemplates.find((x) => x.templateId === req.params.templateId);
    if (!t) return res.status(404).json({ message: 'Not found' });
    Object.assign(t, req.body, { updatedAt: new Date().toISOString() });
    return res.json(t);
  },

  'DELETE /api/global-agent-templates/:templateId': (req: Request, res: Response) => {
    const t = globalAgentTemplates.find((x) => x.templateId === req.params.templateId);
    if (!t) return res.status(404).json({ message: 'Not found' });
    if (t.isBuiltIn) return res.status(400).json({ error: '系统内置模板不允许删除' });
    const idx = globalAgentTemplates.indexOf(t);
    globalAgentTemplates.splice(idx, 1);
    return res.status(204).end();
  },

  // ── Workspace Agent Template Mock ─────────────────────────────

  'GET /api/workspace-agent-templates': (req: Request, res: Response) => {
    const { workspaceId } = req.query;
    if (workspaceId) return res.json(workspaceAgentTemplates.filter((t) => t.workspaceId === workspaceId));
    return res.json(workspaceAgentTemplates);
  },

  'GET /api/workspace-agent-templates/:id': (req: Request, res: Response) => {
    const t = workspaceAgentTemplates.find((x) => x.id === Number(req.params.id));
    if (!t) return res.status(404).json({ message: 'Not found' });
    return res.json(t);
  },

  'POST /api/workspace-agent-templates': (req: Request, res: Response) => {
    const t = { ...req.body, id: workspaceAgentTemplates.length + 1, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() };
    workspaceAgentTemplates.push(t);
    return res.status(201).json(t);
  },

  'PUT /api/workspace-agent-templates/:id': (req: Request, res: Response) => {
    const t = workspaceAgentTemplates.find((x) => x.id === Number(req.params.id));
    if (!t) return res.status(404).json({ message: 'Not found' });
    Object.assign(t, req.body, { updatedAt: new Date().toISOString() });
    return res.json(t);
  },

  'DELETE /api/workspace-agent-templates/:id': (req: Request, res: Response) => {
    const idx = workspaceAgentTemplates.findIndex((x) => x.id === Number(req.params.id));
    if (idx !== -1) workspaceAgentTemplates.splice(idx, 1);
    res.status(204).end();
  },

  // ── Users Mock ────────────────────────────────────────────────

  'GET /api/users': (_req: Request, res: Response) => res.json(mockUsers),

  'GET /api/users/:userId': (req: Request, res: Response) => {
    const u = mockUsers.find((x) => x.userId === req.params.userId);
    if (!u) return res.status(404).json({ message: 'Not found' });
    return res.json(u);
  },

  'POST /api/users': (req: Request, res: Response) => {
    const u = { ...req.body, id: mockUsers.length + 1, roleIds: [], isEnabled: true, createdAt: new Date().toISOString() };
    delete u.password;
    mockUsers.push(u);
    return res.status(201).json(u);
  },

  'PUT /api/users/:userId': (req: Request, res: Response) => {
    const u = mockUsers.find((x) => x.userId === req.params.userId);
    if (!u) return res.status(404).json({ message: 'Not found' });
    Object.assign(u, req.body);
    return res.json(u);
  },

  'PUT /api/users/:userId/password': (_req: Request, res: Response) => res.status(204).end(),

  'PUT /api/users/:userId/roles': (req: Request, res: Response) => {
    const u = mockUsers.find((x) => x.userId === req.params.userId);
    if (!u) return res.status(404).json({ message: 'Not found' });
    u.roleIds = req.body.roleIds;
    return res.json(u);
  },

  'DELETE /api/users/:userId': (req: Request, res: Response) => {
    const idx = mockUsers.findIndex((x) => x.userId === req.params.userId);
    if (idx !== -1) mockUsers.splice(idx, 1);
    return res.status(204).end();
  },

  // ── Roles Mock ────────────────────────────────────────────────

  'GET /api/roles': (_req: Request, res: Response) => res.json(mockRoles),

  'POST /api/roles': (req: Request, res: Response) => {
    const r = { ...req.body, id: mockRoles.length + 1, isSystemRole: false, createdAt: new Date().toISOString() };
    mockRoles.push(r);
    return res.status(201).json(r);
  },

  'PUT /api/roles/:roleId': (req: Request, res: Response) => {
    const r = mockRoles.find((x) => x.roleId === req.params.roleId);
    if (!r) return res.status(404).json({ message: 'Not found' });
    if (r.isSystemRole) return res.status(400).json({ message: '系统内置角色不可修改' });
    Object.assign(r, req.body, { roleId: r.roleId });
    return res.json(r);
  },

  'DELETE /api/roles/:roleId': (req: Request, res: Response) => {
    const r = mockRoles.find((x) => x.roleId === req.params.roleId);
    if (!r) return res.status(404).json({ message: 'Not found' });
    if (r.isSystemRole) return res.status(400).json({ message: '系统内置角色不可删除' });
    const idx = mockRoles.indexOf(r);
    mockRoles.splice(idx, 1);
    return res.status(204).end();
  },

  // ── Teams Mock ────────────────────────────────────────────────

  'GET /api/teams': (_req: Request, res: Response) =>
    res.json(mockTeams.map((t) => ({
      ...t,
      memberCount: mockTeamMembers.filter((m) => m.teamId === t.teamId).length,
      workspaceCount: mockWorkspaces.filter((w) => w.teamId === t.teamId).length,
    }))),

  'GET /api/teams/:teamId': (req: Request, res: Response) => {
    const t = mockTeams.find((x) => x.teamId === req.params.teamId);
    if (!t) return res.status(404).json({ message: 'Not found' });
    const members = mockTeamMembers
      .filter((m) => m.teamId === t.teamId)
      .map((m) => {
        const u = mockUsers.find((u) => u.userId === m.userId);
        return { userId: m.userId, username: u?.username ?? m.userId, displayName: u?.displayName, role: m.role };
      });
    const workspaces = mockWorkspaces
      .filter((w) => w.teamId === t.teamId)
      .map((w) => ({ ...w, teamName: t.name, memberCount: mockWsMembers.filter((m) => m.workspaceId === w.workspaceId).length }));
    return res.json({ ...t, memberCount: members.length, workspaceCount: workspaces.length, members, workspaces });
  },

  'POST /api/teams': (req: Request, res: Response) => {
    const t = { ...req.body, id: mockTeams.length + 1, createdAt: new Date().toISOString() };
    mockTeams.push(t);
    return res.status(201).json({ ...t, memberCount: 0, workspaceCount: 0 });
  },

  'PUT /api/teams/:teamId': (req: Request, res: Response) => {
    const t = mockTeams.find((x) => x.teamId === req.params.teamId);
    if (!t) return res.status(404).json({ message: 'Not found' });
    Object.assign(t, req.body, { teamId: t.teamId });
    return res.json({ ...t, memberCount: mockTeamMembers.filter((m) => m.teamId === t.teamId).length, workspaceCount: mockWorkspaces.filter((w) => w.teamId === t.teamId).length });
  },

  'DELETE /api/teams/:teamId': (req: Request, res: Response) => {
    const wsCount = mockWorkspaces.filter((w) => w.teamId === req.params.teamId).length;
    if (wsCount > 0) return res.status(400).json({ message: '请先删除团队下所有工作区' });
    const idx = mockTeams.findIndex((x) => x.teamId === req.params.teamId);
    if (idx !== -1) mockTeams.splice(idx, 1);
    return res.status(204).end();
  },

  'GET /api/teams/:teamId/members': (req: Request, res: Response) => {
    const members = mockTeamMembers.filter((m) => m.teamId === req.params.teamId).map((m) => {
      const u = mockUsers.find((u) => u.userId === m.userId);
      return { userId: m.userId, username: u?.username ?? m.userId, displayName: u?.displayName, role: m.role };
    });
    return res.json(members);
  },

  'POST /api/teams/:teamId/members': (req: Request, res: Response) => {
    const { userId, role } = req.body;
    const u = mockUsers.find((u) => u.userId === userId);
    mockTeamMembers.push({ teamId: req.params.teamId, userId, role });
    return res.json({ userId, username: u?.username ?? userId, displayName: u?.displayName, role });
  },

  'DELETE /api/teams/:teamId/members/:userId': (req: Request, res: Response) => {
    const idx = mockTeamMembers.findIndex((m) => m.teamId === req.params.teamId && m.userId === req.params.userId);
    if (idx !== -1) mockTeamMembers.splice(idx, 1);
    return res.status(204).end();
  },

  'GET /api/teams/:teamId/workspaces': (req: Request, res: Response) => {
    const t = mockTeams.find((x) => x.teamId === req.params.teamId);
    return res.json(mockWorkspaces.filter((w) => w.teamId === req.params.teamId).map((w) => ({
      ...w, teamName: t?.name ?? '', memberCount: mockWsMembers.filter((m) => m.workspaceId === w.workspaceId).length,
    })));
  },

  'POST /api/teams/:teamId/workspaces': (req: Request, res: Response) => {
    const t = mockTeams.find((x) => x.teamId === req.params.teamId);
    const w = { ...req.body, id: mockWorkspaces.length + 1, teamId: req.params.teamId, slug: req.body.workspaceId, isEnabled: true, isFrozen: false, createdAt: new Date().toISOString() };
    mockWorkspaces.push(w);
    return res.status(201).json({ ...w, teamName: t?.name ?? '', memberCount: 0 });
  },

  'GET /api/teams/workspaces/:workspaceId': (req: Request, res: Response) => {
    const w = mockWorkspaces.find((x) => x.workspaceId === req.params.workspaceId);
    if (!w) return res.status(404).json({ message: 'Not found' });
    const t = mockTeams.find((x) => x.teamId === w.teamId);
    return res.json({ ...w, teamName: t?.name ?? '', memberCount: mockWsMembers.filter((m) => m.workspaceId === w.workspaceId).length });
  },

  'PUT /api/teams/workspaces/:workspaceId': (req: Request, res: Response) => {
    const w = mockWorkspaces.find((x) => x.workspaceId === req.params.workspaceId);
    if (!w) return res.status(404).json({ message: 'Not found' });
    Object.assign(w, req.body, { workspaceId: w.workspaceId, teamId: w.teamId });
    const t = mockTeams.find((x) => x.teamId === w.teamId);
    return res.json({ ...w, teamName: t?.name ?? '', memberCount: mockWsMembers.filter((m) => m.workspaceId === w.workspaceId).length });
  },

  'DELETE /api/teams/workspaces/:workspaceId': (req: Request, res: Response) => {
    const idx = mockWorkspaces.findIndex((x) => x.workspaceId === req.params.workspaceId);
    if (idx !== -1) mockWorkspaces.splice(idx, 1);
    return res.status(204).end();
  },

  'GET /api/teams/workspaces/:workspaceId/members': (req: Request, res: Response) => {
    return res.json(mockWsMembers.filter((m) => m.workspaceId === req.params.workspaceId).map((m) => {
      const u = mockUsers.find((u) => u.userId === m.userId);
      return { ...m, username: u?.username ?? m.userId, displayName: u?.displayName };
    }));
  },

  'POST /api/teams/workspaces/:workspaceId/members': (req: Request, res: Response) => {
    const { userId, accessLevel } = req.body;
    const m = { id: mockWsMembers.length + 1, workspaceId: req.params.workspaceId, userId, accessLevel };
    mockWsMembers.push(m);
    const u = mockUsers.find((u) => u.userId === userId);
    return res.json({ ...m, username: u?.username ?? userId, displayName: u?.displayName });
  },

  'DELETE /api/teams/workspaces/:workspaceId/members/:id': (req: Request, res: Response) => {
    const idx = mockWsMembers.findIndex((m) => m.id === Number(req.params.id));
    if (idx !== -1) mockWsMembers.splice(idx, 1);
    return res.status(204).end();
  },

  // ── Runtime Registry (Controller via /ingress/) ───────────────
  'GET /ingress/runtime-registry/nodes': (_req: Request, res: Response) => {
    // 每次刷新动态更新心跳时间（模拟在线节点持续发心跳）
    runtimeNodes = runtimeNodes.map((n) => {
      if (n.status === 'Online') return { ...n, lastHeartbeat: ago(Math.floor(Math.random() * 25) + 5) };
      return n;
    });
    return res.json(runtimeNodes);
  },

  'GET /ingress/runtime-registry/embedded': (_req: Request, res: Response) => {
    return res.json(runtimeNodes.filter((n) => n.embeddedMode));
  },

  'GET /ingress/runtime-registry/:nodeId/capabilities': (req: Request, res: Response) => {
    const node = runtimeNodes.find((n) => n.nodeId === req.params.nodeId);
    if (!node) return res.status(404).json({ message: 'Node not found' });
    return res.json(node.nativeCapabilities ?? []);
  },

  'POST /ingress/runtime-registry/:nodeId/freeze': (req: Request, res: Response) => {
    const node = runtimeNodes.find((n) => n.nodeId === req.params.nodeId);
    if (!node) return res.status(404).json({ message: 'Node not found' });
    node.isFrozen = true;
    return res.json({ nodeId: node.nodeId, isFrozen: true, reason: req.body?.reason });
  },

  'POST /ingress/runtime-registry/:nodeId/unfreeze': (req: Request, res: Response) => {
    const node = runtimeNodes.find((n) => n.nodeId === req.params.nodeId);
    if (!node) return res.status(404).json({ message: 'Node not found' });
    node.isFrozen = false;
    return res.json({ nodeId: node.nodeId, isFrozen: false });
  },
};

// ── Mock data stores ──────────────────────────────────────────────

const mockUsers: any[] = [
  { id: 1, userId: 'admin', username: 'admin', email: 'admin@pudding.local', displayName: '平台管理员', userType: 'Admin', isEnabled: true, roleIds: [], createdAt: '2024-01-01T00:00:00Z' },
  { id: 2, userId: 'alice', username: 'alice', email: 'alice@example.com', displayName: 'Alice', userType: 'SimpleUser', isEnabled: true, roleIds: ['workspace-admin'], createdAt: '2024-02-01T00:00:00Z' },
  { id: 3, userId: 'bob', username: 'bob', email: 'bob@example.com', displayName: 'Bob', userType: 'SimpleUser', isEnabled: true, roleIds: ['workspace-editor'], createdAt: '2024-03-01T00:00:00Z' },
];

const mockRoles: any[] = [
  { id: 1, roleId: 'workspace-admin', name: 'Workspace 管理员', description: '可管理 Workspace', permissions: ['workspace:manage', 'workspace:write', 'workspace:read', 'agent:manage', 'template:manage'], isSystemRole: true, createdAt: '2024-01-01T00:00:00Z' },
  { id: 2, roleId: 'workspace-editor', name: 'Workspace 编辑', description: '可编辑 Workspace', permissions: ['workspace:write', 'workspace:read', 'agent:run', 'template:read'], isSystemRole: true, createdAt: '2024-01-01T00:00:00Z' },
  { id: 3, roleId: 'workspace-viewer', name: 'Workspace 查看者', description: '只读', permissions: ['workspace:read', 'template:read'], isSystemRole: true, createdAt: '2024-01-01T00:00:00Z' },
  { id: 4, roleId: 'llm-admin', name: 'LLM 资源管理员', description: '管理 LLM 资源池', permissions: ['llm:manage', 'llm:read'], isSystemRole: true, createdAt: '2024-01-01T00:00:00Z' },
];

const mockTeams: any[] = [
  { id: 1, teamId: 'platform-team', name: '平台团队', description: '平台默认团队', isEnabled: true, createdAt: '2024-01-01T00:00:00Z' },
  { id: 2, teamId: 'dev-team', name: '开发团队', description: '产品开发团队', isEnabled: true, createdAt: '2024-02-01T00:00:00Z' },
];

const mockTeamMembers: any[] = [
  { teamId: 'platform-team', userId: 'admin', role: 'Admin' },
  { teamId: 'platform-team', userId: 'alice', role: 'Member' },
  { teamId: 'dev-team', userId: 'alice', role: 'Admin' },
  { teamId: 'dev-team', userId: 'bob', role: 'Member' },
];

const mockWorkspaces: any[] = [
  { id: 1, workspaceId: 'default', slug: 'default', teamId: 'platform-team', name: '默认工作空间', description: '平台内置默认工作空间', teamAccessPolicy: 'Write', companyAccessPolicy: 'ReadOnly', isEnabled: true, isFrozen: false, createdAt: '2024-01-01T00:00:00Z' },
  { id: 2, workspaceId: 'dev-workspace', slug: 'dev-workspace', teamId: 'dev-team', name: '开发工作空间', description: '开发团队专用', teamAccessPolicy: 'Write', companyAccessPolicy: 'None', isEnabled: true, isFrozen: false, createdAt: '2024-02-01T00:00:00Z' },
];

const mockWsMembers: any[] = [
  { id: 1, workspaceId: 'default', userId: 'bob', accessLevel: 'Write' },
];

